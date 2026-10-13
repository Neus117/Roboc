using System.Text;
using Roboc.Core.Interop;

namespace Roboc.App;

/// <summary>
/// Built as WinExe so a context-menu invocation never flashes a console. Launched
/// from a terminal or a pipe we reuse the inherited standard handles; only a fully
/// detached launch needs a console at all, and then only for diagnostics.
/// </summary>
internal static class ConsoleHost
{
    private static bool _initialised;
    private static bool _hasConsole;

    public static bool HasConsole
    {
        get
        {
            Ensure();
            return _hasConsole;
        }
    }

    private static void Ensure()
    {
        if (_initialised)
        {
            return;
        }

        _initialised = true;

        // A redirected stdout (pipe or file) is authoritative. Calling
        // AttachConsole here would move our output onto the parent's console and
        // silently break capturing tools.
        if (HasValidStdHandle(Kernel32.STD_OUTPUT_HANDLE))
        {
            _hasConsole = true;
            Rebind();
            return;
        }

        if (Kernel32.AttachConsole(Kernel32.ATTACH_PARENT_PROCESS) == 0)
        {
            // No console to talk to. Launched through ShellExecute -- which is what
            // PowerShell's Process.Start does by default, and what a shortcut does
            // -- the child inherits no standard handles at all, so every message
            // written from here would go into nothing. A failure the user cannot
            // see is worse than no message, so fall back to a file.
            RebindToLogFile();
            return;
        }

        _hasConsole = true;
        Rebind();
    }

    /// <summary>Where diagnostics land when there is no console.</summary>
    public static string? LogPath { get; private set; }

    /// <summary>True when stdout was redirected to the log file, in which case the
    /// console writer must leave it alone.</summary>
    public static bool UsingLogFile { get; private set; }

    /// <summary>Where a log would be written, without writing one.</summary>
    public static string LogLocationHint()
        => Path.Combine(CandidateLogDirectories().First(), "roboc.log");

    private const long MaxLogBytes = 1024 * 1024;

    private static void RebindToLogFile()
    {
        foreach (string directory in CandidateLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "roboc.log");

                // Keep the file bounded; a portable tool may run for years.
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    File.Delete(path);
                }

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                Console.SetOut(writer);
                Console.SetError(writer);

                LogPath = path;
                UsingLogFile = true;
                writer.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId} " +
                                 $"args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
    }

    /// <summary>Next to the executable first, so a portable copy keeps its own
    /// log; then the user profile, which exists even when the exe does not.</summary>
    private static IEnumerable<string> CandidateLogDirectories()
    {
        string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDirectory))
        {
            yield return exeDirectory;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roboc");
    }

    private static bool HasValidStdHandle(int which)
    {
        nint handle = Kernel32.GetStdHandle(which);
        return handle != 0 && handle != NativeConst.INVALID_HANDLE_VALUE;
    }

    private static void Rebind()
    {
        try
        {
            // Console.OutputEncoding, not a hardcoded UTF8Encoding. This line was the
            // actual cause of the mojibake: it rebuilt stdout as UTF-8 after the
            // encoding had already been chosen, so every Chinese label reached a
            // GBK reader as unreadable bytes no matter what was configured earlier.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
            Console.SetOut(stdout);

            if (HasValidStdHandle(Kernel32.STD_ERROR_HANDLE))
            {
                var stderr = new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true };
                Console.SetError(stderr);
            }
        }
        catch (IOException)
        {
            _hasConsole = false;
        }
    }

    /// <summary>
    /// Uses an inherited console if there is one, and does nothing otherwise.
    ///
    /// This is the safe call for every command. Launched from Explorer there is no
    /// parent console, so nothing happens and no window flashes; launched from a
    /// terminal the output lands where the user is looking.
    /// </summary>
    public static void AttachIfPossible() => Ensure();

    /// <summary>
    /// Creates a console when none exists. Only for diagnostics: from a context
    /// menu this would pop a window the user did not ask for.
    /// </summary>
    public static void AllocateIfMissing()
    {
        Ensure();
        if (_hasConsole || Kernel32.AllocConsole() == 0)
        {
            return;
        }

        _hasConsole = true;
        Rebind();
    }
}
