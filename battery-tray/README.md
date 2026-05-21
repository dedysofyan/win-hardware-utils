# battery-tray

A single tray icon that shows battery levels for:

- **Logitech G502 X PLUS** (Lightspeed wireless mouse) — via the HID++ 2.0
  `UnifiedBattery` feature (`0x1004`).
- **Corsair K70 RGB PRO Mini Wireless** — via the standard BLE GATT Battery
  Service (`0x180F`), characteristic `0x2A19`. Falls back to Windows' cached
  Pnp property if BLE fails.
- **Flydigi Apex 4** — via the named pipe `\\?\pipe\fcs.sock` that
  `SpaceStationService.exe` exposes, speaking the FDG_PROTOCOL framing
  (`"FDG_PROTOCOL\n"` + 4-byte LE length + protobuf-encoded `IpcCommand`).
  The Apex query is `GetDeviceDetailInfo` (cmdId `4099`) and battery comes
  back at `IpcResult.field 7 → GetDeviceDetailInfoResult.field 1 →
  ControllerInfo.field 1 → DeviceInfo.field 9`.

Each row in the tray icon shows a 4-bar level + small device glyph
(mouse/keyboard/gamepad). Right-click for full readout with brand/model and
optional charging state. The icon redraws every 10 s.

## Build

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$facades = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades'
$winmd = 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd'

& $csc /nologo /target:winexe /platform:x64 `
    /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Core.dll,System.Runtime.WindowsRuntime.dll `
    "/reference:$facades\System.Runtime.dll" `
    "/reference:$facades\System.Runtime.InteropServices.WindowsRuntime.dll" `
    "/reference:$winmd" `
    /out:BatteryTray.exe `
    BatteryTray.cs
```

Adjust the `<sdk>` version in the Windows.winmd path to whatever's installed
on your machine (any modern Windows 10/11 build works).

If you also want the optional iCUE-SDK path for the K70 (currently disabled
in code by default), download `iCUESDK.x64_2019.dll` from
[CorsairOfficial/cue-sdk](https://github.com/CorsairOfficial/cue-sdk)
releases and drop it next to `BatteryTray.exe`. iCUE itself must be running
and the exe approved in iCUE's "Game and Application Integration" settings.
We hit a session-handshake timeout against iCUE5 even when approved — leaving
the integration in the code as a fallback but not relying on it.

## Per-device notes

### Logitech G502 X PLUS (HID++ 2.0)

The Lightspeed receiver enumerates as `VID_046D&PID_C547`. The HID++ control
interface is the one with usage page `0xFF00`, usage `0x0002`, and 20-byte
input/output reports — usually `mi_02&col02`. Standard handshake:

1. Send `Root.GetFeature(0x1004)` to learn the `UnifiedBattery` feature index.
2. Send `UnifiedBattery.GetStatus()` and read state-of-charge from byte 4 of
   the response.

The receiver's "device index" is `0x01` for a Lightspeed-paired device.

### Corsair K70 (BLE GATT)

BLE MAC is encoded in the Bluetooth `BTHLE\DEV_<MAC>` instance ID (hex,
lowercase, no separators). Open via
`Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync()`,
get the Battery service, read the Battery Level characteristic.

Known firmware quirk: K70 doesn't push BLE Battery Level updates while
charging — the value stays flat until charge cycle completes. iCUE detects
charging via a Corsair-proprietary channel that's not reachable from this
app. We've documented this rather than worked around it.

### Flydigi Apex 4 (named pipe + protobuf)

Architecture: Flydigi UI is Electron, talks to `SpaceStationService.exe`
over `\\?\pipe\fcs.sock`. The service is **single-client** — while Flydigi's
own UI is connected, our connection times out. The tray app polls
opportunistically and falls back to its last cached value otherwise.

Protocol details extracted from the bundled `main.js`:

- Framing for small payloads: `"FDG_PROTOCOL\n"` + 4-byte little-endian
  payload length + payload. Larger payloads use `"FDG_CHUNK"` /
  `"FDG_COMPRESSED"` (gzip) variants.
- Payload: protobuf-encoded `Flydigi.SharedResources.data.protobuf.IpcCommand`
  (envelope) for requests, `IpcResult` for responses.
- Envelope schema and command IDs were extracted by base64-decoding the
  embedded `FileDescriptorProto` blobs (`re("...", [...])` calls in
  `main.js`). The relevant command is `IpcCommandEnum_GetDeviceDetailInfo`
  = `0x1003` (4099).
- For battery: navigate `IpcResult.field 7` (`GetDeviceDetailInfoResult`)
  → `field 1` (`controllerInfo`) → `field 1` (`parent: DeviceInfo`)
  → `field 9` (`battery int32`). Also check `field 8` (`isConnected bool`)
  to distinguish "controller off" from "controller has 0% battery".
- Battery value range: appears to be 0–4 (a "gear" scale, not percent).
  The keyboard's bar UI shows 4 of 4 when full.

## Install / autostart

After building:

```powershell
$desktop = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $desktop 'BatteryTray.lnk'
$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($lnk)
$sc.TargetPath = (Resolve-Path .\BatteryTray.exe).Path
$sc.WorkingDirectory = (Resolve-Path .).Path
$sc.Save()
```

To pin the tray icon to the always-visible area:

```powershell
# After running the app once so it registers, find its UID in:
#   HKCU:\Control Panel\NotifyIconSettings
# then:
Set-ItemProperty -Path 'HKCU:\Control Panel\NotifyIconSettings\<uid>' -Name 'IsPromoted' -Value 1 -Type DWord
```
