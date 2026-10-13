using System.Runtime.InteropServices;

namespace Roboc.Core.Model;

/// <summary>
/// Which language the user sees, and the one call that reads it.
///
/// <b>Why strings are written inline rather than in a resource file.</b> Every
/// user-visible message is written as <c>Lang.Pick("中文", "English")</c>, both halves at
/// the call site. A resource file would mean keys, a lookup that can miss, and a
/// translation sitting in a different file from the code that uses it -- and the failure
/// mode of a missing key is a blank or a key name shown to the user. With two arguments
/// the compiler will not accept one without the other, so a message cannot be half
/// translated, and neither language can drift out of sight of the other.
///
/// <b>Why the display language and not the time zone.</b> The obvious shortcut is to
/// treat UTC+8 as Chinese, and it is wrong in both directions: UTC+8 covers western
/// Australia, Singapore, Malaysia, the Philippines, Irkutsk and Mongolia, none of which
/// want Chinese; and a Chinese speaker living in Tokyo is at UTC+9. Windows already
/// knows the answer and has always known it -- GetUserDefaultUILanguage returns the
/// language the user chose for their own interface, which is the language they expect
/// this program to speak.
/// </summary>
internal static class Lang
{
    /// <summary>LANG_CHINESE, the low byte of a Chinese LANGID.</summary>
    private const int LanguageChinese = 0x04;

    private static bool _chinese;
    private static bool _resolved;

    /// <summary>True when messages should be shown in Chinese.</summary>
    internal static bool IsChinese
    {
        get
        {
            if (!_resolved)
            {
                _chinese = DetectFromSystem();
                _resolved = true;
            }

            return _chinese;
        }
    }

    /// <summary>
    /// Applies the saved preference, or falls back to the system's own answer.
    ///
    /// Called once at startup, before anything is printed. Settings are per-user and the
    /// detection is per-machine, so the saved value wins: someone who deliberately chose
    /// English on a Chinese system should not be switched back on every launch.
    /// </summary>
    internal static void Apply(string? preference)
    {
        switch (preference)
        {
            case "zh": _chinese = true; break;
            case "en": _chinese = false; break;
            default: _chinese = DetectFromSystem(); break;
        }

        _resolved = true;
    }

    /// <summary>The value to store in settings for a given choice.</summary>
    internal static string Preference(bool chinese) => chinese ? "zh" : "en";

    /// <summary>Both languages, side by side. The only way a message is written.</summary>
    internal static string Pick(string chinese, string english) => IsChinese ? chinese : english;

    /// <summary>
    /// The user's Windows display language.
    ///
    /// GetUserDefaultUILanguage rather than GetSystemDefaultUILanguage: the first is what
    /// the user selected for their own account and can differ from the language the
    /// machine was installed with, which is exactly the case a per-user setting should
    /// follow. On a machine where the call fails, English is the safer default -- it is
    /// the language a reader is least likely to be unable to use at all.
    /// </summary>
    private static bool DetectFromSystem()
    {
        try
        {
            ushort langId = GetUserDefaultUILanguage();

            // Only Chinese, deliberately.
            //
            // The alternative -- "Chinese if it is Chinese, English otherwise" -- is what
            // happens anyway, because there are two translations. Checking the low byte
            // covers Simplified (0x0804), Traditional (0x0404), Hong Kong (0x0C04) and
            // Singapore (0x1004) in one test, since they differ only in the high byte.
            return (langId & 0xFF) == LanguageChinese;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
