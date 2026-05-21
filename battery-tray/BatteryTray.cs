using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

#region Native
static class Native {
    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;
    public const uint DIGCF_ALLCLASSES = 0x04;
    public const uint GENERIC_READ  = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ  = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA {
        public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVINFO_DATA {
        public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)] public struct HIDD_ATTRIBUTES {
        public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber;
    }
    [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        ushort r0,r1,r2,r3,r4,r5,r6,r7,r8,r9,r10,r11,r12,r13,r14,r15,r16;
        ushort nlc,nlv,nv,db_nlc,db_nlv,db_nv,fb_nlc,fb_nlv,fb_nv;
    }
    [StructLayout(LayoutKind.Sequential)] public struct DEVPROPKEY {
        public Guid fmtid; public uint pid;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid HidGuid);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, uint f);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(IntPtr g, IntPtr e, IntPtr p, uint f);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA i);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr h, uint idx, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref SP_DEVICE_INTERFACE_DATA i, IntPtr detail, uint sz, IntPtr req, IntPtr da);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern bool SetupDiGetDeviceProperty(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY PropertyKey, out uint PropertyType, [Out] byte[] PropertyBuffer, uint PropertyBufferSize,
        out uint RequiredSize, uint Flags);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr ds, ref SP_DEVINFO_DATA d, uint prop,
        out uint regType, [Out] byte[] buf, uint bufSize, out uint reqSize);

    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pd);
    [DllImport("hid.dll")] public static extern int  HidP_GetCaps(IntPtr pd, ref HIDP_CAPS caps);

    public const uint SPDRP_FRIENDLYNAME = 0x0C;
    public static DEVPROPKEY DEVPKEY_Device_BatteryLevel = new DEVPROPKEY {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2
    };
}
#endregion

#region Logitech HID++ provider
class LogitechHidPp : IDisposable {
    const byte REPORT_LONG  = 0x11;
    const byte DEV_LIGHTSPEED_SLOT1 = 0x01;
    const byte DEV_DIRECT_USB = 0xFF;
    const byte ROOT_FEATURE = 0x00;
    const byte SW_ID = 0x08;
    const ushort FEAT_UNIFIED_BATTERY = 0x1004;
    const ushort FEAT_BATTERY_VOLTAGE = 0x1001;
    const ushort FEAT_BATTERY_STATUS  = 0x1000;
    const ushort VID_LOGITECH = 0x046D;
    const ushort PID_G502_X_PLUS = 0xC547;

    string _path;
    SafeFileHandle _h;
    FileStream _fs;
    byte _devIdx = DEV_LIGHTSPEED_SLOT1;
    byte _batteryFeatureIdx = 0;
    ushort _batteryFeatureId = 0;
    public string DeviceName = "G502 X PLUS";

    static byte FuncSw(byte func) { return (byte)((func << 4) | SW_ID); }

    public LogitechHidPp() {
        _path = FindHidppLongPath(VID_LOGITECH, PID_G502_X_PLUS);
    }

    static string FindHidppLongPath(ushort vid, ushort pid) {
        Guid g; Native.HidD_GetHidGuid(out g);
        var dev = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        try {
            uint i = 0;
            while (true) {
                var did = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(dev, IntPtr.Zero, ref g, i++, ref did)) break;
                uint req = 0;
                Native.SetupDiGetDeviceInterfaceDetail(dev, ref did, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                req = 256;
                IntPtr buf = Marshal.AllocHGlobal((int)req);
                try {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(dev, ref did, buf, req, IntPtr.Zero, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringAuto(IntPtr.Add(buf, 4));
                    using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                        IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero)) {
                        if (h.IsInvalid) continue;
                        var a = new Native.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
                        if (!Native.HidD_GetAttributes(h, ref a)) continue;
                        if (a.VendorID != vid || a.ProductID != pid) continue;
                        IntPtr pd;
                        if (!Native.HidD_GetPreparsedData(h, out pd)) continue;
                        var caps = new Native.HIDP_CAPS();
                        Native.HidP_GetCaps(pd, ref caps);
                        Native.HidD_FreePreparsedData(pd);
                        // HID++ long: page FF00, output report 20 bytes
                        if (caps.UsagePage == 0xFF00 && caps.OutputReportByteLength == 20) {
                            return path;
                        }
                    }
                } finally { Marshal.FreeHGlobal(buf); }
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(dev); }
        return null;
    }

    void EnsureOpen() {
        if (_fs != null) return;
        if (_path == null) _path = FindHidppLongPath(VID_LOGITECH, PID_G502_X_PLUS);
        if (_path == null) throw new IOException("Logitech HID++ device not found");
        _h = Native.CreateFile(_path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_h.IsInvalid) throw new IOException("Cannot open Logitech HID++: " + Marshal.GetLastWin32Error());
        _fs = new FileStream(_h, FileAccess.ReadWrite, 20, true);
    }

