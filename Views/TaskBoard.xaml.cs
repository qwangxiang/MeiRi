using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Models;
using MeiRi.Services;
using MeiRi.ViewModels;

namespace MeiRi.Views;

/// <summary>
/// The task list used by both the day editor and the undated backlog: uncategorised
/// tasks on top, then category blocks, plus the add bar. Drag and drop is frame-throttled
/// and resolves its targets against geometry captured once when the drag starts, which is
/// what keeps it smooth.
/// </summary>
public partial class TaskBoard : UserControl
{
    private readonly CalendarHost _host;
    private readonly DataStore _store;

    private DayData _data;
    private Category? _target;
    private bool _categoryMode;

    private PressState? _press;
    private PressState? _catPress;
    private DragState? _drag;
    private bool _frameHooked;
    private int _lastFrameTick;

    public TaskBoard(CalendarHost host, DataStore store)
    {
        _host = host;
        _store = store;
        _data = store.Backlog;
        InitializeComponent();
        DataContext = _data;
        UpdatePlaceholder();
        RefreshEmpty();
    }

    public DayData Data => _data;

    /// <summary>The day this board edits, or <c>null</c> when it is the backlog.</summary>
    public DateOnly? DayDate => _data.IsBacklog ? null : _data.Date;

    // ------------------------------------------------------------------ data

    public void SetData(DayData data)
    {
        if (!ReferenceEquals(_data, data))
        {
            ClearTarget();
            _data = data;
            DataContext = data;
            Scroller.ScrollToTop();
        }
        UpdatePlaceholder();
        RefreshEmpty();
    }

    private void RefreshEmpty()
    {
        EmptyHint.Visibility = _data.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _data.IsBacklog ? "待定区还是空的" : "这一天还没有安排";
    }

    /// <summary>Called when another window changed this bucket (drag from the backlog, etc).</summary>
    public void OnExternalChange() => RefreshEmpty();

    private void Notify()
    {
        _store.NotifyTarget(DayDate);
        RefreshEmpty();
    }

