using System.Net;

namespace Hotlab.Proxmox;

/// <summary>Raised when the Proxmox VE API returns a non-success status code.</summary>
public sealed class ProxmoxApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public ProxmoxApiException(HttpStatusCode statusCode, string method, string path, string responseBody)
        : base($"Proxmox API {method} {path} failed with {(int)statusCode} {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
