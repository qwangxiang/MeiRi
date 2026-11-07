using System;
using System.IO;

namespace MeiRi;

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                // shared access so a second (dev) instance can log to the same file
                using var stream = new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite);
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch { /* logging must never throw */ }
    }
}
