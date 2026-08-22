using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Overseer.Models;

internal static class WindowsDisplayInfo
{
    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const int DisplayDeviceMirroringDriver = 0x00000008;

    public static IReadOnlyList<string> EnumerateActiveDisplays()
    {
        var displays = new List<string>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            DISPLAY_DEVICE adapter = CreateDisplayDevice();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0 ||
                (adapter.StateFlags & DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            DEVMODE mode = CreateDevMode();
            bool hasMode = EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref mode);
            string resolution = hasMode && mode.DmPelsWidth > 0 && mode.DmPelsHeight > 0
                ? $"{mode.DmPelsWidth}x{mode.DmPelsHeight}@{mode.DmDisplayFrequency}Hz"
                : "resolution unavailable";

            bool foundMonitor = false;
            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                DISPLAY_DEVICE monitor = CreateDisplayDevice();
                if (!EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 0))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(monitor.DeviceString))
                {
                    continue;
                }

                displays.Add($"{monitor.DeviceString} {mode.DmPelsWidth}x{mode.DmPelsHeight} ({resolution})");
                foundMonitor = true;
            }

            if (!foundMonitor && !string.IsNullOrWhiteSpace(adapter.DeviceString))
            {
                displays.Add($"{adapter.DeviceString} {mode.DmPelsWidth}x{mode.DmPelsHeight} ({resolution})");
            }
        }

        return displays;
    }

    private static DISPLAY_DEVICE CreateDisplayDevice() => new() { Cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

    private static DEVMODE CreateDevMode() => new() { DmSize = (short)Marshal.SizeOf<DEVMODE>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DmDeviceName;
        public short DmSpecVersion, DmDriverVersion, DmSize, DmDriverExtra;
        public int DmFields, DmPositionX, DmPositionY, DmDisplayOrientation, DmDisplayFixedOutput;
        public short DmColor, DmDuplex, DmYResolution, DmTTOption, DmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DmFormName;
        public short DmLogPixels;
        public int DmBitsPerPel, DmPelsWidth, DmPelsHeight, DmDisplayFlags, DmDisplayFrequency;
        public int DmICMMethod, DmICMIntent, DmMediaType, DmDitherType, DmReserved1, DmReserved2, DmPanningWidth, DmPanningHeight;
    }
}
