namespace Roboc.App.Cli;

/// <summary>
/// Small tolerant argument reader.
///
/// It has to be tolerant because Windows appends the selected files to the end of
/// a context-menu command line: our own options appear first, then an arbitrary
/// number of positional paths. Nothing here may assume a fixed order.
/// </summary>
internal sealed class Args
{
    private readonly string[] _raw;

    public Args(string[] raw)
    {
        _raw = raw;
        Command = raw.Length > 0 ? raw[0] : string.Empty;
    }

    public string Command { get; }

    public string? Option(string name)
    {
        for (int i = 1; i < _raw.Length - 1; i++)
        {
            if (string.Equals(_raw[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return _raw[i + 1];
            }
        }

        return null;
    }

    public string Option(string name, string fallback) => Option(name) ?? fallback;

    public int IntOption(string name, int fallback)
        => Option(name) is { } value && int.TryParse(value, out int parsed) ? parsed : fallback;

    public double DoubleOption(string name, double fallback)
        => Option(name) is { } value && double.TryParse(value, out double parsed) ? parsed : fallback;

    public bool Flag(string name)
        => Array.Exists(_raw, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public List<string> Positionals()
    {
        var result = new List<string>(_raw.Length);

        for (int i = 1; i < _raw.Length; i++)
        {
            if (_raw[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (TakesValue(_raw[i]) && i + 1 < _raw.Length)
                {
                    i++;
                }

                continue;
            }

            result.Add(_raw[i]);
        }

        return result;
    }

    /// <summary>
    /// Options that consume the argument after them.
    ///
    /// This list has to be kept in step with every value-taking option, and nothing in
    /// the compiler enforces that. An option missing from here is not rejected -- its
    /// value is taken for a positional path instead, and the failure surfaces as
    /// "source not found: 32", which names neither the option nor the omission and
    /// reads like a path problem rather than a parser one.
    ///
    /// bench/check-wiring.ps1 checks this list against every args.Option call in the
    /// tree. It found --export-reg here, which had been read since it was written and
    /// never registered. Run it after adding an option.
    ///
    /// Options that take no value do not belong here; those are recognised by the
    /// leading "--" alone.
    /// </summary>
    private static bool TakesValue(string option) =>
        option.Equals("--to", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--workers", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--scratch", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--buffer", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--undo-window", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--checkpoint-interval", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--export-reg", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--job", StringComparison.OrdinalIgnoreCase);
}
