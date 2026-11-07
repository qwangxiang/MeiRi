using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;
using MeiRi.ViewModels;

namespace MeiRi.Views;

public partial class CalendarWindow : Window
{
    private readonly CalendarHost _host;
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _hideTimer;
    private DayCell? _hover;
    private bool _headerDown;
    private bool _headerDragging;
    private bool _dragFrameHooked;
    private Point _headerStart;

    public CalendarWindow(CalendarHost host, CalendarViewModel viewModel)
    {
        _host = host;
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) =>
        {
            // A normal window now: clicking it raises it, another window covers it again. It must
            // stay activatable, otherwise a click could never bring it to the front; the tool
            // window flag is only there to keep it out of the taskbar and out of Alt+Tab.
            WindowEffects.MakeToolWindow(this, noActivate: false);
        };
        StateChanged += OnStateChanged;

        _showTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(150) };
        _showTimer.Tick += (_, _) =>
        {
            _showTimer.Stop();
            if (_hover is not null) _host.ShowPreview(_hover, this);
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_hover is null) _host.HidePreviewNow();
        };
    }

    public IntPtr Handle => WindowEffects.Handle(this);

    /// <summary>Anchor for the flyout menu (used by the diagnostics probe).</summary>
    internal FrameworkElement MenuAnchor => MenuButton;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        // Win+D / show desktop must not hide the widget
        WindowState = WindowState.Normal;
    }

    // ------------------------------------------------------------------ cells

    private void Cell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not DayCell cell) return;
        _hideTimer.Stop();
        _hover = cell;
        _showTimer.Stop();
        if (_host.IsPreviewSuppressed) return;
        _showTimer.Start();
    }

    private void Cell_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(_hover, (sender as FrameworkElement)?.Tag)) return;
        _hover = null;
        _showTimer.Stop();
        _hideTimer.Start();
    }

    private void Cell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not DayCell cell) return;
        _hideTimer.Stop();
        _showTimer.Stop();
        _hover = null;
        _host.HidePreviewNow();
        _host.OpenDay(cell.Date, this);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ header drag

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInsideButton(e.OriginalSource)) return;
        _headerDown = true;
        _headerDragging = false;
        _headerStart = e.GetPosition(this);
        Native.GetCursorPos(out var cursor);
        if (CalendarHost.LogTimings)
            Log.Write($"hdr down at {_headerStart.X:F0},{_headerStart.Y:F0} " +
                      $"src={e.OriginalSource?.GetType().Name} cursor={cursor.X},{cursor.Y}");
        _host.BeginWindowDrag(this, cursor);
        Header.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_headerDown)
        {
            return;
        }
        // A move message can arrive after the button-up was already processed (the pointer moved a
        // pixel while the click was being released), so the button state says "released" even
        // though the up event has not been routed yet. Ending the drag as "not moved" here used to
        // throw the whole drag away and snap the widget back to its stored position, so the real
        // drag state decides instead.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndHeaderDrag(moved: _headerDragging);
            return;
        }
        var p = e.GetPosition(this);
        if (!_headerDragging)
        {
            if (Math.Abs(p.X - _headerStart.X) + Math.Abs(p.Y - _headerStart.Y) < 3) return;
            _headerDragging = true;
            if (CalendarHost.LogTimings) Log.Write($"hdr drag starts at {p.X:F0},{p.Y:F0}");
            _host.HidePreviewNow();
            if (!_dragFrameHooked)
            {
                _dragFrameHooked = true;
                CompositionTarget.Rendering += OnDragFrame;
            }
        }
        // frame-driven: one layout update per composited frame instead of per mouse message
        _host.DragWindowsToCursor();
    }

    private void OnDragFrame(object? sender, EventArgs e)
    {
        if (!_headerDragging) return;
        _host.DragWindowsToCursor();
    }

    private void StopDragFrames()
    {
        if (!_dragFrameHooked) return;
        _dragFrameHooked = false;
        CompositionTarget.Rendering -= OnDragFrame;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndHeaderDrag(_headerDragging);

    private void EndHeaderDrag(bool moved)
    {
        if (!_headerDown) return;
        _headerDown = false;
        bool dragged = moved || _headerDragging;
        _headerDragging = false;
        StopDragFrames();
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        if (CalendarHost.LogTimings) Log.Write($"hdr up moved={moved} dragged={dragged}");
        _host.EndWindowDrag(dragged);
    }

    // ------------------------------------------------------------------ commands

    private void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel vm) vm.Shift(-1);
        _host.HidePreviewNow();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel vm) vm.Shift(1);
        _host.HidePreviewNow();
    }

    private void GoToday_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel vm) vm.GoToday();
        _host.HidePreviewNow();
    }

    private void Menu_Click(object sender, RoutedEventArgs e) => _host.ShowMenu(this, MenuButton);

    private void Backlog_Click(object sender, RoutedEventArgs e)
    {
        _host.OpenBacklog(this);
        e.Handled = true;
    }

    private void Card_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _host.ShowMenu(this, Card);
        e.Handled = true;
    }

    private static bool IsInsideButton(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is ButtonBase) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
