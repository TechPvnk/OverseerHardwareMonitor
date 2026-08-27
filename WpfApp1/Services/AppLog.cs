using System;
using System.Diagnostics;
using System.IO;

namespace Overseer.Services;

public static class AppLog
{
    private static readonly object SyncRoot = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechPvnk",
        "Overseer",
        "Overseer.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
            lock (SyncRoot)
            {
                File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:O}] {detail}{Environment.NewLine}");
            }
        }
        catch
        {
            Debug.WriteLine(message);
        }
    }

    public static void Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (!File.Exists(FilePath))
            {
                File.WriteAllText(FilePath, string.Empty);
            }

            Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Write("Unable to open application log.", ex);
        }
    }
}