    byte[] Exchange(byte[] req, int timeoutMs = 1500) {
        EnsureOpen();
        _fs.Write(req, 0, req.Length);
        // Read responses until one matches our software id; ignore unrelated reports.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline) {
            var resp = new byte[20];
            int remaining = (int)Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var t = _fs.ReadAsync(resp, 0, 20);
            if (!t.Wait(remaining)) throw new TimeoutException("HID++ read timed out");
            int n = t.Result;
            if (n <= 0) continue;
            if (resp[0] != REPORT_LONG) continue;
            if (resp[1] != req[1]) continue;          // device index must match
            // Error response: 0x8F at offset 2, original feature index at offset 3
            if (resp[2] == 0x8F && resp[3] == req[2]) {
                throw new InvalidOperationException("HID++ error code 0x" + resp[4].ToString("X2"));
            }
            if (resp[2] != req[2]) continue;          // feature index must match
            if (resp[3] != req[3]) continue;          // funcId+swId must match
            return resp;
        }
        throw new TimeoutException("HID++ no matching response within " + timeoutMs + "ms");
    }

    bool TryResolveBatteryFeature() {
        if (_batteryFeatureIdx != 0) return true;
        foreach (var devIdx in new byte[] { DEV_LIGHTSPEED_SLOT1, DEV_DIRECT_USB }) {
            foreach (var featId in new ushort[] { FEAT_UNIFIED_BATTERY, FEAT_BATTERY_VOLTAGE, FEAT_BATTERY_STATUS }) {
                var req = new byte[20];
                req[0] = REPORT_LONG;
                req[1] = devIdx;
                req[2] = ROOT_FEATURE;
                req[3] = FuncSw(0x00);  // Root.GetFeature
                req[4] = (byte)(featId >> 8);
                req[5] = (byte)(featId & 0xFF);
                try {
                    // Short timeout per probe — when the mouse is off we want to fail fast,
                    // not burn 1.5s per (devIdx, featId) combination (= 9s total worst case).
                    var resp = Exchange(req, 400);
                    byte idx = resp[4];
                    if (idx != 0) {
                        _devIdx = devIdx;
                        _batteryFeatureIdx = idx;
                        _batteryFeatureId = featId;
                        return true;
                    }
                } catch (TimeoutException) {
                    // Likely mouse is off / unreachable — abandon resolution for this run.
                    return false;
                } catch { /* try next */ }
            }
        }
        return false;
    }

    public class BatteryInfo { public int? Percent; public string Status; }

    public BatteryInfo ReadBattery() {
        var info = new BatteryInfo();
        try {
            EnsureOpen();
            if (!TryResolveBatteryFeature()) { info.Status = "no battery feature"; return info; }

            var req = new byte[20];
            req[0] = REPORT_LONG;
            req[1] = _devIdx;
            req[2] = _batteryFeatureIdx;
            req[3] = FuncSw(_batteryFeatureId == FEAT_BATTERY_STATUS ? (byte)0 : (byte)1);
            var resp = Exchange(req);

            // UnifiedBattery.GetStatus: resp[4]=stateOfCharge, resp[6]=status
            info.Percent = resp[4];
            byte st = resp[6];
            string label;
            switch (st) {
                case 0: label = ""; break;                  // discharging (normal)
                case 1: label = "wireless charging"; break;
                case 2: label = "charging"; break;
                case 3: label = "fast charging"; break;
                case 4: label = "charged"; break;
                default: label = ""; break;
            }
            info.Status = label;
            return info;
        } catch (Exception ex) {
            Dispose();
            info.Status = (ex is TimeoutException) ? "offline" : "err";
            return info;
        }
    }

    public void Dispose() {
        try { if (_fs != null) _fs.Dispose(); } catch {}
        try { if (_h != null) _h.Dispose(); } catch {}
        _fs = null; _h = null;
    }
}
#endregion

#region Corsair / Pnp battery provider
static class PnpBattery {
    public class Result { public string Name; public int Percent; }

    public static List<Result> ReadAll(string nameSubstring) {
        var results = new List<Result>();
        Guid empty = Guid.Empty;
        var dev = Native.SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        try {
            uint i = 0;
            while (true) {
                var info = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                if (!Native.SetupDiEnumDeviceInfo(dev, i++, ref info)) break;
                byte[] battery = new byte[1];
                uint pt, req;
                if (!Native.SetupDiGetDeviceProperty(dev, ref info,
                        ref Native.DEVPKEY_Device_BatteryLevel, out pt, battery, 1, out req, 0))
                    continue;
                // Get friendly name
                byte[] nameBuf = new byte[512];
                uint regType, reqName;
                string name = "(unknown)";
                if (Native.SetupDiGetDeviceRegistryProperty(dev, ref info, Native.SPDRP_FRIENDLYNAME,
                        out regType, nameBuf, (uint)nameBuf.Length, out reqName)) {
                    name = System.Text.Encoding.Unicode.GetString(nameBuf, 0, (int)reqName).TrimEnd('\0');
                }
                if (string.IsNullOrEmpty(nameSubstring) ||
                    name.IndexOf(nameSubstring, StringComparison.OrdinalIgnoreCase) >= 0) {
                    results.Add(new Result { Name = name, Percent = battery[0] });
                }
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(dev); }
        return results;
    }
}
#endregion

#region Flydigi Apex 4 provider (named pipe + protobuf)
class FlydigiApex {
    public class BatteryInfo {
        public int? Gear;      // 0..MaxGear, null if unavailable
        public int MaxGear = 4;
        public DateTime LastSuccess;
        public bool Stale;
        public string Status = "";
    }

    // The pipe is single-client (Space Station UI holds it when open). We connect
    // with a short timeout and silently fall back to the cached value when busy.
    const string PIPE_NAME = "fcs.sock";
    // TODO: Could auto-discover via GetConnectedDevices (cmdId 4097) and cache.
    string _deviceCode = "k2";   // Apex 4 device code
    string _uid;
    BatteryInfo _cached = new BatteryInfo();

    public FlydigiApex(string uid) { _uid = uid; }

