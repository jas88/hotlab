using System.Text;

namespace Hotlab.Gateway.Deploy;

/// <summary>Atomic (write-temp-then-rename) file writes with explicit Unix permissions.</summary>
public static class AtomicFile
{
    /// <summary>rw------- (0600).</summary>
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>rwx------ (0700).</summary>
    public const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes <paramref name="content"/> to a temporary file beside <paramref name="path"/>, flushes
    /// it to disk and renames it over the target, so readers see the old or the new file, never
    /// a partial one. On Unix the file gets <paramref name="mode"/>; when null, an existing
    /// target's mode is preserved (new files get 0600).
    /// </summary>
    public static void WriteAllText(string path, string content, UnixFileMode? mode = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = mode ?? (File.Exists(full) ? File.GetUnixFileMode(full) : PrivateFile);

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // UnixCreateMode is filtered by the umask; set it explicitly as well.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, options.UnixCreateMode!.Value);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> (0700 on Unix) if missing. Returns true when it was
    /// created; an existing directory's permissions are left alone. A symbolic link is refused,
    /// whether it was there already or appeared while the directory was being created (deploy
    /// may run as root, and a path-based chmod would follow the link).
    /// </summary>
    /// <remarks>
    /// Residual risk: someone able to write to the parent directory can still swap the
    /// directory for a link after these checks; keep output directories under a parent that
    /// only trusted users can write to.
    /// </remarks>
    /// <exception cref="IOException"><paramref name="path"/> is a symbolic link or not a directory.</exception>
    public static bool EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.Exists(path) || IsLink(path))
        {
            EnsureRealDirectory(path);
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            EnsureRealDirectory(path);
            return true;
        }

        // The create mode is filtered by the umask, which can only remove bits: the result is
        // never group/other accessible, so a chmod (path-based, following links) is only needed
        // when the umask also stripped owner bits.
        Directory.CreateDirectory(path, PrivateDirectory);
        EnsureRealDirectory(path);
        if (File.GetUnixFileMode(path) != PrivateDirectory)
        {
            EnsureRealDirectory(path);
            File.SetUnixFileMode(path, PrivateDirectory);
        }
        return true;
    }

    /// <summary>True when <paramref name="path"/> itself is a symbolic link (lstat semantics; dangling links included).</summary>
    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static void EnsureRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new IOException($"{path} is a symbolic link; refusing to use it as the output directory.");
        if (!info.Exists)
            throw new IOException($"{path} is not a directory.");
    }

    /// <summary>True when an existing directory is accessible to group or others (Unix only).</summary>
    public static bool IsGroupOrOtherAccessible(string path) =>
        !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & ~PrivateDirectory & (UnixFileMode)0b111_111_111) != 0;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
