using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MeiRi.Interop;

namespace MeiRi;

/// <summary>One row inside a flyout menu.</summary>
public sealed class MenuEntry
{
    public string Text { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
    public ICommand? Command { get; set; }
    public bool IsChecked { get; set; }
    public bool IsDanger { get; set; }
    public bool IsSeparator { get; set; }
}

/// <summary>
/// The little glass card that the "..." buttons open — replaces the default WPF
/// <see cref="ContextMenu"/> so the popups look like the rest of the app.
/// </summary>
public static class MenuFlyout
{
    private static Popup? _open;

    /// <summary>Diagnostics: the HWND of the popup that is currently open.</summary>
    public static IntPtr LastPopupHandle { get; private set; }

    public static MenuEntry Separator() => new() { IsSeparator = true };

    public static MenuEntry Item(string text, string glyph, Action action, bool danger = false) => new()
    {
        Text = text,
        Glyph = glyph,
        IsDanger = danger,
        Command = new RelayCommand(() =>
        {
            Close();
            action();
        })
    };

    public static MenuEntry Toggle(string text, string glyph, bool isChecked, Action<bool> action) => new()
    {
        Text = text,
        Glyph = glyph,
        IsChecked = isChecked,
        Command = new RelayCommand(() =>
        {
            Close();
            action(!isChecked);
        })
    };

    public static void Close()
    {
        var popup = _open;
        _open = null;
        if (popup is null) return;
        try
        {
            popup.IsOpen = false;
        }
        catch
        {
            // popup already torn down
        }
    }

    public static Border BuildCard(params MenuEntry[] entries)
    {
        var list = new ItemsControl
        {
            ItemTemplate = (DataTemplate)Application.Current.Resources["FlyoutItemTemplate"],
            ItemsSource = entries,
            MinWidth = 162,
            Focusable = false,
            SnapsToDevicePixels = true
        };
        // A WPF Popup is hosted in its own HwndSource, so inheritable text properties set on the
        // owning window (the app font) do not reach it — without this every Chinese label renders
        // as tofu boxes because the popup falls back to a latin-only font.
        if (Application.Current.Resources["AppFont"] is FontFamily family)
        {
            System.Windows.Documents.TextElement.SetFontFamily(list, family);
            System.Windows.Documents.TextElement.SetFontSize(list, 12);
        }
        return new Border
        {
            Style = (Style)Application.Current.Resources["FlyoutCard"],
            Child = list
        };
    }

    public static void Show(FrameworkElement target, PlacementMode placement, params MenuEntry[] entries)
    {
        Close();
        var card = BuildCard(entries);
        card.Opacity = 0;
        card.RenderTransform = new TranslateTransform(0, -4);
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Child = card
        };
        popup.Opened += (_, _) =>
        {
            LiftToTop(card);
            LastPopupHandle = (PresentationSource.FromVisual(card) as HwndSource)?.Handle ?? IntPtr.Zero;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
            var t = (TranslateTransform)card.RenderTransform;
            t.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, popup)) _open = null;
        };
        _open = popup;
        popup.IsOpen = true;
    }

    /// <summary>A popup owned by an always-bottom widget would otherwise open underneath
    /// other windows, so promote its HWND to topmost.</summary>
    private static void LiftToTop(FrameworkElement child)
    {
        try
        {
            if (PresentationSource.FromVisual(child) is not HwndSource source) return;
            var hwnd = source.Handle;
            if (hwnd == IntPtr.Zero) return;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW;
            ex &= ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch
        {
            // positioning nicety only
        }
    }
}
