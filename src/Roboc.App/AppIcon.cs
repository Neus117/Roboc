using System.Runtime.InteropServices;

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

    /// <summary>The icon handle to hand to WNDCLASSW. Never zero.</summary>
    internal static nint Handle
    {
        get
        {
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
