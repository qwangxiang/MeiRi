using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Models;
using MeiRi.ViewModels;
using MeiRi.Views;

namespace MeiRi.Services;

/// <summary>
/// Owns one desktop calendar window per monitor, keeps their relative position in sync, and
/// owns the floating windows that are anchored to the calendar: the hover preview, the day
/// editor and the backlog. Anchored windows always sit at a fixed offset from the calendar
/// window they belong to, so moving the calendar moves them along.
/// </summary>
public sealed class CalendarHost : IDisposable
{
    /// <summary>Physical offset from the top-right corner of a window's *card* to the mouse.</summary>
    private const double EditorCardMargin = 16;
    private const double PreviewCardMargin = 14;

    private readonly DataStore _store;
    private readonly Settings _settings;
    private readonly List<CalendarWindow> _windows = new();
    private readonly List<ScreenInfo> _monitors = new();
    private readonly DispatcherTimer _relayoutDebounce;
    private readonly DispatcherTimer _settleTimer;
    private int _settleTicks;

    private DayEditorWindow? _editor;
    private BacklogWindow? _backlog;
    private DayPreviewWindow? _preview;
    private HwndSource? _messageSource;
    private bool _systemEventsHooked;

    private Anchor? _editorAnchor;
    private Anchor? _backlogAnchor;
    private Anchor? _previewAnchor;

    private CalendarWindow? _dragWindow;
    private Native.RECT _dragRect;
    private Native.POINT _dragStart;
    private double _liveOffsetX = double.NaN;
    private double _liveOffsetY;

    private bool _disposed;

    public CalendarHost(DataStore store, Settings settings)
    {
        _store = store;
        _settings = settings;
        ViewModel = new CalendarViewModel(store);
        _relayoutDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _relayoutDebounce.Tick += (_, _) =>
        {
            _relayoutDebounce.Stop();
            Rebuild();
        };
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _settleTimer.Tick += (_, _) =>
        {
            LayoutAll();
            SyncAnchored();
            if (++_settleTicks < 6) return;
            _settleTimer.Stop();
            var monitors = ScreenUtil.GetMonitors();
            if (monitors.Count > 0 && !SameMonitors(_monitors, monitors)) Rebuild(force: true);
        };
        _store.DayChanged += OnDayChanged;
    }

    public CalendarViewModel ViewModel { get; }
    public IReadOnlyList<CalendarWindow> Windows => _windows;
    public bool IsPreviewSuppressed { get; private set; }
    public event Action? ExitRequested;

    private sealed class Anchor
    {
        public CalendarWindow Owner = null!;
        public double Dx;
        public double Dy;
    }

    // ------------------------------------------------------------------ startup

