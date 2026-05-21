using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO;

class HidProbe {
    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ  = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ  = 0x01;
    const uint FILE_SHARE_WRITE = 0x02;
    const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA {
        public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)] struct HIDD_ATTRIBUTES {
        public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber;
    }
    [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // followed by ushort Reserved[17] and link/value counts; we don't need them
        ushort r0,r1,r2,r3,r4,r5,r6,r7,r8,r9,r10,r11,r12,r13,r14,r15,r16;
        ushort nlc,nlv,nv,db_nlc,db_nlv,db_nv,fb_nlc,fb_nlv,fb_nv;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid HidGuid);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, uint f);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA i);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto, SetLastError=true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SP_DEVICE_INTERFACE_DATA i, IntPtr d, uint sz, ref uint req, IntPtr da);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto, SetLastError=true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SP_DEVICE_INTERFACE_DATA i, IntPtr detail, uint sz, IntPtr req, IntPtr da);

    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pd);
    [DllImport("hid.dll")] static extern int  HidP_GetCaps(IntPtr pd, ref HIDP_CAPS caps);

    static void Main() {
        Guid g; HidD_GetHidGuid(out g);
        var dev = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        try {
            uint i = 0;
            while (true) {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(dev, IntPtr.Zero, ref g, i++, ref did)) break;

                uint req = 0;
                SetupDiGetDeviceInterfaceDetail(dev, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)req);
                try {
                    // First DWORD is cbSize: 6 on 32-bit / 8 on 64-bit (with alignment)
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(dev, ref did, buf, req, IntPtr.Zero, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringAuto(IntPtr.Add(buf, 4));

                    using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero)) {
                        if (h.IsInvalid) continue;
                        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(h, ref attrs)) continue;
                        // Filter: only Logitech 046D
                        // No vendor filter — show everything.
                        IntPtr pd;
                        if (!HidD_GetPreparsedData(h, out pd)) continue;
                        var caps = new HIDP_CAPS();
                        HidP_GetCaps(pd, ref caps);
                        HidD_FreePreparsedData(pd);
                        Console.WriteLine(
                            string.Format("VID:{0:X4} PID:{1:X4} Page:{2:X4} Usage:{3:X4} In:{4} Out:{5} Feat:{6}  {7}",
                                attrs.VendorID, attrs.ProductID, caps.UsagePage, caps.Usage,
                                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                                path));
                    }
                } finally { Marshal.FreeHGlobal(buf); }
            }
        } finally { SetupDiDestroyDeviceInfoList(dev); }
    }
}
