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
/// Dev-only helper (<c>MeiRi.exe --gallery &lt;dir&gt;</c>): renders the screenshots used by
/// the README from built-in demo data — never the user's own schedule — on a soft desktop
/// backdrop, at 2x so the images stay crisp on high-DPI screens.
/// </summary>
public static class DevGallery
{
    private const double Scale = 2;
    private const double Pad = 36;

    /// <summary>Work that has to run after the first layout pass (see <see cref="Preview"/>).</summary>
    private static readonly System.Collections.Generic.List<Action> AfterLayout = new();

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        AppPaths.Ensure();

        var store = DemoStore();
        var settings = new Settings();
        var host = new CalendarHost(store, settings);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = store.GetExisting(today);

        // a day with far more tasks than the glance card can hold: it must end in "···"
        var longDay = new DayData { Date = today.AddDays(1) };
        var busy = new Category { Title = "工作" };
        string[] overflowing =
        {
            "站会：对一下本周的三件事", "整理数据看板的口径", "把周报里的数字再核对一遍",
            "评审新版设计稿（重点看拖拽）", "和测试过一遍回归清单", "准备周五的方案分享",
            "回复客户关于排期的邮件", "给数据库补两个索引", "把这周的 bug 分个优先级",
            "看一眼前端打包体积", "更新依赖到最新补丁版本", "写一份上线前的检查表",
            "和设计确认空状态的文案", "把仪表盘的加载时间压到 1 秒内", "给新手写一段接入文档",
            "复盘上周那次线上问题"
        };
        foreach (var text in overflowing) busy.Items.Add(new TodoItem { Text = text });
        longDay.Categories.Add(busy);

        // ---- the scene at the top of the README: calendar, day editor, glance card, note ----
        var scene = Scene(1600, 900);
        Place(scene, new CalendarWindow(host, host.ViewModel), 110, 210, 384, 402);
        Place(scene, Editor(host, store, today), 545, 110, 452, 640);
        Place(scene, Preview(today, day), 1035, 140, 330);
        Place(scene, Note(), 1055, 545, 430, 300);
        Render(scene, Path.Combine(dir, "hero.png"));

