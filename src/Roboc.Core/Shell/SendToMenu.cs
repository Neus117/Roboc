using System.Runtime.InteropServices;
using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.Core.Shell;

/// <summary>
/// Adds entries to the Explorer "发送到" menu.
///
/// This exists because the classic context menu could not be made to appear. The
/// registry keys were provably correct -- readable from the user's own session,
/// with values matching a third-party verb that was assumed to work -- and the
/// shell was explicitly notified with SHChangeNotify, yet Explorer never listed
/// them. Whatever filters them is not something this program can observe or
/// change.
///
/// SendTo needs no registry at all: dropping a shortcut into a folder adds the
/// entry. It also passes every selected file as arguments, which is the behaviour
/// the context menu refused to provide -- so the whole per-file batching mechanism
/// that existed only to work around that becomes unnecessary here.
/// </summary>
public static class SendToMenu
{
    /// <summary>Shortcuts are placed here; Explorer shows the folder's contents as menu entries.</summary>
    public static string SendToDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "SendTo");

    /// <summary>Marks the shortcuts this program owns, so uninstall removes only its own.</summary>
    private const string NamePrefix = "Roboc ";

    private static readonly (string Name, string Arguments)[] Entries =
    [
        ("复制", "clipboard --gui-errors"),
        ("回收", "recycle --gui-errors"),
        ("彻底删除", "purge --gui-errors"),
    ];

    /// <summary>
    /// Creates the shortcuts. Returns how many were written, or -1 and a reason.
    /// </summary>
    public static int Install(string executablePath, out string failure, Action<string>? log = null)
    {
        failure = string.Empty;

        try
        {
            Directory.CreateDirectory(SendToDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"无法访问发送到目录：{ex.Message}";
            log?.Invoke(failure);
            return -1;
        }

        int written = 0;

        foreach ((string name, string arguments) in Entries)
        {
            string target = Path.Combine(SendToDirectory, NamePrefix + name + ".lnk");

            if (TryCreateShortcut(target, executablePath, arguments, out string error))
            {
                written++;
            }
            else
            {
                // A .cmd is not as tidy -- it flashes a console -- but it is a plain
                // file with no COM involved, so it works when the shortcut API does
                // not.
                string fallback = Path.Combine(SendToDirectory, NamePrefix + name + ".cmd");

                if (TryWriteCommandFile(fallback, executablePath, arguments, out string fallbackError))
                {
                    written++;
                }
                else
                {
                    failure = $"无法创建 {name}：{error}；备用方式也失败：{fallbackError}";
                    log?.Invoke(failure);
                    return -1;
                }
            }
        }

        NotifyShell(log);
        log?.Invoke($"已在“发送到”菜单中创建 {written} 个入口");
        return written;
    }

    /// <summary>Removes only the shortcuts this program created.</summary>
    public static int Uninstall(Action<string>? log = null)
    {
        int removed = 0;

        try
        {
            foreach (string path in Directory.GetFiles(SendToDirectory, NamePrefix + "*"))
            {
                string extension = Path.GetExtension(path);

                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(path);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"移除时出错：{ex.Message}");
        }

        NotifyShell(log);
        log?.Invoke($"已从“发送到”菜单移除 {removed} 个入口");
        return removed;
    }

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return Directory.Exists(SendToDirectory) &&
                       Directory.GetFiles(SendToDirectory, NamePrefix + "*").Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static void NotifyShell(Action<string>? log)
    {
        try
        {
            Shell32.SHChangeNotify(Shell32.SHCNE_ASSOCCHANGED, Shell32.SHCNF_IDLIST, 0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log?.Invoke($"通知外壳失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ shortcut

    private static bool TryCreateShortcut(string target, string executablePath, string arguments, out string error)
    {
        error = string.Empty;
        object? linkObject = null;

        try
        {
            linkObject = new ShellLinkCoClass();

            var link = (IShellLinkW)linkObject;
            link.SetPath(executablePath);
            link.SetArguments(arguments);
            link.SetIconLocation(executablePath, 0);

            // Without a working directory, a SendTo launch starts wherever the
            // shell happens to be, which matters for 粘贴 -- it falls back to the
            // current directory when %V is unavailable.
            link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? string.Empty);

            ((IPersistFile)linkObject).Save(target, true);
            return File.Exists(target);
        }
        catch (Exception ex)
        {
            // Deliberately broad. A trimmed build reports disabled COM as
            // NotSupportedException rather than COMException, and catching only the
            // expected type meant the fallback never ran and the whole command
            // failed instead.
            error = ex.Message;
            return false;
        }
        finally
        {
            if (linkObject is not null && Marshal.IsComObject(linkObject))
            {
                Marshal.FinalReleaseComObject(linkObject);
            }
        }
    }

    private static bool TryWriteCommandFile(string path, string executablePath, string arguments, out string error)
    {
        error = string.Empty;

        try
        {
            // %* is the whole selection, already quoted by the shell. That is the
            // behaviour the context menu would not provide.
            string content =
                "@echo off\r\n" +
                $"start \"\" \"{executablePath}\" {arguments} %*\r\n";

            File.WriteAllText(path, content, System.Text.Encoding.Default);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    // --------------------------------------------------------------- COM plumbing

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkCoClass
    {
    }

    /// <summary>
    /// IShellLinkW. Every slot is declared, in order, because a COM vtable is
    /// positional: omitting one would silently shift every later method onto the
    /// wrong entry point. The ones this program does not call are placeholders.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription(nint pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(nint pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(nint pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(nint pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
