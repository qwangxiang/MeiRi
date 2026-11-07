using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using MeiRi.Interop;
using MeiRi.Services;

namespace MeiRi;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public DataStore Store { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;
    public CalendarHost Calendar { get; private set; } = null!;
    public QuickNoteManager Notes { get; private set; } = null!;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private GlobalMouseHook? _hook;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startup = Stopwatch.StartNew();

        // dev only: hold the clipboard open for a while, the way a clipboard manager does, so the
        // "cut while the clipboard is busy" case can be reproduced from a second process
        if (e.Args.Length > 0 && e.Args[0] == "--cliphold")
        {
            int ms = e.Args.Length > 1 && int.TryParse(e.Args[1], out int parsed) ? parsed : 2000;
            // a real window as the owner: OpenClipboard(NULL) does not lock other tasks out
            var owner = Native.FindWindow("Shell_TrayWnd", null);
            bool open = Native.OpenClipboard(owner);
            Log.Write($"cliphold# open={open} owner=0x{owner.ToInt64():X} for {ms}ms pid={Environment.ProcessId}");
            Thread.Sleep(ms);
            if (open) Native.CloseClipboard();
            Shutdown();
            return;
        }

        // dev only: render the views to PNG without showing anything
        if (e.Args.Length > 1 && e.Args[0] == "--shot")
        {
            base.OnStartup(e);
            try
            {
                DevShots.Run(e.Args[1]);
            }
            catch (Exception ex)
            {
                Log.Write("shot failed: " + ex);
            }
            Shutdown();
            return;
        }

        // dev only: regenerate the README screenshots (demo data, 2x, desktop backdrop)
        if (e.Args.Length > 1 && e.Args[0] == "--gallery")
        {
            base.OnStartup(e);
            try
            {
                DevGallery.Run(e.Args[1]);
            }
            catch (Exception ex)
            {
                Log.Write("gallery failed: " + ex);
            }
            Shutdown();
            return;
        }

