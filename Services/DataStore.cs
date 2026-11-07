using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using MeiRi.Models;

namespace MeiRi.Services;

/// <summary>
/// In-memory store with a debounced, atomic binary persistence layer.
/// Every day lives in RAM (a few KB per year of tasks) and reads are O(1).
/// The undated "待定区" bucket (backlog) is stored alongside the days.
/// </summary>
public sealed class DataStore : IDisposable
{
    private const int FormatVersion = 3;
    private static readonly byte[] Magic = { (byte)'M', (byte)'R', (byte)'D', (byte)'3' };
    private const int MaxStringBytes = 8192;

    private readonly Dictionary<int, DayData> _days = new();
    private readonly string _path;
    private readonly DispatcherTimer _saveTimer;
    private readonly object _serializeGate = new();
    private bool _disposed;

    public event EventHandler<DateOnly>? DayChanged;
    public event EventHandler? BacklogChanged;

    /// <summary>Tasks that the user wants to do but has not scheduled yet.</summary>
    public DayData Backlog { get; } = new();

    public DataStore(string path)
    {
        _path = path;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public int DayCount => _days.Count;

    public DayData? GetExisting(DateOnly date)
        => _days.TryGetValue(Key(date), out var d) ? d : null;

    public DayData GetOrCreate(DateOnly date)
    {
        int key = Key(date);
        if (_days.TryGetValue(key, out var d)) return d;
        d = new DayData { Date = date };
        _days[key] = d;
        return d;
    }

    /// <summary>Resolves the bucket by date — <c>null</c> means the backlog.</summary>
    public DayData GetTarget(DateOnly? date)
        => date is { } d ? GetOrCreate(d) : Backlog;

    public (int Total, int Done) GetCounts(DateOnly date)
    {
        var d = GetExisting(date);
        return d is null ? (0, 0) : d.Counts();
    }

    public (int Total, int Done) GetBacklogCounts() => Backlog.Counts();

    /// <summary>
    /// Completed-task totals shown at the bottom of the calendar: everything finished in the
    /// calendar month of <paramref name="today"/>, and everything finished in the last 365 days.
    /// One pass over the days in RAM, no allocation — it runs once per edit, not per frame.
    /// </summary>
    public (int Month, int Year) GetDoneTotals(DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var yearStart = today.AddYears(-1).AddDays(1);
        int month = 0, year = 0;
        foreach (var day in _days.Values)
        {
            if (day.IsEmpty) continue;
            var date = day.Date;
            bool inMonth = date >= monthStart && date < monthEnd;
            bool inYear = date >= yearStart && date <= today;
            if (!inMonth && !inYear) continue;
            int done = day.DoneCount;
            if (done == 0) continue;
            if (inMonth) month += done;
            if (inYear) year += done;
        }
        return (month, year);
    }

    /// <summary>Call after any mutation inside a day.</summary>
    public void Notify(DateOnly date)
    {
        StartSave();
        DayChanged?.Invoke(this, date);
    }

    /// <summary>Call after any mutation inside the backlog.</summary>
    public void NotifyBacklog()
    {
        StartSave();
        BacklogChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyTarget(DateOnly? date)
    {
        if (date is { } d) Notify(d);
        else NotifyBacklog();
    }

    private void StartSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static int Key(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;

    private static DateOnly FromKey(int key)
        => new(key / 10000, key / 100 % 100, key % 100);

    // ---------------------------------------------------------------- load

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length < 8) return;
            using var ms = new MemoryStream(bytes, writable: false);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2])
            {
                Log.Write("data file magic mismatch — starting fresh");
                return;
            }
            int version = r.ReadInt32();
            if (version < 1 || version > FormatVersion)
            {
                Log.Write($"unsupported data version {version}");
                return;
            }
            if (version >= 3 && r.ReadBoolean()) ReadBody(r, Backlog, version);

            int dayCount = r.ReadInt32();
            if (dayCount < 0 || dayCount > 200000) throw new InvalidDataException("bad day count");
            for (int i = 0; i < dayCount; i++)
            {
                int key = r.ReadInt32();
                var day = new DayData { Date = FromKey(key) };
                ReadBody(r, day, version);
                _days[key] = day;
            }
        }
        catch (Exception ex)
        {
            Log.Write("load failed: " + ex);
            try
            {
                if (File.Exists(_path)) File.Copy(_path, _path + ".corrupt", true);
            }
            catch { }
            _days.Clear();
            Reset(Backlog);
        }
    }

    private static void Reset(DayData day)
    {
        day.FreeItems.Clear();
        day.Categories.Clear();
    }

    /// <summary>Reads the free items and the categories; remarks only exist from format 3 on.</summary>
    private static void ReadBody(BinaryReader r, DayData day, int version)
    {
        ReadItems(r, day.FreeItems, version);
        int catCount = r.ReadInt32();
        if (catCount < 0 || catCount > 100000) throw new InvalidDataException("bad category count");
        for (int c = 0; c < catCount; c++)
        {
            var cat = new Category { Id = new Guid(r.ReadBytes(16)), Title = ReadString(r) };
            ReadItems(r, cat.Items, version);
            day.Categories.Add(cat);
        }
    }

    private static void ReadItems(BinaryReader r, ObservableCollection<TodoItem> target, int version)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > 200000) throw new InvalidDataException("bad item count");
        for (int i = 0; i < count; i++)
        {
            var item = new TodoItem
            {
                Id = new Guid(r.ReadBytes(16)),
                IsDone = r.ReadBoolean()
            };
            if (version >= 3) item.Note = ReadString(r);
            item.Text = ReadString(r);
            target.Add(item);
        }
    }

    private static string ReadString(BinaryReader r)
    {
        int len = r.Read7BitEncodedInt();
        if (len < 0 || len > MaxStringBytes) throw new InvalidDataException("bad string length");
        return len == 0 ? string.Empty : Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    // ---------------------------------------------------------------- save

    private void SaveNow()
    {
        byte[] data;
        lock (_serializeGate) data = Serialize();
        Task.Run(() => WriteAtomic(data));
    }

    private byte[] Serialize()
    {
        var days = _days.Values.Where(d => !d.IsEmpty).OrderBy(d => d.Date).ToList();
        using var ms = new MemoryStream(Math.Max(4096, days.Count * 512));
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(!Backlog.IsEmpty);
        if (!Backlog.IsEmpty) WriteBody(w, Backlog);
        w.Write(days.Count);
        foreach (var d in days)
        {
            w.Write(Key(d.Date));
            WriteBody(w, d);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteBody(BinaryWriter w, DayData day)
    {
        WriteItems(w, day.FreeItems);
        w.Write(day.Categories.Count);
        foreach (var c in day.Categories)
        {
            w.Write(c.Id.ToByteArray());
            w.Write(c.Title ?? string.Empty);
            WriteItems(w, c.Items);
        }
    }

    private static void WriteItems(BinaryWriter w, IReadOnlyList<TodoItem> items)
    {
        w.Write(items.Count);
        foreach (var it in items)
        {
            w.Write(it.Id.ToByteArray());
            w.Write(it.IsDone);
            w.Write(it.Note ?? string.Empty);
            w.Write(it.Text ?? string.Empty);
        }
    }

    private void WriteAtomic(byte[] data)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Log.Write("save failed: " + ex.Message);
        }
    }

    public void Flush()
    {
        _saveTimer.Stop();
        try
        {
            byte[] data;
            lock (_serializeGate) data = Serialize();
            WriteAtomic(data);
        }
        catch (Exception ex)
        {
            Log.Write("flush failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        Flush();
    }
}
