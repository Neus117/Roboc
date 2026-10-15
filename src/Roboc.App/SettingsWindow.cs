using System.Runtime.InteropServices;
using Roboc.Core.Model;

namespace Roboc.App;

/// <summary>
/// The settings window: every switch, what it buys, what it costs, and what it defaults
/// to -- plus the way out of the program.
///
/// <b>Why one window rather than a menu entry per switch.</b> A static shell verb
/// cannot show whether it is on or off, so four toggles in the context menu would be
/// four entries that always look the same and always need to be clicked to find out
/// what they currently do. One entry that opens everything the program can be told to
/// do is fewer things to learn, not more.
///
/// <b>Why the uninstall button lives here.</b> Removing the shell integration is the
/// one action that makes the menu disappear, and it is the only way to reach it
/// afterwards. Putting it one step behind a window means a stray right-click cannot
/// start it, and the confirmation that follows states plainly what it will and will not
/// touch -- in particular that the recycle bins are left alone.
///
/// Drawn rather than assembled from controls, like every other window here, so it
/// follows the same DPI handling, the same palette and the same measurements as the
/// progress card and the message dialogs. See docs/开发者文档.md §8.
/// </summary>
/// <summary>What the settings window decided.</summary>
internal enum SettingsOutcome
{
    /// <summary>Closed without accepting; nothing was written.</summary>
    Cancelled,

    /// <summary>Settings were changed and saved.</summary>
    Saved,

    /// <summary>The user asked for the program to be removed.</summary>
    Uninstall,
}
internal static class SettingsWindow
{
    /// <summary>
    /// Shows the window and applies whatever the user accepted.
    ///
    /// Returns one value rather than exposing two flags, because the three outcomes are
    /// mutually exclusive and a caller that checked them separately could get the
    /// combination that means nothing -- accepted and uninstalling at once.
    /// </summary>
    public static SettingsOutcome Show()
    {
        var window = new Window();
        return window.Run();
    }

    private sealed class Window
    {
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;

        private const uint WS_OVERLAPPED = 0x00000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;

        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;

        // Logical pixels at 96 DPI, following the rhythm in the developer document.
        private const int Margin = 24;
        private const int RowHeight = 30;

        // The language row sits above the switches rather than among them.
        //
        // It is not a switch -- it has two states and neither is "off" -- and putting it
        // in the same column as five checkboxes would invite the reading that Chinese is
        // the checked state. It is also the one control someone looks for when the window
        // has opened in a language they do not read, so it goes first.
        private const int LanguageRowHeight = 34;
        private const int RowsTop = 20;
        private const int BoxSize = 16;
        private const int ButtonWidth = 96;
        private const int ButtonHeight = 32;
        private int _paneTop;
        private int _paneHeight;

        private readonly RobocSettings _working;
        private readonly IReadOnlyList<SettingInfo> _rows = RobocSettings.Catalogue();

        private nint _window;
        private nint _bodyFont;
        private nint _boldFont;
        private nint _buttonFont;
        private int _dpi = 96;
        private int _width;
        private int _height;

        private int _selected;

        /// <summary>0 = 中文, 1 = English. Mirrors _working.Language.</summary>
        private int _languageChoice;
        private bool _accepted;
        private int _hotButton = -1;
        private int _pressedButton = -1;
        private bool _uninstallRequested;

        internal Window() => _working = RobocSettings.Load();

        private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

        private static readonly WindowProc Procedure = Dispatch;

        internal SettingsOutcome Run()
        {
            _dpi = SystemDpi();
            _languageChoice = _working.Language == "en" ? 1 : 0;
            _width = Scaled(560);

            // Sized for the longest description, not the selected one. A window that
            // grows and shrinks as rows are clicked is worse than one with slack in it,
            // and the slack is a few millimetres.
            _paneTop = Scaled(RowsTop + LanguageRowHeight + (RowHeight * _rows.Count) + 16);
            _paneHeight = MeasurePane(_width);
            _height = _paneTop + _paneHeight + Scaled(76);

            WNDCLASSW wc = new()
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                lpszClassName = "RobocSettings",
                hInstance = GetModuleHandleW(null),
                // Set, not left at zero: a class registered without an icon gives a window
                // without one, which is why the title bars were blank.
                hIcon = AppIcon.Handle,
                hCursor = LoadCursorW(0, 32512),
            };

