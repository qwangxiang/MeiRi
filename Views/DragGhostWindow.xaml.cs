using System;
using System.Windows;
using System.Windows.Media.Animation;
using MeiRi.Interop;

namespace MeiRi.Views;

public partial class DragGhostWindow : Window
{
    public DragGhostWindow(string text, bool isCategory)
    {
        InitializeComponent();
        Label.Text = text;
        Opacity = 0;
        SourceInitialized += (_, _) => WindowEffects.MakeToolWindow(this);
    }

    public void MoveTo(int x, int y)
    {
        var hwnd = WindowEffects.Handle(this);
        if (hwnd == IntPtr.Zero) return;
        // the window is already topmost (see XAML); leaving the z-order alone is much cheaper
        // than re-inserting it on every mouse move
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public void FadeIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.97, TimeSpan.FromMilliseconds(110)));
    }

    public void CloseAnimated()
    {
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }
}
