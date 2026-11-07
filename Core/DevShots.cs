using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeiRi.Models;
using MeiRi.Services;
using MeiRi.Views;

namespace MeiRi;

/// <summary>
/// Dev-only helper (<c>MeiRi.exe --shot &lt;dir&gt;</c>): renders every window to a PNG so the
/// visual result can be inspected without showing anything on screen.
/// </summary>
public static class DevShots
{
    private static readonly Brush Wallpaper = CreateWallpaper();

    private static Brush CreateWallpaper()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#BFD9F2"), 0));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#DCE8F7"), 0.55));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#C9DDEB"), 1));
        b.Freeze();
        return b;
    }

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        AppPaths.Ensure();

        var store = new DataStore(AppPaths.DataFile);
        store.Load();
        Seed(store);

        var settings = new Settings();
        var host = new CalendarHost(store, settings);
        var today = DateOnly.FromDateTime(DateTime.Today);

        ShotWindow(new CalendarWindow(host, host.ViewModel), 384, 402, Path.Combine(dir, "calendar.png"));

        var editor = new DayEditorWindow(host, store);
        editor.SetDay(today);
        var day = store.GetExisting(today);
        var editing = day?.FreeItems.FirstOrDefault()
                      ?? day?.Categories.SelectMany(c => c.Items).FirstOrDefault();
        if (editing is not null) editing.IsEditingText = true;
        ShotWindow(editor, 452, 640, Path.Combine(dir, "editor.png"));
        if (editing is not null) editing.IsEditingText = false;

        ShotWindow(new BacklogWindow(host, store), 452, 580, Path.Combine(dir, "backlog.png"));

        // a selected category (the whole frame is a big target) and a task whose text really is
        // two lines, to check what Ctrl+Enter produces
        var editor2 = new DayEditorWindow(host, store);
        editor2.SetDay(today);
        var todayCat = store.GetExisting(today)?.Categories.FirstOrDefault();
        if (todayCat is not null) todayCat.IsSelected = true;
        var todayItem = store.GetExisting(today)?.FreeItems.FirstOrDefault();
        if (todayItem is not null) todayItem.Text = todayItem.Text + "\n第二行（Ctrl+Enter 换的）";
        ShotWindow(editor2, 452, 640, Path.Combine(dir, "editor-category.png"));
        if (todayItem is not null) todayItem.Text = todayItem.Text.Replace("\n第二行（Ctrl+Enter 换的）", string.Empty);
        if (todayCat is not null) todayCat.IsSelected = false;

        var preview = new DayPreviewWindow();
        preview.ShowDay(today, store.GetOrCreate(today));
        ShotWindow(preview, 304, double.PositiveInfinity, Path.Combine(dir, "preview.png"));

        // a day with more tasks than the card can hold: the list must end in a "…" row,
        // never in a scrollbar (the card is click-through, so a bar could not be dragged)
        var longDay = new DayData { Date = today.AddDays(1) };
        var longWork = new Category { Title = "工作" };
        for (int i = 1; i <= 18; i++)
        {
            longWork.Items.Add(new TodoItem
            {
                Text = $"第 {i} 项：这一行故意写得比较长，用来确认超出一行时是省略号而不是换行",
                Note = "备注不该出现在悬浮卡里",
                IsDone = i % 4 == 0
            });
        }
        longDay.Categories.Add(longWork);
        var previewLong = new DayPreviewWindow();
        previewLong.ShowDay(longDay.Date, longDay);
        ShotWindow(previewLong, 304, double.PositiveInfinity, Path.Combine(dir, "preview-more.png"));

        ShotWindow(NoteWindow(), 430, 300, Path.Combine(dir, "note.png"));

        var card = MenuFlyout.BuildCard(new[]
        {
            MenuFlyout.Item("打开今天", "\uE787", () => { }),
            MenuFlyout.Item("待定区", "\uE8A5", () => { }),
            MenuFlyout.Item("新建便签", "\uE70F", () => { }),
            MenuFlyout.Separator(),
            MenuFlyout.Toggle("开机自启动", "\uE713", true, _ => { }),
            MenuFlyout.Toggle("快捷便签", "\uE70F", true, _ => { }),
            MenuFlyout.Separator(),
            MenuFlyout.Item("关于「每日」", "\uE946", () => { }),
            MenuFlyout.Item("退出", "\uE7E8", () => { }, danger: true)
        });
        ShotElement(card, Path.Combine(dir, "menu.png"));
    }

    private static QuickNoteWindow NoteWindow()
    {
        var group = new QuickNoteGroup();
        var first = new QuickNote { Text = "买牛奶、鸡蛋，顺便取快递" };
        var second = new QuickNote { Text = "周五下午三点 客户电话会议" };
        var third = new QuickNote { Text = "想法：把日历的悬浮预览做成可点" };
        group.Notes.Add(first);
        group.Notes.Add(second);
        group.Notes.Add(third);
        group.Selected = first;
        return new QuickNoteWindow(new QuickNoteManager(), group);
    }

    private static void Seed(DataStore store)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = store.GetOrCreate(today);
        if (day.IsEmpty)
        {
            day.FreeItems.Add(new TodoItem { Text = "回复客户邮件", Note = "附上上周的排期表" });
            day.FreeItems.Add(new TodoItem { Text = "买牛奶" });
            var work = new Category { Title = "工作" };
            work.Items.Add(new TodoItem { Text = "写周报", IsDone = true });
            work.Items.Add(new TodoItem { Text = "评审新版本设计稿", Note = "重点看拖拽手感" });
            var life = new Category { Title = "生活" };
            life.Items.Add(new TodoItem { Text = "预约牙医", IsDone = true });
            day.Categories.Add(work);
            day.Categories.Add(life);
        }

        // give the calendar some completed-task shading on nearby days
        int[] doneCounts = { 1, 3, 5, 9, 2, 6, 12, 4 };
        for (int i = 0; i < doneCounts.Length; i++)
        {
            var d = store.GetOrCreate(today.AddDays(i - 5));
            if (!d.IsEmpty) continue;
            var cat = new Category { Title = "已完成" };
            for (int n = 0; n < doneCounts[i]; n++)
                cat.Items.Add(new TodoItem { Text = "已完成事项 " + (n + 1), IsDone = true });
            d.Categories.Add(cat);
        }

        var backlog = store.Backlog;
        if (backlog.IsEmpty)
        {
            backlog.FreeItems.Add(new TodoItem { Text = "整理书架", Note = "先按类别分堆" });
            var idea = new Category { Title = "想做的项目" };
            idea.Items.Add(new TodoItem { Text = "做一个桌面番茄钟" });
            idea.Items.Add(new TodoItem { Text = "学习 WPF 组合动画", IsDone = true });
            backlog.Categories.Add(idea);
        }
    }

    private static void ShotWindow(Window window, double width, double height, string path)
    {
        var content = window.Content as FrameworkElement;
        window.Content = null;
        if (content is null) return;
        var host = new ContentControl
        {
            Content = content,
            DataContext = window.DataContext,
            FontFamily = window.FontFamily,
            FontSize = window.FontSize,
            Width = width,
            Background = Wallpaper,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        ShotElement(host, path, height);
    }

    private static void ShotElement(FrameworkElement element, string path, double height = double.PositiveInfinity)
    {
        if (!(element is ContentControl))
        {
            var wrapper = new ContentControl
            {
                Content = element,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 12,
                Background = Wallpaper
            };
            TextOptions.SetTextFormattingMode(wrapper, TextFormattingMode.Display);
            element = wrapper;
        }

        double width = double.IsNaN(element.Width) ? double.NaN : element.Width;
        var size = double.IsNaN(width)
            ? new Size(double.PositiveInfinity, height)
            : new Size(width, height);
        element.Measure(size);
        var desired = element.DesiredSize;
        double w = double.IsNaN(width) ? Math.Ceiling(desired.Width) : width;
        double h = double.IsInfinity(height) ? Math.Ceiling(desired.Height) : height;
        element.Arrange(new Rect(0, 0, Math.Max(1, w), Math.Max(1, h)));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Max(1, w), (int)Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
