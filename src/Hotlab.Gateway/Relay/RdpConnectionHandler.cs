using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Hotlab.Gateway.Activation;
using Hotlab.Gateway.Protocol;
using Hotlab.Gateway.Routing;
using Hotlab.Gateway.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hotlab.Gateway.Relay;

/// <summary>
/// Handles one inbound RDP connection: reads the X.224 Connection Request, routes on a
/// signed Hotlab routing token (.rdp <c>loadbalanceinfo</c>) or else the <c>mstshash</c>
/// cookie, activates the user's VM, then forwards the buffered first PDU
/// verbatim and splices both directions. No TLS termination: security negotiation
/// happens end to end between client and guest.
/// </summary>
public sealed class RdpConnectionHandler
{
    private readonly IUserTargetResolver _resolver;
    private readonly IVmActivator _activator;
    private readonly RoutingTokenService _tokens;
    private readonly GatewayOptions _options;
    private readonly ILogger<RdpConnectionHandler> _logger;

    /// <summary>Creates a handler.</summary>
    public RdpConnectionHandler(
        IUserTargetResolver resolver,
        IVmActivator activator,
        RoutingTokenService tokens,
        IOptions<GatewayOptions> options,
        ILogger<RdpConnectionHandler> logger)
    {
        _resolver = resolver;
        _activator = activator;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Serves <paramref name="client"/> to completion. Takes ownership of the socket and
    /// always disposes it. Never throws except for <see cref="ArgumentNullException"/>.
    /// </summary>
    public async Task HandleAsync(Socket client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var total = Stopwatch.StartNew();
        var remote = client.RemoteEndPoint;

        using (client)
        {
            try
            {
                await HandleCoreAsync(client, remote, total, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Connection from {Remote} cancelled by shutdown", remote);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error serving {Remote}", remote);
            }
        }
    }

    private async Task HandleCoreAsync(Socket client, EndPoint? remote, Stopwatch total, CancellationToken ct)
    {
        ConfigureSocket(client);

        var pdu = await ReadFirstPduAsync(client, remote, ct).ConfigureAwait(false);
        if (pdu is null)
            return;

        if (!X224ConnectionRequest.TryParse(pdu, out var request) || request is null)
        {
            Reject(remote, "first PDU is not an X.224 Connection Request");
            return;
        }

        if (!TryRoute(request, remote, out var user, out var target))
            return;

        var activation = Stopwatch.StartNew();
        IPEndPoint backendEp;
        try
        {
            backendEp = await _activator.EnsureReadyAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Rejecting {Remote} user {User}: activation of {Node}/{VmId} failed",
                remote, user, target.Node, target.VmId);
            return;
        }

        activation.Stop();

        using var backend = new Socket(backendEp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.BackendConnectTimeout);
            await backend.ConnectAsync(backendEp, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Rejecting {Remote} user {User}: cannot connect to backend {Backend}",
                remote, user, backendEp);
            return;
        }

        ConfigureSocket(backend);
        await RdpRelay.SendAllAsync(backend, pdu, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Relaying {Remote} user {User} -> {Node}/{VmId} at {Backend} (activation {ActivationMs} ms)",
            remote, user, target.Node, target.VmId, backendEp, activation.ElapsedMilliseconds);

        var result = await RdpRelay.RunAsync(client, backend, ct, _options.HalfCloseTimeout).ConfigureAwait(false);

        _logger.LogInformation(
            "Closed {Remote} user {User} -> {Node}/{VmId}: {Up} bytes up, {Down} bytes down, " +
            "activation {ActivationMs} ms, total {TotalMs} ms{Error}",
            remote, user, target.Node, target.VmId,
            result.ClientToBackendBytes + pdu.Length, result.BackendToClientBytes,
            activation.ElapsedMilliseconds, total.ElapsedMilliseconds,
            result.Error is null ? "" : $" ({result.Error.GetType().Name}: {result.Error.Message})");
    }

    /// <summary>
    /// Picks the target. A valid Hotlab routing token wins and the cookie is ignored; an
    /// invalid Hotlab token is rejected outright (no cookie fallback); any other token, or
    /// none, falls through to the mstshash cookie.
    /// </summary>
    private bool TryRoute(
        X224ConnectionRequest request, EndPoint? remote,
        [NotNullWhen(true)] out string? user, [NotNullWhen(true)] out VmTarget? target)
    {
        user = null;
        target = null;

        if (request.RoutingToken is not null)
        {
            var token = _tokens.Validate(request.RoutingToken);
            switch (token.Status)
            {
                case TokenStatus.Valid:
                    _logger.LogInformation(
                        "Connection from {Remote} carries a valid Hotlab token for user {User} (expires {Expiry:o}){CookieNote}",
                        remote, token.User, token.Expiry,
                        request.Cookie is null ? "" : "; ignoring mstshash cookie");
                    user = token.User!;
                    target = _resolver.Resolve(user);
                    if (target is null)
                    {
                        Reject(remote, $"unknown user '{user}' (from Hotlab token)");
                        return false;
                    }
                    return true;

                case TokenStatus.Invalid:
                    _logger.LogWarning(
                        "Rejecting {Remote}: invalid Hotlab token ({Reason}); claimed user {User}, expiry {Expiry:o}",
                        remote, token.Reason, token.User ?? "?", token.Expiry);
                    return false;

                default:
                    _logger.LogInformation(
                        "Connection from {Remote} carries non-Hotlab routing token {RoutingToken}; routing on cookie",
                        remote, RoutingTokenService.RedactForLog(request.RoutingToken));
                    break;
            }
        }

        return TryRouteCookie(request.Cookie, remote, out user, out target);
    }

    private bool TryRouteCookie(
        string? cookie, EndPoint? remote,
        [NotNullWhen(true)] out string? user, [NotNullWhen(true)] out VmTarget? target)
    {
        var resolution = _resolver.ResolveCookie(cookie);
        user = resolution.User;
        target = resolution.Target;
        switch (resolution.Match)
        {
            case CookieMatch.NoUser:
                Reject(remote, "no usable mstshash cookie");
                return false;
            case CookieMatch.AmbiguousPrefix:
                Reject(remote, $"truncated cookie '{cookie}' matches several users");
                return false;
            case CookieMatch.UniquePrefix:
                _logger.LogInformation("Truncated cookie {Cookie} from {Remote} resolved to user {User}",
                    cookie, remote, user);
                break;
        }

        if (target is null || user is null)
        {
            Reject(remote, $"unknown user '{user}'");
            return false;
        }

        return true;
    }

    private async Task<byte[]?> ReadFirstPduAsync(Socket client, EndPoint? remote, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.FirstPduTimeout);
        await using var stream = new NetworkStream(client, ownsSocket: false);
        try
        {
            return await TpktReader.ReadPduAsync(stream, _options.MaxFirstPduBytes, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Reject(remote, "timed out waiting for first PDU");
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException
                                       or SocketException)
        {
            Reject(remote, $"bad first PDU ({ex.GetType().Name}: {ex.Message})");
        }

        return null;
    }

    private void Reject(EndPoint? remote, string reason) =>
        _logger.LogWarning("Rejecting {Remote}: {Reason}", remote, reason);

    private static void ConfigureSocket(Socket socket)
    {
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }
}