    public void Start()
    {
        CreateMessageWindow();
        HookSystemEvents();
        Rebuild();
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("MeiRi.Host")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);
    }

    /// <summary>
    /// A message-only window never receives HWND_BROADCAST messages, so monitor changes have to
    /// come from <see cref="Microsoft.Win32.SystemEvents"/> — otherwise unplugging a monitor
    /// leaves the calendars stranded on the dead monitor.
    /// </summary>
    private void HookSystemEvents()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _systemEventsHooked = true;
        }
        catch (Exception ex)
        {
            Log.Write("system events hook failed: " + ex.Message);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        ScheduleRelayout();
        // a resolution change usually arrives in bursts; check again once things settle
        var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        once.Tick += (_, _) =>
        {
            once.Stop();
            ScheduleRelayout();
        };
        once.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
            case 0x02E0: // WM_DPICHANGED
                ScheduleRelayout();
                break;
            case Native.WM_SETTINGCHANGE:
                if (lParam != IntPtr.Zero)
                {
                    var area = Marshal.PtrToStringUni(lParam);
                    if (area is "WorkingArea" or "Display") ScheduleRelayout();
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void ScheduleRelayout()
    {
        _relayoutDebounce.Stop();
        _relayoutDebounce.Start();
    }

    private static bool SameMonitors(List<ScreenInfo> a, List<ScreenInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!SameRect(a[i].Bounds, b[i].Bounds)) return false;
            if (!SameRect(a[i].Work, b[i].Work)) return false;
        }
        return true;
    }

    private static bool SameRect(Native.RECT a, Native.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void Rebuild(bool force = false)
    {
        var monitors = ScreenUtil.GetMonitors();
        if (monitors.Count == 0) return;

        bool changed = force || !SameMonitors(_monitors, monitors);
        if (changed) DestroyWindows();

        _monitors.Clear();
        _monitors.AddRange(monitors);

        while (_windows.Count < _monitors.Count)
        {
            var window = new CalendarWindow(this, ViewModel);
            window.Show();
            _windows.Add(window);
        }
        while (_windows.Count > _monitors.Count)
        {
            var window = _windows[^1];
            _windows.RemoveAt(_windows.Count - 1);
            CloseWindow(window);
        }

        if (changed) NormalizeOffsets();
        LayoutAll();
        SettleLayout();
    }

    /// <summary>Throws the widgets away and rebuilds them from the new monitor set.</summary>
    private void DestroyWindows()
    {
        DropFloating();
        foreach (var window in _windows.ToList()) CloseWindow(window);
        _windows.Clear();
        _monitors.Clear();
    }

    /// <summary>Diagnostics only: rebuild everything as if the monitor set had changed.</summary>
    internal void ForceRebuild() => Rebuild(force: true);

    private static void CloseWindow(CalendarWindow window)
    {
        try
        {
            window.Hide();
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Write("close calendar window failed: " + ex.Message);
        }
    }

    private void HideFloating()
    {
        _editorAnchor = null;
        _backlogAnchor = null;
        _previewAnchor = null;
        if (_preview is { IsVisible: true }) _preview.Hide();
        if (_editor is { IsVisible: true }) _editor.Hide();
        if (_backlog is { IsVisible: true }) _backlog.Hide();
    }

    /// <summary>
    /// Closes the floating cards and forgets them. They are owned by a calendar window, so they die
    /// with it — keeping the reference around would leave a closed window that cannot be shown
    /// again when the monitors are rebuilt.
    /// </summary>
    private void DropFloating()
    {
        HideFloating();
        try { _preview?.Close(); }
        catch (Exception ex) { Log.Write("close preview failed: " + ex.Message); }
        try { _editor?.CloseForShutdown(); }
        catch (Exception ex) { Log.Write("close editor failed: " + ex.Message); }
        try { _backlog?.CloseForShutdown(); }
        catch (Exception ex) { Log.Write("close backlog failed: " + ex.Message); }
        _preview = null;
        _editor = null;
        _backlog = null;
    }

    /// <summary>Keeps the saved offset valid for every monitor that currently exists.</summary>
    private void NormalizeOffsets()
    {
        if (!_settings.HasPosition || _windows.Count == 0) return;
        var (w, h) = WindowEffects.PhysicalSize(_windows[0]);
        int maxX = int.MaxValue, maxY = int.MaxValue;
        foreach (var m in _monitors)
        {
            maxX = Math.Min(maxX, m.Work.Width - w);
            maxY = Math.Min(maxY, m.Work.Height - h);
        }
        int ox = (int)Math.Clamp(_settings.OffsetX, 0, Math.Max(0, maxX));
        int oy = (int)Math.Clamp(_settings.OffsetY, 0, Math.Max(0, maxY));
        if (ox == (int)_settings.OffsetX && oy == (int)_settings.OffsetY) return;
        _settings.OffsetX = ox;
        _settings.OffsetY = oy;
        SettingsService.Save(_settings);
    }

    private void SettleLayout()
    {
        _settleTicks = 0;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void LayoutAll()
    {
        for (int i = 0; i < _windows.Count && i < _monitors.Count; i++)
        {
            var window = _windows[i];
            var monitor = _monitors[i];
            var (w, _) = WindowEffects.PhysicalSize(window);
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            int ox, oy;
            if (_settings.HasPosition)
            {
                ox = (int)_settings.OffsetX;
                oy = (int)_settings.OffsetY;
            }
            else
            {
                ox = monitor.Work.Width - w - (int)(48 * scale);
                oy = (int)(44 * scale);
            }
            ApplyPosition(window, monitor, ox, oy);
        }
    }

    private static void ApplyPosition(CalendarWindow window, ScreenInfo monitor, int offsetX, int offsetY)
    {
        var (w, h) = WindowEffects.PhysicalSize(window);
        int x = Math.Clamp(monitor.Work.Left + offsetX, monitor.Work.Left,
            Math.Max(monitor.Work.Left, monitor.Work.Right - w));
        int y = Math.Clamp(monitor.Work.Top + offsetY, monitor.Work.Top,
            Math.Max(monitor.Work.Top, monitor.Work.Bottom - h));
        var hwnd = window.Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ moving all calendars together

    public void BeginWindowDrag(CalendarWindow window, Native.POINT cursor)
    {
        if (_dragWindow is not null) return;
        if (!Native.GetWindowRect(window.Handle, out _dragRect)) return;
        // a pending settle pass would yank the window back to the stored position while the user is
        // dragging it, which looks like the widget fighting the pointer
        _settleTimer.Stop();
        _settleTicks = 0;
        _dragStart = cursor;
        _dragWindow = window;
        _liveOffsetX = double.NaN;
        _liveOffsetY = 0;
        HidePreviewNow();
    }

    public void DragWindowsToCursor()
    {
        if (_dragWindow is null) return;
        if (!Native.GetCursorPos(out var cursor)) return;

        var work = Native.WorkAreaOfWindow(_dragWindow.Handle);
        var (w, h) = WindowEffects.PhysicalSize(_dragWindow);
        int left = Math.Clamp(_dragRect.Left + cursor.X - _dragStart.X, work.Left,
            Math.Max(work.Left, work.Right - w));
        int top = Math.Clamp(_dragRect.Top + cursor.Y - _dragStart.Y, work.Top,
            Math.Max(work.Top, work.Bottom - h));

        _liveOffsetX = left - work.Left;
        _liveOffsetY = top - work.Top;

        for (int i = 0; i < _windows.Count && i < _monitors.Count; i++)
        {
            var (ww, hh) = WindowEffects.PhysicalSize(_windows[i]);
            int x = Math.Clamp(_monitors[i].Work.Left + (int)_liveOffsetX, _monitors[i].Work.Left,
                Math.Max(_monitors[i].Work.Left, _monitors[i].Work.Right - ww));
            int y = Math.Clamp(_monitors[i].Work.Top + (int)_liveOffsetY, _monitors[i].Work.Top,
                Math.Max(_monitors[i].Work.Top, _monitors[i].Work.Bottom - hh));
            Native.SetWindowPos(_windows[i].Handle, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        SyncAnchored();
    }

    public void EndWindowDrag(bool moved)
    {
        if (_dragWindow is null) return;
        var window = _dragWindow;
        _dragWindow = null;

        bool hasRect = Native.GetWindowRect(window.Handle, out var rect);
        // the window is the authority on whether it was moved: a drag whose release was swallowed
        // must still be committed, otherwise the widget jumps back to the stored position
        if (hasRect && (rect.Left != _dragRect.Left || rect.Top != _dragRect.Top)) moved = true;

        if (moved && hasRect)
        {
            var work = Native.WorkAreaOfWindow(window.Handle);
            _liveOffsetX = rect.Left - work.Left;
            _liveOffsetY = rect.Top - work.Top;
        }
        if (moved)
        {
            if (!double.IsNaN(_liveOffsetX))
            {
                _settings.HasPosition = true;
                _settings.OffsetX = Math.Round(_liveOffsetX);
                _settings.OffsetY = Math.Round(_liveOffsetY);
                SettingsService.Save(_settings);
            }
        }
        if (LogTimings)
            Log.Write($"endWindowDrag moved={moved} from={_dragRect.Left},{_dragRect.Top} " +
                      $"to={rect.Left},{rect.Top} saved={(int)_settings.OffsetX},{(int)_settings.OffsetY}");
        LayoutAll();
        SyncAnchored();
        SettleLayout();
    }

    // ------------------------------------------------------------------ anchored floating windows

    /// <summary>Moves the anchored windows so they keep their offset from the calendar.</summary>
    private void SyncAnchored()
    {
        ApplyAnchor(_editor, _editorAnchor);
        ApplyAnchor(_backlog, _backlogAnchor);
        ApplyAnchor(_preview, _previewAnchor);
    }

    private static void ApplyAnchor(Window? window, Anchor? anchor)
    {
        if (window is null || anchor is null || !window.IsVisible) return;
        var hwnd = anchor.Owner.Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Native.GetWindowRect(hwnd, out var ownerRect)) return;
        Native.SetWindowPos(WindowEffects.Handle(window), IntPtr.Zero,
            (int)(ownerRect.Left + anchor.Dx), (int)(ownerRect.Top + anchor.Dy), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Places a floating card and remembers its offset from the owning calendar.
    /// </summary>
    private static void PlaceAnchored(Window window, CalendarWindow owner, int cursorX, int cursorY,
        double cardMarginDip, double designWidthDip, double designHeightDip, double visibleHeightDip,
        Action<Anchor> assign, bool besideOwner = false)
        => assign(BuildAnchor(window, owner, cursorX, cursorY, cardMarginDip, designWidthDip, designHeightDip,
            visibleHeightDip, besideOwner));

    /// <summary>
    /// <paramref name="besideOwner"/> puts the card completely outside the calendar it belongs to
    /// (right of it when the monitor has room, otherwise left). The editor and the backlog are drag
    /// sources: they must not sit on top of the days they are dragged onto, or the day under the
    /// card can never be reached — the card only fades once the pointer leaves its rectangle.
    /// </summary>
    private static Anchor BuildAnchor(Window window, CalendarWindow owner, int cursorX, int cursorY,
        double cardMarginDip, double designWidthDip, double designHeightDip, double visibleHeightDip,
        bool besideOwner = false)
    {
        var work = ScreenUtil.WorkAreaAt(cursorX, cursorY);
        double scale = VisualTreeHelper.GetDpi(owner).DpiScaleX;
        if (scale <= 0) scale = 1;

        // fit the card to the monitor, in DIPs
        double maxW = (work.Width - 2 * cardMarginDip * scale) / scale;
        double maxH = (work.Height - 2 * cardMarginDip * scale) / scale;
        if (!double.IsNaN(designWidthDip)) window.Width = Math.Max(280, Math.Min(designWidthDip, maxW));
        if (!double.IsNaN(designHeightDip)) window.Height = Math.Max(200, Math.Min(designHeightDip, maxH));
        window.UpdateLayout();

        int margin = (int)Math.Round(cardMarginDip * scale);
        var (pw, ph) = WindowEffects.PhysicalSize(window);
        if (pw <= 0 || Math.Abs(pw - window.Width * scale) > 6) pw = (int)Math.Round(window.Width * scale);
        if (!double.IsNaN(window.Height) && (ph <= 0 || Math.Abs(ph - window.Height * scale) > 6))
            ph = (int)Math.Round(window.Height * scale);
        // clamp against what is actually visible, not the oversized window
        if (!double.IsNaN(visibleHeightDip) && visibleHeightDip > 0)
            ph = Math.Min(ph, (int)Math.Round(visibleHeightDip * scale) + margin * 2);

        Native.GetWindowRect(owner.Handle, out var ownerRect);

        int right = ownerRect.Right + margin;
        int left = ownerRect.Left - margin - pw;
        bool fitsRight = right + pw <= work.Right;
        bool fitsLeft = left >= work.Left;

        int x;
        if (besideOwner && (fitsRight || fitsLeft))
        {
            x = fitsRight ? right : left;
        }
        else
        {
            x = cursorX - pw + margin;
            // no room on the left? mirror the card to the right of the cursor so it never swallows
            // the calendar it belongs to
            if (x < work.Left) x = cursorX - margin;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - pw));

        // vertically stay next to the day that was clicked, but keep the whole card on screen
        int y = besideOwner ? cursorY - (int)Math.Round(ph * 0.22) : cursorY - margin;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - ph));

        WindowEffects.SetPhysicalPosition(window, x, y);
        return new Anchor { Owner = owner, Dx = x - ownerRect.Left, Dy = y - ownerRect.Top };
    }

    // ------------------------------------------------------------------ hit testing helpers

    public DayCell? DayCellAt(int screenX, int screenY)
    {
        foreach (var window in _windows)
        {
            if (!window.IsVisible) continue;
            if (!WindowEffects.PointInside(window, screenX, screenY)) continue;
            var local = window.PointFromScreen(new Point(screenX, screenY));
            if (window.InputHitTest(local) is not DependencyObject hit) return null;
            var el = Visuals.AncestorWithTag<DayCell>(hit, out var cell);
            return el is not null ? cell : null;
        }
        return null;
    }

    public TaskBoard? BoardAt(int screenX, int screenY, TaskBoard? exclude)
    {
        if (_editor is { IsVisible: true } && !ReferenceEquals(_editor.Board, exclude)
            && WindowEffects.PointInside(_editor, screenX, screenY)) return _editor.Board;
        if (_backlog is { IsVisible: true } && !ReferenceEquals(_backlog.Board, exclude)
            && WindowEffects.PointInside(_backlog, screenX, screenY)) return _backlog.Board;
        return null;
    }

    /// <summary>
    /// Fades an anchored board window while a task is dragged outside of it, so the calendar
    /// underneath becomes visible and droppable. Only the opacity changes: the drag itself keeps
    /// the mouse captured and resolves targets by geometry.
    /// </summary>
    public void SetBoardDimmed(TaskBoard board, bool dimmed)
    {
        Window? window = null;
        if (_editor is { IsVisible: true } && ReferenceEquals(_editor.Board, board)) window = _editor;
        else if (_backlog is { IsVisible: true } && ReferenceEquals(_backlog.Board, board)) window = _backlog;
        if (window is null) return;
        double target = dimmed ? 0.14 : 1.0;
        if (Math.Abs(window.Opacity - target) < 0.01) return;
        window.Opacity = target;
    }

    public void RefreshBoard(DayData bucket)
    {
        if (bucket.IsBacklog)
        {
            if (_backlog is { IsVisible: true }) _backlog.Board.OnExternalChange();
            return;
        }
        if (_editor is { IsVisible: true } && _editor.Board.Data.Date == bucket.Date)
            _editor.Board.OnExternalChange();
    }

    // ------------------------------------------------------------------ preview

    private void OnDayChanged(object? sender, DateOnly date)
    {
        ViewModel.RefreshDate(date);
        if (_preview is null || !_preview.IsVisible || _preview.DateOfDay != date) return;
        var day = _store.GetExisting(date);
        if (day is null || (day.FreeItems.Count == 0 && day.Categories.All(c => c.Items.Count == 0)))
        {
            HidePreviewNow();
            return;
        }
        _preview.ShowDay(date, day);
        _preview.UpdateLayout();
        _preview.UpdateOverflow();
        ApplyAnchor(_preview, _previewAnchor);
    }

    /// <summary>Dev switch: log the cost of each preview stage.</summary>
    public static bool LogPreviewTimings;

    public void ShowPreview(DayCell cell, CalendarWindow owner)
    {
        if (_settings.ShowPreview is false) return;
        var day = _store.GetExisting(cell.Date);
        if (day is null || (day.FreeItems.Count == 0 && day.Categories.All(c => c.Items.Count == 0)))
        {
            HidePreviewNow();
            return;
        }
        // already showing exactly this day in exactly this place: nothing to do
        if (_preview is { IsVisible: true } && _preview.DateOfDay == cell.Date && _previewAnchor is not null
            && ReferenceEquals(_previewAnchor.Owner, owner)) return;

        var watch = LogPreviewTimings ? System.Diagnostics.Stopwatch.StartNew() : null;
        IsPreviewSuppressed = true;
        try
        {
            _preview ??= new DayPreviewWindow();
            Adopt(_preview, owner);
            bool fresh = !_preview.IsVisible;
            _preview.ShowDay(cell.Date, day);
            long t1 = watch?.ElapsedMilliseconds ?? 0;
            if (fresh) _preview.Show();
            _preview.UpdateLayout();
            _preview.UpdateOverflow();
            long t2 = watch?.ElapsedMilliseconds ?? 0;
            if (!Native.GetCursorPos(out var cursor)) return;
            PlaceAnchored(_preview, owner, cursor.X, cursor.Y, PreviewCardMargin, 304, double.NaN, _preview.VisibleHeightDip, a => _previewAnchor = a);
            // the preview is owned by the calendar as well, so showing it puts it above an already
            // open day card; it belongs directly above the calendar, below those cards
            WindowEffects.PlaceRightAbove(_preview, owner);
            long t3 = watch?.ElapsedMilliseconds ?? 0;
            if (watch is not null)
                Log.Write($"preview stages: day={t1} show+layout={t2 - t1} place={t3 - t2} total={t3}");
        }
        finally
        {
            IsPreviewSuppressed = false;
        }
    }

    public void HidePreviewNow()
    {
        if (_preview is { IsVisible: true }) _preview.Hide();
        _previewAnchor = null;
    }

    // ------------------------------------------------------------------ day editor / backlog

    /// <summary>
    /// Opens the day editor for <paramref name="date"/>. Clicking the day that is already open
    /// closes it again, and the backlog (which is the other anchored card) is always closed first.
    /// </summary>
    public void OpenDay(DateOnly date, CalendarWindow? source, bool toggle = true)
    {
        if (toggle && _editor is { IsVisible: true } && _editor.CurrentDate == date)
        {
            CloseEditor();
            return;
        }
        CloseBacklog();
        HidePreviewNow();
        var anchor = source ?? _windows.FirstOrDefault();
        if (anchor is null) return;

        bool wasVisible = _editor is { IsVisible: true };
        var watch = LogTimings ? System.Diagnostics.Stopwatch.StartNew() : null;
        long ctor = 0;
        if (_editor is null)
        {
            _editor = new DayEditorWindow(this, _store);
            ctor = watch?.ElapsedMilliseconds ?? 0;
        }
        Adopt(_editor, anchor);
        if (!wasVisible) _editor.Show();
        long t1 = watch?.ElapsedMilliseconds ?? 0;
        if (watch is not null && ctor > 0)
            Log.Write($"openDay ctor={ctor} show={t1 - ctor}");

        _editor.SetDay(date);
        long t2 = watch?.ElapsedMilliseconds ?? 0;
        // switching the day on an already visible card must not re-place it (that costs a layout
        // pass plus a window move, and the card is supposed to stay put while browsing dates)
        if (wasVisible && _editorAnchor is not null && ReferenceEquals(_editorAnchor.Owner, anchor))
            ApplyAnchor(_editor, _editorAnchor);
        else
            PlaceAnchored(_editor, anchor, CursorX(anchor), CursorY(anchor), EditorCardMargin, 452, 640,
                double.NaN, a => _editorAnchor = a, besideOwner: true);
        long t3 = watch?.ElapsedMilliseconds ?? 0;
        _editor.Activate();
        _editor.FocusInput();
        long t4 = watch?.ElapsedMilliseconds ?? 0;
        if (watch is not null)
            Log.Write($"openDay stages create+show={t1} setDay={t2 - t1} place={t3 - t2} " +
                      $"activate={t4 - t3}");
    }

    /// <summary>Dev switch: log the cost of each stage when a card is opened.</summary>
    public static bool LogTimings;

    /// <summary>
    /// Builds one throw-away card of every kind off screen and closes it again. The first
    /// Show() of a window type costs a few hundred milliseconds (template warm-up plus the first
    /// layered surface); doing it while the user is not waiting makes the first click instant.
    /// </summary>
    public void WarmUp()
    {
        if (_warmed) return;
        _warmed = true;
        try
        {
            Warm(new DayEditorWindow(this, _store));
            Warm(new BacklogWindow(this, _store));
        }
        catch (Exception ex)
        {
            Log.Write("warmup failed: " + ex);
        }
    }

    private static void Warm(Window window)
    {
        try
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();
            window.UpdateLayout();
        }
        catch (Exception ex)
        {
            Log.Write("warmup window failed: " + ex.Message);
        }
        finally
        {
            switch (window)
            {
                case DayEditorWindow editor:
                    editor.CloseForShutdown();
                    break;
                case BacklogWindow backlog:
                    backlog.CloseForShutdown();
                    break;
                default:
                    window.Close();
                    break;
            }
        }
    }

    private bool _warmed;

    /// <summary>Opens the backlog; clicking 待定 again closes it, and the day editor gives way.</summary>
    public void OpenBacklog(CalendarWindow? source, bool toggle = true)
    {
        if (toggle && _backlog is { IsVisible: true })
        {
            CloseBacklog();
            return;
        }
        CloseEditor();
        HidePreviewNow();
        var anchor = source ?? _windows.FirstOrDefault();
        if (anchor is null) return;

        _backlog ??= new BacklogWindow(this, _store);
        Adopt(_backlog, anchor);
        if (!_backlog.IsVisible) _backlog.Show();

        PlaceAnchored(_backlog, anchor, CursorX(anchor), CursorY(anchor), EditorCardMargin, 452, 580, double.NaN, a => _backlogAnchor = a, besideOwner: true);
        _backlog.Activate();
        _backlog.FocusInput();
    }

    public void CloseEditor()
    {
        if (_editor is { IsVisible: true }) _editor.Hide();
        _editorAnchor = null;
    }

    public void CloseBacklog()
    {
        if (_backlog is { IsVisible: true }) _backlog.Hide();
        _backlogAnchor = null;
    }

    public bool IsEditorOpen(DateOnly date) => _editor is { IsVisible: true } && _editor.CurrentDate == date;

    public bool IsBacklogOpen => _backlog is { IsVisible: true };

    /// <summary>Diagnostics: the hover preview window, so a probe can check its ex-style.</summary>
    internal IntPtr PreviewHandle => _preview is null ? IntPtr.Zero : WindowEffects.Handle(_preview);

    /// <summary>Diagnostics: the hover preview card, so the UI selftest can read what it renders.</summary>
    internal DayPreviewWindow? PreviewWindow => _preview;

    /// <summary>Diagnostics: the open day-editor card, used by the drag/drop selftest.</summary>
    internal DayEditorWindow? EditorWindow => _editor;

    /// <summary>The click position when the pointer is on the calendar, otherwise a point
    /// inside the calendar header (used by menu items and the tray hand-off).</summary>
    private static int CursorX(CalendarWindow anchor)
    {
        if (Native.GetCursorPos(out var c) && WindowEffects.PointInside(anchor, c.X, c.Y)) return c.X;
        Native.GetWindowRect(anchor.Handle, out var r);
        return r.Left + r.Width / 2;
    }

    private static int CursorY(CalendarWindow anchor)
    {
        if (Native.GetCursorPos(out var c) && WindowEffects.PointInside(anchor, c.X, c.Y)) return c.Y;
        Native.GetWindowRect(anchor.Handle, out var r);
        return r.Top + 60;
    }

    /// <summary>
    /// Attaches a floating card to the calendar it belongs to. An owned window is always drawn
    /// above its owner, so clicking the calendar can never push the card behind it — the card is
    /// above the calendar no matter which of the two was clicked last.
    /// </summary>
    private static void Adopt(Window window, CalendarWindow owner)
    {
        if (ReferenceEquals(window.Owner, owner)) return;
        try
        {
            window.Owner = owner;
            if (LogTimings)
                Log.Write($"adopt {window.GetType().Name} own=" +
                          $"0x{Native.GetWindow(WindowEffects.Handle(window), Native.GW_OWNER).ToInt64():X} " +
                          $"cal=0x{owner.Handle.ToInt64():X}");
        }
        catch (Exception ex)
        {
            Log.Write("adopt failed: " + ex.Message);
        }
    }

    /// <summary>Diagnostics: every widget rectangle plus its index in the top-level z-order.</summary>
    public string DescribeLayout()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _windows.Count; i++)
        {
            Append(sb, $"calendar{i}", _windows[i].Handle, _windows[i].IsVisible);
        }
        Append(sb, "editor", _editor is null ? IntPtr.Zero : WindowEffects.Handle(_editor),
            _editor?.IsVisible == true);
        Append(sb, "backlog", _backlog is null ? IntPtr.Zero : WindowEffects.Handle(_backlog),
            _backlog?.IsVisible == true);
        Append(sb, "preview", _preview is null ? IntPtr.Zero : WindowEffects.Handle(_preview),
            _preview?.IsVisible == true);
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, string name, IntPtr hwnd, bool visible)
    {
        if (hwnd == IntPtr.Zero)
        {
            sb.Append(name).Append("=none; ");
            return;
        }
        Native.GetWindowRect(hwnd, out var r);
        sb.Append(name)
            .Append(visible ? "" : "(hidden)")
            .Append(" [").Append(r.Left).Append(',').Append(r.Top).Append(' ')
            .Append(r.Width).Append('x').Append(r.Height).Append("] z=")
            .Append(ZIndex(hwnd))
            .Append("; ");
    }

    /// <summary>0 = topmost window on the desktop. Used only for diagnostics.</summary>
    private static int ZIndex(IntPtr hwnd)
    {
        var h = Native.GetTopWindow(IntPtr.Zero);
        int index = 0;
        while (h != IntPtr.Zero && index < 600)
        {
            if (h == hwnd) return index;
            h = Native.GetWindow(h, Native.GW_HWNDNEXT);
            index++;
        }
        return -1;
    }

    public void OpenToday() => OpenDay(DateOnly.FromDateTime(DateTime.Today), _windows.FirstOrDefault(), toggle: false);

    // ------------------------------------------------------------------ menu

    public void ShowMenu(Window owner, FrameworkElement target)
    {
        var entries = new List<MenuEntry>
        {
            MenuFlyout.Item("打开今天", "\uE787", OpenToday),
            MenuFlyout.Item("待定区", "\uE8A5", () => OpenBacklog(_windows.FirstOrDefault(), toggle: false))
        };
        bool notes = App.Instance.Settings.QuickNotes;
        if (notes) entries.Add(MenuFlyout.Item("新建便签", "\uE70F", () => App.Instance.Notes.OpenAtCursor()));
        entries.Add(MenuFlyout.Separator());
        entries.Add(MenuFlyout.Toggle("开机自启动", "\uE713", AutoStartService.IsRegistered(),
            on => App.Instance.SetAutoStart(on)));
        entries.Add(MenuFlyout.Toggle("快捷便签", "\uE70F", notes, on => App.Instance.SetQuickNotes(on)));
        entries.Add(MenuFlyout.Separator());
        entries.Add(MenuFlyout.Item("关于「每日」", "\uE946", () => MessageBox.Show(owner,
            "每日 1.0\n\n桌面日历 · 每日日程 · 随手便签\n\n• 点击日期编辑当日日程，悬停预览\n" +
            "• 待定区：先记下来，之后再拖到某一天\n• 拖动日历标题可调整位置\n" +
            "• 日历和普通窗口一样，点一下就置顶；编辑窗/待定区始终在日历之上\n" +
            "• 快捷便签可以关掉：菜单 → 快捷便签\n\n数据保存在：" + AppPaths.DataDir,
            "关于每日", MessageBoxButton.OK, MessageBoxImage.Information)));
        entries.Add(MenuFlyout.Item("退出", "\uE7E8", () => ExitRequested?.Invoke(), danger: true));
        MenuFlyout.Show(target, PlacementMode.Bottom, entries.ToArray());
    }

    // ------------------------------------------------------------------ dispose

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.DayChanged -= OnDayChanged;
        _relayoutDebounce.Stop();
        _settleTimer.Stop();
        if (_systemEventsHooked)
        {
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }
            catch { }
            _systemEventsHooked = false;
        }
        _messageSource?.RemoveHook(WndProc);
        _messageSource?.Dispose();
        _messageSource = null;
        try
        {
            _editor?.CloseForShutdown();
        }
        catch { }
        try
        {
            _backlog?.CloseForShutdown();
        }
        catch { }
        _preview?.Close();
        foreach (var window in _windows.ToList()) CloseWindow(window);
        _windows.Clear();
    }
}
