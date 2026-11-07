using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MeiRi.Interop;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only (<c>MeiRi.exe --clip</c>): presses Ctrl+X in a real note with synthetic keys and logs,
/// for every round, what the editor holds, what the model holds and what the clipboard holds,
/// plus how long the keystroke took. It then holds the clipboard open from another thread — the way
/// a clipboard manager does — and presses Ctrl+X once more, which is the case that used to end with
/// "the text is in the clipboard but still on screen".
/// </summary>
public static class DevClip
{
    public static void Run(QuickNoteManager notes, Action done)
    {
        ClipboardEdit.LogKeys = true;
        ProbeClipboard();

        var window = notes.OpenAt(320, 240);
        var editor = window.EditorBox;
        var note = window.Group.Selected;
        Log.Write($"clip# editor clipboard hook={ClipboardEdit.GetEnabled(editor)} " +
                  $"notify={(ClipboardEdit.GetNotify(editor) is null ? "none" : "set")}");
        if (note is null)
        {
            Log.Write("clip# no note");
            done();
            return;
        }

        int step = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            try
            {
                // four plain rounds: a healthy Ctrl+X must empty both the editor and the model
                if (step < 4)
                {
                    int round = step + 1;
                    string text = $"第{round}段剪切测试";
                    Prepare(window, note, text);
                    var watch = Stopwatch.StartNew();
                    CtrlX();
                    double cost = watch.Elapsed.TotalMilliseconds;
                    Log.Write($"clip#{round} ctrl+x {cost:F0}ms: set={text.Length} editor={editor.Text.Length} " +
                              $"model={note.Text.Length} selection={editor.SelectionLength} clip=\"{ClipSnippet()}\"");
                    step++;
                    return;
                }

                switch (step)
                {
                    case 4:
                    {
                        Prepare(window, note, "第5段剪切板被占用");
                        // a second process holds the clipboard, which is what actually happens when a
                        // clipboard manager reads it; the retry inside ClipboardEdit has to recover
                        var exe = Environment.ProcessPath;
                        if (exe is not null)
                        {
                            var info = new ProcessStartInfo(exe, "--cliphold 1000") { UseShellExecute = false };
                            var proc = Process.Start(info);
                            Log.Write($"clip#5 clipboard holder pid={proc?.Id}");
                        }
                        Thread.Sleep(120);
                        break;
                    }

                    case 5:
                    {
                        var watch = Stopwatch.StartNew();
                        CtrlX();
                        double cost = watch.Elapsed.TotalMilliseconds;
                        Log.Write($"clip#5 ctrl+x with clipboard busy {cost:F0}ms: editor={editor.Text.Length} " +
                                  $"model={note.Text.Length} selection={editor.SelectionLength} " +
                                  $"clip=\"{ClipSnippet()}\"");
                        break;
                    }

                    case 6:
                        Log.Write($"clip#6 300ms later: editor={editor.Text.Length} model={note.Text.Length} " +
                                  $"clip=\"{ClipSnippet()}\"");
                        break;

                    case 7:
                        Log.Write($"clip#7 after retries: editor={editor.Text.Length} model={note.Text.Length} " +
                                  $"clip=\"{ClipSnippet()}\" (want 0/0)");
                        break;

                    case 8:
                    {
                        Prepare(window, note, "第6段剪切板已恢复");
                        var watch = Stopwatch.StartNew();
                        CtrlX();
                        double cost = watch.Elapsed.TotalMilliseconds;
                        Log.Write($"clip#8 ctrl+x after release {cost:F0}ms: editor={editor.Text.Length} " +
                                  $"model={note.Text.Length} clip=\"{ClipSnippet()}\"");
                        break;
                    }

                    case 9:
                    {
                        // copy must not touch the text, and paste must put the cut text back
                        Prepare(window, note, "复制粘贴测试");
                        Ctrl(Native.VK_C);
                        Log.Write($"clip#9 ctrl+c: editor={editor.Text.Length} clip=\"{ClipSnippet()}\"");
                        break;
                    }

                    case 10:
                    {
                        Prepare(window, note, "剪切再粘贴");
                        CtrlX();
                        Log.Write($"clip#10 after cut: editor={editor.Text.Length} clip=\"{ClipSnippet()}\" " +
                                  $"count=\"{window.CountLabel}\"");
                        Ctrl(Native.VK_V);
                        Log.Write($"clip#10b after paste: editor={editor.Text.Length} " +
                                  $"text=\"{editor.Text}\" caret={editor.CaretIndex} (want {editor.Text.Length}) " +
                                  $"count=\"{window.CountLabel}\"");
                        break;
                    }

                    case 11:
                    {
                        // paste into the middle of the note: the caret has to end behind the pasted
                        // text, not in front of it
                        Prepare(window, note, "甲乙丙丁戊");
                        ClipText.TrySet("XY", out _);
                        editor.Select(2, 0);
                        Pump();
                        Ctrl(Native.VK_V);
                        Log.Write($"clip#11 paste in the middle: text=\"{editor.Text}\" " +
                                  $"caret={editor.CaretIndex} (want 4, 甲乙XY丙丁戊) " +
                                  $"count=\"{window.CountLabel}\"");
                        break;
                    }

                    case 12:
                    {
                        // what the framework does on its own, for the record: the caret stays at the
                        // start of what SelectedText inserted, which is the bug this helper works around
                        editor.Select(0, 0);
                        editor.SelectedText = "abcXY";
                        Pump();
                        Log.Write($"clip#12 raw SelectedText: caret={editor.CaretIndex} " +
                                  $"sel={editor.SelectionLength} (framework default)");
                        break;
                    }

                    case 13:
                    {
                        // the counter: total while nothing is selected, total + selected while it is
                        Prepare(window, note, "字数字数字数\n第二行");   // 9 characters, 1 line break
                        editor.Select(2, 3);
                        Pump();
                        Log.Write($"clip#13 selection 3 of 9: count=\"{window.CountLabel}\"");
                        editor.Select(0, editor.Text.Length);
                        Pump();
                        Log.Write($"clip#13b select all ({editor.Text.Length} units): " +
                                  $"count=\"{window.CountLabel}\"");
                        editor.Select(editor.Text.Length, 0);
                        Pump();
                        Log.Write($"clip#13c no selection: count=\"{window.CountLabel}\"");
                        break;
                    }

                    default:
                        timer.Stop();
                        window.Close();
                        notes.CloseAll();
                        Log.Write("clip# done");
                        done();
                        break;
                }
                step++;
            }
            catch (Exception ex)
            {
                Log.Write("clip# failed: " + ex);
                timer.Stop();
                notes.CloseAll();
                done();
            }
        };
        timer.Start();
    }

    /// <summary>What the Win32 clipboard really says, before any note window exists.</summary>
    private static void ProbeClipboard()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, "clip-probe-wpf"), true);
            Log.Write($"clip#0 WPF SetDataObject(copy:true) ok in {watch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Log.Write($"clip#0 WPF SetDataObject(copy:true) FAILED after {watch.ElapsedMilliseconds}ms: " +
                      $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        watch.Restart();
        bool ok = ClipText.TrySet("clip-probe-win32", out var error);
        Log.Write($"clip#0 Win32 set ok={ok} in {watch.ElapsedMilliseconds}ms {error}");

        watch.Restart();
        bool read = ClipText.TryGet(out var text, out error);
        Log.Write($"clip#0 Win32 read ok={read} in {watch.ElapsedMilliseconds}ms text=\"{text}\" {error}");
        Pump();
    }

    private static void Prepare(QuickNoteWindow window, QuickNote note, string text)
    {
        WindowEffects.ForceForeground(window);
        note.Text = text;
        window.UpdateLayout();
        var editor = window.EditorBox;
        editor.Focus();
        editor.CaretIndex = text.Length;
        editor.SelectAll();
        Pump();
        Log.Write($"clip# focused={Native.GetForegroundWindow() == WindowEffects.Handle(window)} " +
                  $"sel={editor.SelectionLength} text={text.Length}");
    }

    private static void CtrlX()
    {
        Key(Native.VK_CONTROL, down: true);
        Key(Native.VK_X, down: true);
        Key(Native.VK_X, down: false);
        Key(Native.VK_CONTROL, down: false);
        Thread.Sleep(60);
        Pump();
        Thread.Sleep(60);
        Pump();
    }

    private static void Ctrl(byte vk)
    {
        Key(Native.VK_CONTROL, down: true);
        Key(vk, down: true);
        Key(vk, down: false);
        Key(Native.VK_CONTROL, down: false);
        Thread.Sleep(60);
        Pump();
        Thread.Sleep(60);
        Pump();
    }

    private static void Key(byte vk, bool down)
        => Native.keybd_event(vk, 0, down ? 0 : Native.KEYEVENTF_KEYUP, IntPtr.Zero);

    private static string ClipSnippet()
    {
        if (!ClipText.TryGet(out var text, out var error)) return "FAILED:" + error;
        return text.Length <= 12 ? text : text[..12] + "…";
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Input);
}
