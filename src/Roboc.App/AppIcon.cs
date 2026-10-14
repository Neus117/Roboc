using System.Runtime.InteropServices;
using Roboc.Core.Shell;

namespace Roboc.App;

/// <summary>
/// The icon the windows put in their title bars.
///
/// <b>Why the windows had none.</b> Every window here registers its own class, and all
/// three declared <c>hIcon</c> in the WNDCLASSW they pass to RegisterClassW -- and all
/// three left it zero. A class registered without an icon produces a window without one,
/// so the title bar showed the generic blank slot while the executable carried a proper
/// nine-resolution icon the whole time.
///
/// <b>Why the executable's own icon.</b> The build embeds it (see ApplicationIcon in the
/// csproj), so there is nothing to ship beside the program and nothing to resolve at
/// run time. The menu icons are files because a static registry verb can only name a
/// file; a window class can hold a handle, so it uses the one already inside the binary.
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// IDI_APPLICATION. The .NET SDK embeds ApplicationIcon at this identifier, which is
    /// also the slot LoadIconW looks in when asked for the system's default -- so the
    /// same call returns the program's icon where one exists and a usable one where it
    /// does not, and a window never ends up with an empty title bar.
    /// </summary>
    private const int IdiApplication = 32512;

    /// <summary>
    /// The icon handle to hand to WNDCLASSW. Never zero.
    ///
    /// <b>Light or dark, chosen here.</b> The title bar is drawn by the program, not by the
    /// shell, so this is the one place a theme-appropriate icon costs nothing: no registry
    /// write, no lag, no second right-click. The dark artwork is near-white and the light
    /// artwork near-black, and against a dark caption the black one is what disappears --
    /// which is the whole reason this exists.
    ///
    /// The embedded icon is always the light artwork, because the build embeds Roboc.ico.
    /// The dark variant can only come from the file beside the executable, so a build whose
    /// assets folder is missing falls back to the embedded one -- legible on dark, if not
    /// ideal, and never an empty title bar.
    /// </summary>
    internal static nint Handle
    {
        get
        {
            nint dark = ContextMenuInstaller.DarkAppsTheme ? LoadDarkFromFile() : 0;

            if (dark != 0)
            {
                return dark;
            }

            // The module handle first: that asks for the icon embedded in this
            // executable. A null instance would ask the system for its own default and
            // quietly succeed, which is why the order matters rather than the result.
            nint handle = LoadIconW(GetModuleHandleW(null), IdiApplication);

            if (handle != 0)
            {
                return handle;
            }

            // Nothing embedded -- a build without ApplicationIcon, or a resource that
            // failed to attach. The system icon is worse than the program's own and far
            // better than nothing.
            return LoadIconW(0, IdiApplication);
        }
    }

    /// <summary>The dark artwork from beside the executable, or zero when there is none.</summary>
    private static nint LoadDarkFromFile()
    {
        try
        {
            string? directory = Path.GetDirectoryName(Environment.ProcessPath);

            if (string.IsNullOrEmpty(directory))
            {
                return 0;
            }

            string candidate = Path.Combine(directory, "assets", "icons", "Roboc-dark.ico");

            if (!File.Exists(candidate))
            {
                return 0;
            }

            // Deliberately not destroyed. The handle goes to RegisterClassW and has to stay
            // valid for as long as any window of that class exists, and the class is
            // registered once and never unregistered. This process shows a dialog and exits,
            // so what is held is one icon for the life of a process measured in seconds --
            // and the alternative is a class holding a handle that has been freed, which
            // draws nothing at all.
            return LoadImageW(0, candidate, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DefaultSize = 0x00000040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
    private static extern nint LoadImageW(
        nint instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
