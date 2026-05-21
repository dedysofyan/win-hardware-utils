using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

class ToggleInput {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct PHYSICAL_MONITOR {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)]
        public string szPhysicalMonitorDescription;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }
    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, ref uint count);
    [DllImport("dxva2.dll")]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")]
    public static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")]
    public static extern bool SetVCPFeature(IntPtr h, byte vcp, uint val);
    [DllImport("dxva2.dll")]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte vcp, IntPtr pvct, out uint cur, out uint max);

    [DllImport("kernel32.dll")] public static extern uint GetLastError();

    // SendInput for synthesizing the MSI fallback hotkey
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }  // overshoots safely on x64
    [DllImport("user32.dll", SetLastError=true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    const uint KEYEVENTF_KEYUP = 0x0002;

    static void SynthesizeHotkey(ushort[] keys) {
        // Press down in order, release in reverse
        int n = keys.Length;
        INPUT[] inputs = new INPUT[n * 2];
        for (int i = 0; i < n; i++) {
            inputs[i].type = 1;
            inputs[i].ki.wVk = keys[i];
            inputs[n * 2 - 1 - i].type = 1;
            inputs[n * 2 - 1 - i].ki.wVk = keys[i];
            inputs[n * 2 - 1 - i].ki.dwFlags = KEYEVENTF_KEYUP;
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    static string LogPath = Path.Combine(Path.GetTempPath(), "toggle-input.log");
    static void Log(string s) {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n"); } catch {}
    }

    static int Main(string[] args) {
        Log("--- run start ---");
        Log("args: " + string.Join(" ", args));
        string match = "AOC";
        byte dp = 0x0F, hdmi = 0x11;
        ushort[] fallbackKeys = null;  // e.g. Ctrl+Alt+F10
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--match" && i + 1 < args.Length) { match = args[++i]; }
            else if (args[i] == "--dp" && i + 1 < args.Length) { dp = Convert.ToByte(args[++i], 16); }
            else if (args[i] == "--hdmi" && i + 1 < args.Length) { hdmi = Convert.ToByte(args[++i], 16); }
            else if (args[i] == "--fallback" && i + 1 < args.Length) {
                // comma-separated list of VK hex codes, e.g. "11,12,79" for Ctrl+Alt+F10
                var parts = args[++i].Split(',');
                fallbackKeys = new ushort[parts.Length];
                for (int j = 0; j < parts.Length; j++) fallbackKeys[j] = Convert.ToUInt16(parts[j], 16);
            }
        }

        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { handles.Add(h); return true; },
            IntPtr.Zero);

        foreach (var h in handles) {
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, ref count) || count == 0) continue;
            var arr = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(h, count, arr)) continue;
            try {
                foreach (var pm in arr) {
                    Log("monitor: '" + pm.szPhysicalMonitorDescription + "'");
                    if (pm.szPhysicalMonitorDescription != null &&
                        pm.szPhysicalMonitorDescription.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) {
                        Log("  matched against '" + match + "'");

                        string stateFile = Path.Combine(Path.GetTempPath(),
                            "toggle-input-" + match.ToLowerInvariant() + ".state");
                        uint prevState = dp;
                        try {
                            if (File.Exists(stateFile)) {
                                prevState = Convert.ToUInt32(File.ReadAllText(stateFile).Trim(), 16);
                            }
                        } catch {}
                        Log(string.Format("  state file prev=0x{0:X2}", prevState));

                        // Try a short read. If it fails AND we have a fallback hotkey,
                        // synthesize that hotkey (e.g. MSI's own Ctrl+Alt+F10) because
                        // DDC/CI writes silently no-op when the handle is stale.
                        uint cur = 0, max = 0;
                        bool readOk = false;
                        for (int i = 0; i < 3 && !readOk; i++) {
                            readOk = GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, 0x60, IntPtr.Zero, out cur, out max);
                            uint e = GetLastError();
                            Log(string.Format("  read 0x60 attempt {0}: ok={1} cur=0x{2:X2} err=0x{3:X8}", i+1, readOk, cur, e));
                            if (!readOk) Thread.Sleep(150);
                        }

                        if (!readOk && fallbackKeys != null) {
                            Log(string.Format("  read failed; synthesizing fallback hotkey [{0}]",
                                string.Join(",", Array.ConvertAll(fallbackKeys, k => "0x" + k.ToString("X2")))));
                            SynthesizeHotkey(fallbackKeys);
                            // Flip the state file so the next call assumes the fallback worked.
                            uint assumedNext = (prevState == dp) ? (uint)hdmi : (uint)dp;
                            try { File.WriteAllText(stateFile, string.Format("{0:X2}", assumedNext)); } catch {}
                            Log("  --- run end (fallback fired) ---");
                            return 0;
                        }

                        uint currentState = readOk ? cur : prevState;
                        uint next = (currentState == dp) ? (uint)hdmi : (uint)dp;
                        Log(string.Format("  source=0x{0:X2} ({1}), writing 0x{2:X2}",
                            currentState, readOk ? "live" : "state-file", next));

                        for (int i = 0; i < 10; i++) {
                            bool wr = SetVCPFeature(pm.hPhysicalMonitor, 0x60, next);
                            uint e = GetLastError();
                            Log(string.Format("  write 0x60=0x{0:X2} attempt {1}: ok={2} err=0x{3:X8}", next, i+1, wr, e));
                            if (wr) {
                                try { File.WriteAllText(stateFile, string.Format("{0:X2}", next)); } catch {}
                                Log("  --- run end (ok) ---");
                                return 0;
                            }
                            Thread.Sleep(200);
                        }
                        Log("  giving up: write failed");
                        return 4;
                    }
                }
            } finally {
                DestroyPhysicalMonitors(count, arr);
            }
        }
        return 2;
    }
}