    public BatteryInfo Read() {
        if (string.IsNullOrEmpty(_uid)) {
            _cached.Gear = null;
            _cached.Status = "no uid configured";
            return _cached;
        }
        try {
            using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PIPE_NAME, System.IO.Pipes.PipeDirection.InOut)) {
                pipe.Connect(800);  // short — if Space Station UI is open we'll fail fast
                byte[] payload = BuildGetDeviceDetailInfoRequest(_deviceCode, _uid);
                byte[] wire = Frame(payload);
                pipe.Write(wire, 0, wire.Length);
                pipe.Flush();
                string hdr = ReadHeaderLine(pipe);
                if (hdr == null) throw new IOException("no header");
                byte[] lenBuf = ReadExact(pipe, 4);
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 1024 * 1024) throw new IOException("bad len " + len);
                byte[] data = ReadExact(pipe, len);
                if (hdr.StartsWith("FDG_COMPRESSED")) {
                    using (var ms = new MemoryStream(data))
                    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (var outMs = new MemoryStream()) {
                        gz.CopyTo(outMs);
                        data = outMs.ToArray();
                    }
                }
                ParseResult pr = ParseFromIpcResult(data);
                if (pr.Connected && pr.Battery.HasValue) {
                    _cached.Gear = pr.Battery.Value;
                    _cached.MaxGear = 4;
                    _cached.LastSuccess = DateTime.Now;
                    _cached.Stale = false;
                    _cached.Status = "";
                } else {
                    // Service responded but device is off / not connected — clear cached gear.
                    _cached.Gear = null;
                    _cached.Stale = false;
                    _cached.Status = "off";
                }
                return _cached;
            }
        } catch (Exception) {
            // Connect timeout / pipe busy / no service. Keep cached value if any.
            if (_cached.Gear.HasValue) {
                _cached.Stale = true;
                _cached.Status = "stale";
            } else {
                _cached.Status = "offline";
            }
            return _cached;
        }
    }

    // ---- Wire format helpers ----
    const string HDR = "FDG_PROTOCOL\n";

    static byte[] BuildGetDeviceDetailInfoRequest(string deviceCode, string uid) {
        var inner = new MemoryStream();
        WriteString(inner, 1, deviceCode);
        WriteString(inner, 2, uid);
        var env = new MemoryStream();
        WriteVarintField(env, 1, 4099);   // cmdId = GetDeviceDetailInfo
        WriteVarintField(env, 2, 1);      // category = Controller
        WriteLenDelim(env, 4, inner.ToArray());
        return env.ToArray();
    }
    static byte[] Frame(byte[] payload) {
        var ms = new MemoryStream();
        var hdr = System.Text.Encoding.UTF8.GetBytes(HDR);
        ms.Write(hdr, 0, hdr.Length);
        var len = BitConverter.GetBytes(payload.Length);
        ms.Write(len, 0, 4);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }
    static void WriteTag(Stream s, int fn, int wt) { WriteVarint(s, ((uint)fn << 3) | (uint)wt); }
    static void WriteVarint(Stream s, uint v) {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }
    static void WriteVarintField(Stream s, int fn, uint val) { WriteTag(s, fn, 0); WriteVarint(s, val); }
    static void WriteString(Stream s, int fn, string str) {
        byte[] b = System.Text.Encoding.UTF8.GetBytes(str);
        WriteTag(s, fn, 2); WriteVarint(s, (uint)b.Length); s.Write(b, 0, b.Length);
    }
    static void WriteLenDelim(Stream s, int fn, byte[] data) {
        WriteTag(s, fn, 2); WriteVarint(s, (uint)data.Length); s.Write(data, 0, data.Length);
    }
    static byte[] ReadExact(Stream s, int n) {
        byte[] buf = new byte[n]; int off = 0;
        while (off < n) {
            int r = s.Read(buf, off, n - off);
            if (r <= 0) throw new IOException("EOF after " + off + "/" + n);
            off += r;
        }
        return buf;
    }
    static string ReadHeaderLine(Stream s) {
        var sb = new System.Text.StringBuilder();
        byte[] one = new byte[1];
        while (sb.Length < 64) {
            int r = s.Read(one, 0, 1);
            if (r <= 0) return null;
            if (one[0] == 10) return sb.ToString();
            sb.Append((char)one[0]);
        }
        return null;
    }

    public struct ParseResult { public bool Connected; public int? Battery; }

    // IpcResult.field 7 (getDeviceDetailInfo) → GetDeviceDetailInfoResult.field 1 (controllerInfo)
    // → ControllerInfo.field 1 (parent: DeviceInfo) → DeviceInfo.field 8 (isConnected) / .field 9 (battery)
    static ParseResult ParseFromIpcResult(byte[] buf) {
        var r = new ParseResult { Connected = false };
        byte[] inner = GetField(buf, 7, 2);
        if (inner == null) return r;
        byte[] controllerInfo = GetField(inner, 1, 2);
        if (controllerInfo == null) return r;
        byte[] devInfo = GetField(controllerInfo, 1, 2);
        if (devInfo == null) return r;
        // DeviceInfo.isConnected (field 8, bool). When the controller is on we observe
        // this set to 1. When off, the service omits it (proto3 default = false) or sets 0.
        // Either way, treat anything other than "explicitly true" as offline.
        uint? isConnected = GetFieldVarint(devInfo, 8);
        r.Connected = isConnected.HasValue && isConnected.Value != 0;
        r.Battery = (int?)GetFieldVarint(devInfo, 9);
        return r;
    }
    static byte[] GetField(byte[] buf, int targetFn, int targetWt) {
        int p = 0;
        while (p < buf.Length) {
            uint tag = ReadVarint(buf, ref p);
            int fn = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            if (fn == targetFn && wt == targetWt) {
                if (wt == 2) {
                    int len = (int)ReadVarint(buf, ref p);
                    byte[] r = new byte[len];
                    Buffer.BlockCopy(buf, p, r, 0, len);
                    return r;
                }
            }
            SkipField(buf, ref p, wt);
        }
        return null;
    }
    static uint? GetFieldVarint(byte[] buf, int targetFn) {
        int p = 0;
        while (p < buf.Length) {
            uint tag = ReadVarint(buf, ref p);
            int fn = (int)(tag >> 3);
            int wt = (int)(tag & 7);
            if (fn == targetFn && wt == 0) return ReadVarint(buf, ref p);
            SkipField(buf, ref p, wt);
        }
        return null;
    }
    static uint ReadVarint(byte[] buf, ref int p) {
        uint v = 0; int sh = 0;
        while (true) {
            byte b = buf[p++];
            v |= (uint)(b & 0x7F) << sh;
            if ((b & 0x80) == 0) return v;
            sh += 7;
        }
    }
    static void SkipField(byte[] buf, ref int p, int wt) {
        switch (wt) {
            case 0: ReadVarint(buf, ref p); break;
            case 1: p += 8; break;
            case 2: { int len = (int)ReadVarint(buf, ref p); p += len; break; }
            case 5: p += 4; break;
            default: throw new Exception("wire " + wt);
        }
    }
}
#endregion

