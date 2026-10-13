using System.Text;
using Roboc.Core.Diagnostics;
using Roboc.Core.Interop;

namespace Roboc.App;

/// <summary>
/// A console writer that is correct in both directions.
///
/// The default behaviour was wrong in a way that mattered: .NET writes UTF-8 to
/// a redirected standard output, while the program reading it -- PowerShell 5.1,
/// or any Windows tool -- decodes using the system's OEM code page. On a Chinese
/// installation that is 936, so every Chinese label arrived as mojibake and the
/// tool's own diagnostics were unreadable. Reporting a failure in a form the user
/// cannot read is not much better than not reporting it.
///
/// Two cases, handled separately because they are genuinely different:
///
///  * <b>A console is attached.</b> Characters go straight to the console through
///    WriteConsoleW. No code page is involved, so nothing can be mis-converted and
///    the result does not depend on the active code page at all.
///
///  * <b>Output is redirected.</b> There is no console to write wide characters
///    to, so the text is converted with WideCharToMultiByte using the same code
///    page the reader will use. Matching the reader is the whole point; emitting
///    a fixed UTF-8 stream would be correct in the abstract and unreadable here.
/// </summary>
internal sealed class ConsoleOutput : TextWriter
{
    private readonly bool _toConsole;
    private readonly nint _handle;
    private readonly uint _codePage;

    private ConsoleOutput(bool toConsole, nint handle, uint codePage)
    {
        _toConsole = toConsole;
        _handle = handle;
        _codePage = codePage;
    }

    public override Encoding Encoding => Encoding.Unicode;

    /// <summary>
    /// Points console output at the encoding the reader will use.
    ///
    /// This is the whole fix. .NET writes UTF-8 to a redirected standard output
    /// while a Windows reader -- PowerShell 5.1, or anything using the console
    /// defaults -- decodes with the OEM code page. On this machine GetOEMCP() is
    /// 936, so every Chinese label arrived as mojibake.
    ///
    /// Setting Console.OutputEncoding is preferred over a hand-written TextWriter
    /// because the runtime owns every path that reaches the console, so there is
    /// no way for one of them to bypass the conversion. A custom writer was tried
    /// first and never ran at all.
    /// </summary>
    public static void Install()
    {
        try
        {
            // Code page 936 is not in the default set; the provider adds the legacy
            // Windows code pages back.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Console output codepage when a console is attached, since that is
            // exactly what the reader will use. Otherwise the ANSI code page, which
            // is what a Windows program reading a pipe assumes by default.
            //
            // GetOEMCP is deliberately not used. It described this process as 65001
            // while the parent reported 936, and the earlier version of this code
            // then skipped the assignment entirely by excluding 65001 as "already
            // UTF-8" -- which is precisely why nothing changed.
            // An explicit override, so a wrong guess is recoverable without a new
            // build. If output ever looks like mojibake, setting this to 936 (or
            // 65001) settles it immediately.
            string? forced = Environment.GetEnvironmentVariable("ROBOC_CONSOLE_CP");

            if (!string.IsNullOrWhiteSpace(forced) &&
                int.TryParse(forced, out int forcedCodePage) && forcedCodePage > 0)
            {
                Console.OutputEncoding = Encoding.GetEncoding(forcedCodePage);
                return;
            }

            uint codePage = Kernel32.GetConsoleOutputCP();

            if (codePage == 0)
            {
                codePage = Kernel32.GetACP();
            }

            if (codePage == 0)
            {
                codePage = 65001;
            }

            Console.OutputEncoding = Encoding.GetEncoding((int)codePage);
            return;
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (IOException) { }

        InstallFallback();
    }

    private static void InstallFallback()
    {
        nint stdout = Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE);

        if (stdout != 0 && stdout != NativeConst.INVALID_HANDLE_VALUE &&
            Kernel32.GetConsoleMode(stdout, out _) != 0)
        {
            // A real console: wide characters, no code page in the path.
            nint console = Kernel32.CreateFile(
                "CONOUT$", NativeConst.GENERIC_WRITE,
                NativeConst.FILE_SHARE_READ | NativeConst.FILE_SHARE_WRITE,
                0, NativeConst.OPEN_EXISTING, 0, 0);

            var writer = new ConsoleOutput(true, console != NativeConst.INVALID_HANDLE_VALUE ? console : stdout, 0);
            Console.SetOut(writer);
            Console.SetError(writer);
            return;
        }

        // Redirected, or nothing attached. Match whatever the reader will assume.
        uint codePage = Kernel32.GetOEMCP();

        var redirected = new ConsoleOutput(false, stdout, codePage);
        Console.SetOut(redirected);
        Console.SetError(redirected);
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (_toConsole)
        {
            WriteWide(value);
            return;
        }

        WriteConverted(value);
    }

    public override void WriteLine(string? value) => Write((value ?? string.Empty) + "\n");

    private unsafe void WriteWide(string text)
    {
        // WriteConsoleW has a length limit per call, and a long line would silently
        // truncate at it, so the write is chunked rather than handed over whole.
        const int Chunk = 8192;
        int offset = 0;

        while (offset < text.Length)
        {
            int count = Math.Min(Chunk, text.Length - offset);

            fixed (char* p = text)
            {
                if (Kernel32.WriteConsoleW(_handle, p + offset, (uint)count, out uint written, null) == 0)
                {
                    return;
                }

                if (written == 0)
                {
                    return;
                }

                offset += (int)written;
            }
        }
    }

    private unsafe void WriteConverted(string text)
    {
        // Worst case for a multi-byte code page is two bytes per UTF-16 unit, plus
        // room for a terminating NUL.
        int capacity = (text.Length * 2) + 2;
        var buffer = new byte[capacity];

        fixed (char* source = text)
        fixed (byte* destination = buffer)
        {
            int written = Kernel32.WideCharToMultiByte(
                _codePage, 0, source, text.Length, destination, capacity, null, null);

            if (written <= 0)
            {
                return;
            }

            try
            {
                Stream stream = Console.OpenStandardOutput();
                stream.Write(buffer, 0, written);
                stream.Flush();
            }
            catch (IOException)
            {
                // The reader went away. Nothing useful is left to do.
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_toConsole && _handle != 0 && _handle != NativeConst.INVALID_HANDLE_VALUE)
        {
            Kernel32.CloseHandle(_handle);
        }

        base.Dispose(disposing);
    }
}
