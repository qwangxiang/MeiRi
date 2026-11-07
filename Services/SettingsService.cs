using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MeiRi.Services;

public sealed class Settings
{
    public bool HasPosition { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool AutoStart { get; set; } = true;
    public bool ShowPreview { get; set; } = true;

    /// <summary>Quick notes (taskbar double click / menu entry). Off means the whole feature is off.</summary>
    public bool QuickNotes { get; set; } = true;
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<Settings>(json, Options);
                if (s is not null) return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, Options);
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }
}

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MeiRi";

    public static string ExePath => Environment.ProcessPath ?? string.Empty;

    public static void Apply(bool enabled)
    {
        try
        {
            // never register a build output as a startup item
            if (ExePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && enabled)
            {
                Log.Write("autostart skipped for build output");
                return;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var desired = "\"" + ExePath + "\"";
                if (key.GetValue(ValueName) as string != desired) key.SetValue(ValueName, desired);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("autostart apply failed: " + ex.Message);
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
