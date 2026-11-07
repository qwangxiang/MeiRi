using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;

namespace MeiRi.Views;

public partial class QuickNoteWindow : Window
{
    private readonly QuickNoteManager _manager;
    private readonly WindowMover _mover = new();
    private readonly DispatcherTimer _titleTimer;
    private bool _headerDown;
    private bool _dragging;
    private Point _headerStart;
    private QuickNoteWindow? _mergeTarget;

    private bool _frameHooked;
    private int _lastFrameTick;
    private int _frameCount;

    public QuickNoteWindow(QuickNoteManager manager, QuickNoteGroup group)
    {
        _manager = manager;
        Group = group;
        InitializeComponent();
        DataContext = group;

        // the window title ends up in the taskbar caption; refreshing it on every keystroke makes
        // some IMEs restart their composition context, so it is debounced instead
        _titleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer.Stop();
            Group.Selected?.NotifyTitleChanged();
            UpdateTitle();
        };

        SourceInitialized += (_, _) => UpdateTitle();
        group.Notes.CollectionChanged += OnNotesChanged;
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickNoteGroup.Selected)) UpdateTitle();
        };
        Closed += (_, _) =>
        {
            _titleTimer.Stop();
            _manager.NotifyClosed(this);
        };
        Activated += (_, _) => WindowEffects.RaiseWithoutActivation(this);
        StateChanged += OnStateChanged;
        AttachClipboard();
        AttachCounter();
        RefreshTabs();
        UpdateTitle();
    }

    public QuickNoteGroup Group { get; }

    // ---- accessors used by the dev diagnostics (Core/DevLab.cs) ----
    internal TextBox EditorBox => Editor;
    internal FrameworkElement CardElement => Card;
    internal FrameworkElement FlyoutAnchor => CopyButton;
    internal string CountLabel => CharCountText.Text;
    internal bool SuppressTitle { get; set; }

    private void OnNotesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshTabs();
        UpdateTitle();
    }

    public void RefreshTabs()
    {
        if (Group.Selected is null && Group.Notes.Count > 0) Group.Selected = Group.Notes[0];
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollSelectedTabIntoView));
    }

    private void ScrollSelectedTabIntoView()
    {
        if (Group.Selected is null) return;
        Tabs.UpdateLayout();
        if (Tabs.ItemContainerGenerator.ContainerFromItem(Group.Selected) is FrameworkElement container)
            container.BringIntoView();
    }

    private void UpdateTitle()
    {
        if (SuppressTitle) return;
        var note = Group.Selected;
        var label = note?.Title;
        if (string.IsNullOrWhiteSpace(label) || label == "空便签") label = "新便签";
        Title = Group.Notes.Count > 1 ? $"每日 · {label}（{Group.Notes.Count}）" : $"每日 · {label}";
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        _titleTimer.Stop();
        _titleTimer.Start();
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>
    /// Ctrl+X / Ctrl+C / Ctrl+V are handled by <see cref="ClipboardEdit"/> instead of by the text
    /// box: WPF's own cut copies the selection through OLE and only then deletes it, so a busy
    /// clipboard (clipboard managers hold it open all the time) leaves the text on screen while it
    /// already sits in the clipboard — the intermittent "cut did nothing" bug.
    /// </summary>
    private void AttachClipboard()
    {
        ClipboardEdit.SetNotify(Editor, ShowToast);
        ClipboardEdit.SetEnabled(Editor, true);
    }

    // ------------------------------------------------------------------ character count

    private int _countedTotal = -1;
    private int _countedSelected = -1;

    /// <summary>
    /// The gray counter in the bottom left corner: how many characters the note holds, and how many
    /// of them are selected. <see cref="TextBoxBase.SelectionChanged"/> is a routed event, so typed
    /// text, Shift+arrows, Ctrl+A and dragging a selection all arrive here.
    /// </summary>
    private void AttachCounter()
    {
        Editor.TextChanged += (_, _) => UpdateCount();
        Editor.SelectionChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    private void UpdateCount()
    {
        string text = Editor.Text;
        int total = CountChars(text);
        int selected = Editor.SelectionLength > 0
            ? CountChars(text, Editor.SelectionStart, Editor.SelectionLength)
            : 0;
        if (total == _countedTotal && selected == _countedSelected) return;
        _countedTotal = total;
        _countedSelected = selected;
        CharCountText.Text = selected > 0
            ? $"已选 {selected} 字 · 共 {total} 字"
            : $"共 {total} 字";
    }

    /// <summary>Characters, not UTF-16 units: a line break is not something the user typed as a 字.</summary>
    private static int CountChars(string text)
    {
        int count = 0;
        foreach (char c in text)
            if (c != '\r' && c != '\n') count++;
        return count;
    }

    private static int CountChars(string text, int start, int length)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(from + Math.Max(0, length), text.Length);
        int count = 0;
        for (int i = from; i < to; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n') count++;
        }
        return count;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Normal) return;
        WindowEffects.RaiseWithoutActivation(this);
        FocusEditor();
    }

    /// <summary>Brings the window to the top of the normal band without stealing the keyboard.</summary>
    public void BringToFront()
    {
        WindowEffects.ForceForeground(this);
        WindowEffects.RaiseWithoutActivation(this);
    }

    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            WindowEffects.ForceForeground(this);
            Editor.Focus();
            Keyboard.Focus(Editor);
            Editor.CaretIndex = Editor.Text.Length;
        }), DispatcherPriority.Input);
    }

    public void SetMergeHighlight(bool on)
        => MergeHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    public void ShowToast(string text)
    {
        Toast.Text = text;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        ToastHost.BeginAnimation(OpacityProperty, anim);
    }

    // ------------------------------------------------------------------ buttons

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = Group.Selected?.Text ?? string.Empty;
        if (text.Length == 0)
        {
            ShowToast("没有内容");
            return;
        }
        if (!ClipText.TrySet(text, out var error))
        {
            Log.Write("note copy all failed: " + error);
            ShowToast("剪切板被占用，请稍后再试");
            return;
        }
        ShowToast("已复制");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Group.Selected is null) return;
        Editor.IsUndoEnabled = false;
        Editor.Clear();
        Editor.IsUndoEnabled = true;
        Group.Selected.Text = string.Empty;
        Editor.Focus();
        ShowToast("已清空");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseActiveNote();

    private void CloseActiveNote()
    {
        var note = Group.Selected;
        if (note is null)
        {
            Close();
            return;
        }
        CloseNote(note);
    }

    /// <summary>Removes one column of this window; the window itself closes with its last column.</summary>
    public void CloseNote(QuickNote note)
    {
        int index = Group.IndexOf(note);
        Group.Notes.Remove(note);
        if (Group.Notes.Count == 0)
        {
            Close();
            return;
        }
        Group.Selected = Group.Notes[Math.Clamp(index, 0, Group.Notes.Count - 1)];
        RefreshTabs();
        FocusEditor();
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        AddNote();
        e.Handled = true;
    }

    /// <summary>Appends a fresh column to this window and starts typing in it.</summary>
    public void AddNote()
    {
        var note = new QuickNote();
        note.Text = string.Empty;
        Group.Notes.Add(note);
        Group.Selected = note;
        RefreshTabs();
        UpdateTitle();
        Editor.Focus();
        Keyboard.Focus(Editor);
        Editor.CaretIndex = 0;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Clamp(Width + e.HorizontalChange, 300, 900);
        Height = Math.Clamp(Height + e.VerticalChange, 190, 820);
    }

    // ------------------------------------------------------------------ tabs

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickNote note) return;
        BeginHeaderDrag(e);
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            _manager.Detach(this, note);
            return;
        }
        if (!ReferenceEquals(Group.Selected, note))
        {
            Group.Selected?.NotifyTitleChanged();
            Group.Selected = note;
            UpdateTitle();
        }
        e.Handled = true;
    }

    private void Tab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is QuickNote note) CloseNote(note);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ window drag / merge

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginHeaderDrag(e);

    private void BeginHeaderDrag(MouseEventArgs e)
    {
        if (_headerDown || IsInsideButton(e.OriginalSource)) return;
        _headerDown = true;
        _dragging = false;
        _frameCount = 0;
        _headerStart = e.GetPosition(this);
        _mover.Begin(this);
        Header.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_headerDown) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndHeaderDrag();
            return;
        }
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _headerStart.X) + Math.Abs(p.Y - _headerStart.Y) < 4) return;
            _dragging = true;
        }
        RequestDragFrame();
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndHeaderDrag();

    /// <summary>
    /// A pointer at 1 kHz would move the layered window a thousand times a second; the move is
    /// driven by the compositor instead, one update per frame.
    /// </summary>
    private void RequestDragFrame()
    {
        if (_frameHooked) return;
        _frameHooked = true;
        _lastFrameTick = 0;
        CompositionTarget.Rendering += OnDragFrame;
    }

    private void OnDragFrame(object? sender, EventArgs e)
    {
        if (!_headerDown)
        {
            StopDragFrames();
            return;
        }
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndHeaderDrag();
            return;
        }
        int now = Environment.TickCount;
        if (now - _lastFrameTick < 8) return;
        _lastFrameTick = now;
        _mover.Update();
        // hit-testing every other window is the expensive part, so it runs at a third of the rate
        if (++_frameCount % 3 == 0) UpdateMergeTarget();
    }

    private void StopDragFrames()
    {
        if (!_frameHooked) return;
        _frameHooked = false;
        CompositionTarget.Rendering -= OnDragFrame;
    }

    private void EndHeaderDrag()
    {
        if (!_headerDown) return;
        _headerDown = false;
        bool dragged = _dragging;
        _dragging = false;
        StopDragFrames();
        _mover.End();
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();

        if (dragged) UpdateMergeTarget();
        var target = _mergeTarget;
        SetMergeTarget(null);

        if (dragged && target is not null && !ReferenceEquals(target, this))
        {
            _manager.Merge(this, target);
        }
    }

    private void UpdateMergeTarget()
    {
        if (!_dragging)
        {
            SetMergeTarget(null);
            return;
        }
        if (!Native.GetCursorPos(out var cur))
        {
            SetMergeTarget(null);
            return;
        }
        var target = _manager.WindowAt(cur.X, cur.Y, this);
        if (target is not null)
        {
            Native.GetWindowRect(WindowEffects.Handle(target), out var rect);
            // only the header of the other note is a drop zone, so the body stays usable
            if (cur.Y > rect.Top + 56) target = null;
        }
        SetMergeTarget(target);
    }

    private void SetMergeTarget(QuickNoteWindow? target)
    {
        if (ReferenceEquals(_mergeTarget, target)) return;
        _mergeTarget?.SetMergeHighlight(false);
        _mergeTarget = target;
        _mergeTarget?.SetMergeHighlight(true);
    }

    private static bool IsInsideButton(object? source) => Visuals.IsInsideInteractive(source);
}