            RegisterClassW(ref wc);

            RECT frame = new() { Left = 0, Top = 0, Right = _width, Bottom = _height };
            uint style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;

            try { AdjustWindowRectExForDpi(ref frame, style, 0, 0, (uint)_dpi); }
            catch (EntryPointNotFoundException) { }

            int w = frame.Right - frame.Left;
            int h = frame.Bottom - frame.Top;

            _window = CreateWindowExW(
                0, "RobocSettings", Lang.Pick("Roboc 设置", "Roboc Settings"),
                style | WS_VISIBLE,
                (GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2,
                w, h, 0, 0, wc.hInstance, 0);

            if (_window == 0)
            {
                return SettingsOutcome.Cancelled;
            }

            GCHandle handle = GCHandle.Alloc(this);
            SetWindowLongPtrW(_window, -21, GCHandle.ToIntPtr(handle));

            ApplyWindows11Style(_window);
            UpdateWindow(_window);

            while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            if (_uninstallRequested)
            {
                return SettingsOutcome.Uninstall;
            }

            if (!_accepted)
            {
                return SettingsOutcome.Cancelled;
            }

            if (!_working.Save(out string error))
            {
                MessageDialog.Show(
                    Lang.Pick($"⚠️设置无法保存：\r\n\r\n{error}\r\n\r\n", $"⚠️Settings could not be saved:\r\n\r\n{error}\r\n\r\n") +
                    Lang.Pick("程序仍按原有设置运行。", "The program will carry on with the settings it already had."),
                    Lang.Pick("Roboc — 保存失败", "Roboc — Save failed"),
                    DialogButtons.Acknowledge,
                    warning: true);

                return SettingsOutcome.Cancelled;
            }

            return SettingsOutcome.Saved;
        }

        private static nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
        {
            nint state = GetWindowLongPtrW(hwnd, -21);

            if (state == 0 || GCHandle.FromIntPtr(state).Target is not Window window)
            {
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }

            return window.Handle(hwnd, message, wParam, lParam);
        }

        private nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
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
                    Hot(PointOf(lParam));
                    return 0;

                case WM_LBUTTONDOWN:
                    _pressedButton = HitButton(PointOf(lParam));
                    InvalidateRect(hwnd, 0, 1);
                    return 0;

                case WM_LBUTTONUP:
                    Released(PointOf(lParam));
                    return 0;

                case WM_KEYDOWN:
                    if ((int)wParam == VK_RETURN) { _accepted = true; DestroyWindow(hwnd); return 0; }
                    if ((int)wParam == VK_ESCAPE) { DestroyWindow(hwnd); return 0; }
                    return 0;

                case WM_CLOSE:
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

        // ------------------------------------------------------------------ input

        private static (int X, int Y) PointOf(nint lParam)
            => ((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));

        /// <summary>Which switch row a point falls in, or -1.</summary>
        /// <summary>Which half of the language row a point falls in, or -1.</summary>
        private int HitLanguage((int X, int Y) point)
        {
            int top = Scaled(RowsTop);
            int height = Scaled(LanguageRowHeight);

            if (point.Y < top || point.Y >= top + height)
            {
                return -1;
            }

            (int firstLeft, int firstWidth) = LanguageSegment(0);
            return point.X < firstLeft + firstWidth ? 0 : 1;
        }

        /// <summary>The rectangle of one language segment, in physical pixels.</summary>
        private (int Left, int Width) LanguageSegment(int index)
        {
            int margin = Scaled(Margin);
            int left = margin + Scaled(150) + (index * Scaled(84));
            return (left, Scaled(84));
        }

