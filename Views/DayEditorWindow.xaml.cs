using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeiRi.Models;
using MeiRi.Services;

namespace MeiRi.Views;

/// <summary>
/// The per-day editor. It is anchored to the day cell it was opened from — it cannot be
/// dragged, and it follows the calendar when the calendar is moved.
/// </summary>
public partial class DayEditorWindow : Window
{
    private readonly CalendarHost _host;
    private readonly DataStore _store;
    private bool _closingForShutdown;

    public DayEditorWindow(CalendarHost host, DataStore store)
    {
        _host = host;
        _store = store;
        InitializeComponent();

        Board = new TaskBoard(host, store);
        BoardHost.Content = Board;
        Board.SetData(store.GetOrCreate(DateOnly.FromDateTime(DateTime.Today)));
        UpdateHeader();

        SourceInitialized += (_, _) =>
        {
            // owned by the calendar it was opened from, so it is always drawn above it
            WindowEffects.MakeToolWindow(this, noActivate: false);
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        };
        store.DayChanged += OnDayChanged;
        Closed += (_, _) => store.DayChanged -= OnDayChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public TaskBoard Board { get; }

    public DateOnly CurrentDate => Board.Data.Date;

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closingForShutdown)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ day

    public void FocusInput()
    {
        WindowEffects.ForceForeground(this);
        Activate();
        Board.FocusInput();
    }

    public void SetDay(DateOnly date)
    {
        if (Board.Data.Date != date)
        {
            Board.SetData(_store.GetOrCreate(date));
        }
        UpdateHeader();
    }

    public void PlayEntrance()
    {
        Card.Opacity = 0;
        var tf = Card.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Card.RenderTransform = tf;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        tf.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void UpdateHeader()
    {
        var d = Board.Data.Date;
        DateTitle.Text = $"{d.Month}月{d.Day}日";
        TodayBadge.Visibility = d == DateOnly.FromDateTime(DateTime.Today) ? Visibility.Visible : Visibility.Collapsed;
        var (total, done) = Board.Data.Counts();
        SummaryText.Text = total == 0
            ? $"{WeekName(d)} · 暂无安排"
            : $"{WeekName(d)} · {done}/{total} 已完成";
    }

    private void OnDayChanged(object? sender, DateOnly date)
    {
        if (date != Board.Data.Date) return;
        UpdateHeader();
    }

    private static string WeekName(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日"
    };

    // ------------------------------------------------------------------ commands

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !(Keyboard.FocusedElement is TextBox { Text.Length: > 0 }))
        {
            Hide();
            e.Handled = true;
        }
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var entries = new List<MenuEntry>
        {
            MenuFlyout.Item("复制当日日程", "\uE8C8", CopyDayText)
        };
        if (App.Instance.Settings.QuickNotes)
            entries.Add(MenuFlyout.Item("新建便签", "\uE70F", () => App.Instance.Notes.OpenAtCursor()));
        entries.Add(MenuFlyout.Separator());
        entries.Add(MenuFlyout.Item("关闭", "\uE711", () => Hide()));
        MenuFlyout.Show(MenuButton, PlacementMode.Bottom, entries.ToArray());
        e.Handled = true;
    }

    private void CopyDayText()
    {
        if (Board.Data.IsEmpty) return;
        var day = Board.Data;
        var sb = new StringBuilder();
        sb.AppendLine($"{day.Date:yyyy年M月d日}  {WeekName(day.Date)}");
        foreach (var it in day.FreeItems) Append(sb, it, string.Empty);
        foreach (var c in day.Categories)
        {
            sb.AppendLine($"【{c.Title}】");
            foreach (var it in c.Items) Append(sb, it, "  ");
        }
        if (ClipText.TrySet(sb.ToString(), out var error)) return;
        Log.Write("copy day failed: " + error);

        static void Append(StringBuilder sb, TodoItem it, string indent)
        {
            sb.AppendLine($"{indent}{(it.IsDone ? "√" : "○")} {it.Text}");
            if (it.HasNote) sb.AppendLine($"{indent}   · {it.Note}");
        }
    }
}
