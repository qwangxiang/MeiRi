using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using MeiRi.Services;

namespace MeiRi.ViewModels;

public enum DotKind
{
    None,
    Red,
    Green
}

public sealed class DayCell : ObservableObject
{
    private bool _isToday;
    private DotKind _dot;
    private bool _isDropTarget;
    private int _level;

    public DayCell(DateOnly date, bool isCurrentMonth)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    public DateOnly Date { get; }
    public int Day => Date.Day;
    public bool IsCurrentMonth { get; }
    public bool IsWeekend { get; }

    public bool IsToday
    {
        get => _isToday;
        set => Set(ref _isToday, value);
    }

    public DotKind Dot
    {
        get => _dot;
        set => Set(ref _dot, value);
    }

    /// <summary>
    /// How many tasks were finished on this day, bucketed like a git contribution graph
    /// (1-2 → level 1, 3-4 → 2, 5-7 → 3, 8+ → 4). 0 means "nothing finished".
    /// </summary>
    public int Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public void Refresh(DataStore store, DateOnly today)
    {
        IsToday = Date == today;
        var (total, done) = store.GetCounts(Date);
        Dot = total == 0 ? DotKind.None : done >= total ? DotKind.Green : DotKind.Red;
        Level = done switch
        {
            0 => 0,
            <= 2 => 1,
            <= 4 => 2,
            <= 7 => 3,
            _ => 4
        };
    }
}

public sealed class CalendarViewModel : ObservableObject
{
    private readonly DataStore _store;
    private DateTime _month;
    private readonly DispatcherTimer _midnight;
    private DateTime _lastToday;
    private int _backlogLeft;
    private string _statsLine = string.Empty;
    private DateOnly _today;

    public CalendarViewModel(DataStore store)
    {
        _store = store;
        var today = DateTime.Today;
        _today = DateOnly.FromDateTime(today);
        _month = new DateTime(today.Year, today.Month, 1);
        _lastToday = today;
        Build();
        store.DayChanged += (_, date) => RefreshDate(date);
        store.BacklogChanged += (_, _) => RefreshBacklog();
        _midnight = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _midnight.Tick += (_, _) =>
        {
            var t = DateTime.Today;
            if (t == _lastToday) return;
            _lastToday = t;
            _today = DateOnly.FromDateTime(t);
            foreach (var cell in Cells) cell.Refresh(_store, _today);
            RefreshStats();
        };
        _midnight.Start();
        RefreshBacklog();
    }

    public ObservableCollection<DayCell> Cells { get; } = new();

    public string MonthTitle => $"{_month:yyyy年M月}";

    public bool IsViewingThisMonth
        => _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month;

    public bool IsNotViewingThisMonth => !IsViewingThisMonth;

    public DateTime Month => _month;

    public RelayCommand PrevMonthCommand { get; private set; } = null!;
    public RelayCommand NextMonthCommand { get; private set; } = null!;
    public RelayCommand TodayCommand { get; private set; } = null!;

    /// <summary>True while the backlog still holds unfinished tasks (drives the red dot).</summary>
    public bool HasBacklogLeft => _backlogLeft > 0;

    public string BacklogHint => _backlogLeft > 0
        ? $"待定区 · {_backlogLeft} 项未完成"
        : "待定区";

    /// <summary>The one-line footer of the calendar: completed tasks this month / in the last year.</summary>
    public string StatsLine => _statsLine;

    private void RefreshStats()
    {
        var (month, year) = _store.GetDoneTotals(_today);
        var text = $"本月已完成日程：{month} 项     最近一年已完成日程：{year} 项";
        if (text == _statsLine) return;
        _statsLine = text;
        OnPropertyChanged(nameof(StatsLine));
    }

    private void Build()
    {
        Cells.Clear();
        _today = DateOnly.FromDateTime(DateTime.Today);
        var first = _month;
        int offset = ((int)first.DayOfWeek + 6) % 7; // monday-first
        var start = first.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var date = DateOnly.FromDateTime(start.AddDays(i));
            var cell = new DayCell(date, date.Month == _month.Month && date.Year == _month.Year);
            cell.Refresh(_store, _today);
            Cells.Add(cell);
        }
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(IsViewingThisMonth));
        OnPropertyChanged(nameof(IsNotViewingThisMonth));
        RefreshStats();
        PrevMonthCommand = new RelayCommand(() => Shift(-1));
        NextMonthCommand = new RelayCommand(() => Shift(1));
        TodayCommand = new RelayCommand(GoToday);
    }

    public void Shift(int months)
    {
        _month = _month.AddMonths(months);
        Build();
    }

    public void GoToday()
    {
        var today = DateTime.Today;
        if (_month.Year == today.Year && _month.Month == today.Month)
        {
            foreach (var cell in Cells) cell.Refresh(_store, _today);
            RefreshStats();
        }
        else
        {
            _month = new DateTime(today.Year, today.Month, 1);
            Build();
        }
    }

    public void RefreshDate(DateOnly date)
    {
        foreach (var cell in Cells)
        {
            if (cell.Date == date) cell.Refresh(_store, _today);
        }
        RefreshBacklog();
        RefreshStats();
    }

    private void RefreshBacklog()
    {
        var (total, done) = _store.GetBacklogCounts();
        int left = total - done;
        if (left == _backlogLeft) return;
        _backlogLeft = left;
        OnPropertyChanged(nameof(HasBacklogLeft));
        OnPropertyChanged(nameof(BacklogHint));
    }

    public DayCell? CellAt(DateOnly date)
    {
        foreach (var c in Cells)
            if (c.Date == date) return c;
        return null;
    }
}
