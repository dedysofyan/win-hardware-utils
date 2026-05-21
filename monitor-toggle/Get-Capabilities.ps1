Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class DdcCap {
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
    public static extern bool GetCapabilitiesStringLength(IntPtr h, out uint length);
    [DllImport("dxva2.dll", CharSet=CharSet.Ansi)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder ascii, uint length);
}
'@

$handles = New-Object System.Collections.Generic.List[IntPtr]
[void][DdcCap]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero,
    {param([IntPtr]$h,[IntPtr]$hdc,[DdcCap+RECT]$r,[IntPtr]$d) $handles.Add($h); $true},
    [IntPtr]::Zero)

foreach ($h in $handles) {
    $count = 0
    if (-not [DdcCap]::GetNumberOfPhysicalMonitorsFromHMONITOR($h, [ref]$count)) { continue }
    $arr = New-Object 'DdcCap+PHYSICAL_MONITOR[]' $count
    if (-not [DdcCap]::GetPhysicalMonitorsFromHMONITOR($h, $count, $arr)) { continue }
    foreach ($pm in $arr) {
        Write-Host ""
        Write-Host "=== $($pm.szPhysicalMonitorDescription) ===" -ForegroundColor Cyan
        $len = 0
        if ([DdcCap]::GetCapabilitiesStringLength($pm.hPhysicalMonitor, [ref]$len) -and $len -gt 0) {
            $sb = New-Object Text.StringBuilder ([int]$len)
            if ([DdcCap]::CapabilitiesRequestAndCapabilitiesReply($pm.hPhysicalMonitor, $sb, $len)) {
                Write-Host $sb.ToString()
            } else {
                Write-Host "(capability reply failed)"
            }
        } else {
            Write-Host "(no capability string)"
        }
    }
    [void][DdcCap]::DestroyPhysicalMonitors($count, $arr)
}
