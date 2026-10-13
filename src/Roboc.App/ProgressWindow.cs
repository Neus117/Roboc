using System.Runtime.InteropServices;
using Roboc.Core.Interop;

namespace Roboc.App;

/// <summary>
/// The progress window shown while a long operation runs.
///
/// <b>Why it is a real window.</b> MessageBox cannot be used here: it is modal and
/// blocks the calling thread, so its text can never change once shown. A window that
/// reports "剩余 1,234 项" has to be updated while the work continues, which means
/// owning a message loop.
///
/// <b>Why it appears late.</b> It is created only after the operation has been
/// running for <see cref="DelayMs"/>. Most operations finish well inside that, and
/// they then never see a window at all -- not one that flashes into existence and is
/// torn down again.
///
/// <b>Why the worker never touches it.</b> The operation thread only writes numbers
/// into fields. A timer on the window's own thread reads them and updates the
/// controls. Touching a window handle from another thread is the classic way to
/// deadlock a UI, and there is no reason to risk it for a progress readout.
/// </summary>
internal sealed class ProgressWindow : IDisposable
{
    private const int DelayMs = 2000;
    private const uint TimerId = 1;
        /// <summary>
    /// 100 ms, not 200.
    ///
    /// This is the horizontal resolution of the rate graph: sixty samples spread over
    /// 892 pixels put a corner every fifteen pixels, which reads as a bent line rather
    /// than a curve. Twice the samples at the same history length halves that. The
    /// cost is one extra repaint per tenth of a second, which the double buffer makes
    /// invisible.
    /// </summary>
    private const uint RefreshMs = 100;

    private readonly string _title;
    private readonly string _subtitle;
    private readonly Roboc.Core.Control.JobControl _control;

    private Thread? _thread;
    private nint _window;

    // _detail, _headlineControl and _percentControl were here. They held the three
    // STATIC child windows this card used to put its text in, and survived their
    // removal: the text is drawn now, so nothing read them again. The compiler said so
    // on every build -- three CS0169 warnings -- and they sat in that noise for as long
    // as the drawing code has existed.
    private nint _cancelButton;
    private nint _pauseButton;
    private nint _font;
    private int _dpi = 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    private volatile bool _stop;
    private bool _cancelArmed;
    private volatile bool _paused;
    private string _pauseLabel = "暂停";
    private int _refreshCount;
    private int _distinctPercents;
    private long _lastPercent = long.MinValue;
    private long _maxPercent = -1;

    // Written by the operation thread, read by the window thread. Longs are read
    // and written atomically on x64, and a stale reading by one tick is harmless in
    // a progress display.
    /// <summary>
    /// Recent transfer rates, newest last.
    ///
    /// A graph of the instantaneous rate is a noise band; what the eye reads as speed
    /// is the shape over the last few seconds. Sixty samples at the refresh interval
    /// is about twelve seconds of history, which is what the native dialog shows.
    /// </summary>
    private readonly long[] _history = new long[120];
    private int _historyCount;
    private int _historyIndex;
    private long _peakRate;

    private long _remainingItems;
    private long _remainingBytes;
    private long _remainingSeconds = -1;
    private long _bytesPerSecond;
    private long _percent = -1;
    private volatile string _phase = string.Empty;
    private volatile string _headline = string.Empty;

    private ProgressWindow(
        string title, string subtitle, Roboc.Core.Control.JobControl control)
    {
        _title = title;
        _subtitle = subtitle;
        _control = control;
    }

    /// <summary>
    /// Starts the delayed display. Nothing is created until the delay elapses, so a
    /// quick operation costs one timer thread and no window.
    /// </summary>
    internal static ProgressWindow Start(
        string title, string subtitle, Roboc.Core.Control.JobControl control)
    {
        var window = new ProgressWindow(title, subtitle, control);
        window._thread = new Thread(window.Run)
        {
            IsBackground = true,
            Name = "Roboc progress",
        };
        window._thread.Start();
        return window;
    }

    /// <summary>Publishes new numbers. Called from the operation thread.</summary>
    internal void Report(
        string headline, string phase,
        long remainingItems, long remainingBytes, long bytesPerSecond,
        double fraction, double? remainingSeconds)
    {
        _headline = headline;
        _phase = phase;
        Interlocked.Exchange(ref _remainingItems, remainingItems);
        Interlocked.Exchange(ref _remainingBytes, remainingBytes);
        Interlocked.Exchange(ref _bytesPerSecond, bytesPerSecond);
        Interlocked.Exchange(ref _percent, fraction < 0 ? -1 : (long)Math.Round(fraction * 100));
        Interlocked.Exchange(ref _remainingSeconds, remainingSeconds is null ? -1 : (long)remainingSeconds.Value);
    }

