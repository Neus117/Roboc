using System.Runtime.InteropServices;
using Roboc.Core.Model;

namespace Roboc.App;

/// <summary>Which buttons a message offers.</summary>
internal enum DialogButtons
{
    /// <summary>A single acknowledge button.</summary>
    Acknowledge,

    /// <summary>Yes and No.</summary>
    YesNo,

    /// <summary>Yes, No and Cancel.</summary>
    YesNoCancel,
}

/// <summary>
/// The message dialog, drawn to the same specification as the progress window.
///
/// <b>Why MessageBox is gone.</b> Every dialog in this program used to be a
/// MessageBox, and a MessageBox has no font, colour, corner or spacing setting at
/// all -- it is drawn entirely by the system. That is why the cards looked like two
/// different programs: the progress window followed the Windows 11 interface and the
/// confirmations did not. There is no parameter that fixes that; the only fix is to
/// draw them.
///
/// <b>What is shared.</b> Colours, fonts, DPI handling, corner treatment and the
/// caption theme all come from <see cref="ProgressTheme"/> and the same DWM calls the
/// progress window uses, so the two cannot drift apart as either is adjusted. The
/// measurements in docs/开发者文档.md (§8 视觉规范) are the measurements used here.
///
/// <b>Why it runs on the calling thread.</b> A MessageBox blocks its caller, and every
/// caller here is a command that must not continue until the user has answered. A
/// nested message loop on the calling thread reproduces that exactly, without the
/// extra thread the progress window needs -- that one has to survive a long operation,
/// this one just collects a click.
/// </summary>
internal static class MessageDialog
{
    /// <summary>
    /// Shows the dialog and returns the chosen button, counting from zero.
    ///
    /// Zero is the affirmative outcome for every button set, so callers can read it as
    /// "yes" or "acknowledged" and treat anything else as a decline.
    /// </summary>
    internal static int Show(
        string text, string caption, DialogButtons buttons,
        bool warning = false, bool centre = false)
    {
        var dialog = new DialogWindow(text, caption, buttons, warning, centre);
        return dialog.Run();
    }

    private sealed class DialogWindow
    {
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_CLOSE = 0x0010;

        private const uint WS_OVERLAPPED = 0x00000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_VISIBLE = 0x10000000;

        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;

        // DrawText flags, named because the two call sites differ by one of them and
        // mixing them up draws nothing at all.
        private const uint DT_CENTER = 0x00000001;
        private const uint DT_WORDBREAK = 0x00000010;
        private const uint DT_CALCRECT = 0x00000400;
        private const uint DT_NOPREFIX = 0x00000800;

        private readonly string _text;
        private readonly string _caption;
        private readonly DialogButtons _buttons;
        private readonly bool _warning;
        private readonly bool _centre;

        private nint _window;
        private nint _bodyFont;
        private nint _buttonFont;
        private int _dpi = 96;
        private int _result;
        private int _hotButton = -1;
        private int _pressedButton = -1;

        // Filled in during layout, read during painting and hit testing.
        private int _bodyHeight;
        private int _buttonTop;
        private readonly List<(int Left, int Width)> _buttonRects = [];

        internal DialogWindow(
            string text, string caption, DialogButtons buttons, bool warning, bool centre)
        {
            _text = text;
            _caption = caption;
            _buttons = buttons;
            _warning = warning;
            _centre = centre;
        }

        private string[] Labels => _buttons switch
        {
            DialogButtons.YesNo => [Lang.Pick("是", "Yes"), Lang.Pick("否", "No")],
            DialogButtons.YesNoCancel => [Lang.Pick("是", "Yes"), Lang.Pick("否", "No"), Lang.Pick("取消", "Cancel")],
            _ => [Lang.Pick("确定", "OK")],
        };

        /// <summary>The button a plain Enter should choose: the last one, which is
        /// Cancel in a three-button set and the only one in a one-button set.</summary>
        private int DefaultButton => _buttons == DialogButtons.YesNoCancel ? 2 : 0;