        // ---- one card per feature, on the same backdrop ----
        Card(dir, "calendar.png", new CalendarWindow(host, host.ViewModel), 384, 402);
        Card(dir, "editor.png", Editor(host, store, today), 452, 640);
        Card(dir, "preview.png", Preview(today, day), 330);
        Card(dir, "backlog.png", new BacklogWindow(host, store), 452, 580);
        Card(dir, "note.png", Note(), 430, 300);
        Card(dir, "preview-more.png", Preview(longDay.Date, longDay), 330);
        Card(dir, "menu.png", MenuFlyout.BuildCard(new[]
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
        }));
    }

    private static Window Editor(CalendarHost host, DataStore store, DateOnly day)
    {
        var window = new DayEditorWindow(host, store);
        window.SetDay(day);
        return window;
    }

    private static Window Preview(DateOnly day, DayData? data)
    {
        var window = new DayPreviewWindow();
        window.ShowDay(day, data);
        // the "···" row depends on a real layout pass, which an off-screen render does before
        // the visibility check would otherwise have run
        AfterLayout.Add(window.UpdateOverflow);
        return window;
    }

    private static Window Note()
    {
        var group = new QuickNoteGroup();
        group.Notes.Add(new QuickNote
        {
            Text = "今天要做的事\n· 买牛奶、鸡蛋，顺便取快递\n· 周五 15:00 客户电话会议\n· 想法：把悬浮预览做成可点"
        });
        group.Notes.Add(new QuickNote { Text = "会议记录\n1. 周报改成双周\n2. 便签支持多栏" });
        group.Notes.Add(new QuickNote { Text = "灵感\n把日历贴在桌面右上角，抬头就能看见。" });
        group.Selected = group.Notes[0];
        return new QuickNoteWindow(new QuickNoteManager(), group);
    }

    /// <summary>Demo content, written to a throwaway file: the real schedule is never touched.</summary>
    private static DataStore DemoStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "meiri-gallery");
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(dir)) File.Delete(file);

        var store = new DataStore(Path.Combine(dir, "schedule.dat"));
        store.Load();

        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = store.GetOrCreate(today);
        day.FreeItems.Add(new TodoItem { Text = "回复客户邮件", Note = "附上上周的排期表" });
        day.FreeItems.Add(new TodoItem { Text = "买牛奶" });

        var work = new Category { Title = "工作" };
        work.Items.Add(new TodoItem { Text = "写周报", IsDone = true });
        work.Items.Add(new TodoItem { Text = "评审新版设计稿", Note = "重点看拖拽手感" });
        work.Items.Add(new TodoItem { Text = "整理会议纪要" });
        var life = new Category { Title = "生活" };
        life.Items.Add(new TodoItem { Text = "预约牙医", IsDone = true });
        life.Items.Add(new TodoItem { Text = "跑步 5 公里" });
        day.Categories.Add(work);
        day.Categories.Add(life);

        // a few nearby days with completed tasks, so the heat map is visible
        int[] done = { 1, 3, 5, 9, 2, 6, 12, 4 };
        for (int i = 0; i < done.Length; i++)
        {
            var d = store.GetOrCreate(today.AddDays(i - 5));
            if (!d.IsEmpty) continue;
            var cat = new Category { Title = "已完成" };
            for (int n = 0; n < done[i]; n++)
                cat.Items.Add(new TodoItem { Text = "已完成事项 " + (n + 1), IsDone = true });
            d.Categories.Add(cat);
        }

        var backlog = store.Backlog;
        backlog.FreeItems.Add(new TodoItem { Text = "整理书架", Note = "先按类别分堆" });
        var ideas = new Category { Title = "想做的项目" };
        ideas.Items.Add(new TodoItem { Text = "做一个桌面番茄钟" });
        ideas.Items.Add(new TodoItem { Text = "学习 WPF 组合动画", IsDone = true });
        backlog.Categories.Add(ideas);

        return store;
    }

    // ---------------------------------------------------------------- rendering

    private static Grid Scene(double width, double height)
    {
        double w = width, h = height;
        var root = new Grid { Width = w, Height = h, UseLayoutRounding = true };
        root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = Gradient("#E7EEFA", "#F5F0FA", "#DDE8F7") });
        root.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = Glow("#5C7BE0", 0.30, new Point(0.16, 0.10), 0.62)
        });
        root.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Fill = Glow("#7FC8C2", 0.22, new Point(0.88, 0.86), 0.58)
        });
        var layer = new Canvas { Width = w, Height = h };
        root.Children.Add(layer);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        return root;
    }

    private static void Place(Grid scene, Window window, double x, double y, double width, double height = double.NaN)
        => Place(scene, Detach(window, width, height), x, y);

    private static void Place(Grid scene, FrameworkElement element, double x, double y)
    {
        var layer = scene.Children.OfType<Canvas>().First();
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        layer.Children.Add(element);
    }

    private static void Card(string dir, string name, Window window, double width, double height = double.NaN)
        => Card(dir, name, Detach(window, width, height));

    private static void Card(string dir, string name, FrameworkElement element)
    {
        if (double.IsNaN(element.Width) || double.IsNaN(element.Height)) element = Detach(element);
        double w = element.Width;
        double h = double.IsNaN(element.Height) ? element.ActualHeight : element.Height;
        var scene = Scene(w + Pad * 2, h + Pad * 2);
        Place(scene, element, Pad, Pad);
        Render(scene, Path.Combine(dir, name));
    }

    /// <summary>
    /// Takes the content out of a window and gives it a fixed size, so it can be arranged
    /// anywhere. An unknown height is measured first, and a free-standing element (the
    /// flyout card) is measured the same way.
    /// </summary>
    private static FrameworkElement Detach(Window window, double width, double height)
    {
        var content = window.Content as FrameworkElement;
        window.Content = null;
        var host = new ContentControl
        {
            Content = content,
            DataContext = window.DataContext,
            FontFamily = window.FontFamily,
            FontSize = window.FontSize,
            Width = width,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        if (double.IsNaN(height))
        {
            // one real layout pass first: the preview's "···" row only appears once the list
            // has been measured, and the card grows by that row's height
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, Math.Ceiling(host.DesiredSize.Height)));
            host.UpdateLayout();
            foreach (var action in AfterLayout) action();
            AfterLayout.Clear();
            host.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Ceiling(host.DesiredSize.Height);
        }
        host.Height = height;
        return host;
    }

    private static FrameworkElement Detach(FrameworkElement element)
    {
        var wrapper = new ContentControl
        {
            Content = element,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(wrapper, TextFormattingMode.Display);
        wrapper.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        wrapper.Width = Math.Ceiling(wrapper.DesiredSize.Width);
        wrapper.Height = Math.Ceiling(wrapper.DesiredSize.Height);
        return wrapper;
    }

    private static void Render(Grid root, string path)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(root.Width * Scale), (int)Math.Round(root.Height * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Brush Gradient(string from, string mid, string to)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(ColorOf(from), 0));
        brush.GradientStops.Add(new GradientStop(ColorOf(mid), 0.55));
        brush.GradientStops.Add(new GradientStop(ColorOf(to), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>A soft coloured blob for depth, the way a desktop wallpaper would have one.</summary>
    private static Brush Glow(string color, double alpha, Point center, double radius)
    {
        var c = ColorOf(color);
        var brush = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RadiusX = radius,
            RadiusY = radius
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 0.45 * 255), c.R, c.G, c.B), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        brush.Freeze();
        return brush;
    }

    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
