<#
.SYNOPSIS
    Toggles the AOC monitor's input source between DP1 and HDMI1 via DDC/CI.

.DESCRIPTION
    Bypasses G-Menu and talks to the monitor directly using dxva2.dll's
    SetVCPFeature with VCP code 0x60 (Input Source Select).
    Standard values: 0x0F = DP1, 0x11 = HDMI1.

.PARAMETER Discover
    Prints all monitors found, with manufacturer code, current input value,
    and any other diagnostics. Run this once to verify the AOC is detected.

.PARAMETER ManufacturerMatch
    EDID manufacturer code to match. Default: AOC.

.PARAMETER DpValue
    VCP value for DP1. Default 0x0F (15) per DDC/CI spec.

.PARAMETER HdmiValue
    VCP value for HDMI1. Default 0x11 (17) per DDC/CI spec.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Toggle-MonitorInput.ps1 -Discover

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Toggle-MonitorInput.ps1
#>
[CmdletBinding()]
param(
    [switch]$Discover,
    [string]$ManufacturerMatch = 'AOC',
    [byte]$DpValue   = 0x0F,
    [byte]$HdmiValue = 0x11
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

public static class DdcCi {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE {
        public int    cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int    StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rect, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("dxva2.dll")]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

    [DllImport("dxva2.dll")]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    public static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll")]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

    [DllImport("dxva2.dll")]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor, byte vcpCode, IntPtr pvct,
        out uint pdwCurrentValue, out uint pdwMaximumValue);

    public static List<IntPtr> Enumerate() {
        var list = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { list.Add(h); return true; },
            IntPtr.Zero);
        return list;
    }
}
'@

function Get-AdapterDeviceIdForHMonitor {
    param([IntPtr]$HMonitor)
    $info = New-Object DdcCi+MONITORINFOEX
    $info.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($info)
    [void][DdcCi]::GetMonitorInfo($HMonitor, [ref]$info)
    $dd = New-Object DdcCi+DISPLAY_DEVICE
    $dd.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($dd)
    [void][DdcCi]::EnumDisplayDevices($info.szDevice, 0, [ref]$dd, 1)  # 1 = EDD_GET_DEVICE_INTERFACE_NAME
    return [PSCustomObject]@{
        Device       = $info.szDevice
        AdapterID    = $dd.DeviceID
        AdapterDesc  = $dd.DeviceString
    }
}

function Decode-WmiCharArray {
    param($arr)
    if (-not $arr) { return $null }
    -join ($arr | Where-Object { $_ -ne 0 } | ForEach-Object { [char][int]$_ })
}

function Get-EdidMonitorTable {
    # Returns map: deviceIdKeyFragment -> @{ ManufacturerCode, ProductCodeID, FriendlyName, InstanceName }
    $list = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue
    $result = @()
    foreach ($m in $list) {
        $result += [PSCustomObject]@{
            InstanceName     = $m.InstanceName
            ManufacturerCode = Decode-WmiCharArray $m.ManufacturerName
            ProductCodeID    = Decode-WmiCharArray $m.ProductCodeID
            FriendlyName     = Decode-WmiCharArray $m.UserFriendlyName
            SerialNumberID   = Decode-WmiCharArray $m.SerialNumberID
        }
    }
    return $result
}

