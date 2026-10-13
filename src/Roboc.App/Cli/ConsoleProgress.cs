using Roboc.Core.Model;

namespace Roboc.App.Cli;

/// <summary>
/// Live progress on a single rewriting line. Writes are throttled and the line is
/// only rewritten when the text changed: at 10 Hz a naive implementation spends
/// real time in console IO, and when stdout is a pipe that cost lands on the
/// measurement.
/// </summary>
internal sealed class ConsoleProgress
{
    private readonly object _gate = new();
    private string _last = string.Empty;
    private int _lastWidth;
    private int _tick;

    /// <summary>
    /// Progress as a fraction, preferring bytes.
    ///
    /// The file count reads as 0.0% for the whole of a large-file copy, because no
    /// file completes until the end -- so the terminal line showed no movement while
    /// gigabytes were being written.
    /// </summary>
    private static double Fraction(ProgressSnapshot s)
        => !s.HasTotals ? 0 : s.BytesTotal > 0 && s.BytesDone > 0 ? s.BytesFraction : (s.FilesTotal > 0 ? s.FilesFraction : 0);
    public void Report(ProgressSnapshot snapshot)
    {
        string line = Format(snapshot);

        lock (_gate)
        {
            if (line == _last)
            {
                return;
            }

            _last = line;
            _tick++;

            try
            {
                if (Console.IsOutputRedirected)
                {
                    if (_tick % 20 == 0)
                    {
                        Console.WriteLine(line);
                    }

                    return;
                }

                int usable = Math.Max(20, SafeWidth() - 1);
                string display = line.Length > usable ? line[..usable] : line;

                Console.Write('\r');
                Console.Write(display.PadRight(Math.Max(_lastWidth, display.Length)));
                _lastWidth = display.Length;
            }
            catch (IOException)
            {
                // Console went away; keep going, just stop narrating.
            }
        }
    }

    private static int SafeWidth()
    {
        try
        {
            return Math.Max(40, Console.BufferWidth);
        }
        catch (IOException)
        {
            return 120;
        }
    }

    private static string Format(ProgressSnapshot s)
    {
        string percent = s.HasTotals ? $"{Fraction(s) * 100,5:F1}%" : "  --- ";
        string eta = s.Remaining is { } r ? $"{r.Minutes:D2}:{r.Seconds:D2}" : "--:--";

        return $" {percent}  {s.FilesDone:N0}/{s.FilesTotal:N0}  " +
               $"{FormatBytes(s.BytesDone)}/{FormatBytes(s.BytesTotal)}  " +
               $"{s.FilesPerSecond:N0} f/s  {FormatBytes((long)s.BytesPerSecond)}/s  " +
               $"ETA {eta}  {s.Elapsed.Minutes:D2}:{s.Elapsed.Seconds:D2}";
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };

    public static void Finish()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine();
            }
        }
        catch (IOException)
        {
        }
    }
}
