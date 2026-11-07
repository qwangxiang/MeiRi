using System;
using System.Windows;
using System.Windows.Automation;

namespace MeiRi.Interop;

/// <summary>
/// Tells the empty part of the taskbar from its icons.
/// <para>
/// The taskbar of Windows 11 is drawn by XAML inside one window (the buttons are not child
/// windows), so the window class under the pointer is the same for an app icon and for the empty
/// strip. The UI Automation tree of the taskbar does contain one element per icon with a bounding
/// rectangle, so the rule is: the point must not fall inside any of them.
/// </para>
/// </summary>
public static class TaskbarQuery
{
    /// <summary>True when the point is taskbar background rather than an icon of any kind.</summary>
    public static bool IsBlankArea(int screenX, int screenY)
    {
        try
        {
            var taskbar = TaskbarWindow(screenX, screenY);
            if (taskbar == IntPtr.Zero) return false;
            return !HitIcon(taskbar, screenX, screenY);
        }
        catch (Exception ex)
        {
            Log.Write("taskbar query failed: " + ex.Message);
            return false;
        }
    }

    private static IntPtr TaskbarWindow(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT(x, y));
        for (int i = 0; i < 6 && hwnd != IntPtr.Zero; i++)
        {
            var cls = Native.ClassNameOf(hwnd);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return hwnd;
            hwnd = Native.GetParent(hwnd);
        }
        return IntPtr.Zero;
    }

    /// <summary>Walks the automation subtree and reports whether an icon owns that point.</summary>
    private static bool HitIcon(IntPtr taskbar, int x, int y)
    {
        var icons = Icons(taskbar);
        foreach (var r in icons)
        {
            if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) return true;
        }
        return false;
    }

    /// <summary>Every icon-ish rectangle in the taskbar (buttons, tray icons, search box, clock…).</summary>
    private static System.Collections.Generic.List<Rect> Icons(IntPtr taskbar)
    {
        var rects = new System.Collections.Generic.List<Rect>();
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            if (root is null) return rects;
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            var found = root.FindAll(TreeScope.Descendants, condition);
            int count = found.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var r = found[i].Current.BoundingRectangle;
                    if (r.Width > 1 && r.Height > 1 && r.Width < 4000) rects.Add(r);
                }
                catch (ElementNotAvailableException)
                {
                    // the taskbar changed while we were looking at it
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("taskbar icons failed: " + ex.Message);
        }
        return rects;
    }

    /// <summary>Diagnostics: the automation subtree of the taskbar (control type, name, rect).</summary>
    public static string DescribeTree(IntPtr taskbar, int max = 30)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            if (root is null) return "no element";
            int found = 0;
            Walk(root, 0);
            void Walk(AutomationElement node, int depth)
            {
                if (found >= max || depth > 4) return;
                try
                {
                    var current = node.Current;
                    var r = current.BoundingRectangle;
                    sb.Append(' ', depth * 2)
                      .Append(current.ControlType?.ProgrammaticName).Append(" \"")
                      .Append(current.Name).Append("\" [")
                      .Append((int)r.Left).Append(',').Append((int)r.Top).Append(' ')
                      .Append((int)r.Width).Append('x').Append((int)r.Height).Append("] ")
                      .Append(current.ClassName).AppendLine();
                    found++;
                    var child = TreeWalker.RawViewWalker.GetFirstChild(node);
                    while (child is not null && found < max)
                    {
                        Walk(child, depth + 1);
                        child = TreeWalker.RawViewWalker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            sb.Append("uia tree failed: ").Append(ex.Message);
        }
        return sb.ToString();
    }

    /// <summary>Diagnostics: window class chain plus the automation element at the point.</summary>
    public static string Describe(int screenX, int screenY)
    {
        var sb = new System.Text.StringBuilder();
        var hwnd = Native.WindowFromPoint(new Native.POINT(screenX, screenY));
        for (int i = 0; i < 5 && hwnd != IntPtr.Zero; i++)
        {
            sb.Append(Native.ClassNameOf(hwnd)).Append(" < ");
            hwnd = Native.GetParent(hwnd);
        }
        sb.Append(" || ");
        try
        {
            var element = AutomationElement.FromPoint(new Point(screenX, screenY));
            for (int depth = 0; element is not null && depth < 3; depth++)
            {
                var current = element.Current;
                sb.Append('[').Append(current.ControlType?.ProgrammaticName)
                  .Append(" \"").Append(current.Name)
                  .Append("\" cls=").Append(current.ClassName).Append("] ");
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception ex)
        {
            sb.Append("uia failed: ").Append(ex.Message);
        }
        return sb.ToString();
    }
}
