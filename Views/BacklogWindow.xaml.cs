using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeiRi.Services;

namespace MeiRi.Views;

/// <summary>
/// The undated bucket ("待定区"): the same category + task structure as a day, but the items
/// have not been scheduled yet. Anything can be dragged from here onto a calendar day.
/// </summary>
public partial class BacklogWindow : Window
{
    private readonly CalendarHost _host;
    private readonly DataStore _store;
    private bool _closingForShutdown;

    public BacklogWindow(CalendarHost host, DataStore store)
    {
        _host = host;
        _store = store;
        InitializeComponent();

        Board = new TaskBoard(host, store);
        BoardHost.Content = Board;
        Board.SetData(store.Backlog);
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
        store.BacklogChanged += OnBacklogChanged;
        store.DayChanged += OnDayChanged;
        Closed += (_, _) =>
        {
            store.BacklogChanged -= OnBacklogChanged;
            store.DayChanged -= OnDayChanged;
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public TaskBoard Board { get; }

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

    public void FocusInput()
    {
        WindowEffects.ForceForeground(this);
        Activate();
        Board.FocusInput();
    }

    public void PlayEntrance()
    {
        Card.Opacity = 0;
        var tf = new TranslateTransform(10, 0);
        Card.RenderTransform = tf;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        tf.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void UpdateHeader()
    {
        var (total, done) = Board.Data.Counts();
        SummaryText.Text = total == 0
            ? "想到就记下来，之后再安排到具体日期"
            : done == total
                ? $"{total} 项 · 全部完成，可以安排到日历了"
                : $"{done}/{total} 已完成 · 拖到日历上的某一天即可排期";
    }

    private void OnBacklogChanged(object? sender, EventArgs e) => UpdateHeader();

    private void OnDayChanged(object? sender, DateOnly date) => UpdateHeader();

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
        var entries = new List<MenuEntry>();
        if (App.Instance.Settings.QuickNotes)
            entries.Add(MenuFlyout.Item("新建便签", "\uE70F", () => App.Instance.Notes.OpenAtCursor()));
        entries.Add(MenuFlyout.Separator());
        entries.Add(MenuFlyout.Item("关闭", "\uE711", () => Hide()));
        MenuFlyout.Show(MenuButton, PlacementMode.Bottom, entries.ToArray());
        e.Handled = true;
    }
}