    private void Run()
    {
        // The delay is the whole point: an operation that finishes first never shows
        // anything, so it is spent before the window exists rather than by hiding one.
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (waited.ElapsedMilliseconds < DelayMs && !_stop)
        {
            Thread.Sleep(50);
        }

        if (_stop)
        {
            return;
        }

        try
        {
            Show();
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // ------------------------------------------------------------------ window

    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_TIMER = 0x0113;

    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>
    /// The frame the card uses: a dialog, not a document window.
    ///
    /// The native progress dialog is a fixed-size dialog box, and so is this. The
    /// resizable frame was tried first and removed: a thick frame on a window with
    /// rounded corners and a custom-drawn client area produces a composited edge that
    /// does not line up with the drawn content, which is what showed as an irregular
    /// translucent smear outside the window.
    /// </summary>
    private const uint WS_CLIPCHILDREN = 0x02000000;

    /// <summary>
    /// A fixed-size dialog that leaves its child windows alone.
    ///
    /// WS_CLIPCHILDREN is not optional here. Without it the parent's painting is not
    /// clipped to exclude its children, and this card repaints its whole client area
    /// ten times a second -- so the two buttons were created, positioned correctly and
    /// then painted over on every frame. Their handles were always valid, which is why
    /// nothing looked wrong from the code's side.
    /// </summary>
    private const uint WindowStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;
    private const uint WM_SIZE = 0x0005;
    private const uint SS_LEFT = 0x00000000;
    private const uint BS_PUSHBUTTON = 0x00000000;
    private const uint CW_USEDEFAULT = 0x80000000;

    private static readonly nint IdCancel = 1001;
    private static readonly nint IdPause = 1002;
    private static WindowProc? _proc;   // Held so the delegate is not collected.

    private void Show()
    {
        _proc = Dispatch;

        var wc = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = "RobocProgress",
            hInstance = GetModuleHandleW(null),
            // Set, not left at zero: a class registered without an icon gives a window
                // without one, which is why the title bars were blank.
                hIcon = AppIcon.Handle,
                hCursor = LoadCursorW(0, 32512),   // IDC_ARROW
        };

        RegisterClassW(ref wc);

        // Sized for the longest line the renderer produces, rather than for a guess.
        // The previous size clipped "正在将 N 个项目从 … 复制到 …" and cut the cancel
        // button in half.
        // GetDpiForSystem, not GetDpiForWindow: the window does not exist yet, and
        // calling the per-window form with a null handle returns the 96 DPI default
        // regardless of the display. A 175% monitor then got a window sized for 96 DPI
        // while the fonts inside it were sized for 175%, so the text overflowed the
        // card by more than half its width.
        int dpiNow = 96;

        try
        {
            uint system = GetDpiForSystem();
            dpiNow = system == 0 ? 96 : (int)system;
        }
        catch (EntryPointNotFoundException) { }

        // Logical size, converted once. 600x480 is what the card is designed at.
        // 510 x 352 logical: 320 client plus the caption. Shorter than the last card
        // because the detail block no longer floats well above the bottom edge.
        int width = 510 * dpiNow / 96;
        int height = 352 * dpiNow / 96;

        // The window rectangle includes the frame and caption, which are also scaled,
        // so the client area would be short by their height without this.
        RECT frame = new() { Left = 0, Top = 0, Right = width, Bottom = height };

        try
        {
            AdjustWindowRectExForDpi(ref frame, WindowStyle, 0, 0, (uint)dpiNow);
            width = frame.Right - frame.Left;
            height = frame.Bottom - frame.Top;
        }
        catch (EntryPointNotFoundException) { }
        int x = (GetSystemMetrics(0) - width) / 2;
        int y = (GetSystemMetrics(1) - height) / 2;

        _window = CreateWindowExW(
            0, "RobocProgress", _title,
            WindowStyle | WS_VISIBLE,
            x, y, width, height, 0, 0, wc.hInstance, 0);

        if (_window == 0)
        {
            return;
        }

        nint font = GetStockObject(17);   // DEFAULT_GUI_FONT

        // Children are positioned against the client area, not the window rectangle.
        // Using the window height put the button below the visible area -- the frame
        // and caption are part of the window but not of the client.
        // Owner-drawn, and drawn by the same routine the message dialogs use.
        //
        // A standard BUTTON control brings its own font, its own corners and its own
        // colours, none of which can be set -- so the two buttons at the bottom of a
        // Windows 11 card rendered as Windows 95 grey rectangles. Reusing the dialog's
        // painter rather than writing a second one is what keeps them identical.
        _font = font;
        _cancelButton = CreateChild("BUTTON", "取消", 0, 0, 100, 32, font, IdCancel, ownerDrawn: true);
        _pauseButton = CreateChild("BUTTON", "暂停", 0, 0, 100, 32, font, IdPause, ownerDrawn: true);

        // No STATIC controls: the card draws its own text. A child window paints
        // after its parent, so any label left here would sit on top of the custom
        // drawing and double every line.

        // The DPI is settled first: Layout positions the buttons in scaled pixels, and
        // running it while _dpi still held its 96 default laid them out for a different
        // display than the one the window was sized for.
        ApplyWindows11Style(_window);
        _dpi = ScaleFor(_window);
        CreateFonts(_dpi);

        // Built here, not alongside the buttons, because _dpi is only known once the
        // window exists. Passing the 96 default made these labels 12 physical pixels
        // tall on a 175% display where every other line in the card is 21 -- small
        // enough to look like a rendering fault rather than a font choice.
        ButtonFont = MessageDialog.Fonts.Create(10.0, 400, _dpi);

        Layout();



        UpdateWindow(_window);

        // Rendered once immediately, before the first timer tick. An operation that
        // finishes just past the delay otherwise showed an empty window: it appeared
        // at two seconds and the first refresh was another 200 ms away.
        Refresh();

        SetTimer(_window, TimerId, RefreshMs, 0);

        while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private nint CreateChild(
        string cls, string text, int x, int y, int w, int h, nint font, nint id,
        bool ownerDrawn = false)
    {
        // BS_OWNERDRAW (0x0B) makes the button ask its parent to paint it, which is
        // the only way to give it rounded corners and the interface colours.
        uint style = WS_CHILD | WS_VISIBLE | SS_LEFT | (ownerDrawn ? 0x0000000Bu : 0u);

        nint handle = CreateWindowExW(0, cls, text, style, x, y, w, h, _window, id, 0, 0);

        if (font != 0)
        {
            SendMessageW(handle, 0x0030, font, 0);   // WM_SETFONT
        }

        return handle;
    }

    /// <summary>
    /// Places the controls for the current client size. Called on creation and again
    /// whenever the window is resized, so the layout survives the user dragging the
    /// frame instead of leaving the button stranded.
    /// </summary>
    private void Layout()
    {
        if (GetClientRect(_window, out RECT client) == 0)
        {
            return;
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        int inner = Math.Max(60, width - 40);
        int buttonWidth = Scaled(100, _dpi);
        int buttonHeight = Scaled(32, _dpi);

        _ = inner;

        int detailTop = 80;
        int detailHeight = Math.Max(40, height - detailTop - buttonHeight - 28);
        _ = detailHeight;

        int buttonY = Math.Max(detailTop, height - buttonHeight - 18);
        int right = Math.Max(20, width - buttonWidth - 20);

        MoveWindow(_cancelButton, right, buttonY, buttonWidth, buttonHeight, 1);
        MoveWindow(
            _pauseButton, Math.Max(20, right - buttonWidth - 8), buttonY,
            buttonWidth, buttonHeight, 1);
    }

    private nint Dispatch(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case 0x000F:   // WM_PAINT
                // BeginPaint/EndPaint, not a bare GetDC.
                //
                // Painting without validating the update region leaves it dirty, so
                // Windows sends WM_PAINT again immediately and forever. That loop
                // competes with the timer that feeds the graph and with the drawing
                // itself, which is enough to make frames arrive torn or half finished.
                BeginPaint(hwnd, out PAINTSTRUCT paint);
                Paint(hwnd);
                EndPaint(hwnd, ref paint);
                return 0;

            case 0x0014:   // WM_ERASEBKGND -- painting covers the whole client area
                return 1;

            case 0x002B:   // WM_DRAWITEM
                DrawOwnerButton(lParam);
                return 1;

            case WM_SIZE:
                Layout();
                return 0;

            case WM_TIMER:
                Refresh();
                return 0;

            case WM_COMMAND:
                if (wParam == IdCancel)
                {
                    RequestCancel(hwnd);
                    return 0;
                }

                if (wParam == IdPause)
                {
                    TogglePause();
                    return 0;
                }

                break;

            case WM_CLOSE:
                // Closing the window is a cancel request, not a way to hide it: the
                // operation is still running and would otherwise continue invisibly.
                RequestCancel(hwnd);
                return 0;

            case WM_DESTROY:
                KillTimer(hwnd, TimerId);
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Pauses or resumes. The engine already supports this: workers park on a gate
    /// between chunks, so a pause takes effect within one chunk even in the middle of
    /// a large file rather than only between files.
    /// </summary>
    private void TogglePause()
    {
        if (_control.IsPaused)
        {
            _control.Resume();
            _pauseLabel = "暂停";
            InvalidateRect(_pauseButton, 0, 1);
            _paused = false;
        }
        else
        {
            _control.Pause();
            _pauseLabel = "继续";
            InvalidateRect(_pauseButton, 0, 1);
            _paused = true;
        }
    }

    private void RequestCancel(nint hwnd)
    {
        if (_cancelArmed)
        {
            return;
        }

        // A second confirmation, because the window is small and the button sits near
        // the close box; one stray click should not throw away a long operation.
        //
        // The same drawn dialog every other confirmation uses. A MessageBox here would
        // be the one prompt in the program with the system's font and the system's
        // corners, appearing directly on top of a card that has neither.
        int answer = MessageDialog.Show(
            "⚠️确定要停止当前操作吗？\r\n\r\n已经完成的项目会保留，尚未处理的会被放弃。",
            "Roboc — 停止操作",
            DialogButtons.YesNo,
            warning: true,
            centre: true);

        if (answer != 0)
        {
            return;
        }

        _cancelArmed = true;
        _phase = "正在停止…";
        _control.Cancel();
    }

    private void Refresh()
    {
        long items = Interlocked.Read(ref _remainingItems);
        long bytes = Interlocked.Read(ref _remainingBytes);
        long seconds = Interlocked.Read(ref _remainingSeconds);
        long speed = Interlocked.Read(ref _bytesPerSecond);
        long percent = Interlocked.Read(ref _percent);

        // The card is painted from these fields; nothing is pushed into a control.

        // Mirrors the wording Explorer uses, which is what makes it readable at a
        // glance: the label says what the number is, so nothing has to be inferred.


        var text = new System.Text.StringBuilder();

        if (_phase.Length > 0)
        {
            text.AppendLine($"名称: {_phase}");
        }

        // Suppressed until an estimate exists: "大约 0 秒" on the first tick reads as
        // a broken promise rather than as an estimate still forming.
        if (seconds >= 0)
        {
            text.AppendLine(seconds >= 60
                ? $"剩余时间: 大约 {seconds / 60} 分 {seconds % 60} 秒"
                : $"剩余时间: 大约 {seconds} 秒");
        }

        text.AppendLine($"剩余项目: {items:N0} ({FormatBytes(bytes)})");

        if (speed > 0)
        {
            text.AppendLine($"速度: {FormatBytes(speed)}/秒");
        }

        // Built by the renderer now; the buffer below is no longer needed for it.
        _ = text;

        _refreshCount++;

        if (percent != _lastPercent)
        {
            _distinctPercents++;
            _lastPercent = percent;
        }

        if (percent > _maxPercent)
        {
            _maxPercent = percent;
        }

        _history[_historyIndex] = speed;
        _historyIndex = (_historyIndex + 1) % _history.Length;

        if (_historyCount < _history.Length)
        {
            _historyCount++;
        }

        if (speed > _peakRate)
        {
            _peakRate = speed;
        }

        Paint(_window);
    }

    /// <summary>Same units Explorer shows, so the numbers read the same way.</summary>
    private static string FormatBytes(long value)
    {
        if (value >= 1L << 30) return $"{value / (double)(1L << 30):F2} GB";
        if (value >= 1L << 20) return $"{value / (double)(1L << 20):F1} MB";
        if (value >= 1L << 10) return $"{value / (double)(1L << 10):F1} KB";
        return $"{value} B";
    }

    /// <summary>The label font for the two buttons, shared by both.</summary>
    private nint ButtonFont;

    /// <summary>
    /// Paints one owner-drawn button.
    ///
    /// Both are drawn as secondary actions: neither discards work outright, so neither
    /// should carry the accent colour that Windows 11 reserves for the action a dialog
    /// is recommending.
    /// </summary>
    private void DrawOwnerButton(nint lParam)
    {
        DRAWITEMSTRUCT item = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
        ProgressTheme.Palette palette = ProgressTheme.Current();

        bool pressed = (item.itemState & 0x0001) != 0;   // ODS_SELECTED
        bool isPause = item.CtlID == IdPause;
        string label = isPause ? _pauseLabel : "取消";

        // While paused, 继续 is the action the card is recommending -- it is how the
        // user gets the work moving again, and the accent colour is how Windows 11
        // marks exactly that. Before the pause both buttons are secondary.
        bool primary = isPause && _paused;

        if (ButtonFont == 0)
        {
    
        }

        MessageDialog.DrawButtonCore(
            item.hDC, item.rcItem.Left, item.rcItem.Top,
            item.rcItem.Right - item.rcItem.Left,
            item.rcItem.Bottom - item.rcItem.Top,
            label, primary, hot: pressed, pressed: pressed,
            palette, ButtonFont);
    }

    // ------------------------------------------------------------------ painting

    private nint _titleFont;
    private nint _bigFont;
    private nint _bodyFont;

    /// <summary>
    /// Draws the whole card: the background, the progress bar and the rate graph.
    ///
    /// Double buffered. Drawing straight to the window makes the bar and the graph
    /// flicker on every tick, because each repaint exposes the half-erased state
    /// underneath -- and at five repaints a second that is the first thing anyone
    /// notices.
    /// </summary>
    private void Paint(nint hwnd)
    {
        if (GetClientRect(hwnd, out RECT client) == 0)
        {
            return;
        }

        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        nint screen = GetDC(hwnd);
        nint buffer = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, width, height);
        nint previous = SelectObject(buffer, bitmap);

        ProgressTheme.Palette palette = ProgressTheme.Current();

        Fill(buffer, 0, 0, width, height, palette.Window);

        long percent = Interlocked.Read(ref _percent);
        long speed = Interlocked.Read(ref _bytesPerSecond);

        // The bar occupies the width minus the card's margins, and the graph sits
        // below it with the same width so the two read as one column.
        int margin = Scaled(24, _dpi);
        int inner = Math.Max(40, width - margin * 2);

        // Vertical rhythm, in logical pixels at 96 DPI:
        //   24  headline (11 pt, ~15 px tall)      -> 24..39
        //   52  percentage (26 pt, ~35 px tall)    -> 52..87
        //  104  current item (10 pt)               -> 104..118
        //  134  progress bar, 8 px tall            -> 134..142
        //  166  graph
        //  320  three detail lines, 30 px apart    -> 320..380
        // The window is 480 logical pixels tall, so the last line clears the bottom.
                // Vertical rhythm, in logical pixels at 96 DPI. Every band is stated once,
        // here and nowhere else.
        //
        // The previous set was carried over unchanged when the card was made 15%
        // smaller and its fonts a point smaller, so the bands no longer matched the
        // text they held: the current-item line grew into the progress bar, and the
        // detail block drifted far below a graph that had also shrunk.
        //    20  headline    (10 pt, ~13 px)  20..33
        //    46  percentage  (19 pt, ~25 px)  46..71
        //    86  current item (9 pt, ~12 px)  86..98
        //   108  bar, 8 px                    108..116
        //   134  graph, 92 px                 134..226
        //   250  three detail lines, 26 apart 250..306
        // Client height is 320, so the last line clears the bottom by 14.
        int barTop = Scaled(108, _dpi);
        int barHeight = Scaled(8, _dpi);
        DrawBar(buffer, margin, barTop, inner, barHeight, percent, palette);

        int graphTop = Scaled(134, _dpi);

        // Fixed height, not the leftover space. An elastic graph absorbed every
        // rounding difference between the text bands and its own edges, which left it
        // touching the detail block on one build and floating on the next.
        int graphHeight = Scaled(92, _dpi);
        DrawGraph(buffer, margin, graphTop, inner, graphHeight, palette);

        DrawTextBlock(buffer, margin, barTop, inner, palette, percent, speed);

        BitBlt(screen, 0, 0, width, height, buffer, 0, 0, 0x00CC0020);

        SelectObject(buffer, previous);
        DeleteObject(bitmap);
        DeleteDC(buffer);
        ReleaseDC(hwnd, screen);
    }

    /// <summary>Rounded progress bar: track, then the filled portion.</summary>
    private static void DrawBar(
        nint dc, int x, int y, int width, int height, long percent, ProgressTheme.Palette palette)
    {
        // Every brush is deleted. A process has a finite number of GDI objects, and
        // leaking three per repaint at five repaints a second exhausts that quota in
        // minutes -- after which every drawing call fails silently and the card goes
        // blank rather than wrong, which is far harder to diagnose.
        nint track = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, height, height);
        nint trackBrush = CreateSolidBrush(palette.Track);
        FillRgn(dc, track, trackBrush);
        DeleteObject(trackBrush);
        DeleteObject(track);

        if (percent <= 0)
        {
            return;
        }

        int filled = (int)Math.Clamp(width * percent / 100.0, 0, width);

        if (filled < height)
        {
            filled = height;   // Below this the rounded cap degrades into a smudge.
        }

        nint fill = CreateRoundRectRgn(x, y, x + filled + 1, y + height + 1, height, height);
        nint fillBrush = CreateSolidBrush(palette.Accent);
        FillRgn(dc, fill, fillBrush);
        DeleteObject(fillBrush);
        DeleteObject(fill);
    }

    /// <summary>
    /// The transfer-rate graph: a smoothed area under a smoothed curve.
    ///
    /// <b>Why it is smoothed twice.</b> The raw samples are per-tick rates, and a
    /// per-tick rate is spiky by nature -- it depends on which files happened to
    /// finish in that fifth of a second. Drawn literally it is a comb. A short moving
    /// average removes the spikes, and a spline through what is left removes the
    /// corners, which together read as a wave rather than as a chart.
    ///
    /// <b>Why one path serves as both fill and line.</b> GDI's StrokeAndFillPath
    /// paints the interior with the current brush and the outline with the current
    /// pen, so the area and its edge cannot disagree about where the curve is -- which
    /// they would if the polygon and the polyline were built separately.
    /// </summary>
    private void DrawGraph(
        nint dc, int x, int y, int width, int height, ProgressTheme.Palette palette)
    {
        nint background = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, 8, 8);
        nint backgroundBrush = CreateSolidBrush(palette.Track);
        FillRgn(dc, background, backgroundBrush);
        DeleteObject(backgroundBrush);

        // A one-pixel outline, so the plot area has a visible edge instead of fading
        // into the card at the corners where the rounded region is thin.
        nint borderRegion = CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, 8, 8);
        nint borderBrush = CreateSolidBrush(palette.Border);
        FrameRgn(dc, borderRegion, borderBrush, 1, 1);
        DeleteObject(borderBrush);
        DeleteObject(borderRegion);
        DeleteObject(background);


        int count = _historyCount;

        if (count < 2)
        {
            // A flat line along the baseline. An empty box and a graph that has not
            // collected enough samples yet look identical to the user, and the second
            // reads as broken.
            nint idlePen = CreatePen(0, 2, palette.GraphLine);
            nint idleOld = SelectObject(dc, idlePen);
            MoveToEx(dc, x, y + height - 2, 0);
            LineTo(dc, x + width, y + height - 2);
            SelectObject(dc, idleOld);
            DeleteObject(idlePen);
            return;
        }

        // Newest last, oldest first.
        var raw = new long[count];

        for (int i = 0; i < count; i++)
        {
            raw[i] = _history[(_historyIndex - count + i + _history.Length * 2) % _history.Length];
        }

        long peak = 0;

        foreach (long value in raw)
        {
            if (value > peak)
            {
                peak = value;
            }
        }

        if (peak <= 0)
        {
            return;
        }

        // Moving average, window 3, edges clamped so the curve starts and ends at the
        // values it actually has rather than trailing off toward zero.
        //
        // Three rather than five: with twice as many samples the same amount of
        // smoothing can be had from a narrower window, and a narrower window leaves
        // more of the transfer's actual shape intact.
        var smoothed = new long[count];

        for (int i = 0; i < count; i++)
        {
            long sum = 0;
            int used = 0;

            for (int k = -1; k <= 1; k++)
            {
                int j = i + k;

                if (j < 0 || j >= count)
                {
                    continue;
                }

                sum += raw[j];
                used++;
            }

            smoothed[i] = used == 0 ? raw[i] : sum / used;
        }

        var points = new POINT[count];

        for (int i = 0; i < count; i++)
        {
            points[i].X = x + width * i / Math.Max(1, count - 1);
            points[i].Y = y + height - 4 - (int)((long)(height - 8) * smoothed[i] / peak);
        }

        // Catmull-Rom through the smoothed samples, expressed as the cubic Beziers
        // GDI wants. The tangent at each point is taken from its neighbours, which is
        // what makes the joins continuous instead of cornered.
        var curves = new POINT[(count - 1) * 3];

        for (int i = 0; i < count - 1; i++)
        {
            POINT p0 = points[Math.Max(0, i - 1)];
            POINT p1 = points[i];
            POINT p2 = points[i + 1];
            POINT p3 = points[Math.Min(count - 1, i + 2)];

            curves[i * 3].X = p1.X + (p2.X - p0.X) / 6;
            curves[i * 3].Y = p1.Y + (p2.Y - p0.Y) / 6;
            curves[(i * 3) + 1].X = p2.X - (p3.X - p1.X) / 6;
            curves[(i * 3) + 1].Y = p2.Y - (p3.Y - p1.Y) / 6;
            curves[(i * 3) + 2] = p2;
        }

        nint fillBrush = CreateSolidBrush(palette.GraphFill);
        nint linePen = CreatePen(0, 2, palette.GraphLine);
        nint oldBrush = SelectObject(dc, fillBrush);
        nint oldPen = SelectObject(dc, linePen);

        BeginPath(dc);
        MoveToEx(dc, points[0].X, points[0].Y, 0);
        PolyBezierTo(dc, curves, curves.Length);

        // Closed along the baseline, which is what makes the path fillable.
        LineTo(dc, x + width, y + height);
        LineTo(dc, x, y + height);
        EndPath(dc);

        StrokeAndFillPath(dc);

        // Rules drawn after the fill, not before it.
        //
        // They were written first and the filled area painted straight over them, so
        // the graph had gridlines that no one could ever see, including me: the code
        // was there, the picture showed nothing, and only the user's report settled
        // which of the two was true.
        //
        // Their own colour, not the window border colour: over the filled area a
        // border-tone line is nearly invisible, which is the same problem one layer
        // down.
        // A 10 x 4 mesh: nine vertical rules and three horizontal ones.
        //
        // Vertical as well as horizontal because the graph's one meaningful axis is
        // time, and a horizontal-only grid gives no way to read how long a peak
        // lasted or where in the twelve-second window it sat.
        nint gridPen = CreatePen(0, 1, palette.GraphGrid);
        nint gridOld = SelectObject(dc, gridPen);

        for (int line = 1; line <= 9; line++)
        {
            int gx = x + width * line / 10;
            MoveToEx(dc, gx, y + 1, 0);
            LineTo(dc, gx, y + height - 1);
        }

        for (int line = 1; line <= 3; line++)
        {
            int gy = y + height * line / 4;
            MoveToEx(dc, x + 1, gy, 0);
            LineTo(dc, x + width - 1, gy);
        }

        SelectObject(dc, gridOld);
        DeleteObject(gridPen);

        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(fillBrush);
        DeleteObject(linePen);
    }
    /// <summary>Headline, percentage and the detail lines.</summary>
    private void DrawTextBlock(
        nint dc, int margin, int barTop, int inner,
        ProgressTheme.Palette palette, long percent, long speed)
    {
        SetBkMode(dc, 1);   // TRANSPARENT

        // Headline
        nint previous = SelectObject(dc, _titleFont);
        SetTextColor(dc, palette.Text);
        TextOut(dc, margin, Scaled(20, _dpi), _headline, _headline.Length);

        // Percentage, in the large face the native dialog uses for it.
        SelectObject(dc, _bigFont);
        string percentText = _paused
            ? "已暂停"
            : percent >= 0 ? $"已完成 {percent}%" : "正在准备…";

        TextOut(dc, margin, Scaled(46, _dpi), percentText, percentText.Length);

        // Detail lines under the graph.
        SelectObject(dc, _bodyFont);
        SetTextColor(dc, palette.SecondaryText);

        long items = Interlocked.Read(ref _remainingItems);
        long bytes = Interlocked.Read(ref _remainingBytes);
        long seconds = Interlocked.Read(ref _remainingSeconds);

        // The current item sits between the percentage and the bar, where the native
        // dialog puts it: it names the work in progress without competing with the
        // number the user is actually watching.
        if (_phase.Length > 0)
        {
            TextOut(dc, margin, Scaled(86, _dpi), _phase, _phase.Length);
        }

        int y = Scaled(250, _dpi);
        string left = $"剩余项目 {items:N0} ({FormatBytes(bytes)})";
        TextOut(dc, margin, y, left, left.Length);

        if (speed > 0)
        {
            string rate = $"速度 {FormatBytes(speed)}/秒";
            TextOut(dc, margin, y + Scaled(26, _dpi), rate, rate.Length);
        }

        if (seconds >= 0)
        {
            string eta = seconds >= 60
                ? $"剩余时间 大约 {seconds / 60} 分 {seconds % 60} 秒"
                : $"剩余时间 大约 {seconds} 秒";

            TextOut(dc, margin, y + Scaled(52, _dpi), eta, eta.Length);
        }

        SelectObject(dc, previous);
    }

