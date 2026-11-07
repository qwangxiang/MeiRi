using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MeiRi;

/// <summary>
/// Clipboard keys for a <see cref="TextBox"/> that cannot half-fail.
///
/// The text box would copy the selection through OLE first and delete it afterwards; when the
/// clipboard is busy the delete never happens, so Ctrl+X looks like it worked (the text *is* on the
/// clipboard) while the text stays on screen — and only sometimes, depending on who is holding the
/// clipboard at that moment. Here the selection is deleted only after <see cref="ClipText"/> really
/// accepted it, a busy clipboard is retried a few times, and a final failure is reported instead of
/// being swallowed.
/// </summary>
public sealed class ClipboardEdit
{
    /// <summary>Attach with <c>local:ClipboardEdit.Enabled="True"</c> on the text box.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ClipboardEdit), new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>Optional <c>Action&lt;string&gt;</c> that shows a message ("已复制" / "剪切板被占用").</summary>
    public static readonly DependencyProperty NotifyProperty = DependencyProperty.RegisterAttached(
        "Notify", typeof(Action<string>), typeof(ClipboardEdit), new PropertyMetadata(null, OnNotifyChanged));

    private static readonly DependencyProperty InstanceProperty = DependencyProperty.RegisterAttached(
        "Instance", typeof(ClipboardEdit), typeof(ClipboardEdit), new PropertyMetadata(null));

    private const int MaxRetries = 3;
    private const int RetryDelayMs = 400;

    private readonly TextBox _box;
    private readonly DispatcherTimer _retry;
    private Action<string>? _notify;
    private string _pendingText = string.Empty;
    private int _pendingStart;
    private int _retries;