        private int HitRow((int X, int Y) point)
        {
            int top = Scaled(RowsTop + LanguageRowHeight);
            int height = Scaled(RowHeight);

            if (point.Y < top || point.Y >= top + (height * _rows.Count))
            {
                return -1;
            }

            int index = (point.Y - top) / height;

            // The whole row is the target, not just the box. A sixteen-pixel square is
            // a small thing to ask someone to hit, and the label beside it already says
            // what the row is for.
            return index >= 0 && index < _rows.Count ? index : -1;
        }

        private void Hot((int X, int Y) point)
        {
            int row = HitLanguage(point) >= 0 ? -1 : HitRow(point);
            int button = HitButton(point);

            if (row >= 0 && _selected != row)
            {
                _selected = row;
                InvalidateRect(_window, 0, 1);
            }

            if (button != _hotButton)
            {
                _hotButton = button;
                InvalidateRect(_window, 0, 1);
            }
        }

        private void Released((int X, int Y) point)
        {
            // The language row is tested first: it sits above the switches and its
            // segments are narrower than a row, so a row test would never match it, but
            // ordering it first says so rather than leaving it to arithmetic.
            int language = HitLanguage(point);

            if (language >= 0 && language != _languageChoice)
            {
                _languageChoice = language;

                // Applied immediately, so the new wording is visible in this very window
                // rather than only after a restart. Lang is process-wide, so this is all
                // it takes -- the window redraws from its own paint handler.
                Lang.Apply(language == 0 ? "zh" : "en");
                _working.Language = language == 0 ? "zh" : "en";

                InvalidateRect(_window, 0, 1);
                return;
            }

            int row = HitRow(point);

            if (row >= 0)
            {
                string key = _rows[row].Key;
                _working.Set(key, !_working.Get(key));
                _selected = row;
                _pressedButton = -1;
                InvalidateRect(_window, 0, 1);
                return;
            }

            int button = HitButton(point);

            if (button < 0 || button != _pressedButton)
            {
                _pressedButton = -1;
                InvalidateRect(_window, 0, 1);
                return;
            }

            switch (button)
            {
                case 0:
                    _uninstallRequested = true;
                    DestroyWindow(_window);
                    break;

                case 2:
                    _accepted = true;
                    DestroyWindow(_window);
                    break;

                default:
                    DestroyWindow(_window);
                    break;
            }
        }

        // ------------------------------------------------------------------ layout

        private (int Left, int Width) ButtonRect(int index)
        {
            int count = 3;
            int gap = Scaled(8);
            int width = Scaled(ButtonWidth);
            int right = Scaled(560 - Margin);

            // Laid out from the right so the primary action sits at the corner the eye
            // already goes to, and the destructive one is furthest from it.
            int left = right - (width * (count - index)) - (gap * (count - 1 - index));
            return (left, width);
        }

        private int ButtonTop => _paneTop + _paneHeight + Scaled(24);

