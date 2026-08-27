using System;
using System.IO;
using System.Text.Json;

namespace Overseer.Services;

public enum SidebarDockEdge
{
    Top,
    Right,
    Bottom,
    Left
}

public sealed class SidebarSettings
{
    public bool IsOpen { get; set; }
    public bool IsAlwaysOnTop { get; set; } = true;
    public bool IsClickThrough { get; set; }
    public double BackgroundOpacity { get; set; } = 0.9d;
    public SidebarDockEdge DockEdge { get; set; } = SidebarDockEdge.Right;
    public string? MonitorDeviceName { get; set; }
    public bool ShowMinMax { get; set; } = true;
    public bool ShowFps { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowDrives { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public string? SelectedDriveName { get; set; }
    public string? SelectedNetworkAdapterId { get; set; }
}

public sealed class SidebarSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private SidebarSettingsService()
    {
        Settings = Load();
    }

    public static SidebarSettingsService Instance { get; } = new();

    public SidebarSettings Settings { get; }

    public string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechPvnk",
        "Overseer",
        "sidebar-settings.json");

    public void Save()
    {
        try
        {
            Settings.BackgroundOpacity = Math.Clamp(Settings.BackgroundOpacity, 0.4d, 1d);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, SerializerOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write("Unable to save Sidebar Mode settings.", ex);
        }
    }

    private SidebarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new SidebarSettings();
            }

            SidebarSettings? settings = JsonSerializer.Deserialize<SidebarSettings>(File.ReadAllText(FilePath));
            if (settings is null)
            {
                return new SidebarSettings();
            }

            settings.BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.4d, 1d);
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Write("Unable to load Sidebar Mode settings; defaults will be used.", ex);
            return new SidebarSettings();
        }
    }
}
