using System;
using System.IO;

namespace MeiRi;

public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();
    public static string DataDir => Path.Combine(Root, "Data");
    public static string DataFile => Path.Combine(DataDir, "schedule.dat");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "meiri.log");

    private static string ResolveRoot()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // published layout: <root>\app\MeiRi.exe
        if (string.Equals(Path.GetFileName(baseDir), "app", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(baseDir) ?? baseDir;

        // dev layout: <root>\bin\Debug\net8.0-windows -> walk up to the project directory
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeiRi.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return baseDir;
    }

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
    }
}