        // dev only: keystroke cost / flyout pixel captures / taskbar hit testing
        if (e.Args.Length > 0 && (e.Args[0] == "--typing" || e.Args[0] == "--flyout" || e.Args[0] == "--taskbar"))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // --hw: compare against hardware rendering instead of the default software one
            if (!e.Args.Contains("--hw"))
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            AppPaths.Ensure();
            if (e.Args[0] == "--typing") DevLab.Typing(Shutdown);
            else if (e.Args[0] == "--taskbar") DevLab.Taskbar(Shutdown);
            else DevLab.Flyout(e.Args.Length > 1 ? e.Args[1] : null, Shutdown);
            return;
        }

        _mutex = new Mutex(true, @"Global\MeiRi.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(@"Global\MeiRi.Show");
                handle.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("unhandled: " + args.Exception);
            args.Handled = true;
        };

        try
        {
            AppPaths.Ensure();
            Settings = SettingsService.Load();
            Store = new DataStore(AppPaths.DataFile);
            var load = Stopwatch.StartNew();
            Store.Load();
            Log.Write($"loaded {Store.DayCount} day(s) in {load.Elapsed.TotalMilliseconds:F1} ms");

            AutoStartService.Apply(Settings.AutoStart);

            Notes = new QuickNoteManager { Enabled = Settings.QuickNotes };
            Calendar = new CalendarHost(Store, Settings);
            Calendar.ExitRequested += ShutdownApp;
            Calendar.Start();

            _hook = new GlobalMouseHook();
            _hook.TaskbarDoubleClick += OnTaskbarDoubleClick;
            _hook.Install();

            CreateShowListener();
            InstallStallLogger();
            ScheduleWarmUp();
            if (e.Args.Contains("--diag"))
            {
                var diag = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10)
                };
                diag.Tick += (_, _) =>
                {
                    using var p = System.Diagnostics.Process.GetCurrentProcess();
                    Log.Write($"diag ws={p.WorkingSet64 / 1048576}MB priv={p.PrivateMemorySize64 / 1048576}MB " +
                              $"managed={GC.GetTotalMemory(false) / 1048576}MB gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                              $"windows={Windows.Count} notes={Notes.Windows.Count} hookMoves={_hook?.MovesSeen}");
                };
                diag.Start();
            }
            if (e.Args.Contains("--probe")) DevProbe.Run(Calendar, Store, ShutdownApp);
            if (e.Args.Contains("--drag")) DevDrag.Run(Calendar, Notes, ShutdownApp);
            if (e.Args.Contains("--stack")) DevStack.Run(Calendar, ShutdownApp);
            if (e.Args.Contains("--preview")) DevPreview.Run(Calendar, Store, ShutdownApp);
            if (e.Args.Contains("--drop")) DevDrop.Run(Calendar, Store, ShutdownApp);
            if (e.Args.Contains("--ui")) DevUi.Run(Calendar, Store, Notes, Settings, ShutdownApp);
            if (e.Args.Contains("--clip")) DevClip.Run(Notes, ShutdownApp);
            if (e.Args.Contains("--hold")) DevProbe.Hold(Calendar, Store, !e.Args.Contains("--nopreview"), ShutdownApp);
            if (e.Args.Contains("--frames")) DevLab.Interactions(Calendar, Store, ShutdownApp);
            Log.Write($"startup complete in {startup.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Write("startup failed: " + ex);
            MessageBox.Show("「每日」启动失败：" + ex.Message, "每日", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Builds one throw-away window of each kind a moment after startup, so that the first click
    /// on a day (or the first note) does not wait for the first Show() of that window type.
    /// </summary>
    private void ScheduleWarmUp()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Calendar?.WarmUp();
            if (Settings?.QuickNotes == true) Notes?.WarmUp();
        };
        timer.Start();
    }

    private void InstallStallLogger()
    {
        // A dispatcher timer at background priority is the cheapest way to notice that the UI
        // thread stopped pumping: it simply fires late. Only real stalls (>400 ms, at most one
        // line per 5 s) are logged, so the file stays clean while still recording why typing or
        // dragging felt sticky.
        var watch = Stopwatch.StartNew();
        double last = 0;
        double lastLog = -10000;
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        timer.Tick += (_, _) =>
        {
            double now = watch.Elapsed.TotalMilliseconds;
            double gap = now - last - 120;
            last = now;
            if (gap < 400 || now - lastLog < 5000) return;
            lastLog = now;
            Log.Write($"ui stall {gap:F0} ms (notes={Notes.Windows.Count}, windows={Windows.Count})");
        };
        timer.Start();
    }

    private void CreateShowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\MeiRi.Show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            // the usual cause is a double click on the app's own taskbar button
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Settings.QuickNotes) return;
                Notes.OpenAtCursor();
            }));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OnTaskbarDoubleClick(int x, int y)
    {
        if (_shuttingDown) return;
        if (!Settings.QuickNotes)
        {
            Log.Write("taskbar double click ignored: quick notes are off");
            return;
        }
        // the taskbar is one window: only its empty strip opens a note, never an icon
        // (app button, pinned app, tray icon or the start button)
        if (!TaskbarQuery.IsBlankArea(x, y))
        {
            Log.Write($"taskbar double click ignored at {x},{y}: icon");
            return;
        }
        // the point is inside the taskbar, so the note is clamped to the work area above it
        Notes.OpenAt(x, y);
    }

    public void SetAutoStart(bool enabled)
    {
        Settings.AutoStart = enabled;
        AutoStartService.Apply(enabled);
        SettingsService.Save(Settings);
    }

    /// <summary>Turns the quick-note feature on or off; existing notes are closed when it goes off.</summary>
    public void SetQuickNotes(bool enabled)
    {
        Settings.QuickNotes = enabled;
        Notes.Enabled = enabled;
        if (!enabled) Notes.CloseAll();
        SettingsService.Save(Settings);
        Log.Write($"quick notes {(enabled ? "on" : "off")}");
    }

    public void ShutdownApp()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            Calendar?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("calendar dispose: " + ex.Message);
        }
        try
        {
            Notes?.CloseAll();
        }
        catch { }
        try
        {
            Store?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("store dispose: " + ex.Message);
        }
        _hook?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Store?.Flush();
        }
        catch { }
        _hook?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}
