using System;
using System.IO;

namespace Overseer.Services;

public sealed class TestLabSettingsService
{
    private const string FileName = "testlab-settings.txt";
    public static TestLabSettingsService Instance { get; } = new();
    public bool SuppressStressWarning { get; private set; }

    private TestLabSettingsService()
    {
        try { SuppressStressWarning = File.Exists(Path) && File.ReadAllText(Path).Trim() == "suppress-warning"; } catch { }
    }

    public void SetSuppressStressWarning(bool value)
    {
        SuppressStressWarning = value;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); File.WriteAllText(Path, value ? "suppress-warning" : "show-warning"); } catch { }
    }

    private static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TechPvnk", "Overseer", FileName);
}