function Match-EdidToAdapter {
    # The WMI InstanceName looks like:  DISPLAY\AOC2790\5&3...&UID4353_0
    # The adapter DeviceID looks like:  \\?\DISPLAY#AOC2790#5&3...&UID4353#{...}
    # We match by the middle two tokens (model + instance).
    param($EdidList, [string]$AdapterDeviceId)
    if (-not $AdapterDeviceId) { return $null }
    $token = $AdapterDeviceId -replace '^\\\\\?\\', '' -replace '#', '\'
    # token like: DISPLAY\AOC2790\5&3...&UID4353\{...}
    $parts = $token.Split('\')
    if ($parts.Count -lt 3) { return $null }
    $key = "$($parts[0])\$($parts[1])\$($parts[2])"  # DISPLAY\<MODEL>\<INSTANCE>
    foreach ($e in $EdidList) {
        if ($e.InstanceName -like "$key*") { return $e }
    }
    return $null
}

function Get-PhysicalMonitorsInfo {
    $edid = Get-EdidMonitorTable
    $hMonitors = [DdcCi]::Enumerate()
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($h in $hMonitors) {
        $count = 0
        if (-not [DdcCi]::GetNumberOfPhysicalMonitorsFromHMONITOR($h, [ref]$count)) { continue }
        if ($count -le 0) { continue }
        $arr = New-Object 'DdcCi+PHYSICAL_MONITOR[]' $count
        if (-not [DdcCi]::GetPhysicalMonitorsFromHMONITOR($h, $count, $arr)) { continue }
        $adapter = Get-AdapterDeviceIdForHMonitor -HMonitor $h
        $edidMatch = Match-EdidToAdapter -EdidList $edid -AdapterDeviceId $adapter.AdapterID
        foreach ($pm in $arr) {
            $result.Add([PSCustomObject]@{
                Handle           = $pm.hPhysicalMonitor
                Description      = $pm.szPhysicalMonitorDescription
                Device           = $adapter.Device
                AdapterDesc      = $adapter.AdapterDesc
                AdapterID        = $adapter.AdapterID
                ManufacturerCode = if ($edidMatch) { $edidMatch.ManufacturerCode } else { $null }
                ProductCodeID    = if ($edidMatch) { $edidMatch.ProductCodeID }    else { $null }
                FriendlyName     = if ($edidMatch) { $edidMatch.FriendlyName }     else { $null }
                # Keep the array so we can destroy properly later
                _ArrayRef        = $arr
            })
        }
    }
    return ,$result
}

function Read-Vcp {
    param([IntPtr]$Handle, [byte]$Vcp)
    $cur = 0; $max = 0
    $ok = [DdcCi]::GetVCPFeatureAndVCPFeatureReply($Handle, $Vcp, [IntPtr]::Zero, [ref]$cur, [ref]$max)
    if (-not $ok) { return $null }
    return [PSCustomObject]@{ Current = $cur; Maximum = $max }
}

function Write-Vcp {
    param([IntPtr]$Handle, [byte]$Vcp, [uint32]$Value)
    return [DdcCi]::SetVCPFeature($Handle, $Vcp, $Value)
}

# ---- Main ----
$monitors = Get-PhysicalMonitorsInfo

if ($Discover) {
    $rows = foreach ($m in $monitors) {
        $vcp = Read-Vcp -Handle $m.Handle -Vcp 0x60
        $cur = if ($vcp) { '0x{0:X2}' -f $vcp.Current } else { '(no DDC/CI)' }
        [PSCustomObject]@{
            Device            = $m.Device
            Mfr               = $m.ManufacturerCode
            Model             = $m.ProductCodeID
            FriendlyName      = $m.FriendlyName
            Description       = $m.Description
            CurrentInput_0x60 = $cur
            HandleHex         = '0x{0:X}' -f [int64]$m.Handle
        }
    }
    $rows | Format-Table -AutoSize -Wrap | Out-String | Write-Host
    # cleanup
    $cleanup = $monitors | Group-Object _ArrayRef
    foreach ($g in $cleanup) {
        $arr = $g.Group[0]._ArrayRef
        [void][DdcCi]::DestroyPhysicalMonitors([uint32]$arr.Length, $arr)
    }
    return
}

# Toggle mode
$target = $monitors | Where-Object { $_.ManufacturerCode -eq $ManufacturerMatch } | Select-Object -First 1
if (-not $target) {
    # Fallback: any monitor whose current 0x60 reads as DP1 or HDMI1
    foreach ($m in $monitors) {
        $vcp = Read-Vcp -Handle $m.Handle -Vcp 0x60
        if ($vcp -and ($vcp.Current -eq $DpValue -or $vcp.Current -eq $HdmiValue)) {
            $target = $m
            break
        }
    }
}

if (-not $target) {
    Write-Error "No matching monitor found (looked for manufacturer '$ManufacturerMatch'). Run with -Discover."
    exit 2
}

$vcp = Read-Vcp -Handle $target.Handle -Vcp 0x60
if (-not $vcp) {
    Write-Error "Monitor '$($target.FriendlyName)' did not respond to DDC/CI VCP 0x60. Ensure DDC/CI is enabled in the OSD."
    exit 3
}

$next = if ($vcp.Current -eq $DpValue) { $HdmiValue } else { $DpValue }
$ok = Write-Vcp -Handle $target.Handle -Vcp 0x60 -Value $next
if (-not $ok) {
    Write-Error ("SetVCPFeature failed (current=0x{0:X2}, target=0x{1:X2})." -f $vcp.Current, $next)
    exit 4
}

# Cleanup all physical monitor handles
$cleanup = $monitors | Group-Object _ArrayRef
foreach ($g in $cleanup) {
    $arr = $g.Group[0]._ArrayRef
    [void][DdcCi]::DestroyPhysicalMonitors([uint32]$arr.Length, $arr)
}