        internal int Run()
        {
            _dpi = SystemDpi();

            WNDCLASSW wc = new()
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                lpszClassName = "RobocDialog",
                hInstance = GetModuleHandleW(null),
                // Set, not left at zero: a class registered without an icon gives a window
                // without one, which is why the title bars were blank.
                hIcon = AppIcon.Handle,
                hCursor = LoadCursorW(0, 32512),
            };

            RegisterClassW(ref wc);

            Layout(wc.hInstance);

            if (_window == 0)
            {
                return 0;
            }

            while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            return _result;
        }

        private static readonly WindowProc Procedure = StaticDispatch;

        /// <summary>
        /// Measures the text before creating the window, so the card is the size of its
        /// content rather than a fixed size that is wrong for every message.
        /// </summary>
        private void Layout(nint instance)
        {
            int margin = Scaled(24, _dpi);
            int maxTextWidth = Scaled(420, _dpi);

            nint measureDc = GetDC(0);
            nint previous = SelectObject(measureDc, BodyFont());
            RECT measured = new() { Left = 0, Top = 0, Right = maxTextWidth, Bottom = 0 };
            DrawTextW(
                measureDc, _text, _text.Length, ref measured,
                DT_WORDBREAK | DT_CALCRECT | (_centre ? DT_CENTER : 0));
            SelectObject(measureDc, previous);
            ReleaseDC(0, measureDc);

            _bodyHeight = measured.Bottom - measured.Top;

            // Centring is per line, by DT_CENTER in Paint. An attempt to centre the
            // whole block instead -- measure the longest line, then left-align inside
            // that width -- was reverted: the measurement did not work out and the
            // text ended up pinned to the left edge, which looked worse than the
            // per-line centring it replaced.

            int buttonHeight = Scaled(32, _dpi);
            int buttonWidth = Scaled(96, _dpi);
            int gap = Scaled(8, _dpi);

            string[] labels = Labels;
            int buttonsWidth = (labels.Length * buttonWidth) + ((labels.Length - 1) * gap);

            int bodyTop = Scaled(24, _dpi);
            int buttonsTop = bodyTop + _bodyHeight + Scaled(28, _dpi);

            int clientWidth = Math.Max(maxTextWidth, buttonsWidth) + (margin * 2);
            int clientHeight = buttonsTop + buttonHeight + Scaled(20, _dpi);

            _buttonTop = buttonsTop;
            _buttonRects.Clear();

            int left = clientWidth - margin - buttonsWidth;

            foreach (string _ in labels)
            {
                _buttonRects.Add((left, buttonWidth));
                left += buttonWidth + gap;
            }

            RECT frame = new() { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
            uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU;

            try
            {
                AdjustWindowRectExForDpi(ref frame, style, 0, 0, (uint)_dpi);
            }
            catch (EntryPointNotFoundException) { }

            int width = frame.Right - frame.Left;
            int height = frame.Bottom - frame.Top;

            int x = (GetSystemMetrics(0) - width) / 2;
            int y = (GetSystemMetrics(1) - height) / 2;

            _window = CreateWindowExW(
                0, "RobocDialog", _caption, style | WS_VISIBLE,
                x, y, width, height, 0, 0, instance, 0);


            if (_window == 0)
            {
                return;
            }

            // The instance is reachable from the window procedure through this, which
            // is what lets the procedure be static.
            GCHandle handle = GCHandle.Alloc(this);
            SetWindowLongPtrW(_window, -21, GCHandle.ToIntPtr(handle));

            ApplyWindows11Style(_window);
            UpdateWindow(_window);
        }

        private nint BodyFont()
        {
            if (_bodyFont == 0)
            {
                // 10 pt, matching the progress card's headline.
                //
                // This is the dialog's primary content, and at 9 it read as a size
                // smaller than the card it was modelled on -- the same text, in the
                // same style, one point too small to look like the same program.
                _bodyFont = Fonts.Create(10.0, 400, _dpi);
            }

            return _bodyFont;
        }

        private nint ButtonFont()
        {
            if (_buttonFont == 0)
            {
                _buttonFont = Fonts.Create(10.0, 400, _dpi);
            }

            return _buttonFont;
        }

        private void ApplyWindows11Style(nint hwnd)
        {
            try
            {
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

                _ = ProgressTheme.Current();
            ProgressTheme.ApplyCaptionColour(hwnd, ProgressTheme.Current());
                int dark = ProgressTheme.IsDark ? 1 : 0;

                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>
        /// The window procedure. Static, with the instance recovered from the window's
        /// user data: a delegate field cannot be initialised from an instance method,
        /// and the delegate has to outlive the call or Windows is left holding a
        /// pointer into freed memory.
        /// </summary>
        private static nint StaticDispatch(nint hwnd, uint message, nint wParam, nint lParam)
        {
            nint state = GetWindowLongPtrW(hwnd, -21);   // GWLP_USERDATA

            // Recovered through the handle, not by marshalling.
            //
            // Marshal.PtrToStructure<T> accepts only value types. Given the class this
            // was storing, it throws ArgumentException -- and that throw happens inside
            // the first WM_PAINT, which UpdateWindow sends from inside the call that is
            // creating the window. An exception crossing that boundary ends the process
            // with no message and exit code zero, so the dialog never appeared and
            // nothing anywhere said why.
            if (state == 0 ||
                GCHandle.FromIntPtr(state).Target is not DialogWindow dialog)
            {
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }

            return dialog.Dispatch(hwnd, message, wParam, lParam);
        }

        private nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WM_PAINT:
                    BeginPaint(hwnd, out PAINTSTRUCT paint);
                    Paint(hwnd);
                    EndPaint(hwnd, ref paint);
                    return 0;

                case WM_ERASEBKGND:
                    return 1;

                case WM_MOUSEMOVE:
                    TrackHot(PointOf(lParam));
                    return 0;

                case WM_LBUTTONDOWN:
                    TrackHot(PointOf(lParam));
                    _pressedButton = _hotButton;
                    InvalidateRect(hwnd, 0, 1);
                    return 0;

                case WM_LBUTTONUP:
                    int released = HitTest(PointOf(lParam));

                    if (released >= 0 && released == _pressedButton)
                    {
                        _result = released;
                        DestroyWindow(hwnd);
                        return 0;
                    }

                    _pressedButton = -1;
                    InvalidateRect(hwnd, 0, 1);
                    return 0;

                case WM_KEYDOWN:
                    if ((int)wParam == VK_RETURN)
                    {
                        _result = DefaultButton;
                        DestroyWindow(hwnd);
                        return 0;
                    }

                    if ((int)wParam == VK_ESCAPE)
                    {
                        // Escape declines. One-button dialogs have nothing to decline,
                        // so it acknowledges instead of trapping the user.
                        _result = Labels.Length == 1 ? 0 : Labels.Length - 1;
                        DestroyWindow(hwnd);
                        return 0;
                    }

                    return 0;

                case WM_CLOSE:
                    _result = Labels.Length == 1 ? 0 : Labels.Length - 1;
                    DestroyWindow(hwnd);
                    return 0;

                case WM_DESTROY:
                    nint state = GetWindowLongPtrW(hwnd, -21);

                    if (state != 0)
                    {
                        SetWindowLongPtrW(hwnd, -21, 0);
                        GCHandle.FromIntPtr(state).Free();
                    }

                    PostQuitMessage(0);
                    return 0;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private (int X, int Y) PointOf(nint lParam)
            => ((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));

        private int HitTest((int X, int Y) point)
        {
            for (int i = 0; i < _buttonRects.Count; i++)
            {
                (int left, int width) = _buttonRects[i];

                if (point.X >= left && point.X < left + width &&
                    point.Y >= _buttonTop && point.Y < _buttonTop + Scaled(32, _dpi))
                {
                    return i;
                }
            }

            return -1;
        }

        private void TrackHot((int X, int Y) point)
        {
            int hot = HitTest(point);

            if (hot != _hotButton)
            {
                _hotButton = hot;
                InvalidateRect(_window, 0, 1);
            }
        }

        // ------------------------------------------------------------- painting

        private void Paint(nint hwnd)
        {
            if (GetClientRect(hwnd, out RECT client) == 0)
            {
                return;
            }

            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;

            nint screen = GetDC(hwnd);
            nint buffer = CreateCompatibleDC(screen);
            nint bitmap = CreateCompatibleBitmap(screen, width, height);
            nint previous = SelectObject(buffer, bitmap);

            ProgressTheme.Palette palette = ProgressTheme.Current();

            Fill(buffer, 0, 0, width, height, palette.Window);

            int margin = Scaled(24, _dpi);

            SetBkMode(buffer, 1);
            SetTextColor(buffer, palette.Text);

            nint oldFont = SelectObject(buffer, BodyFont());
            RECT body = new()
            {
                Left = margin,
                Top = Scaled(24, _dpi),
                Right = width - margin,
                Bottom = Scaled(24, _dpi) + _bodyHeight,
            };

            // DT_WORDBREAK | DT_NOPREFIX. NOT DT_CALCRECT.
            //
            // 0x400 is DT_CALCRECT, which measures the text and draws nothing -- it was
            // copied here from the measurement call above, and it produced dialogs with
            // buttons and no message at all. The buttons rendered because their flags
            // came from somewhere else.
            DrawTextW(
                buffer, _text, _text.Length, ref body,
                DT_WORDBREAK | DT_NOPREFIX | (_centre ? DT_CENTER : 0));
            SelectObject(buffer, oldFont);

            string[] labels = Labels;

            for (int i = 0; i < _buttonRects.Count && i < labels.Length; i++)
            {
                DrawButton(buffer, i, labels[i], palette);
            }

            BitBlt(screen, 0, 0, width, height, buffer, 0, 0, 0x00CC0020);

            SelectObject(buffer, previous);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            ReleaseDC(hwnd, screen);
        }

        /// <summary>
        /// One button, in the Windows 11 style: the affirmative action carries the
        /// accent colour, everything else is a surface with a hairline border.
        ///
        /// Filled rectangles rather than a real button control, because a real one
        /// brings its own font and its own rendering, and neither can be made to match
        /// the rest of the card.
        /// </summary>
        private void DrawButton(nint dc, int index, string label, ProgressTheme.Palette palette)
        {
            (int left, int width) = _buttonRects[index];
            int height = Scaled(32, _dpi);
            int radius = Scaled(6, _dpi);

            bool primary = index == DefaultButton;
            bool hot = index == _hotButton;
            bool pressed = index == _pressedButton;

            uint background = primary
                ? (hot ? Lighten(palette.Accent, pressed) : palette.Accent)
                : (hot ? palette.Track : palette.Window);

            RoundedBox.Draw(
                dc, left, _buttonTop, width, height, radius,
                background, primary ? null : palette.Border);

            SetBkMode(dc, 1);
            SetTextColor(dc, primary ? 0x00FFFFFF : palette.Text);

            nint oldFont = SelectObject(dc, ButtonFont());
            RECT area = new()
            {
                Left = left,
                Top = _buttonTop,
                Right = left + width,
                Bottom = _buttonTop + height,
            };

            DrawTextW(dc, label, label.Length, ref area, 0x00000001 | 0x00000004 | 0x00000020);
            // DT_CENTER | DT_VCENTER | DT_SINGLELINE
            SelectObject(dc, oldFont);
        }

        /// <summary>The accent, nudged for the pressed state.</summary>
        private static uint Lighten(uint color, bool pressed)
        {
            if (!pressed)
            {
                return color;
            }

            uint r = Math.Max(0, (color & 0xFF) - 24);
            uint g = Math.Max(0, ((color >> 8) & 0xFF) - 24);
            uint b = Math.Max(0, ((color >> 16) & 0xFF) - 24);

            return (b << 16) | (g << 8) | r;
        }

        private static void Fill(nint dc, int x, int y, int width, int height, uint color)
        {
            nint brush = CreateSolidBrush(color);
            RECT rect = new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
            FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }
    }

    // ------------------------------------------------------------------ interop

    /// <summary>
    /// Draws one button in the Windows 11 style, for any caller.
    ///
    /// Exposed rather than kept private because the progress window's buttons are
    /// real child controls and this dialog's are painted rectangles -- two
    /// mechanisms that would otherwise grow two appearances, which is exactly the
    /// drift that made the confirmations look like a different program.
    /// </summary>
    internal static void DrawButtonCore(
        nint dc, int left, int top, int width, int height, string label,
        bool primary, bool hot, bool pressed, ProgressTheme.Palette palette, nint font)
    {
        int radius = height / 5;

        uint background = primary
            ? (pressed ? Darken(palette.Accent) : palette.Accent)
            : (hot ? palette.Track : palette.Window);

        // Drawn as geometry rather than as a filled region.
        //
        // A GDI region is a set of whole pixels, so CreateRoundRectRgn can only approximate
        // the corner one pixel at a time and the result is a visible staircase -- worst on
        // the primary button, where a saturated blue meets a pale background and there is
        // no texture to hide it behind. A GraphicsPath is a curve, and filling it with
        // anti-aliasing samples how much of each pixel the curve covers instead of
        // deciding whether it is inside. See RoundedBox for the whole of it.
        RoundedBox.Draw(dc, left, top, width, height, radius, background, primary ? null : palette.Border);

        SetBkMode(dc, 1);
        SetTextColor(dc, primary ? 0x00FFFFFF : palette.Text);

        nint oldFont = SelectObject(dc, font);
        RECT area = new() { Left = left, Top = top, Right = left + width, Bottom = top + height };
        DrawTextW(dc, label, label.Length, ref area, 0x00000001 | 0x00000004 | 0x00000020);
        SelectObject(dc, oldFont);
    }

    private static uint Darken(uint color)
    {
        uint r = Math.Max(0, (color & 0xFF) - 24);
        uint g = Math.Max(0, ((color >> 8) & 0xFF) - 24);
        uint b = Math.Max(0, ((color >> 16) & 0xFF) - 24);
        return (b << 16) | (g << 8) | r;
    }

    /// <summary>Shared font construction, so a dialog and the progress card cannot
    /// disagree about what the interface font is.</summary>
    internal static class Fonts
    {
        internal static nint Create(double points, int weight, int dpi)
        {
            int height = -(int)Math.Round(points * dpi / 72.0);

            nint font = CreateFontW(
                height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, ProgressTheme.Face);

            return font != 0
                ? font
                : CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, ProgressTheme.FallbackFace);
        }
    }

    private static int SystemDpi()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            return dpi == 0 ? 96 : (int)dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static int Scaled(int pixels, int dpi) => pixels * dpi / 96;

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextExtentPoint32W(nint dc, string text, int length, out SIZE size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern int TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern int DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int InvalidateRect(nint hwnd, nint rect, int erase);

    [DllImport("user32.dll")]
    private static extern int UpdateWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern int EndPaint(nint hwnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint dc, string text, int length, ref RECT rect, uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, int name);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int AdjustWindowRectExForDpi(
        ref RECT rect, uint style, int menu, uint extendedStyle, uint dpi);

    [DllImport("gdi32.dll")]
    private static extern int DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int BitBlt(
        nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);




    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref RECT rect, nint brush);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint dc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string face);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
