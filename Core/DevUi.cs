using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Models;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --ui</c>): drives the real windows with synthetic mouse and keyboard
/// input to check the six behavioural changes of this round — the one-line hover preview with its
/// "…" strip, the category frame toggling selection, tick-only completion, Ctrl+Enter line breaks,
/// the calendar footer totals and the quick-note switch — and writes what it saw as <c>ui#</c>
/// lines plus two screen captures.
/// </summary>
public static class DevUi
{
    private const string OutDir = @"D:\Projects\MeiRi\docs\shots";
    private const string NoteText = "这句备注不该出现在悬浮卡里";

    public static void Run(CalendarHost host, DataStore store, QuickNoteManager notes, Settings settings,
        Action done)
    {
        var calendar = host.Windows.OrderBy(w => VisualTreeHelper.GetDpi(w).DpiScaleX).FirstOrDefault();
        if (calendar is null)
        {
            done();
            return;
        }

        Native.GetCursorPos(out var originalCursor);
        var foreground = Native.GetForegroundWindow();
        bool previewWasOn = settings.ShowPreview;
        settings.ShowPreview = true;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var longDate = new DateOnly(2026, 12, 9);
        var shortDate = new DateOnly(2026, 12, 10);
        var editDate = new DateOnly(2026, 12, 11);

        var longDay = store.GetOrCreate(longDate);
        var shortDay = store.GetOrCreate(shortDate);
        var editDay = store.GetOrCreate(editDate);
        // a previous run that was killed before it cleaned up must not stack up test data
        foreach (var day in new[] { longDay, shortDay, editDay }) Prepare(day);

        // 18 long tasks: more than the card can show, so the "…" strip must appear
        var longCat = new Category { Title = "验证用类别" };
        for (int i = 0; i < 18; i++)
        {
            longCat.Items.Add(new TodoItem
            {
                Text = $"预览第 {i + 1} 项：故意写得很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长",
                Note = NoteText
            });
        }
        longDay.Categories.Add(longCat);

        shortDay.FreeItems.Add(new TodoItem { Text = "短的一天，只有一项", IsDone = true });

        // a compact day for the click tests, so every element is inside the visible card
        var editCat = new Category { Title = "验证用类别" };
        var first = new TodoItem { Text = "第一项日程", Note = NoteText };
        editCat.Items.Add(first);
        editCat.Items.Add(new TodoItem { Text = "第二项日程" });
        editDay.Categories.Add(editCat);

        DayEditorWindow? editor = null;
        FrameworkElement? rowEl = null;
        int step = 0;
        int noteLength = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(420) };
        timer.Tick += (_, _) =>
        {
            try
            {
                switch (step++)
                {
                    // ---------------------------------------------------------- preview
                    case 0:
                        Describe(host, calendar, "ui#1 long day", longDate, capture: true);
                        break;

                    case 1:
                        Describe(host, calendar, "ui#1b short day", shortDate, capture: false);
                        host.HidePreviewNow();
                        break;

                    // ---------------------------------------------------------- editor
                    case 2:
                    {
                        host.OpenDay(editDate, calendar, toggle: false);
                        editor = host.EditorWindow;
                        if (editor is null || !editor.IsVisible)
                        {
                            Fail("editor did not open");
                            break;
                        }
                        // another app may own the pixels next to the calendar: raise the card first,
                        // otherwise the synthetic clicks land on that app instead of the board
                        WindowEffects.ForceForeground(editor);
                        Pump();
                        Native.GetWindowRect(WindowEffects.Handle(editor), out var er);
                        Log.Write($"ui#2 editor=[{er.Left},{er.Top} {er.Width}x{er.Height}] " +
                                  $"hit={HitOwner(er.Left + 90, er.Top + 300)} " + host.DescribeLayout());
                        break;
                    }

                    case 3:
                    {
                        var box = ContainerOf(editor!.Board.CategoriesHost, 0);
                        if (box is null)
                        {
                            Fail("category box not found");
                            break;
                        }
                        var r = ScreenRect(box);
                        var p = new Point(r.Left + 14, r.Bottom - 3);
                        WindowEffects.ForceForeground(editor);
                        Click((int)p.X, (int)p.Y);
                        Log.Write($"ui#3 click category frame at {(int)p.X},{(int)p.Y} " +
                                  $"box=[{(int)r.Left},{(int)r.Top} {(int)r.Width}x{(int)r.Height}] " +
                                  $"hit={HitOwner((int)p.X, (int)p.Y)} " +
                                  $"selected={editCat.IsSelected} (want True)");
                        Capture(calendar, editor, "category-select.png", "ui#3 frame click");
                        break;
                    }

                    case 4:
                    {
                        var box = ContainerOf(editor!.Board.CategoriesHost, 0);
                        if (box is null)
                        {
                            Fail("category box not found (second click)");
                            break;
                        }
                        var r = ScreenRect(box);
                        Click((int)(r.Left + 14), (int)(r.Bottom - 3));
                        Log.Write($"ui#4 click category frame again: selected={editCat.IsSelected} (want False)");
                        break;
                    }

                    case 5:
                    {
                        rowEl = FindElement(editor!.Board, first);
                        var textEl = rowEl is null ? null : FindNamed(rowEl, "Text") as FrameworkElement;
                        if (textEl is null)
                        {
                            Fail("task text element not found");
                            break;
                        }
                        var c = Center(ScreenRect(textEl));
                        Click((int)c.X, (int)c.Y);
                        Log.Write($"ui#5 click task text: editing={first.IsEditingText} done={first.IsDone} " +
                                  $"categorySelected={editCat.IsSelected} (want editing=True, done/cat untouched)");
                        break;
                    }

                    case 6:
                    {
                        first.IsEditingText = false;
                        var tick = rowEl is null ? null : FindNamed(rowEl, "Tick") as FrameworkElement;
                        if (tick is null)
                        {
                            Fail("tick element not found");
                            break;
                        }
                        var c = Center(ScreenRect(tick));
                        Click((int)c.X, (int)c.Y);
                        Log.Write($"ui#6 click tick: done={first.IsDone} (want True) editing={first.IsEditingText} " +
                                  $"hit={HitOwner((int)c.X, (int)c.Y)}");
                        break;
                    }

                    case 7:
                    {
                        var tick = rowEl is null ? null : FindNamed(rowEl, "Tick") as FrameworkElement;
                        if (tick is null)
                        {
                            Fail("tick element vanished");
                            break;
                        }
                        var c = Center(ScreenRect(tick));
                        Click((int)c.X, (int)c.Y);
                        Log.Write($"ui#6b click tick again: done={first.IsDone} (want False)");
                        break;
                    }

                    // ---------------------------------------------------------- Ctrl+Enter
                    case 8:
                    {
                        first.IsEditingNote = true;
                        editor!.Board.UpdateLayout();
                        var tb = rowEl is null ? null : FindNamed(rowEl, "NoteEdit") as TextBox;
                        if (tb is null)
                        {
                            Fail("note box not found");
                            break;
                        }
                        WindowEffects.ForceForeground(editor);
                        tb.Focus();
                        tb.CaretIndex = tb.Text.Length;
                        Pump();
                        noteLength = tb.Text.Length;
                        Log.Write($"ui#8 note box: focused={tb.IsFocused} acceptReturns={tb.AcceptsReturn} " +
                                  $"len={noteLength} foreground={Native.GetForegroundWindow() == WindowEffects.Handle(editor)}");
                        CtrlEnter();
                        break;
                    }

                    case 9:
                    {
                        var tb = rowEl is null ? null : FindNamed(rowEl, "NoteEdit") as TextBox;
                        if (tb is null)
                        {
                            Fail("note box vanished");
                            break;
                        }
                        Log.Write($"ui#9 after ctrl+enter: len={tb.Text.Length} (was {noteLength}) " +
                                  $"newline={tb.Text.Contains('\n')} stillEditing={first.IsEditingNote} (want all True) " +
                                  $"modifiers={Keyboard.Modifiers} focused={tb.IsKeyboardFocused}");
                        Enter();
                        break;
                    }

                    case 10:
                    {
                        var tb = rowEl is null ? null : FindNamed(rowEl, "NoteEdit") as TextBox;
                        Log.Write($"ui#10 synthetic enter: stillEditing={first.IsEditingNote} len={tb?.Text.Length} " +
                                  $"foreground={Native.GetForegroundWindow() == WindowEffects.Handle(editor!)}");
                        if (tb is not null && first.IsEditingNote)
                        {
                            RaiseEnter(tb);
                            Log.Write($"ui#10b dispatched enter: stillEditing={first.IsEditingNote} (want False) " +
                                      $"len={tb.Text.Length}");
                        }
                        first.Note = NoteText;
                        break;
                    }

                    // ---------------------------------------------------------- footer / switch
                    case 11:
                    {
                        var (month, year) = store.GetDoneTotals(today);
                        var el = FindNamed(calendar, "StatsText") as TextBlock;
                        string shown = el?.Text ?? "(missing)";
                        string expect = $"本月已完成日程：{month} 项     最近一年已完成日程：{year} 项";
                        Log.Write($"ui#11 footer shown=\"{shown}\" expect=\"{expect}\" match={shown == expect}");
                        break;
                    }

                    case 12:
                    {
                        var before = notes.Windows.Count;
                        notes.Enabled = false;
                        settings.QuickNotes = false;
                        var opened = notes.OpenAtCursor();
                        Log.Write($"ui#12 quick notes off: opened={(opened is null ? "none" : "OPENED")} " +
                                  $"windowsBefore={before} windowsAfter={notes.Windows.Count} (want none, equal)");
                        notes.Enabled = true;
                        settings.QuickNotes = true;
                        break;
                    }

                    case 13:
                        timer.Stop();
                        Finish();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write("ui# failed: " + ex);
                timer.Stop();
                Finish();
            }
        };
        timer.Start();
        return;

        void Fail(string why)
        {
            Log.Write("ui# FAILED: " + why);
            timer.Stop();
            Finish();
        }

        void Finish()
        {
            try
            {
                foreach (var day in new[] { longDay, shortDay, editDay })
                {
                    day.Categories.Clear();
                    day.FreeItems.Clear();
                    // the note commit inside the test saves the whole store, so the removal has to
                    // be written out as well — otherwise the test data comes back on next start
                    store.Notify(day.Date);
                }
                settings.ShowPreview = previewWasOn;
                host.HidePreviewNow();
                host.CloseEditor();
                Native.SetCursorPos(originalCursor.X, originalCursor.Y);
                if (foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
                Log.Write("ui# done (test data removed)");
            }
            catch (Exception ex)
            {
                Log.Write("ui# cleanup failed: " + ex);
            }
            done();
        }
    }

    /// <summary>Drops whatever an interrupted earlier run left behind on a test day.</summary>
    private static void Prepare(DayData day)
    {
        for (int i = day.Categories.Count - 1; i >= 0; i--)
        {
            var title = day.Categories[i].Title;
            if (title == "验证用类别") day.Categories.RemoveAt(i);
        }
        for (int i = day.FreeItems.Count - 1; i >= 0; i--)
        {
            var text = day.FreeItems[i].Text;
            if (text == "短的一天，只有一项" || text == "第二项日程") day.FreeItems.RemoveAt(i);
        }
    }

    /// <summary>Shows the hover preview for a day and reports exactly what the card renders.</summary>
    private static void Describe(CalendarHost host, CalendarWindow calendar, string tag, DateOnly date,
        bool capture)
    {
        Native.GetWindowRect(calendar.Handle, out var cr);
        Native.SetCursorPos(cr.Left + cr.Width / 2, cr.Top + cr.Height / 2);
        host.ShowPreview(new ViewModels.DayCell(date, true), calendar);

        var preview = host.PreviewWindow;
        if (preview is null || !preview.IsVisible)
        {
            Log.Write($"{tag}: preview did not open");
            return;
        }
        preview.UpdateLayout();

        int rows = CountNamed(preview, "T");
        int sections = CountNamed(preview, "SecTitle");
        bool note = FindText(preview, NoteText);
        var hintEl = FindNamed(preview, "MoreHint") as FrameworkElement;
        bool more = hintEl is not null && hintEl.Visibility == Visibility.Visible;
        string hintBox = "(none)";
        if (hintEl is not null)
        {
            var at = hintEl.TranslatePoint(new Point(0, 0), preview);
            hintBox = $"[{at.X:F0},{at.Y:F0} {hintEl.ActualWidth:F0}x{hintEl.ActualHeight:F0}]";
        }
        string scroller = "(none)";
        if (FindNamed(preview, "SectionScroller") is System.Windows.Controls.ScrollViewer sv)
        {
            var at = sv.TranslatePoint(new Point(0, 0), preview);
            scroller = $"[{at.X:F0},{at.Y:F0} {sv.ActualWidth:F0}x{sv.ActualHeight:F0}] " +
                       $"extent={sv.ExtentHeight:F0} viewport={sv.ViewportHeight:F0}";
        }
        Native.GetWindowRect(WindowEffects.Handle(preview), out var pr);
        Log.Write($"{tag}: rows={rows} sections={sections} noteShown={note} overflow={more} hint={hintBox} " +
                  $"list={scroller} card=[{pr.Left},{pr.Top} {pr.Width}x{pr.Height}]");

        if (!capture) return;
        try
        {
            Directory.CreateDirectory(OutDir);
            int pad = 10;
            var path = Native.CaptureScreen(pr.Left - pad, pr.Top - pad, pr.Width + pad * 2,
                pr.Height + pad * 2, Path.Combine(OutDir, "preview-more.png"));
            Log.Write($"{tag}: captured {path ?? "FAILED"}");
        }
        catch (Exception ex)
        {
            Log.Write($"{tag}: capture failed: {ex.Message}");
        }
    }

    private static string HitOwner(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return "none";
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return (pid == Environment.ProcessId ? "ours" : "pid" + pid) + ":" + Native.ClassNameOf(hwnd);
    }

    private static void Click(int x, int y)
    {
        Native.SetCursorPos(x, y);
        Pump();
        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Pump();
        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Pump();
    }

    private static void CtrlEnter()
    {
        Native.keybd_event(Native.VK_CONTROL, 0, 0, IntPtr.Zero);
        Native.keybd_event(Native.VK_RETURN, 0, 0, IntPtr.Zero);
        Native.keybd_event(Native.VK_RETURN, 0, Native.KEYEVENTF_KEYUP, IntPtr.Zero);
        Native.keybd_event(Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    private static void Enter()
    {
        Native.keybd_event(Native.VK_RETURN, 0, 0, IntPtr.Zero);
        Native.keybd_event(Native.VK_RETURN, 0, Native.KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    /// <summary>Fires the real KeyDown handler without going through the keyboard driver.</summary>
    private static void RaiseEnter(TextBox tb)
    {
        var source = PresentationSource.FromVisual(tb);
        if (source is null) return;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        tb.RaiseEvent(args);
        Pump();
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Input);

    private static FrameworkElement? ContainerOf(ItemsControl control, int index)
        => control.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private static FrameworkElement? FindElement(DependencyObject root, object data)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement el && ReferenceEquals(el.DataContext, data)
                && el.ActualWidth > 0 && el.ActualHeight > 0)
                return el;
            if (FindElement(child, data) is { } found) return found;
        }
        return null;
    }

    private static DependencyObject? FindNamed(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement el && el.Name == name) return el;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private static int CountNamed(DependencyObject root, string name)
    {
        int total = 0;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement el && el.Name == name) total++;
            total += CountNamed(child, name);
        }
        return total;
    }