    private ClipboardEdit(TextBox box)
    {
        _box = box;
        _retry = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(RetryDelayMs) };
        _retry.Tick += (_, _) => RetryCut();
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        box.Unloaded += (_, _) => _retry.Stop();
    }

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetNotify(DependencyObject element, Action<string>? value)
        => element.SetValue(NotifyProperty, value);

    public static Action<string>? GetNotify(DependencyObject element)
        => (Action<string>?)element.GetValue(NotifyProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        if (box.GetValue(InstanceProperty) is ClipboardEdit) return;
        var helper = new ClipboardEdit(box) { _notify = GetNotify(box) };
        box.SetValue(InstanceProperty, helper);
        if (LogKeys) Log.Write($"clip attach {(box.Name.Length == 0 ? box.GetType().Name : box.Name)}");
    }

    /// <summary>Dev switch: log every clipboard key the helper sees (first few only).</summary>
    public static bool LogKeys;
    private static int _logged;

    private static void OnNotifyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox box && box.GetValue(InstanceProperty) is ClipboardEdit helper)
            helper._notify = e.NewValue as Action<string>;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (LogKeys && _logged++ < 40)
            Log.Write($"clip key seen key={key} ctrl={ctrl} shift={shift} box={_box.Name}");

        if (ctrl && !shift)
        {
            switch (key)
            {
                case Key.X:
                    e.Handled = true;
                    Cut();
                    return;
                case Key.C:
                    e.Handled = true;
                    Copy();
                    return;
                case Key.V:
                    e.Handled = true;
                    Paste();
                    return;
                case Key.Insert:
                    e.Handled = true;
                    Copy();
                    return;
            }
        }
        if (shift && !ctrl)
        {
            switch (key)
            {
                case Key.Delete:
                    e.Handled = true;
                    Cut();
                    return;
                case Key.Insert:
                    e.Handled = true;
                    Paste();
                    return;
            }
        }
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MenuFlyout.Show(_box, PlacementMode.MousePoint,
            MenuFlyout.Item("剪切", "\uE8C6", Cut),
            MenuFlyout.Item("复制", "\uE8C8", Copy),
            MenuFlyout.Item("粘贴", "\uE77F", Paste),
            MenuFlyout.Separator(),
            MenuFlyout.Item("全选", "\uE8B3", () => _box.SelectAll()));
    }

    // ------------------------------------------------------------------ commands

    public void Cut()
    {
        string selection = _box.SelectedText;
        int start = _box.SelectionStart;
        if (selection.Length == 0) return;
        _retry.Stop();
        _retries = 0;

        if (ClipText.TrySet(selection, out var error))
        {
            DeleteRange(start, selection.Length);
            _pendingText = string.Empty;
            return;
        }

        Log.Write("cut deferred: " + error);
        _pendingText = selection;
        _pendingStart = start;
        Message("剪切板被占用，稍后自动重试");
        _retry.Start();
    }

    public void Copy()
    {
        string selection = _box.SelectedText;
        if (selection.Length == 0) return;
        if (ClipText.TrySet(selection, out var error))
        {
            Message("已复制");
            return;
        }
        Log.Write("copy failed: " + error);
        Message("剪切板被占用，请稍后再试");
    }

    public void Paste()
    {
        if (!ClipText.TryGet(out var text, out var error))
        {
            Log.Write("paste failed: " + error);
            Message("剪切板被占用，请稍后再试");
            return;
        }
        if (text.Length == 0)
        {
            Message("剪切板里没有文字");
            return;
        }
        ReplaceSelection(text);
    }

    // ------------------------------------------------------------------ retry

    /// <summary>
    /// The clipboard is usually busy for a few milliseconds only, so a refused cut is replayed.
    /// Only the exact range that was cut is deleted, and only while it still says what it said.
    /// </summary>
    private void RetryCut()
    {
        if (_pendingText.Length == 0)
        {
            _retry.Stop();
            return;
        }
        if (_pendingStart + _pendingText.Length > _box.Text.Length
            || string.CompareOrdinal(_box.Text, _pendingStart, _pendingText, 0, _pendingText.Length) != 0)
        {
            Log.Write("cut retry dropped: the text changed");
            _pendingText = string.Empty;
            _retry.Stop();
            return;
        }

        if (ClipText.TrySet(_pendingText, out var error))
        {
            _retry.Stop();
            DeleteRange(_pendingStart, _pendingText.Length);
            _pendingText = string.Empty;
            Log.Write($"cut retried ok after {_retries} refusal(s)");
            Message("已剪切");
            return;
        }

        Log.Write($"cut retry {_retries + 1} failed: {error}");
        if (++_retries <= MaxRetries) return; // the timer keeps ticking, nothing to re-arm
        _retry.Stop();
        _pendingText = string.Empty;
        Message("剪切板一直被占用，内容没动");
    }

    // ------------------------------------------------------------------ text edits

    /// <summary>Deletes a range, keeping the text box's undo stack intact.</summary>
    private void DeleteRange(int start, int length)
    {
        _box.Select(start, length);
        _box.SelectedText = string.Empty;
        _box.CaretIndex = Math.Min(start, _box.Text.Length);
        _box.Focus();
    }

    /// <summary>
    /// Inserts <paramref name="text"/> over the selection. <see cref="TextBox.SelectedText"/> keeps
    /// the undo stack and the caret's scroll position, but it leaves the caret where the inserted
    /// text *starts* — pasting looked like it had not moved on. The caret is parked behind the
    /// pasted text explicitly, so the next keystroke lands at its end like in every other editor.
    /// </summary>
    private void ReplaceSelection(string text)
    {
        int start = _box.SelectionStart;
        _box.SelectedText = text;
        int caret = Math.Min(start + text.Length, _box.Text.Length);
        _box.Select(caret, 0);
        _box.CaretIndex = caret;
        int line = _box.GetLineIndexFromCharacterIndex(caret);
        if (line >= 0) _box.ScrollToLine(line);
        _box.Focus();
    }

    private void Message(string text) => _notify?.Invoke(text);
}
