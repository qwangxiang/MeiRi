using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MeiRi;

/// <summary>Small visual-tree helpers shared by the windows and the task board.</summary>
public static class Visuals
{
    public static DependencyObject? ParentOf(DependencyObject d)
    {
        if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
        if (d is ContentElement ce) return ContentOperations.GetParent(ce) ?? (ce as FrameworkContentElement)?.Parent;
        return LogicalTreeHelper.GetParent(d);
    }

    public static T? AncestorOf<T>(DependencyObject? start) where T : DependencyObject
    {
        var d = start;
        while (d is not null)
        {
            if (d is T t) return t;
            d = ParentOf(d);
        }
        return null;
    }

    public static FrameworkElement? AncestorWithTag<T>(DependencyObject? start, out T? tag) where T : class
    {
        var d = start;
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Tag is T t)
            {
                tag = t;
                return fe;
            }
            d = ParentOf(d);
        }
        tag = null;
        return null;
    }

    public static Rect BoundsIn(FrameworkElement element, Visual ancestor)
    {
        try
        {
            return element.TransformToVisual(ancestor)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    /// <summary>True when the click came from a control that owns its own input (button, text box).</summary>
    public static bool IsInsideInteractive(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is ButtonBase or System.Windows.Controls.TextBox) return true;
            d = ParentOf(d);
        }
        return false;
    }
}
