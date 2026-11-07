using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeiRi;

/// <summary>
/// The clipboard, read and written straight through Win32.
///
/// WPF's <c>Clipboard</c> goes through OLE: it puts the managed object on the clipboard and then
/// flushes it. When any other program is holding the clipboard open for a moment — clipboard
/// managers and IME helpers do that all the time — the flush retries for about a second and then
/// fails, so <c>TextBox.Cut()</c> never reaches the "delete the selection" step: the text is on the
/// clipboard and still on screen. These calls fail within tens of milliseconds instead, and they
/// report the failure, so the caller can simply keep the text and say so.
/// </summary>
public static class ClipText
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr mem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr mem);

    /// <summary>
    /// Replaces the clipboard with <paramref name="text"/>. The data is a plain global block, so it
    /// stays readable after this process has gone away. False = the clipboard was busy and nothing
    /// was changed.
    /// </summary>
    public static bool TrySet(string text, out string error)
    {
        error = string.Empty;
        text ??= string.Empty;
        if (!TryOpen(out error)) return false;
        try
        {
            if (!EmptyClipboard())
            {
                error = "empty clipboard failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            int bytes = (text.Length + 1) * 2;
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (mem == IntPtr.Zero)
            {
                error = "global alloc failed: " + Marshal.GetLastWin32Error();
                return false;
            }

            var target = GlobalLock(mem);
            if (target == IntPtr.Zero)
            {
                GlobalFree(mem);
                error = "global lock failed: " + Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                if (text.Length > 0) Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0); // terminating null
            }
            finally
            {
                GlobalUnlock(mem);
            }

            // on success the clipboard owns the block and frees it itself
            if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                GlobalFree(mem);
                error = "set clipboard data failed: " + code;
                return false;
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Reads the clipboard text. False = the clipboard was busy (nothing is guessed in that case);
    /// true with an empty string means "no text on the clipboard".
    /// </summary>
    public static bool TryGet(out string text, out string error)
    {
        text = string.Empty;
        if (!TryOpen(out error)) return false;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                error = "clipboard holds no text";
                return true;
            }
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero)
            {
                error = "clipboard lock failed: " + Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                text = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Opening the clipboard is the only contended step. A few quick retries cover the usual
    /// millisecond-long window of a clipboard manager; the whole loop is bounded to well under
    /// 100 ms so it can run on the UI thread without a visible hitch.
    /// </summary>
    private static bool TryOpen(out string error)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                error = string.Empty;
                return true;
            }
            Thread.Sleep(8);
        }
        error = "clipboard busy: " + Marshal.GetLastWin32Error();
        return false;
    }
}
