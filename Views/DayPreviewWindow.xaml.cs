using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Animation;
using MeiRi.Models;

namespace MeiRi.Views;

public sealed class PreviewSection
{
    public string Title { get; init; } = string.Empty;
    public bool ShowTitle => Title.Length > 0;
    public List<TodoItem> Items { get; } = new();
}

public partial class DayPreviewWindow : Window
{
    public DateOnly? DateOfDay { get; private set; }

    /// <summary>Height of the visible card (the window is taller than the card by design).</summary>
    public double VisibleHeightDip => Card.ActualHeight > 0 ? Card.ActualHeight : 200;

    public DayPreviewWindow()
    {
        InitializeComponent();
        // the "…" row follows the real list height, so it is right no matter how the card was
        // populated (hover, day edited while shown, or rendered off screen for a screenshot)
        SectionScroller.LayoutUpdated += (_, _) => UpdateOverflow();
        SourceInitialized += (_, _) =>
        {
            // the card sits right under the cursor on purpose, so it must not take mouse input
            WindowEffects.MakeToolWindow(this);
            WindowEffects.MakeClickThrough(this);
        };
    }

    public void ShowDay(DateOnly date, DayData? day)
    {
        DateOfDay = date;
        DateText.Text = date.Month + "月" + date.Day + "日";
        WeekText.Text = WeekName(date);
        if (day is null)
        {
            CountText.Text = string.Empty;
            Sections.ItemsSource = Array.Empty<PreviewSection>();
            UpdateOverflow();
            return;
        }

        var (total, done) = day.Counts();
        CountText.Text = done == total ? $"全部完成 · {total}" : $"{done}/{total} 已完成";

        var sections = new List<PreviewSection>();
        if (day.FreeItems.Count > 0)
        {
            var s = new PreviewSection();
            s.Items.AddRange(day.FreeItems);
            sections.Add(s);
        }
        foreach (var c in day.Categories)
        {
            if (c.Items.Count == 0) continue;
            var s = new PreviewSection { Title = c.Title };
            s.Items.AddRange(c.Items);
            sections.Add(s);
        }
        Sections.ItemsSource = sections;
        UpdateOverflow();
    }

    /// <summary>
    /// Shows the "…" row when the list is taller than the card instead of a scrollbar: the
    /// preview is click-through, so a scrollbar could never be dragged anyway.
    /// </summary>
    public void UpdateOverflow()
    {
        if (SectionScroller.ViewportHeight <= 0) return; // not laid out yet: nothing to decide
        var want = SectionScroller.ExtentHeight > SectionScroller.ViewportHeight + 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (MoreHint.Visibility != want) MoreHint.Visibility = want;
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

    public void PlayEntrance()
    {
        Card.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        var slide = new ThicknessAnimation(new Thickness(0, 6, 0, 0), new Thickness(0), TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Card.BeginAnimation(OpacityProperty, fade);
        Card.BeginAnimation(MarginProperty, slide);
    }
}