    public void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    // ------------------------------------------------------------------ add bar

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (Placeholder is null) return;
        _categoryMode = ModeCategory?.IsChecked == true;
        UpdatePlaceholder();
        InputBox?.Focus();
    }

    private void UpdatePlaceholder()
    {
        Placeholder.Text = _categoryMode
            ? "添加类别，按 Enter 保存"
            : _target is null
                ? _data.IsBacklog ? "添加到待定区，按 Enter 保存" : "添加日程，按 Enter 保存"
                : $"添加到「{_target.Title}」，按 Enter 保存";
        Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
        => Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddFromInput();
        }
        else if (e.Key == Key.Escape && InputBox.Text.Length > 0)
        {
            InputBox.Clear();
            e.Handled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddFromInput();

    private void AddFromInput()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        if (_categoryMode)
        {
            var cat = new Category { Title = text };
            _data.Categories.Add(cat);
            InputBox.Clear();
            Notify();
            Scroller.ScrollToEnd();
            SelectCategory(cat);
        }
        else
        {
            var item = new TodoItem { Text = text };
            if (_target is not null) _target.Items.Add(item);
            else _data.FreeItems.Add(item);
            InputBox.Clear();
            Notify();
            if (_target is null) Scroller.ScrollToTop(); else Scroller.ScrollToEnd();
        }
        FocusInput();
    }

    // ------------------------------------------------------------------ categories

    private void SelectCategory(Category? cat)
    {
        if (cat is not null)
        {
            if (ReferenceEquals(_target, cat))
            {
                cat.IsSelected = false;
                _target = null;
            }
            else
            {
                if (_target is not null) _target.IsSelected = false;
                _target = cat;
                cat.IsSelected = true;
            }
        }
        else
        {
            if (_target is not null) _target.IsSelected = false;
            _target = null;
        }
        UpdatePlaceholder();
        FocusInput();
    }

    private void ClearTarget()
    {
        if (_target is not null) _target.IsSelected = false;
        _target = null;
        UpdatePlaceholder();
    }

    private void CatAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Category cat) return;
        ModeTodo.IsChecked = true;
        if (!ReferenceEquals(_target, cat)) SelectCategory(cat);
        FocusInput();
        e.Handled = true;
    }

    private void CatMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not Category cat) return;
        MenuFlyout.Show(el, System.Windows.Controls.Primitives.PlacementMode.Bottom,
            MenuFlyout.Item("重命名", "\uE70F", () => cat.IsEditing = true),
            MenuFlyout.Item("在此类别下添加日程", "\uE710", () =>
            {
                ModeTodo.IsChecked = true;
                SelectCategory(cat);
            }),
            MenuFlyout.Separator(),
            MenuFlyout.Item("删除类别", "\uE74D", () => DeleteCategory(cat), danger: true));
        e.Handled = true;
    }

    private void DeleteCategory(Category cat)
    {
        var count = cat.Items.Count;
        if (count > 0)
        {
            var result = MessageBox.Show(Window.GetWindow(this),
                $"删除类别「{cat.Title}」以及其中的 {count} 项日程？", "每日",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;
        }
        if (ReferenceEquals(_target, cat)) ClearTarget();
        _data.Categories.Remove(cat);
        Notify();
    }

    private void CatEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Visibility != Visibility.Visible) return;
        tb.Tag = (tb.DataContext as Category)?.Title;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }));
    }

    private void CatEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not Category cat) return;
        if (e.Key == Key.Enter)
        {
            // Ctrl+Enter is a line break, Enter alone saves — the same in category, task and note
            if (IsPlainEnter())
            {
                CommitCategoryTitle(tb, cat);
            }
            else
            {
                InsertLineBreak(tb);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (tb.Tag is string original) cat.Title = original;
            cat.IsEditing = false;
            e.Handled = true;
        }
    }

    private void CatEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not Category cat) return;
        if (!cat.IsEditing) return;
        CommitCategoryTitle(tb, cat);
    }

    private void CommitCategoryTitle(TextBox tb, Category cat)
    {
        var text = (tb.Text ?? string.Empty).Trim();
        cat.Title = text.Length == 0 ? "未命名类别" : text;
        cat.IsEditing = false;
        Notify();
    }

    // ------------------------------------------------------------------ task rows

    private void Note_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TodoItem item) return;
        if (item.IsEditingNote)
        {
            item.IsEditingNote = false;
            Notify();
        }
        else
        {
            item.IsEditingNote = true;
        }
        e.Handled = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TodoItem item) return;
        FindItemList(item)?.Remove(item);
        Notify();
        e.Handled = true;
    }

    private System.Collections.ObjectModel.ObservableCollection<TodoItem>? FindItemList(TodoItem item)
    {
        if (_data.FreeItems.Contains(item)) return _data.FreeItems;
        foreach (var c in _data.Categories)
            if (c.Items.Contains(item)) return c.Items;
        return null;
    }

    private void NoteEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (!item.IsEditingNote) return;
        tb.Tag = item.Note;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        }));
    }

    private void NoteEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (e.Key == Key.Enter)
        {
            if (IsPlainEnter())
            {
                item.IsEditingNote = false;
                Notify();
            }
            else
            {
                InsertLineBreak(tb);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (tb.Tag is string original) item.Note = original;
            item.IsEditingNote = false;
            e.Handled = true;
        }
    }

    private void NoteEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (!item.IsEditingNote) return;
        item.IsEditingNote = false;
        Notify();
    }

    // ------------------------------------------------------------------ task text (inline edit)

    private void TextEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (!item.IsEditingText) return;
        tb.Tag = item.Text;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        }));
    }

    private void TextEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (e.Key == Key.Enter)
        {
            if (IsPlainEnter())
            {
                CommitTaskText(tb, item);
            }
            else
            {
                InsertLineBreak(tb);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (tb.Tag is string original) item.Text = original;
            item.IsEditingText = false;
            e.Handled = true;
        }
    }

    /// <summary>Enter without Ctrl: saves the edit. Ctrl+Enter inserts a line break instead.</summary>
    private static bool IsPlainEnter()
        => (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    private static void InsertLineBreak(TextBox tb)
    {
        int at = tb.CaretIndex;
        tb.SelectedText = "\n";
        tb.CaretIndex = Math.Min(at + 1, tb.Text.Length);
    }

    private void TextEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TodoItem item) return;
        if (!item.IsEditingText) return;
        CommitTaskText(tb, item);
    }

    private void CommitTaskText(TextBox tb, TodoItem item)
    {
        var text = (tb.Text ?? string.Empty).Trim();
        item.Text = text.Length == 0 ? "未命名日程" : text;
        item.IsEditingText = false;
        Notify();
    }

    // ------------------------------------------------------------------ press / drag

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Visuals.IsInsideInteractive(e.OriginalSource)) return;
        if (sender is not FrameworkElement row || row.Tag is not TodoItem item) return;
        if (!Native.GetCursorPos(out var cur)) return;
        _press = new PressState
        {
            Element = row,
            Item = item,
            Category = FindCategoryOf(row),
            ScreenX = cur.X,
            ScreenY = cur.Y,
            OnTick = IsOnTick(e.OriginalSource, row)
        };
        row.CaptureMouse();
    }

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (_press is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPress();
            return;
        }
        if (_drag is not null)
        {
            RequestDragUpdate();
            return;
        }
        if (!Native.GetCursorPos(out var cur)) return;
        if (Math.Abs(cur.X - _press.ScreenX) + Math.Abs(cur.Y - _press.ScreenY) < 5) return;
        BeginDrag(_press.Item, _press.Category, _press.Element, isCategory: false);
        RequestDragUpdate();
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_press is null) return;
        var press = _press;
        _press = null;
        if (press.Element.IsMouseCaptured) press.Element.ReleaseMouseCapture();
        if (_drag is not null)
        {
            EndDrag();
            return;
        }
        if (press.Item is null) return;
        // Only the little circle ticks a task off. Clicking anywhere else on the row edits the
        // text instead: "I meant to fix the wording" must never strike the task through.
        if (press.OnTick)
        {
            press.Item.IsDone = !press.Item.IsDone;
            Notify();
            return;
        }
        press.Item.IsEditingText = true;
    }

    private void CatHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Visuals.IsInsideInteractive(e.OriginalSource)) return;
        if (sender is not FrameworkElement el || el.DataContext is not Category cat) return;
        if (e.ClickCount == 2)
        {
            cat.IsEditing = true;
            e.Handled = true;
            return;
        }
        if (!Native.GetCursorPos(out var cur)) return;
        _catPress = new PressState
        {
            Element = el,
            Category = cat,
            ScreenX = cur.X,
            ScreenY = cur.Y,
            OnText = IsOnName(e.OriginalSource, el, "CatTitle")
        };
        el.CaptureMouse();
    }

    /// <summary>
    /// The category frame — the padding around the header and the tasks inside it — is a big
    /// target for "select this category for the next task", so the small "+" is no longer the
    /// only way in. Clicks on the header keep their own meaning (rename / drag), and clicks on a
    /// task row are left to the row.
    /// </summary>
    private void CatBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _catBoxDown = false;
        if (Visuals.IsInsideInteractive(e.OriginalSource)) return;
        if (sender is not FrameworkElement box || box.Tag is not Category) return;
        if (IsInsideNamed(e.OriginalSource, box, "CatHeader", "Row")) return;
        _catBoxDown = true;
    }

    private void CatBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_catBoxDown) return;
        _catBoxDown = false;
        if (Visuals.IsInsideInteractive(e.OriginalSource)) return;
        if (sender is not FrameworkElement box || box.Tag is not Category cat) return;
        if (IsInsideNamed(e.OriginalSource, box, "CatHeader", "Row")) return;
        SelectCategory(cat);
        e.Handled = true;
    }

    private bool _catBoxDown;

    private void CatHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_catPress is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndCatPress();
            return;
        }
        if (_drag is not null)
        {
            RequestDragUpdate();
            return;
        }
        if (!Native.GetCursorPos(out var cur)) return;
        if (Math.Abs(cur.X - _catPress.ScreenX) + Math.Abs(cur.Y - _catPress.ScreenY) < 5) return;
        BeginDrag(null, _catPress.Category, _catPress.Element, isCategory: true);
        RequestDragUpdate();
    }

    private void CatHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_catPress is null) return;
        var press = _catPress;
        _catPress = null;
        if (press.Element.IsMouseCaptured) press.Element.ReleaseMouseCapture();
        if (_drag is not null)
        {
            EndDrag();
            return;
        }
        if (press.Category is null) return;
        // same as tasks: clicking the name renames it instead of selecting the category
        if (press.OnText) press.Category.IsEditing = true;
        else SelectCategory(press.Category);
    }

    private void EndPress()
    {
        if (_press is null) return;
        var el = _press.Element;
        _press = null;
        if (el.IsMouseCaptured) el.ReleaseMouseCapture();
    }

    private void EndCatPress()
    {
        if (_catPress is null) return;
        var el = _catPress.Element;
        _catPress = null;
        if (el.IsMouseCaptured) el.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------ drag engine

    private void BeginDrag(TodoItem? item, Category? category, FrameworkElement source, bool isCategory)
    {
        if (_drag is not null) return;
        _host.HidePreviewNow();
        var text = isCategory ? category?.Title : item?.Text;
        var ghost = new DragGhostWindow(string.IsNullOrWhiteSpace(text) ? "未命名" : text!, isCategory);
        _drag = new DragState
        {
            IsCategory = isCategory,
            Item = item,
            Category = category,
            Source = source,
            Ghost = ghost
        };
        CaptureGeometry();
        source.Opacity = 0.32;
        ghost.Show();
        ghost.FadeIn();
        if (Native.GetCursorPos(out var cur)) ghost.MoveTo(cur.X + 14, cur.Y + 9);
        UpdateDrag();
        RequestDragUpdate();
    }

    /// <summary>Snapshots every row/category rectangle once, so the hot path stays pure math.</summary>
    private void CaptureGeometry()
    {
        if (_drag is null) return;
        _drag.Rows.Clear();
        _drag.Cats.Clear();

        for (int i = 0; i < _data.FreeItems.Count; i++)
        {
            if (FreeHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el) continue;
            _drag.Rows.Add(new RowBox
            {
                Item = _data.FreeItems[i],
                Cat = null,
                Bounds = Visuals.BoundsIn(el, Overlay)
            });
        }

        for (int i = 0; i < _data.Categories.Count; i++)
        {
            var cat = _data.Categories[i];
            if (CategoriesHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement block) continue;
            var inner = FindInnerList(block, cat);
            _drag.Cats.Add(new CatBox
            {
                Cat = cat,
                Block = Visuals.BoundsIn(block, Overlay),
                Inner = inner is null ? Rect.Empty : Visuals.BoundsIn(inner, Overlay)
            });
            if (inner is null) continue;
            for (int j = 0; j < cat.Items.Count; j++)
            {
                if (inner.ItemContainerGenerator.ContainerFromIndex(j) is not FrameworkElement el) continue;
                _drag.Rows.Add(new RowBox
                {
                    Item = cat.Items[j],
                    Cat = cat,
                    Bounds = Visuals.BoundsIn(el, Overlay)
                });
            }
        }
    }

    /// <summary>
    /// The drag cursor is followed once per composited frame instead of once per mouse message.
    /// Pointer messages can arrive at 1 kHz; doing hit-testing and moving a layered window that
    /// often is what made the old implementation stutter.
    /// </summary>
    private void RequestDragUpdate()
    {
        if (_drag is null || _frameHooked) return;
        _frameHooked = true;
        _lastFrameTick = 0;
        CompositionTarget.Rendering += OnDragFrame;
    }

    private void OnDragFrame(object? sender, EventArgs e)
    {
        if (_drag is null)
        {
            StopDragFrames();
            return;
        }
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        int now = Environment.TickCount;
        if (now - _lastFrameTick < 8) return;
        _lastFrameTick = now;
        UpdateDrag();
    }

    private void StopDragFrames()
    {
        if (!_frameHooked) return;
        _frameHooked = false;
        CompositionTarget.Rendering -= OnDragFrame;
    }

    private void UpdateDrag()
    {
        var drag = _drag;
        if (drag is null) return;
        if (!Native.GetCursorPos(out var cur)) return;

        drag.Ghost?.MoveTo(cur.X + 14, cur.Y + 9);
        UpdateDim(cur.X, cur.Y);

        // the floating boards are always above the calendars, so they get first refusal:
        // 1) the other board window (待定区 <-> 具体某天)
        var other = _host.BoardAt(cur.X, cur.Y, this);
        if (other is not null)
        {
            SetDayTarget(null);
            HideDropLine();
            drag.Plan = null;
            SetExternalBoard(other);
            return;
        }
        SetExternalBoard(null);

        // 2) reorder inside this board
        var local = Overlay.PointFromScreen(new Point(cur.X, cur.Y));
        if (new Rect(0, 0, Overlay.ActualWidth, Overlay.ActualHeight).Contains(local))
        {
            SetDayTarget(null);
            var plan = drag.IsCategory ? ResolveCategoryDrop(local) : ResolveItemDrop(local);
            drag.Plan = plan;
            ShowDropLine(plan);
            return;
        }

        // 3) a day of any calendar window
        var cell = _host.DayCellAt(cur.X, cur.Y);
        if (cell is not null)
        {
            HideDropLine();
            drag.Plan = null;
            SetDayTarget(cell);
            return;
        }

        SetDayTarget(null);
        HideDropLine();
        drag.Plan = null;
    }

    private void SetDayTarget(DayCell? cell)
    {
        if (_drag is null || ReferenceEquals(_drag.DayTarget, cell)) return;
        if (_drag.DayTarget is not null) _drag.DayTarget.IsDropTarget = false;
        _drag.DayTarget = cell;
        if (cell is not null) cell.IsDropTarget = true;
    }

    /// <summary>
    /// While a drag leaves the editor, the editor fades almost away so the calendar underneath
    /// becomes the target (and is readable); dragging back inside brings it back. The window is
    /// only faded — the drag keeps its mouse capture, so the drop logic is unaffected.
    /// </summary>
    private void UpdateDim(int screenX, int screenY)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        bool outside = !WindowEffects.PointInside(window, screenX, screenY);
        _host.SetBoardDimmed(this, outside);
    }

    private void SetExternalBoard(TaskBoard? board)
    {
        if (_drag is null || ReferenceEquals(_drag.ExternalTarget, board)) return;
        _drag.ExternalTarget?.SetExternalHighlight(false);
        _drag.ExternalTarget = board;
        board?.SetExternalHighlight(true);
    }

    public void SetExternalHighlight(bool on)
        => DropRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private void EndDrag()
    {
        var drag = _drag;
        _drag = null;
        HideDropLine();
        if (drag is null) return;

        if (drag.DayTarget is not null)
        {
            drag.DayTarget.IsDropTarget = false;
            if (drag.IsCategory) MoveCategoryTo(GetDayBucket(drag.DayTarget.Date), drag.Category!);
            else MoveItemTo(GetDayBucket(drag.DayTarget.Date), drag.Item!, drag.Category);
        }
        else if (drag.ExternalTarget is not null)
        {
            drag.ExternalTarget.SetExternalHighlight(false);
            if (drag.IsCategory) MoveCategoryTo(drag.ExternalTarget.Data, drag.Category!);
            else MoveItemTo(drag.ExternalTarget.Data, drag.Item!, drag.Category);
        }
        else if (drag.Plan is not null)
        {
            if (drag.IsCategory) MoveCategoryToIndex(drag.Category!, drag.Plan.Index);
            else MoveItemInBoard(drag.Item!, drag.Category, drag.Plan.TargetCategory, drag.Plan.Index);
        }

        if (drag.Source is not null) drag.Source.Opacity = 1;
        drag.Ghost?.CloseAnimated();
        StopDragFrames();
        _host.SetBoardDimmed(this, false);
    }

    private DayData GetDayBucket(DateOnly date) => _store.GetOrCreate(date);

    // ------------------------------------------------------------------ drop lines

    private void ShowDropLine(DropPlan? plan)
    {
        if (_drag is null) return;
        if (plan is null || plan.Line.Width <= 0)
        {
            HideDropLine();
            return;
        }
        if (_lineVisible && _lastLine == plan.Line) return;
        _lastLine = plan.Line;
        Canvas.SetLeft(DropLine, plan.Line.Left);
        Canvas.SetTop(DropLine, plan.Line.Top);
        DropLine.Width = Math.Max(12, plan.Line.Width);
        if (!_lineVisible)
        {
            DropLine.Visibility = Visibility.Visible;
            _lineVisible = true;
        }
    }

    private void HideDropLine()
    {
        if (!_lineVisible) return;
        _lineVisible = false;
        _lastLine = Rect.Empty;
        DropLine.Visibility = Visibility.Collapsed;
    }

    private Rect _lastLine = Rect.Empty;
    private bool _lineVisible;

    private DropPlan? ResolveItemDrop(Point p)
    {
        var drag = _drag;
        if (drag is null) return null;

        foreach (var row in drag.Rows)
        {
            if (!row.Bounds.Contains(p)) continue;
            var list = row.Cat?.Items ?? _data.FreeItems;
            int index = list.IndexOf(row.Item);
            if (index < 0) index = list.Count;
            bool after = p.Y > row.Bounds.Top + row.Bounds.Height / 2;
            return new DropPlan
            {
                TargetCategory = row.Cat,
                Index = after ? index + 1 : index,
                Line = new Rect(row.Bounds.Left + 2, (after ? row.Bounds.Bottom : row.Bounds.Top) - 1,
                    Math.Max(24, row.Bounds.Width - 6), 2)
            };
        }

        foreach (var box in drag.Cats)
        {
            if (!box.Block.Contains(p)) continue;
            if (box.Inner.IsEmpty)
            {
                return new DropPlan
                {
                    TargetCategory = box.Cat,
                    Index = box.Cat.Items.Count,
                    Line = new Rect(box.Block.Left + 12, box.Block.Top + 32, Math.Max(24, box.Block.Width - 24), 2)
                };
            }
            bool above = p.Y < box.Inner.Top;
            return new DropPlan
            {
                TargetCategory = box.Cat,
                Index = above ? 0 : box.Cat.Items.Count,
                Line = new Rect(box.Block.Left + 12, above ? box.Inner.Top : box.Inner.Bottom,
                    Math.Max(24, box.Block.Width - 24), 2)
            };
        }

        // between the uncategorised block and the first category
        if (_data.Categories.Count > 0)
        {
            var firstCat = drag.Cats.FirstOrDefault();
            double freeBottom = drag.Rows.Count > 0 && _data.FreeItems.Count > 0
                ? drag.Rows[Math.Min(_data.FreeItems.Count, drag.Rows.Count) - 1].Bounds.Bottom
                : 0;
            if (firstCat is not null && p.Y < firstCat.Block.Top)
            {
                return new DropPlan
                {
                    TargetCategory = null,
                    Index = _data.FreeItems.Count,
                    Line = new Rect(firstCat.Block.Left + 12,
                        Math.Max(firstCat.Block.Top - 2, freeBottom), Math.Max(24, firstCat.Block.Width - 24), 2)
                };
            }
        }

        // anything else: append to the last block
        var listBounds = Visuals.BoundsIn(ListHost, Overlay);
        if (_data.Categories.Count > 0)
        {
            var last = _data.Categories[^1];
            var lastBox = drag.Cats.FirstOrDefault(c => ReferenceEquals(c.Cat, last));
            double y = lastBox is not null ? lastBox.Block.Bottom - 2 : listBounds.Bottom - 2;
            return new DropPlan
            {
                TargetCategory = last,
                Index = last.Items.Count,
                Line = new Rect(listBounds.Left + 12, y, Math.Max(24, listBounds.Width - 24), 2)
            };
        }
        return new DropPlan
        {
            TargetCategory = null,
            Index = _data.FreeItems.Count,
            Line = new Rect(listBounds.Left + 2, Math.Max(listBounds.Top, listBounds.Bottom - 2),
                Math.Max(24, listBounds.Width - 6), 2)
        };
    }

    private DropPlan? ResolveCategoryDrop(Point p)
    {
        var drag = _drag;
        if (drag is null || drag.Cats.Count == 0) return null;

        int insert = drag.Cats.Count;
        double y = drag.Cats[^1].Block.Bottom;
        for (int i = 0; i < drag.Cats.Count; i++)
        {
            var b = drag.Cats[i].Block;
            if (p.Y < b.Top + b.Height * 0.5)
            {
                insert = i;
                y = b.Top;
                break;
            }
        }
        var reference = drag.Cats[0].Block;
        return new DropPlan
        {
            Index = insert,
            Line = new Rect(reference.Left + 4, y, Math.Max(24, reference.Width - 8), 2)
        };
    }

    private static ItemsControl? FindInnerList(DependencyObject? root, Category cat)
    {
        if (root is null) return null;
        if (root is ItemsControl ic && ReferenceEquals(ic.Tag, cat)) return ic;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindInnerList(VisualTreeHelper.GetChild(root, i), cat);
            if (found is not null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------ moves

    private void MoveItemInBoard(TodoItem item, Category? sourceCat, Category? targetCat, int index)
    {
        var src = sourceCat?.Items ?? _data.FreeItems;
        var dst = targetCat?.Items ?? _data.FreeItems;
        int s = src.IndexOf(item);
        if (s < 0) return;
        if (ReferenceEquals(src, dst))
        {
            int t = index;
            if (t > s) t--;
            t = Math.Clamp(t, 0, src.Count - 1);
            if (t == s) return;
            src.Move(s, t);
        }
        else
        {
            src.RemoveAt(s);
            dst.Insert(Math.Clamp(index, 0, dst.Count), item);
        }
        Notify();
    }

    private void MoveCategoryToIndex(Category cat, int index)
    {
        int from = _data.Categories.IndexOf(cat);
        if (from < 0) return;
        int to = index;
        if (to > from) to--;
        to = Math.Clamp(to, 0, _data.Categories.Count - 1);
        if (to == from) return;
        _data.Categories.Move(from, to);
        Notify();
    }

    /// <summary>Moves a task into another bucket; a category with the same name is reused.</summary>
    private void MoveItemTo(DayData bucket, TodoItem item, Category? sourceCat)
    {
        if (ReferenceEquals(bucket, _data)) return;
        var src = sourceCat?.Items ?? _data.FreeItems;
        if (!src.Remove(item)) return;
        if (sourceCat is null)
        {
            bucket.FreeItems.Add(item);
        }
        else
        {
            EnsureCategory(bucket, sourceCat.Title).Items.Add(item);
        }
        Notify();
        _store.NotifyTarget(bucket.IsBacklog ? null : bucket.Date);
        RefreshExternal(bucket);
    }

    private void MoveCategoryTo(DayData bucket, Category cat)
    {
        if (ReferenceEquals(bucket, _data)) return;
        if (!_data.Categories.Remove(cat)) return;
        var existing = bucket.FindCategory(cat.Title);
        if (existing is null)
        {
            bucket.Categories.Add(cat);
        }
        else
        {
            foreach (var item in cat.Items.ToList())
            {
                cat.Items.Remove(item);
                existing.Items.Add(item);
            }
        }
        Notify();
        _store.NotifyTarget(bucket.IsBacklog ? null : bucket.Date);
        RefreshExternal(bucket);
    }

    private void RefreshExternal(DayData bucket)
    {
        _host.RefreshBoard(bucket);
    }

    private static Category EnsureCategory(DayData bucket, string title)
    {
        var existing = bucket.FindCategory(title);
        if (existing is not null) return existing;
        var cat = new Category { Title = title.Trim() };
        bucket.Categories.Add(cat);
        return cat;
    }

    // ------------------------------------------------------------------ helpers

    private Category? FindCategoryOf(FrameworkElement row)
        => Visuals.AncestorOf<ItemsControl>(row)?.Tag as Category;

    /// <summary>Whether the press started on the element called <paramref name="name"/>
    /// (the task text / category title, which double as edit handles).</summary>
    private static bool IsOnName(object? source, FrameworkElement stopAt, string name)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Name == name) return true;
            if (ReferenceEquals(d, stopAt)) return false;
            d = Visuals.ParentOf(d);
        }
        return false;
    }

    /// <summary>Whether the click landed on one of the named elements inside <paramref name="stopAt"/>.</summary>
    private static bool IsInsideNamed(object? source, FrameworkElement stopAt, params string[] names)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement fe)
            {
                for (int i = 0; i < names.Length; i++)
                    if (fe.Name == names[i]) return true;
            }
            if (ReferenceEquals(d, stopAt)) return false;
            d = Visuals.ParentOf(d);
        }
        return false;
    }

    /// <summary>The completion circle is the only part of a row that ticks the task off.</summary>
    private static bool IsOnTick(object? source, FrameworkElement row)
        => IsOnName(source, row, "Tick");

    private sealed class PressState
    {
        public FrameworkElement Element = null!;
        public TodoItem? Item;
        public Category? Category;
        public int ScreenX;
        public int ScreenY;
        public bool OnText;
        public bool OnTick;
    }

    private sealed class RowBox
    {
        public TodoItem Item = null!;
        public Category? Cat;
        public Rect Bounds;
    }

    private sealed class CatBox
    {
        public Category Cat = null!;
        public Rect Block;
        public Rect Inner;
    }

    private sealed class DragState
    {
        public bool IsCategory;
        public TodoItem? Item;
        public Category? Category;
        public FrameworkElement? Source;
        public DragGhostWindow? Ghost;
        public DayCell? DayTarget;
        public TaskBoard? ExternalTarget;
        public DropPlan? Plan;
        public readonly List<RowBox> Rows = new();
        public readonly List<CatBox> Cats = new();
    }

    private sealed class DropPlan
    {
        public Category? TargetCategory;
        public int Index;
        public Rect Line;
    }
}