    private static bool FindText(DependencyObject root, string text)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && tb.Text == text && tb.Visibility == Visibility.Visible) return true;
            if (FindText(child, text)) return true;
        }
        return false;
    }

    private static Rect ScreenRect(FrameworkElement el)
    {
        var origin = el.PointToScreen(new Point(0, 0));
        return new Rect(origin.X, origin.Y, el.ActualWidth, el.ActualHeight);
    }

    private static Point Center(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static void Capture(CalendarWindow calendar, Window? editor, string file, string tag)
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            var box = new List<Native.RECT>();
            if (Native.GetWindowRect(calendar.Handle, out var cr)) box.Add(cr);
            if (editor is not null && Native.GetWindowRect(WindowEffects.Handle(editor), out var er)) box.Add(er);
            if (box.Count == 0) return;
            int left = box.Min(r => r.Left) - 12;
            int top = box.Min(r => r.Top) - 12;
            int right = box.Max(r => r.Right) + 12;
            int bottom = box.Max(r => r.Bottom) + 12;
            var path = Native.CaptureScreen(left, top, right - left, bottom - top, Path.Combine(OutDir, file));
            Log.Write($"{tag}: captured {path ?? "FAILED"}");
        }
        catch (Exception ex)
        {
            Log.Write($"{tag}: capture failed: {ex.Message}");
        }
    }
}