    private static void Fill(nint dc, int x, int y, int width, int height, uint color)
    {
        nint brush = CreateSolidBrush(color);
        RECT rect = new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }
    // --------------------------------------------------------------- lifecycle

    /// <summary>Closes the window. Safe to call before it ever appeared.</summary>
    public void Dispose()
    {
        _stop = true;

        nint window = _window;

        if (window != 0)
        {
            // WM_CLOSE is deliberately not used here: it would be read as a cancel
            // request. Destroying the window directly just ends the message loop.
            PostMessageW(window, WM_DESTROY, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        // Written to the log so the next run can answer "is the timer firing?" with a
        // number instead of an inference. A window that never updates and a timer
        // that never fires look identical from the outside.
        //
        // Written straight to the log file, never to stderr. Purge captures stderr to
        // decide whether anything failed and shows whatever it caught as an error
        // dialog -- so a diagnostic sent there made every successful delete report a
        // failure, and was itself invisible while doing so.
        try
        {
            // "refreshes" alone cannot tell a working window from a frozen one: a
            // window that repaints the same numbers 161 times looks identical to one
            // whose numbers never change. The distinct reading count is what
            // distinguishes them.
            string? logPath = ConsoleHost.LogPath;

            if (logPath is not null)
            {
                File.AppendAllText(
                    logPath,
                    $"progress window: shown={_window != 0}, refreshes={_refreshCount}, " +
                    $"distinct percent values={_distinctPercents}, highest={_maxPercent}" +
                    Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ interop

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
    private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTextW(nint hwnd, string text);

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hwnd, uint id, uint interval, nint proc);

    [DllImport("user32.dll")]
    private static extern int KillTimer(nint hwnd, uint id);

    [DllImport("user32.dll")]
    private static extern int UpdateWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int InvalidateRect(nint hwnd, nint rect, int erase);

    [DllImport("user32.dll")]
    private static extern int GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int MoveWindow(nint hwnd, int x, int y, int width, int height, int repaint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, int name);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
    // ------------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public nint hwndItem;
        public nint hDC;
        public RECT rcItem;
        public nint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern int BitBlt(
        nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(
        int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern int FillRgn(nint dc, nint region, nint brush);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref RECT rect, nint brush);
    [DllImport("gdi32.dll")] private static extern int Polygon(nint dc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern int Polyline(nint dc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int TextOutW(
        nint dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string face);

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

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

    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);
    [DllImport("user32.dll")] private static extern int EndPaint(nint hwnd, ref PAINTSTRUCT paint);
    [DllImport("gdi32.dll")] private static extern int MoveToEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] private static extern int LineTo(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern int FrameRgn(
        nint dc, nint region, nint brush, int width, int height);
    [DllImport("gdi32.dll")] private static extern int BeginPath(nint dc);
    [DllImport("gdi32.dll")] private static extern int EndPath(nint dc);
    [DllImport("gdi32.dll")] private static extern int PolyBezierTo(nint dc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern int StrokeAndFillPath(nint dc);

    [DllImport("user32.dll")]
    private static extern int AdjustWindowRectExForDpi(
        ref RECT rect, uint style, int menu, uint extendedStyle, uint dpi);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref int value, int size);

    private static int TextOut(nint dc, int x, int y, string text, int length)
        => text.Length == 0 ? 0 : TextOutW(dc, x, y, text, text.Length);

    /// <summary>
    /// The window's DPI, so that pixels can be converted to a physical size.
    ///
    /// Everything in this card is laid out in pixels, and a pixel is not a unit of
    /// size -- it is a unit of the display. The system interface scales with the
    /// monitor; a card that does not shows its body text at roughly two thirds of the
    /// size of the menu it was opened from on a 150% display, which is exactly how
    /// this looked.
    /// </summary>
    private static int ScaleFor(nint hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96 : (int)dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    /// <summary>Converts a size designed at 96 DPI to the current display.</summary>
    private static int Scaled(int pixels, int dpi) => pixels * dpi / 96;

    /// <summary>
    /// The three faces the card needs.
    ///
    /// 12, 30 and 10.5 points. The percentage is the one element the native dialog
    /// makes large enough to read at a glance; the rest matches the system interface.
    /// </summary>
    private void CreateFonts(int dpi)
    {
        // Sized against the system interface, in points so the DPI conversion is the
        // system's own.
        //
        // The system uses 9 pt, which is 9 * dpi / 72 physical pixels -- 21 on a 175%
        // display. Two earlier attempts got this wrong in opposite directions: fixed
        // pixel heights of -12 and -14 stayed at 12 and 14 physical pixels whatever
        // the display, so they read as about half the size of the surrounding menus
        // on this monitor; then 15 and 14 points, chosen as a correction, came out at
        // 35 and 33 pixels and overflowed the card. 10 pt is 23 pixels here, which is
        // the system size plus the small amount a headline wants.
        _titleFont = MakeFont(10.0, 400, dpi);
        // 20 pt, down from 26. The larger size was chosen to be unmistakable and came
        // out dominating a card whose other text had already been corrected.
        _bigFont = MakeFont(19.0, 600, dpi);
        _bodyFont = MakeFont(9.0, 400, dpi);
    }

    /// <summary>
    /// A font of the given point size on the given display.
    ///
    /// Points, not pixels: a point is a physical size that converts through the DPI,
    /// while a pixel is whatever the monitor happens to call one. Passing a pixel
    /// height is why this card's text did not grow with the system interface.
    /// </summary>
    private static nint MakeFont(double points, int weight, int dpi)
    {
        int height = -(int)Math.Round(points * dpi / 72.0);

        nint font = CreateFontW(
            height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, ProgressTheme.Face);

        // ClearType quality (5) for the crisp look; the fallback face covers builds
        // without Segoe UI Variable, where CreateFontW substitutes silently.
        return font != 0
            ? font
            : CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, ProgressTheme.FallbackFace);
    }

    /// <summary>
    /// Rounded corners and a themed caption, and deliberately nothing else.
    ///
    /// A Mica backdrop was tried and removed. Mica is designed for windows that do
    /// not paint their own background -- it shows the desktop through the window
    /// surface. This card fills its entire client area with an opaque colour, so the
    /// backdrop had nothing to show through and only complicated the composited edge.
    /// </summary>
    private void ApplyWindows11Style(nint hwnd)
    {
        try
        {
            int round = 2;   // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

            // The caption is drawn by DWM, not by this program, so nothing painted
            // into the client area can reach it. It stayed light in the dark theme
            // because immersive dark mode is off by default for windows that do not
            // opt in, and there is no other way to ask for it.
            //
            // The palette is read first because the theme is only settled when
            // Current() runs.
            _ = ProgressTheme.Current();
            ProgressTheme.ApplyCaptionColour(hwnd, ProgressTheme.Current());

            int dark = ProgressTheme.IsDark ? 1 : 0;

            // 20 on Windows 11; 19 on Windows 10 before build 18985. Trying the newer
            // one and falling back beats assuming which build this is.
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

