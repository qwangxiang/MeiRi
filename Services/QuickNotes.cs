using System;
using System.Collections.ObjectModel;
using System.Linq;
using MeiRi.Interop;
using MeiRi.Views;

namespace MeiRi.Services;

public sealed class QuickNote : ObservableObject
{
    private string _text = string.Empty;
    private bool _isSelected;

    public Guid Id { get; } = Guid.NewGuid();

    public string Text
    {
        get => _text;
        set
        {
            // deliberately no notification for Title here: a note can hold megabytes of text and
            // the tab caption is refreshed from a debounced timer instead (see QuickNoteWindow)
            if (!Set(ref _text, value ?? string.Empty)) return;
            _title = null;
        }
    }

    private string? _title;

    public string Title
    {
        get
        {
            if (_title is not null) return _title;
            _title = BuildTitle(_text);
            return _title;
        }
    }

    private static string BuildTitle(string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            int s = start, e = end;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            if (e > s)
            {
                var line = text.AsSpan(s, e - s);
                return line.Length > 12 ? string.Concat(line[..12], "…") : line.ToString();
            }
            start = end + 1;
        }
        return "空便签";
    }

    /// <summary>Re-reads <see cref="Title"/> after the text changed (called on a debounce).</summary>
    public void NotifyTitleChanged()
    {
        _title = null;
        OnPropertyChanged(nameof(Title));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class QuickNoteGroup : ObservableObject
{
    private QuickNote? _selected;

    public ObservableCollection<QuickNote> Notes { get; } = new();

    public QuickNote? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var n in Notes) n.IsSelected = ReferenceEquals(n, value);
            OnPropertyChanged(nameof(HasMultiple));
        }
    }

    public bool HasMultiple => Notes.Count > 1;

    public int IndexOf(QuickNote note) => Notes.IndexOf(note);
}

/// <summary>Owns all floating note windows; handles creation, merging and detaching.</summary>
public sealed class QuickNoteManager
{
    private readonly System.Collections.Generic.List<QuickNoteWindow> _windows = new();
    private int _cascade;

    public System.Collections.Generic.IReadOnlyList<QuickNoteWindow> Windows => _windows;

    /// <summary>
    /// The settings switch. When it is off nothing may open a note: the taskbar double click,
    /// the tray hand-off and every "新建便签" menu entry all funnel through <see cref="OpenAtCursor"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public QuickNoteWindow? OpenAtCursor()
    {
        if (!Enabled) return null;
        if (!Native.GetCursorPos(out var cur)) return null;
        return OpenAt(cur.X, cur.Y);
    }

    public QuickNoteWindow OpenAt(int screenX, int screenY)
    {
        var group = new QuickNoteGroup();
        var note = new QuickNote();
        group.Notes.Add(note);
        group.Selected = note;

        var window = new QuickNoteWindow(this, group);
        _windows.Add(window);
        window.Show();

        int offset = (_cascade++ % 6) * 26;
        int desiredX = screenX + 16 + offset;
        int desiredY = screenY + 14 + offset;
        WindowEffects.PlaceWindow(window, desiredX, desiredY);
        window.Dispatcher.BeginInvoke(new Action(() => WindowEffects.PlaceWindow(window, desiredX, desiredY)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        window.BringToFront();
        window.FocusEditor();
        return window;
    }

    /// <summary>
    /// The note under the point, but only if it lives on the desktop the user is looking at:
    /// windows of other virtual desktops are invisible yet still report their old rectangle,
    /// which used to make a note "merge" into a window on another desktop and follow it there.
    /// </summary>
    public QuickNoteWindow? WindowAt(int x, int y, QuickNoteWindow? exclude = null)
    {
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            if (ReferenceEquals(w, exclude) || !w.IsVisible) continue;
            if (!VirtualDesktops.IsOnCurrentDesktop(WindowEffects.Handle(w))) continue;
            if (WindowEffects.PointInside(w, x, y)) return w;
        }
        return null;
    }

    public void NotifyClosed(QuickNoteWindow window)
    {
        _windows.Remove(window);
    }

    public void Merge(QuickNoteWindow source, QuickNoteWindow target)
    {
        if (ReferenceEquals(source, target)) return;
        if (!VirtualDesktops.IsOnCurrentDesktop(WindowEffects.Handle(target))) return;
        var notes = source.Group.Notes.ToList();
        if (notes.Count == 0) return;

        var selected = source.Group.Selected;
        foreach (var note in notes)
        {
            source.Group.Notes.Remove(note);
            target.Group.Notes.Add(note);
        }
        target.Group.Selected = selected ?? target.Group.Notes[^1];
        target.RefreshTabs();
        source.Close();
        target.BringToFront();
        target.FocusEditor();
        target.ShowToast("已合并");
    }

    public void Detach(QuickNoteWindow source, QuickNote note)
    {
        if (!source.Group.Notes.Contains(note)) return;
        if (source.Group.Notes.Count <= 1) return;

        int index = source.Group.Notes.IndexOf(note);
        source.Group.Notes.Remove(note);

        var group = new QuickNoteGroup();
        group.Notes.Add(note);
        group.Selected = note;

        var window = new QuickNoteWindow(this, group);
        _windows.Add(window);
        window.Show();

        Native.GetWindowRect(WindowEffects.Handle(source), out var rect);
        int desiredX = rect.Left + 30;
        int desiredY = rect.Top + 30;
        WindowEffects.PlaceWindow(window, desiredX, desiredY);
        window.Dispatcher.BeginInvoke(new Action(() => WindowEffects.PlaceWindow(window, desiredX, desiredY)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        if (source.Group.Notes.Count == 0)
        {
            source.Close();
        }
        else
        {
            source.Group.Selected = source.Group.Notes[Math.Min(index, source.Group.Notes.Count - 1)];
            source.RefreshTabs();
        }

        window.BringToFront();
        window.FocusEditor();
    }

    /// <summary>
    /// Creates and throws away one note window off screen, so the first real note does not pay the
    /// several-hundred-millisecond cost of the first Show() of its window type.
    /// </summary>
    public void WarmUp()
    {
        try
        {
            var group = new QuickNoteGroup();
            var note = new QuickNote();
            group.Notes.Add(note);
            group.Selected = note;
            var window = new QuickNoteWindow(this, group)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000
            };
            window.Show();
            window.UpdateLayout();
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Write("note warmup failed: " + ex.Message);
        }
    }

    public void CloseAll()
    {
        foreach (var w in _windows.ToList()) w.Close();
        _windows.Clear();
    }
}
