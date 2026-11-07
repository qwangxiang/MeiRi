using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeiRi.Models;

/// <summary>A single task entry. One object per day (days never share items).</summary>
public sealed class TodoItem : ObservableObject
{
    private string _text = string.Empty;
    private string _note = string.Empty;
    private bool _isDone;
    private bool _isEditingNote;
    private bool _isEditingText;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    /// <summary>Free-form remark rendered under the task in a smaller grey font.</summary>
    public string Note
    {
        get => _note;
        set
        {
            if (!Set(ref _note, (value ?? string.Empty).Replace("\r", string.Empty))) return;
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => _note.Length > 0;

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    /// <summary>UI-only: the remark input under this row is open.</summary>
    public bool IsEditingNote
    {
        get => _isEditingNote;
        set => Set(ref _isEditingNote, value);
    }

    /// <summary>UI-only: the task text itself is being edited inline (click the text).</summary>
    public bool IsEditingText
    {
        get => _isEditingText;
        set => Set(ref _isEditingText, value);
    }

    public TodoItem Clone() => new() { Id = Id, Text = Text, Note = Note, IsDone = IsDone };
}

/// <summary>A heading (category) that groups tasks inside one day.</summary>
public sealed class Category : ObservableObject
{
    private string _title = string.Empty;
    private bool _isSelected;
    private bool _isEditing;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title
    {
        get => _title;
        set => Set(ref _title, value ?? string.Empty);
    }

    public ObservableCollection<TodoItem> Items { get; set; } = new();

    /// <summary>UI-only: category is the target for newly added tasks.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>UI-only: header title is being edited inline.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => Set(ref _isEditing, value);
    }
}

/// <summary>
/// Everything scheduled for exactly one date — and, with <see cref="Date"/> left at its
/// default value, the single "待定区" (backlog) bucket, which has no date at all.
/// </summary>
public sealed class DayData : ObservableObject
{
    public DateOnly Date { get; set; }

    public bool IsBacklog => Date == default;

    /// <summary>Tasks that do not belong to any category — rendered at the very top.</summary>
    public ObservableCollection<TodoItem> FreeItems { get; set; } = new();

    public ObservableCollection<Category> Categories { get; set; } = new();

    public bool IsEmpty => FreeItems.Count == 0 && Categories.Count == 0;

    public int TotalCount
    {
        get
        {
            int n = FreeItems.Count;
            foreach (var c in Categories) n += c.Items.Count;
            return n;
        }
    }

    public int DoneCount
    {
        get
        {
            int n = 0;
            foreach (var it in AllItems)
                if (it.IsDone) n++;
            return n;
        }
    }

    public IEnumerable<TodoItem> AllItems
    {
        get
        {
            foreach (var it in FreeItems) yield return it;
            foreach (var c in Categories)
                foreach (var it in c.Items) yield return it;
        }
    }

    public (int Total, int Done) Counts() => (TotalCount, DoneCount);

    public Category? FindCategory(string title)
    {
        var t = (title ?? string.Empty).Trim();
        return Categories.FirstOrDefault(c =>
            string.Equals(c.Title.Trim(), t, StringComparison.OrdinalIgnoreCase));
    }
}
