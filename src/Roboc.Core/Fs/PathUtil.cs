using System.Text;

namespace Roboc.Core.Fs;

/// <summary>
/// Path handling for the native layer.
///
/// Every path that reaches a P/Invoke call goes through ToExtended. Without the
/// \\?\ prefix the Win32 layer still applies MAX_PATH truncation and its own
/// normalisation (stripping trailing dots and spaces, resolving . and ..), which
/// silently produces the wrong file for deep or unusual paths.
/// </summary>
internal static class PathUtil
{
    public const string ExtendedPrefix = @"\\?\";
    public const string ExtendedUncPrefix = @"\\?\UNC\";
    public const string NtPrefix = @"\??\";
    public const string NtUncPrefix = @"\??\UNC\";
    public const string DevicePrefix = @"\\.\";

    public static bool IsExtended(string path) => path.StartsWith(ExtendedPrefix, StringComparison.Ordinal);
    public static bool IsDevicePath(string path) => path.StartsWith(DevicePrefix, StringComparison.Ordinal);
    public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !IsExtended(path);

    /// <summary>
    /// Whether this is a path the Win32 file APIs can be asked about at all.
    ///
    /// <b>Why this exists.</b> Shell namespace items -- a portable device over MTP, a
    /// library, a search result, a zip's contents -- have a <c>Path</c> property, and it
    /// is a string, so it arrives here looking like a path. It is not one. A device item
    /// reads:
    ///
    /// <code>
    /// ::{20D04FE0-...}\\\?\usb#vid_22d9&amp;pid_2765&amp;mi_00#7&amp;...#{6ac27878-...}\SID-{10001,,493075931136}\{6AD0215E-...}
    /// </code>
    ///
    /// Nothing that reaches a file API can resolve that. <c>Path.GetFullPath</c> does not
    /// reject it -- it treats it as a relative path and produces something that is
    /// silently wrong -- and then <c>GetFileAttributes</c> fails, so the caller reports
    /// "source not found" and shows the user a device identifier instead of an
    /// explanation.
    ///
    /// <b>What is deliberately not claimed.</b> This does not test whether the path
    /// exists, and it is not a general parse of the shell grammar. It only answers the
    /// question the callers actually have: could a Win32 file API mean anything by this
    /// string? A rooted path with a drive letter or a UNC prefix can; everything else
    /// cannot, and saying so plainly is the whole point.
    /// </summary>
    public static bool LooksLikeFileSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.Trim();

        // A drive-qualified path: "C:\..." or "C:/..." or a bare "C:".
        if (trimmed.Length >= 2 &&
            char.IsAsciiLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed.Length == 2 || trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return true;
        }

        // A UNC path, including the \\?\ and \\.\ forms.
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal);
    }

    public static bool IsUncExtended(string path) =>
        path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Absolute + extended. Relative input is resolved first, because
    /// \\?\ paths must be fully qualified.</summary>
    public static string ToExtended(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (IsExtended(path) || IsDevicePath(path))
        {
            return path;
        }

        string full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ExtendedUncPrefix + full[2..];
        }

        return ExtendedPrefix + full;
    }

    public static string ToDisplay(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path[ExtendedPrefix.Length..];
        }

        return path;
    }

    /// <summary>NT object-manager form, required by NtOpenFile / NtCreateFile.</summary>
    public static string ToNtPath(string path)
    {
        string display = ToDisplay(path);

        return display.StartsWith(@"\\", StringComparison.Ordinal)
            ? NtUncPrefix + display[2..]
            : NtPrefix + display;
    }

    public static bool NeedsExtendedPrefix(string path)
    {
        if (path.Length >= 248)
        {
            return true;
        }

        foreach (string segment in path.Split('\\', '/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            char last = segment[^1];
            if (last is '.' or ' ')
            {
                return true;
            }

            int dot = segment.IndexOf('.');
            string stem = dot < 0 ? segment : segment[..dot];
            if (IsReservedDeviceName(stem))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReservedDeviceName(string stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9');

    /// <summary>
    /// True when the path is a volume root (\\?\C:\, \\?\UNC\srv\share\, or the
    /// plain display forms).
    /// </summary>
    public static bool IsVolumeRoot(string path)
    {
        if (IsUncExtended(path))
        {
            string rest = path[ExtendedUncPrefix.Length..];
            int first = rest.IndexOf('\\');
            if (first < 0)
            {
                return false;
            }

            int second = rest.IndexOf('\\', first + 1);
            return second >= 0 && second == rest.Length - 1;
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            string rest = path[ExtendedPrefix.Length..];
            return rest.Length == 3 && rest[1] == ':' && rest[2] == '\\';
        }

        return (path.Length == 3 && path[1] == ':' && path[2] == '\\')
            || (path.Length == 2 && path[1] == ':');
    }

    public static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 3)
        {
            return path;
        }

        // Never strip the separator off a volume root. "\\?\D:" is not a valid
        // path -- GetFileAttributes fails on it, so a volume root silently stops
        // looking like it exists and any guard keyed on "does this path exist"
        // ends up passing for the wrong reason.
        if (IsVolumeRoot(path))
        {
            return path;
        }

        return path.TrimEnd('\\');
    }

    public static string EnsureTrailingSeparator(string path) =>
        path.EndsWith('\\') ? path : path + '\\';

    public static string GetFileName(string path)
    {
        int index = path.LastIndexOf('\\');
        return index < 0 ? path : path[(index + 1)..];
    }

    public static string GetDirectoryName(string path)
    {
        int index = path.LastIndexOf('\\');
        if (index < 0)
        {
            return string.Empty;
        }

        if (index == 2 && path.Length > 2 && path[1] == ':')
        {
            return path[..3];
        }

        return path[..index];
    }

    public static string CombineExtended(string directory, string leaf) =>
        directory.EndsWith('\\') ? directory + leaf : directory + '\\' + leaf;

    /// <summary>Volume root in extended form, e.g. \\?\C:\ or \\?\UNC\srv\share\.</summary>
    public static string GetVolumeRoot(string extendedPath)
    {
        if (IsUncExtended(extendedPath))
        {
            string rest = extendedPath[ExtendedUncPrefix.Length..];
            int first = rest.IndexOf('\\');
            if (first < 0)
            {
                return EnsureTrailingSeparator(extendedPath);
            }

            int second = rest.IndexOf('\\', first + 1);
            return second < 0
                ? EnsureTrailingSeparator(extendedPath)
                : ExtendedUncPrefix + rest[..second] + '\\';
        }

        string display = ToDisplay(extendedPath);
        if (display.Length >= 2 && display[1] == ':')
        {
            return ExtendedPrefix + char.ToUpperInvariant(display[0]) + @":\";
        }

        return EnsureTrailingSeparator(extendedPath);
    }

    public static string GetVolumeRootDisplay(string path)
    {
        string display = ToDisplay(path);

        if (display.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string rest = display[2..];
            int first = rest.IndexOf('\\');
            if (first < 0)
            {
                return display;
            }

            int second = rest.IndexOf('\\', first + 1);
            return second < 0 ? display : display[..(second + 2)];
        }

        return display.Length >= 2 && display[1] == ':' ? display[..2] : display;
    }

    public static string SanitizeFileNameComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < 32
                ? '_'
                : c);
        }

        return builder.ToString();
    }
}