#region iCUE SDK provider (preferred for Corsair when iCUE is running)
// Uses Corsair's public iCUE SDK v4 (iCUESDK.x64_2019.dll). Requires iCUE to
// be running. Returns the same battery / charging info iCUE itself displays.
static class CorsairSdk {
    const string DLL = "iCUESDK.x64_2019.dll";

    // CorsairError codes
    public const int CE_Success = 0;
    // CorsairDevicePropertyId
    public const int CDPI_BatteryLevel = 9;
    // CorsairDataType (from iCUESDK.h — base values start at 0)
    public const int CT_Boolean = 0;
    public const int CT_Int32 = 1;
    public const int CT_Float64 = 2;
    public const int CT_String = 3;
    // CorsairSessionState (from iCUESDK.h — CONFIRMED ordering!)
    public const int CSS_Invalid = 0;
    public const int CSS_Closed = 1;
    public const int CSS_Connecting = 2;
    public const int CSS_Timeout = 3;
    public const int CSS_ConnectionRefused = 4;
    public const int CSS_ConnectionLost = 5;
    public const int CSS_Connected = 6;
    // CorsairDeviceType mask (1<<n bits)
    public const uint CDT_Unknown = 0;
    public const uint CDT_Keyboard = 1;
    public const uint CDT_Mouse = 2;
    public const uint CDT_Mousemat = 4;
    public const uint CDT_Headset = 8;
    public const uint CDT_HeadsetStand = 16;
    public const uint CDT_FanLedController = 32;
    public const uint CDT_LedController = 64;
    public const uint CDT_MemoryModule = 128;
    public const uint CDT_Cooler = 256;
    public const uint CDT_Motherboard = 512;
    public const uint CDT_GraphicsCard = 1024;
    public const uint CDT_Touchbar = 2048;
    public const uint CDT_GameController = 4096;
    public const uint CDT_All = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct CorsairSessionStateChanged {
        public int state;             // CorsairSessionState
        public CorsairVersion clientVersion;
        public CorsairVersion serverVersion;
        public int serverHostVersion;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct CorsairVersion { public int major, minor, patch; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CorsairDeviceFilter { public uint deviceTypeMask; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct CorsairDeviceInfo {
        public int    type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string serial;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string model;
        public int    ledCount;
        public int    channelCount;
    }

    // Discriminated union — type tells you which value field is meaningful.
    [StructLayout(LayoutKind.Explicit)]
    public struct DataValueUnion {
        [FieldOffset(0)] public byte   boolean;
        [FieldOffset(0)] public int    int32;
        [FieldOffset(0)] public double float64;
        [FieldOffset(0)] public IntPtr stringPtr;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct CorsairProperty {
        public int type;
        public DataValueUnion value;
    }

    public delegate void SessionStateChangedHandler(IntPtr context, IntPtr eventData);

    [DllImport(DLL)] public static extern int CorsairConnect(SessionStateChangedHandler handler, IntPtr context);
    [DllImport(DLL)] public static extern int CorsairDisconnect();
    [DllImport(DLL)] public static extern int CorsairGetDevices(ref CorsairDeviceFilter filter, int sizeMax, [Out] CorsairDeviceInfo[] devices, ref int size);
    [DllImport(DLL, CharSet = CharSet.Ansi)] public static extern int CorsairReadDeviceProperty(string deviceId, int propertyId, uint index, ref CorsairProperty property);
    [DllImport(DLL)] public static extern int CorsairFreeProperty(ref CorsairProperty property);

    // ---- Higher-level wrapper ----
    static int _state = CSS_Closed;
    static SessionStateChangedHandler _handler;  // pinned to prevent GC
    static readonly object _lock = new object();
    static bool _connectAttempted;

    public class K70Result {
        public int? Percent;
        public bool Connected;       // SDK session connected to iCUE
        public string Error;
    }

    static System.Collections.Generic.List<int> _stateHistory = new System.Collections.Generic.List<int>();

    public static K70Result ReadK70() {
        var r = new K70Result();
        lock (_lock) {
            // Reconnect if the previous session is in a bad terminal state.
            bool needReconnect = !_connectAttempted ||
                _state == CSS_Closed || _state == CSS_ConnectionLost ||
                _state == CSS_ConnectionRefused || _state == CSS_Timeout;
            if (needReconnect) {
                if (_connectAttempted) {
                    try { CorsairDisconnect(); } catch {}
                }
                _connectAttempted = true;
                _state = CSS_Closed;
                _stateHistory.Clear();
                _handler = (ctx, evt) => {
                    if (evt != IntPtr.Zero) {
                        var st = Marshal.ReadInt32(evt);
                        _state = st;
                        lock (_stateHistory) { _stateHistory.Add(st); }
                    }
                };
                try {
                    int err = CorsairConnect(_handler, IntPtr.Zero);
                    if (err != CE_Success) { r.Error = "Connect err " + err; return r; }
                } catch (Exception ex) { r.Error = "Connect exc: " + ex.Message; return r; }
            }
        }

        // Wait up to 4s for the session to come up. Log every state transition we see.
        var deadline = DateTime.UtcNow.AddMilliseconds(4000);
        while (_state != CSS_Connected && DateTime.UtcNow < deadline) Thread.Sleep(50);
        if (_state != CSS_Connected) {
            string history;
            lock (_stateHistory) { history = string.Join(",", _stateHistory); }
            r.Error = "state=" + _state + " history=[" + history + "]";
            return r;
        }
        r.Connected = true;

        try {
            var filter = new CorsairDeviceFilter { deviceTypeMask = CDT_Keyboard };
            var devices = new CorsairDeviceInfo[16];
            int count = devices.Length;
            int err = CorsairGetDevices(ref filter, count, devices, ref count);
            if (err != CE_Success) { r.Error = "GetDevices err " + err; return r; }
            for (int i = 0; i < count; i++) {
                if (devices[i].model != null && devices[i].model.IndexOf("K70", StringComparison.OrdinalIgnoreCase) >= 0) {
                    var prop = new CorsairProperty();
                    int e2 = CorsairReadDeviceProperty(devices[i].id, CDPI_BatteryLevel, 0, ref prop);
                    if (e2 == CE_Success && prop.type == CT_Int32) {
                        r.Percent = prop.value.int32;
                        CorsairFreeProperty(ref prop);
                        return r;
                    }
                    CorsairFreeProperty(ref prop);
                    r.Error = "ReadProperty err " + e2 + " type " + prop.type;
                    return r;
                }
            }
            r.Error = "K70 not found (got " + count + " keyboards)";
        } catch (Exception ex) { r.Error = "exc: " + ex.Message; }
        return r;
    }
}
#endregion

#region BLE GATT battery (fresh read for K70)
// The Pnp DEVPKEY_Device_BatteryLevel value is whatever the device last *notified*.
// To get a fresh value (matching iCUE), we read the standard BLE Battery Service
// (UUID 0x180F) Battery Level characteristic (UUID 0x2A19) directly via WinRT.
static class BleBattery {
    public class Result { public int? Percent; public string Error; }

    // Discovers the K70's BLE MAC from the Bluetooth Pnp instance ID at runtime
    // (BTHLE\DEV_<MAC>\... where the friendly name contains "K70").
    // Override with the BATTERYTRAY_K70_MAC env var (hex, no separators) if needed.
    static ulong? _cachedK70Mac;

    public static Result ReadK70() {
        var r = new Result();
        if (!_cachedK70Mac.HasValue) {
            _cachedK70Mac = DiscoverK70Mac();
            if (!_cachedK70Mac.HasValue) { r.Error = "K70 BLE device not found"; return r; }
        }
        return ReadByAddress(_cachedK70Mac.Value);
    }

    static ulong? DiscoverK70Mac() {
        var ovr = Environment.GetEnvironmentVariable("BATTERYTRAY_K70_MAC");
        if (!string.IsNullOrEmpty(ovr)) {
            ovr = ovr.Replace(":", "").Replace("-", "").Trim();
            ulong v;
            if (ulong.TryParse(ovr, System.Globalization.NumberStyles.HexNumber, null, out v)) return v;
        }
        var dev = Native.SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        try {
            uint i = 0;
            while (true) {
                var info = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                if (!Native.SetupDiEnumDeviceInfo(dev, i++, ref info)) break;
                byte[] nameBuf = new byte[512];
                uint regType, req;
                if (!Native.SetupDiGetDeviceRegistryProperty(dev, ref info, Native.SPDRP_FRIENDLYNAME,
                        out regType, nameBuf, (uint)nameBuf.Length, out req)) continue;
                string name = System.Text.Encoding.Unicode.GetString(nameBuf, 0, (int)req).TrimEnd('\0');
                if (name.IndexOf("K70", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var sb = new System.Text.StringBuilder(256);
                if (CM_Get_Device_IDW(info.DevInst, sb, (uint)sb.Capacity, 0) == 0) {
                    var m = System.Text.RegularExpressions.Regex.Match(sb.ToString(),
                        @"BTHLE\\DEV_([0-9A-Fa-f]{12})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) {
                        ulong mac;
                        if (ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out mac))
                            return mac;
                    }
                }
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(dev); }
        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint dnDevInst, System.Text.StringBuilder buffer, uint bufferLen, uint flags);

    public static Result ReadByAddress(ulong macAddress) {
        var r = new Result();
        try {
            var device = Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(macAddress).AsTask().GetAwaiter().GetResult();
            if (device == null) { r.Error = "no device"; return r; }
            try {
                var svcResult = device.GetGattServicesForUuidAsync(
                    Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids.Battery)
                    .AsTask().GetAwaiter().GetResult();
                if (svcResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success
                    || svcResult.Services.Count == 0) { r.Error = "no svc"; return r; }
                var svc = svcResult.Services[0];
                try {
                    var chResult = svc.GetCharacteristicsForUuidAsync(
                        Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicUuids.BatteryLevel)
                        .AsTask().GetAwaiter().GetResult();
                    if (chResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success
                        || chResult.Characteristics.Count == 0) { r.Error = "no char"; return r; }
                    var ch = chResult.Characteristics[0];
                    var rd = ch.ReadValueAsync().AsTask().GetAwaiter().GetResult();
                    if (rd.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success) {
                        r.Error = "read " + rd.Status; return r;
                    }
                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(rd.Value);
                    r.Percent = reader.ReadByte();
                } finally { svc.Dispose(); }
            } finally { device.Dispose(); }
        } catch (Exception ex) { r.Error = ex.GetType().Name + ": " + ex.Message; }
        return r;
    }
}
#endregion

#region Corsair charging detection
// The Pnp battery-level property doesn't include charging state. Heuristic that
// works without the iCUE SDK: when the K70 is plugged into USB it enumerates
// under USB\VID_1B1C as a wired HID device, in addition to its BLE entry.
// If we see that USB entry present, treat as charging.
static class CorsairCharging {
    const uint SPDRP_HARDWAREID = 1;

    public static bool IsK70Charging() {
        var dev = Native.SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        try {
            uint i = 0;
            while (true) {
                var info = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                if (!Native.SetupDiEnumDeviceInfo(dev, i++, ref info)) break;
                byte[] buf = new byte[1024];
                uint regType, req;
                if (!Native.SetupDiGetDeviceRegistryProperty(dev, ref info, SPDRP_HARDWAREID,
                        out regType, buf, (uint)buf.Length, out req)) continue;
                string s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)req);
                if (s.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    s.IndexOf("VID_1B1C", StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
            }
        } catch {}
        finally { Native.SetupDiDestroyDeviceInfoList(dev); }
        return false;
    }
}
#endregion

#region Tray app
class TrayApp : ApplicationContext {
    readonly NotifyIcon _ni;
    readonly System.Windows.Forms.Timer _timer;
    readonly LogitechHidPp _logi = new LogitechHidPp();
    readonly FlydigiApex _apex = new FlydigiApex(LoadApexUid());

    // Loads the Flydigi Apex UID from (in order):
    //   1) BATTERYTRAY_APEX_UID env var
    //   2) "apex-uid.txt" next to BatteryTray.exe
    //   3) returns empty (Apex row will show "offline" until configured)
    // Find your UID by tailing Flydigi Space Station's logs/main.log while it
    // connects; look for a "uid":"…" string in the GetDeviceDetailInfo request.
    static string LoadApexUid() {
        var ovr = Environment.GetEnvironmentVariable("BATTERYTRAY_APEX_UID");
        if (!string.IsNullOrEmpty(ovr)) return ovr.Trim();
        try {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apex-uid.txt");
            if (File.Exists(p)) return File.ReadAllText(p).Trim();
        } catch {}
        return "";
    }

    int? _logiPct;
    string _logiStatus = "";
    bool _logiCharging = false;
    int? _corsairPct;
    string _corsairName = "";
    bool _corsairCharging = false;
    int? _apexGear;
    int _apexMaxGear = 4;
    bool _apexStale = false;
    string _apexStatus = "";

    const int BARS_MAX = 4;
    static int PctToBars(int pct) {
        if (pct <= 0) return 0;
        if (pct >= 100) return BARS_MAX;
        return Math.Max(1, (pct + 24) / 25);
    }
    static int GearToBars(int gear, int maxGear) {
        if (maxGear <= 0) return 0;
        return Math.Max(0, Math.Min(BARS_MAX, (int)Math.Round((double)gear * BARS_MAX / maxGear)));
    }

    public TrayApp() {
        var menu = new ContextMenuStrip();
        // Use Segoe Fluent Icons (Win11) / Segoe MDL2 Assets (Win10) glyphs — same
        // icon font Windows itself uses. Code points: Mouse=E962, Keyboard=E92E,
        // XboxOneConsole/Gamepad=E7FC.
        var iconMouse  = MakeMdl2Icon("");
        var iconKbd    = MakeMdl2Icon("");
        var iconPad    = MakeMdl2Icon("");

        var miLogi    = new ToolStripMenuItem("Logitech G502 X PLUS: …",              iconMouse) { Enabled = false };
        var miCorsair = new ToolStripMenuItem("CORSAIR K70 RGB PRO Mini Wireless: …", iconKbd)   { Enabled = false };
        var miApex    = new ToolStripMenuItem("Flydigi Apex 4: …",                    iconPad)   { Enabled = false };
        var miRefresh = new ToolStripMenuItem("Refresh now");
        var miExit = new ToolStripMenuItem("Exit");
        miRefresh.Click += (s, e) => Refresh();
        miExit.Click += (s, e) => { _ni.Visible = false; Application.Exit(); };
        menu.Items.Add(miLogi);
        menu.Items.Add(miCorsair);
        menu.Items.Add(miApex);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miRefresh);
        menu.Items.Add(miExit);
        menu.Opening += (s, e) => {
            miLogi.Text    = "Logitech G502 X PLUS — " + FmtBars(_logiPct, _logiCharging);
            string cName   = string.IsNullOrEmpty(_corsairName) ? "CORSAIR K70 RGB PRO Mini Wireless" : _corsairName;
            miCorsair.Text = cName + " — " + FmtBars(_corsairPct, _corsairCharging);
            miApex.Text    = "Flydigi Apex 4 — " + FmtApex(_apexGear, _apexMaxGear, _apexStale);
        };

        _ni = new NotifyIcon {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "Battery Tray (loading...)",
            Icon = BuildIcon3(-1, false, -1, false, -1, false)
        };
        _ni.MouseClick += (s, e) => {
            if (e.Button == MouseButtons.Left) {
                var m = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m != null) m.Invoke(_ni, null);
            }
        };

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 10000;  // 10s — keeps off-detection latency low.
        _timer.Tick += (s, e) => Refresh();
        _timer.Start();
        Refresh();
    }

    static string FmtBars(int? pct, bool charging) {
        if (!pct.HasValue) return "offline";
        int bars = PctToBars(pct.Value);
        return bars + "/" + BARS_MAX + " (" + pct.Value + "%" + (charging ? ", charging" : "") + ")";
    }
    static string FmtApex(int? gear, int maxGear, bool stale) {
        if (!gear.HasValue) return "offline";
        int bars = GearToBars(gear.Value, maxGear);
        return bars + "/" + BARS_MAX + (stale ? " (cached — close Space Station to refresh)" : "");
    }

    static void DbgLog(string s) {
        try {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "battery-tray.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n");
        } catch {}
    }

    void Refresh() {
        DbgLog("refresh start");
        var tLogi = Task.Run(() => {
            try {
                var info = _logi.ReadBattery();
                _logiPct = info.Percent;
                _logiStatus = info.Status;
                _logiCharging = !string.IsNullOrEmpty(info.Status) && info.Status.IndexOf("charg", StringComparison.OrdinalIgnoreCase) >= 0;
                DbgLog("logi: pct=" + (_logiPct.HasValue ? _logiPct.Value.ToString() : "null") + " status=" + _logiStatus);
            } catch (Exception ex) { DbgLog("logi EXC: " + ex.GetType().Name + " " + ex.Message); }
        });
        var tCorsair = Task.Run(() => {
            try {
                // 1) iCUE SDK — what iCUE itself sees (most accurate, supports charging state).
                var sdk = CorsairSdk.ReadK70();
                if (sdk.Percent.HasValue) {
                    _corsairPct = sdk.Percent;
                    _corsairName = "CORSAIR K70 RGB PRO Mini Wireless";
                    _corsairCharging = false;  // TODO: query CDPI_BatteryStatus when available
                    DbgLog("corsair SDK: pct=" + sdk.Percent);
                    return;
                }
                DbgLog("corsair SDK failed: " + (sdk.Error ?? "(none)") + " — trying BLE");
                // 2) BLE GATT direct read (works even when iCUE is closed).
                var ble = BleBattery.ReadK70();
                if (ble.Percent.HasValue) {
                    _corsairPct = ble.Percent;
                    _corsairName = "CORSAIR K70 RGB PRO Mini Wireless";
                    _corsairCharging = false;
                    DbgLog("corsair BLE: pct=" + ble.Percent);
                    return;
                }
                DbgLog("corsair BLE failed: " + ble.Error + " — falling back to Pnp");
                // 3) Last resort: Windows-cached Pnp value (may be very stale).
                var list = PnpBattery.ReadAll("K70");
                if (list.Count > 0) {
                    _corsairPct = list[0].Percent;
                    _corsairName = list[0].Name;
                } else {
                    _corsairPct = null;
                }
                _corsairCharging = false;
                DbgLog("corsair Pnp: pct=" + (_corsairPct.HasValue ? _corsairPct.Value.ToString() : "null"));
            } catch (Exception ex) { DbgLog("corsair EXC: " + ex.GetType().Name + " " + ex.Message); }
        });
        var tApex = Task.Run(() => {
            try {
                var info = _apex.Read();
                _apexGear = info.Gear;
                _apexMaxGear = info.MaxGear;
                _apexStale = info.Stale;
                _apexStatus = info.Status;
                DbgLog("apex: gear=" + (_apexGear.HasValue ? _apexGear.Value.ToString() : "null") + " stale=" + _apexStale + " status=" + _apexStatus);
            } catch (Exception ex) { DbgLog("apex EXC: " + ex.GetType().Name + " " + ex.Message); }
        });
        Task.Run(() => Task.WaitAll(new[] { tLogi, tCorsair, tApex }, 5000))
            .ContinueWith(_ => { DbgLog("update ui"); UpdateUi(); }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void UpdateUi() {
        if (_ni == null) { DbgLog("update ui: _ni null"); return; }
        int logiBars = _logiPct.HasValue    ? PctToBars(_logiPct.Value)         : -1;
        int kbdBars  = _corsairPct.HasValue ? PctToBars(_corsairPct.Value)      : -1;
        int apexBars = _apexGear.HasValue   ? GearToBars(_apexGear.Value, _apexMaxGear) : -1;
        DbgLog("update ui: logiBars=" + logiBars + " kbdBars=" + kbdBars + " apexBars=" + apexBars);

        // Set icon first — if tooltip text fails, at least the icon updates.
        try {
            var newIcon = BuildIcon3(logiBars, _logiCharging, kbdBars, _corsairCharging, apexBars, false);
            var old = _ni.Icon;
            _ni.Icon = newIcon;
            try { if (old != null) old.Dispose(); } catch {}
            DbgLog("icon set ok");
        } catch (Exception ex) { DbgLog("icon EXC: " + ex.GetType().Name + " " + ex.Message); }

        // NotifyIcon.Text limit is 63 chars. Keep it short.
        string tip =
            ShortFmt("M", _logiPct, _logiCharging) + " " +
            ShortFmt("K", _corsairPct, _corsairCharging) + " " +
            ShortApex("A", _apexGear, _apexMaxGear, _apexStale);
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        try { _ni.Text = tip; } catch (Exception ex) { DbgLog("text EXC: " + ex.Message); }
    }

    static string ShortFmt(string prefix, int? pct, bool charging) {
        if (!pct.HasValue) return prefix + ":-";
        int bars = PctToBars(pct.Value);
        return prefix + ":" + bars + "/" + BARS_MAX + "(" + pct.Value + "%" + (charging ? "+" : "") + ")";
    }
    static string ShortApex(string prefix, int? gear, int maxGear, bool stale) {
        if (!gear.HasValue) return prefix + ":-";
        int bars = GearToBars(gear.Value, maxGear);
        return prefix + ":" + bars + "/" + BARS_MAX + (stale ? "(stale)" : "");
    }

    enum DeviceKind { Mouse, Keyboard, Gamepad }

    static Icon BuildIcon3(int mouseBars, bool mouseCharging,
                           int kbdBars,   bool kbdCharging,
                           int apexBars,  bool apexCharging) {
        // 32x32. Three rows, each ~10 px tall (with 1 px gap).
        // Layout per row: [glyph 10px] [gap 1px] [4 bars × 5px = 20px] [1 px margin]
        using (var bmp = new Bitmap(32, 32)) {
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                DrawRow(g, DeviceKind.Mouse,    mouseBars, mouseCharging, 0);
                DrawRow(g, DeviceKind.Keyboard, kbdBars,   kbdCharging,   11);
                DrawRow(g, DeviceKind.Gamepad,  apexBars,  apexCharging,  22);
            }
            IntPtr hIcon = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
            finally { DestroyIcon(hIcon); }
        }
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static void DrawRow(Graphics g, DeviceKind kind, int bars, bool charging, int y) {
        int h = 10;
        using (var bg = new SolidBrush(Color.FromArgb(30, 30, 30))) g.FillRectangle(bg, 0, y, 32, h);
        // Glyph in left 10 px
        DrawGlyph(g, kind, 0, y, 10, h, Color.White);
        // Bars in right 22 px (4 cells × 5 px + 3 × 0 gap), but leave 1 px gap from glyph
        int barsAreaX = 11;
        int barsAreaW = 21;
        if (bars < 0) {
            using (var pen = new Pen(Color.FromArgb(120, 120, 120), 2))
                g.DrawLine(pen, barsAreaX + 2, y + h / 2, barsAreaX + barsAreaW - 2, y + h / 2);
            return;
        }
        Color fill = bars >= 3 ? Color.FromArgb(64, 220, 64)
                   : bars >= 2 ? Color.FromArgb(230, 200, 50)
                   : bars >= 1 ? Color.FromArgb(230, 130, 50)
                               : Color.FromArgb(230, 64, 64);
        int cellW = 5;
        for (int i = 0; i < BARS_MAX; i++) {
            int x = barsAreaX + i * (cellW + 0);  // no gap to maximize cell width
            Color c = i < bars ? fill : Color.FromArgb(60, 60, 60);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y + 2, cellW - 1, h - 4);
        }
        if (charging) DrawLightning(g, barsAreaX + barsAreaW / 2, y + h / 2);
    }

    static void DrawGlyph(Graphics g, DeviceKind kind, int x, int y, int w, int h, Color color) {
        switch (kind) {
            case DeviceKind.Mouse:    DrawMouseGlyph(g, x, y, w, h, color); break;
            case DeviceKind.Keyboard: DrawKeyboardGlyph(g, x, y, w, h, color); break;
            case DeviceKind.Gamepad:  DrawGamepadGlyph(g, x, y, w, h, color); break;
        }
    }

    static void DrawMouseGlyph(Graphics g, int x, int y, int w, int h, Color color) {
        using (var p = new Pen(color, 1.2f)) {
            // Mouse body: tall oval
            g.DrawEllipse(p, x + 2, y + 1, w - 4, h - 2);
            // Scroll wheel: small vertical line down middle top
            g.DrawLine(p, x + w / 2, y + 2, x + w / 2, y + 4);
        }
    }
    static void DrawMouseGlyph(Graphics g) { DrawMouseGlyph(g, 0, 0, 32, 32, Color.Black); }

    static void DrawKeyboardGlyph(Graphics g, int x, int y, int w, int h, Color color) {
        using (var p = new Pen(color, 1f)) {
            // Keyboard body: rounded rect
            g.DrawRectangle(p, x + 1, y + 2, w - 3, h - 4);
            // Keys: 2 rows of 3 small ticks
            using (var b = new SolidBrush(color)) {
                for (int row = 0; row < 2; row++) {
                    for (int col = 0; col < 3; col++) {
                        g.FillRectangle(b, x + 2 + col * 2, y + 3 + row * 2, 1, 1);
                    }
                }
            }
        }
    }
    static void DrawKeyboardGlyph(Graphics g) { DrawKeyboardGlyph(g, 0, 0, 32, 32, Color.Black); }

    static void DrawGamepadGlyph(Graphics g, int x, int y, int w, int h, Color color) {
        using (var p = new Pen(color, 1.2f)) {
            // Gamepad body: pill shape
            g.DrawEllipse(p, x + 0, y + 2, w - 1, h - 4);
            // Two small joystick circles
            using (var b = new SolidBrush(color)) {
                g.FillEllipse(b, x + 2, y + h / 2, 2, 2);
                g.FillEllipse(b, x + w - 5, y + h / 2, 2, 2);
            }
        }
    }
    static void DrawGamepadGlyph(Graphics g) { DrawGamepadGlyph(g, 0, 0, 32, 32, Color.Black); }

    static Image MakeGlyph(Action<Graphics> draw) {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(0.5f, 0.5f);
            draw(g);
        }
        return bmp;
    }

    static Image MakeMdl2Icon(string glyph) {
        // Render a Segoe Fluent Icons glyph to a 16x16 menu image.
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            // Prefer Win11 Fluent name; fall back to MDL2 (Win10) — GDI+ handles fallback automatically.
            using (var f = new Font("Segoe Fluent Icons", 11f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.Black)) {
                var sz = g.MeasureString(glyph, f);
                g.DrawString(glyph, f, b, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f);
            }
        }
        return bmp;
    }

    static void DrawLightning(Graphics g, int cx, int cy) {
        // Cartoon lightning bolt, ~12 px tall, centered on (cx, cy).
        Point[] pts = new[] {
            new Point(cx + 1, cy - 7),
            new Point(cx - 4, cy + 1),
            new Point(cx - 1, cy + 1),
            new Point(cx - 3, cy + 7),
            new Point(cx + 5, cy - 1),
            new Point(cx + 1, cy - 1),
            new Point(cx + 4, cy - 7),
        };
        using (var outline = new Pen(Color.Black, 2f)) g.DrawPolygon(outline, pts);
        using (var br = new SolidBrush(Color.FromArgb(255, 250, 90))) g.FillPolygon(br, pts);
    }
}

static class Program {
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Trim working set every 60s so Task Manager reports a sane number.
        // Memory is paged out lazily; the committed footprint of a .NET 4 WinForms
        // app is ~30MB no matter what — that's runtime + GDI + WinForms, not a leak.
        var trim = new System.Windows.Forms.Timer();
        trim.Interval = 60000;
        trim.Tick += (s, e) => SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        trim.Start();
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        Application.Run(new TrayApp());
    }
}
#endregion
