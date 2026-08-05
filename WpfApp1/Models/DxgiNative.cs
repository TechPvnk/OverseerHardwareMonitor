using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Overseer.Models
{
    internal static class DxgiNative
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long AdapterLuid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }

        [ComImport]
        [Guid("0c817476-c20e-4c40-9e83-0d49751fada0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory
        {
            [PreserveSig]
            int EnumAdapters(uint Adapter, out IDXGIAdapter ppAdapter);
        }

        [ComImport]
        [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534b4d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            [PreserveSig]
            int GetDesc(out DXGI_ADAPTER_DESC pDesc);
        }

        [ComImport]
        [Guid("645967bd-43b1-4bef-ba26-a439d19c8f99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter3
        {
            // Keep vtable layout compatible; only declare methods we need
            [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
            [PreserveSig] int GetDesc1(IntPtr pDesc);
            [PreserveSig] int GetDesc2(IntPtr pDesc);
            [PreserveSig] int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr hEvent, out uint pdwCookie);
            [PreserveSig] void UnregisterHardwareContentProtectionTeardownStatusEvent(uint dwCookie);
            [PreserveSig] int QueryVideoMemoryInfo(uint NodeIndex, uint MemorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        }

        [DllImport("dxgi.dll", SetLastError = true, EntryPoint = "CreateDXGIFactory")]
        private static extern int CreateDXGIFactory(ref Guid riid, out IDXGIFactory ppFactory);

        private static readonly Guid IID_IDXGIFactory = new("0c817476-c20e-4c40-9e83-0d49751fada0");
        private const uint DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0;
        private const uint DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1;

        public sealed record AdapterInfo(
            string Description,
            uint VendorId,
            uint DeviceId,
            uint SubSysId,
            uint Revision,
            ulong DedicatedVideoMemory,
            ulong DedicatedSystemMemory,
            ulong SharedSystemMemory,
            long AdapterLuid,
            VideoMemoryInfo? LocalMemory,
            VideoMemoryInfo? NonLocalMemory);

        public sealed record VideoMemoryInfo(ulong Budget, ulong CurrentUsage, ulong AvailableForReservation, ulong CurrentReservation);

        public static IReadOnlyList<AdapterInfo> EnumerateAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                Guid iid = IID_IDXGIFactory;
                int hr = CreateDXGIFactory(ref iid, out IDXGIFactory factory);
                if (hr < 0 || factory == null)
                {
                    return list;
                }

                uint index = 0;
                while (true)
                {
                    int enumHr = factory.EnumAdapters(index, out IDXGIAdapter adapter);
                    if (enumHr != 0 || adapter == null)
                    {
                        break;
                    }

                    try
                    {
                        if (adapter.GetDesc(out DXGI_ADAPTER_DESC desc) >= 0)
                        {
                            ulong dedicated = desc.DedicatedVideoMemory.ToUInt64();
                            ulong dedicatedSys = desc.DedicatedSystemMemory.ToUInt64();
                            ulong shared = desc.SharedSystemMemory.ToUInt64();

                            VideoMemoryInfo? localInfo = null;
                            VideoMemoryInfo? nonLocalInfo = null;

                            try
                            {
                                // try to QI to IDXGIAdapter3
                                var adapter3 = adapter as IDXGIAdapter3;
                                if (adapter3 != null)
                                {
                                    if (adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO local) >= 0)
                                    {
                                        localInfo = new VideoMemoryInfo(local.Budget, local.CurrentUsage, local.AvailableForReservation, local.CurrentReservation);
                                    }

                                    if (adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO nonLocal) >= 0)
                                    {
                                        nonLocalInfo = new VideoMemoryInfo(nonLocal.Budget, nonLocal.CurrentUsage, nonLocal.AvailableForReservation, nonLocal.CurrentReservation);
                                    }
                                }
                            }
                            catch
                            {
                                // ignore video memory query failures
                            }

                            list.Add(new AdapterInfo(desc.Description?.Trim() ?? string.Empty,
                                desc.VendorId,
                                desc.DeviceId,
                                desc.SubSysId,
                                desc.Revision,
                                dedicated,
                                dedicatedSys,
                                shared,
                                desc.AdapterLuid,
                                localInfo,
                                nonLocalInfo));
                        }
                    }
                    catch
                    {
                        // ignore adapter
                    }
                    finally
                    {
                        try { Marshal.ReleaseComObject(adapter); } catch { }
                    }

                    index++;
                }

                try { Marshal.ReleaseComObject(factory); } catch { }
            }
            catch
            {
                // ignore DXGI errors
            }

            return list;
        }
    }
}