        private int HitButton((int X, int Y) point)
        {
            int top = ButtonTop;

            if (point.Y < top || point.Y >= top + Scaled(ButtonHeight))
            {
                return -1;
            }

            for (int i = 0; i < 3; i++)
            {
                (int left, int width) = ButtonRect(i);

                if (point.X >= left && point.X < left + width)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int Scaled(int pixels, int dpi) => pixels * dpi / 96;
        private int Scaled(int pixels) => Scaled(pixels, _dpi);

        // ------------------------------------------------------------------ painting

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

            DrawLanguageRow(buffer, palette);
            DrawRows(buffer, palette);
            DrawPane(buffer, width, palette);
            DrawButtons(buffer, palette);

            BitBlt(screen, 0, 0, width, height, buffer, 0, 0, 0x00CC0020);

            SelectObject(buffer, previous);
            DeleteObject(bitmap);
            DeleteDC(buffer);
            ReleaseDC(hwnd, screen);
        }

        /// <summary>
        /// The language row: a label and two segments, the active one filled.
        ///
        /// Drawn as two segments rather than as a checkbox on purpose. A checkbox has two
        /// states and one of them is "off", which is the wrong idea here -- neither
        /// language is a feature being disabled. Two side-by-side segments show the choice
        /// and the current answer at the same time, which is also what makes this usable
        /// by someone who has just been shown a window in a language they cannot read.
        /// </summary>
        private void DrawLanguageRow(nint dc, ProgressTheme.Palette palette)
        {
            int margin = Scaled(Margin);
            int top = Scaled(RowsTop);
            int height = Scaled(LanguageRowHeight);
            int segmentHeight = Scaled(26);
            int segmentTop = top + ((height - segmentHeight) / 2);

            SetBkMode(dc, 1);

            nint old = SelectObject(dc, BodyFont());
            SetTextColor(dc, palette.Text);

            string label = Lang.Pick("界面语言", "Language");

            RECT text = new()
            {
                Left = margin,
                Top = top,
                Right = margin + Scaled(150),
                Bottom = top + height,
            };

            DrawTextW(dc, label, label.Length, ref text, 0x00000004 | 0x00000020 | 0x00000800);

            for (int i = 0; i < 2; i++)
            {
                (int left, int width) = LanguageSegment(i);
                bool active = i == _languageChoice;

                int radius = Scaled(6);
                RoundedBox.Draw(
                    dc, left, segmentTop, width, segmentHeight, radius,
                    active ? palette.Accent : palette.Track,
                    active ? palette.Accent : palette.Border);

                // The inactive label is drawn in the ordinary text colour and the active
                // one in white, because the accent fill is dark enough in both themes to
                // carry white text and light enough not to swallow dark text elsewhere.
                string caption = i == 0 ? "中文" : "English";
                SetTextColor(dc, active ? 0x00FFFFFFu : palette.Text);

                RECT box = new()
                {
                    Left = left,
                    Top = segmentTop,
                    Right = left + width,
                    Bottom = segmentTop + segmentHeight,
                };

                DrawTextW(dc, caption, caption.Length, ref box,
                    0x00000001 | 0x00000004 | 0x00000020 | 0x00000800);
            }

            SelectObject(dc, old);
        }
        private void DrawRows(nint dc, ProgressTheme.Palette palette)
        {
            SetBkMode(dc, 1);

            int margin = Scaled(Margin);
            int box = Scaled(BoxSize);
            int top = Scaled(RowsTop + LanguageRowHeight);
            int rowHeight = Scaled(RowHeight);

            for (int i = 0; i < _rows.Count; i++)
            {
                SettingInfo info = _rows[i];
                bool on = _working.Get(info.Key);
                int y = top + (rowHeight * i);
                int boxTop = y + ((rowHeight - box) / 2);

                if (i == _selected)
                {
                    // A quiet band rather than a highlight: it marks where the
                    // description below is coming from without competing with the text.
                    Fill(dc, margin - Scaled(8), y, Scaled(560 - Margin) - margin + Scaled(16),
                        rowHeight, palette.Track);
                }

                DrawBox(dc, margin, boxTop, box, on, palette);

                nint font = i == _selected ? BoldFont() : BodyFont();
                nint old = SelectObject(dc, font);
                SetTextColor(dc, palette.Text);

                RECT label = new()
                {
                    Left = margin + box + Scaled(12),
                    Top = y,
                    Right = margin + box + Scaled(400),
                    Bottom = y + rowHeight,
                };

                DrawTextW(dc, info.Label, info.Label.Length, ref label, 0x00000004 | 0x00000020 | 0x00000800);

                // The default is stated on every row, because "what does this do" and
                // "what did it do before I touched it" are different questions and only
                // the second one lets someone undo an experiment.
                string marker = info.Default ? Lang.Pick("默认：开", "default: on") : Lang.Pick("默认：关", "default: off");
                SetTextColor(dc, palette.SecondaryText);

                RECT tag = new()
                {
                    Left = Scaled(560 - Margin) - Scaled(90),
                    Top = y,
                    Right = Scaled(560 - Margin),
                    Bottom = y + rowHeight,
                };

                DrawTextW(dc, marker, marker.Length, ref tag, 0x00000002 | 0x00000004 | 0x00000020 | 0x00000800);
                SelectObject(dc, old);
            }
        }

        /// <summary>
        /// One checkbox: a rounded square, with two strokes forming a tick when set.
        ///
        /// Drawn rather than using a button control with BS_CHECKBOX, for the same reason
        /// the dialog buttons are drawn -- a real control brings its own font, colours
        /// and corners, none of which can be made to match the rest of the window.
        /// </summary>
        private void DrawBox(nint dc, int left, int top, int size, bool on, ProgressTheme.Palette palette)
        {
            int radius = Scaled(4);

            RoundedBox.Draw(
                dc, left, top, size, size, radius,
                on ? palette.Accent : palette.Window,
                on ? palette.Accent : palette.Border);

            if (!on)
            {
                return;
            }

            nint pen = CreatePen(0, Math.Max(2, Scaled(2)), 0x00FFFFFF);
            nint old = SelectObject(dc, pen);

            MoveToEx(dc, left + (size / 5), top + (size / 2), 0);
            LineTo(dc, left + (size * 2 / 5), top + (size * 7 / 10));
            LineTo(dc, left + (size * 4 / 5), top + (size / 4));

            SelectObject(dc, old);
            DeleteObject(pen);
        }

        /// <summary>
        /// The benefit, cost and advice for whichever row is selected, as one block
        /// inside one panel.
        ///
        /// The panel is sized to its content, and the content is measured rather than
        /// assumed. The first version guessed: it drew each paragraph into a rectangle
        /// 160 pixels tall and advanced by whatever <c>DrawTextW</c> left in
        /// <c>rect.Bottom</c> -- which, with DT_SINGLELINE set, is nothing at all. The
        /// three paragraphs therefore marched down the window in 160-pixel steps, the
        /// first inside the panel, the second below it, the third off the bottom; and
        /// because DT_SINGLELINE also refuses to wrap, the one that was visible ran off
        /// the right edge. The comment beside those flags said "SINGLELINE off" while
        /// the flags said otherwise, which is how it survived review.
        /// </summary>
        private void DrawPane(nint dc, int width, ProgressTheme.Palette palette)
        {
            int margin = Scaled(Margin);
            int top = _paneTop;
            int paneHeight = _paneHeight;
            int inner = width - (margin * 2) - Scaled(24);
            int inset = margin + Scaled(12);
            int gap = Scaled(10);

            RoundedBox.Draw(
                dc, margin, top, width - (margin * 2), paneHeight, Scaled(8),
                palette.Track, palette.Border);

            SettingInfo info = _rows[Math.Clamp(_selected, 0, _rows.Count - 1)];

            SetBkMode(dc, 1);

            nint old = SelectObject(dc, BodyFont());
            int y = top + Scaled(12);

            y = Paragraph(dc, inset, y, inner, $"✅ {info.Benefit}", palette.Text);
            y = Paragraph(dc, inset, y + gap, inner, $"⚠️ {info.Cost}", palette.SecondaryText);
            Paragraph(dc, inset, y + gap, inner, $"💡 {info.Advice}", palette.SecondaryText);

            SelectObject(dc, old);
        }

        /// <summary>
        /// Draws one wrapped paragraph and returns the y it actually ended at.
        ///
        /// Measured first with DT_CALCRECT, then drawn with the same wrapping flags and
        /// without DT_CALCRECT -- which is the pairing that works. DT_CALCRECT reports
        /// the height and draws nothing; including it in the drawing call produces empty
        /// space, and leaving out DT_WORDBREAK produces one long line.
        /// </summary>
        private int Paragraph(nint dc, int x, int y, int maxWidth, string text, uint color)
        {
            SetTextColor(dc, color);

            RECT measured = new() { Left = x, Top = y, Right = x + maxWidth, Bottom = y };
            DrawTextW(dc, text, text.Length, ref measured, DT_WORDBREAK | DT_NOPREFIX | DT_CALCRECT);

            int height = measured.Bottom - measured.Top;

            RECT rect = new() { Left = x, Top = y, Right = x + maxWidth, Bottom = y + height };
            DrawTextW(dc, text, text.Length, ref rect, DT_WORDBREAK | DT_NOPREFIX);

            return y + height;
        }

        /// <summary>Height the selected row's description needs, measured the same way.</summary>
        private int MeasurePane(int width)
        {
            int margin = Scaled(Margin);
            int inner = width - (margin * 2) - Scaled(24);
            int inset = margin + Scaled(12);
            int gap = Scaled(10);

            nint dc = GetDC(_window == 0 ? 0 : _window);
            nint old = SelectObject(dc, BodyFont());
            int total = Scaled(24);

            foreach (SettingInfo info in _rows)
            {
                int height = 0;

                foreach (string text in new[] { $"✅ {info.Benefit}", $"⚠️ {info.Cost}", $"💡 {info.Advice}" })
                {
                    RECT rect = new() { Left = inset, Top = 0, Right = inset + inner, Bottom = 0 };
                    DrawTextW(dc, text, text.Length, ref rect, DT_WORDBREAK | DT_NOPREFIX | DT_CALCRECT);
                    height += rect.Bottom - rect.Top + gap;
                }

                total = Math.Max(total, height);
            }

            SelectObject(dc, old);
            ReleaseDC(_window == 0 ? 0 : _window, dc);

            // A deliberate surplus on top of the measurement.
            //
            // The drawn block needs twenty points of lead-in plus two gaps, and the
            // measurement above already allows a gap after every paragraph including the
            // last, so it should have been generous. It came out short anyway -- the last
            // line of the advice was sliced through by the panel edge. The reason was not
            // worth chasing further: the cost of being generous here is a few millimetres
            // of grey, and the cost of being wrong is half a sentence. Clipped text is a
            // defect; empty space is not.
            return total + Scaled(40);
        }

        private const uint DT_WORDBREAK = 0x00000010;
        private const uint DT_CALCRECT = 0x00000400;
        private const uint DT_NOPREFIX = 0x00000800;

        private void DrawButtons(nint dc, ProgressTheme.Palette palette)
        {
            string[] labels = [Lang.Pick("卸载", "Remove"), Lang.Pick("取消", "Cancel"), Lang.Pick("确定", "OK")];
            int top = ButtonTop;
            int height = Scaled(ButtonHeight);

            for (int i = 0; i < labels.Length; i++)
            {
                (int left, int width) = ButtonRect(i);

                // 卸载 is deliberately not the accent button. Windows 11 marks the
                // recommended action with that colour, and removing the program is not
                // something this window recommends.
                MessageDialog.DrawButtonCore(
                    dc, left, top, width, height, labels[i],
                    primary: i == 2,
                    hot: i == _hotButton,
                    pressed: i == _pressedButton,
                    palette, ButtonFont());
            }
        }

        private static void Fill(nint dc, int x, int y, int width, int height, uint color)
        {
            nint brush = CreateSolidBrush(color);
            RECT rect = new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
            FillRect(dc, ref rect, brush);
            DeleteObject(brush);
        }

        // ------------------------------------------------------------------ fonts

        private nint BodyFont() => _bodyFont != 0
            ? _bodyFont
            : _bodyFont = MessageDialog.Fonts.Create(9.0, 400, _dpi);

        private nint BoldFont() => _boldFont != 0
            ? _boldFont
            : _boldFont = MessageDialog.Fonts.Create(9.0, 600, _dpi);

        private nint ButtonFont() => _buttonFont != 0
            ? _buttonFont
            : _buttonFont = MessageDialog.Fonts.Create(10.0, 400, _dpi);

        private static void ApplyWindows11Style(nint hwnd)
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

    // ---------------------------------------------------------------------- interop

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

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

    [DllImport("gdi32.dll")]
    private static extern nint CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    private static extern int MoveToEx(nint dc, int x, int y, nint previous);

    [DllImport("gdi32.dll")]
    private static extern int LineTo(nint dc, int x, int y);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
}
