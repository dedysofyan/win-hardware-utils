# G-Menu Input Toggle

Bypasses AOC's G-Menu app to switch the AOC monitor's input source via a
keyboard shortcut, since G-Menu's built-in hotkey editor doesn't expose
INPUT SELECTION (only Game Mode / Brightness / Shadow Control / Volume).

## How it works

Talks to the monitor directly using the Windows DDC/CI API in `dxva2.dll`.
VCP code `0x60` is the DDC/CI standard "Input Source Select" feature:

| Value | Input |
|------:|-------|
| `0x0F` | DisplayPort 1 |
| `0x10` | DisplayPort 2 |
| `0x11` | HDMI 1 |
| `0x12` | HDMI 2 |

The exe enumerates physical monitors, finds the one whose
`PHYSICAL_MONITOR.szPhysicalMonitorDescription` contains a match string
(default `"AOC"`), reads current `0x60`, and writes the opposite of the
two configured values.

If the read fails (which happens when the monitor's active source is
not the PC's cable — see the MSI section), and `--fallback` is set,
the exe synthesises a configured Ctrl+Alt+key combo via `SendInput`
so the monitor's own first-party software can perform the switch.

## Files

| File | Purpose |
|------|---------|
| `ToggleInput.cs` | Source for the toggle exe |
| `ToggleInput.exe` | Compiled toggle (winexe, no console flash) |
| `Toggle-MonitorInput.ps1` | Original PowerShell version + `-Discover` mode |
| `Toggle-MonitorInput.vbs` | Legacy silent launcher for the .ps1 (kept as backup) |

Desktop shortcut `Toggle AOC Input.lnk` points at `ToggleInput.exe`.

## Usage

- Double-click the desktop shortcut, **or**
- Press the assigned Windows hotkey (set via Properties → Shortcut key).

CLI overrides:

```
ToggleInput.exe --match "AOC" --dp 0F --hdmi 11 --fallback 11,12,BE
```

| Flag | Meaning |
|------|---------|
| `--match <str>` | Substring to match against `szPhysicalMonitorDescription`. Default `"AOC"`. |
| `--dp <hex>` | VCP value to write for DP. Default `0F`. |
| `--hdmi <hex>` | VCP value to write for HDMI. Default `11`. |
| `--fallback <vk,vk,...>` | Comma-separated VK codes (hex) to synthesise via `SendInput` when DDC/CI read fails. Modifiers first. Example: `11,12,BE` = Ctrl+Alt+`.`. Omit to disable the fallback. |

Diagnostic log appended to `%TEMP%\toggle-input.log` each run; safe
to delete.

## Discovery / diagnostics

To list all monitors with manufacturer / model / current VCP `0x60`:

```powershell
powershell -ExecutionPolicy Bypass -File C:\G-Menu-Toggle\Toggle-MonitorInput.ps1 -Discover
```

Example output on this machine:

```
Device       Mfr Model FriendlyName Description            CurrentInput_0x60
\\.\DISPLAY1 MSI 3CA8  MAG274QRF-QD MSI Optix MAG274QRF-QD 0x0F
\\.\DISPLAY2 AOC B403  Q27G4        AOC Q27G4              0x0F
```

## MSI toggle

Desktop shortcut `Toggle MSI Input.lnk` → `ToggleInput.exe` with args:

```
--match "MSI" --dp 0F --hdmi 11 --fallback 11,12,BE
```

Hotkey: **Ctrl+Alt+`,`** (assigned via binary patch at .lnk offset
`0x40` = `BC 06`).

Use case: flip MSI between PC (DP1) and PS5 (HDMI1).

### Why the hybrid (--fallback) path is needed

DDC/CI from the PC to the MSI only works when the MSI's *active*
input is the DP cable. Once MSI switches to HDMI:

- `GetVCPFeatureAndVCPFeatureReply` returns
  `0xC0262589 ERROR_GRAPHICS_MONITOR_NO_LONGER_EXISTS`.
- `SetVCPFeature` lies — returns `true` while the command silently no-ops.

So the exe can switch DP → HDMI via DDC/CI, but can't switch back. To
solve this, MSI's own software is assigned the hotkey **Ctrl+Alt+`.`**
(period) which performs an active-port input cycle. The exe's
`--fallback 11,12,BE` synthesises that keystroke (VK_CONTROL=`0x11`,
VK_MENU=`0x12`, VK_OEM_PERIOD=`0xBE`) whenever its DDC/CI read fails.

Net effect: a single press of Ctrl+Alt+`,` always toggles, regardless
of direction. The DP→HDMI path uses pure DDC/CI; the HDMI→DP path
proxies through MSI's already-running software.

### Caveats

- MSI's Ctrl+Alt+`.` cycles through *active* inputs, not a fixed
  toggle. If a third active source is ever plugged in (e.g. a second
  HDMI device), the fallback could land on it instead of PC.
- The `--fallback` VK list is order-sensitive; modifiers must come
  first (Ctrl=`11`, Alt=`12`, then the key).
- If MSI's hotkey is ever changed, update `--fallback` to match its
  new VK code.

### OneNote conflict (resolved)

`Ctrl+Alt+N` is globally claimed by OneNote's "Send to OneNote" tray
launcher (`ONENOTEM.EXE /tsr`). The autostart shortcut
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Send to
OneNote.lnk` was moved to `C:\G-Menu-Toggle\backups\`. Re-enable from
Task Manager → Startup if ever needed.

OneNote desktop itself is unaffected; only the tray launcher and its
global hotkeys are gone. Windows Copilot taskbar button hidden via
`HKCU\...\Explorer\Advanced\ShowCopilotButton=0`; Copilot policy set
via `HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot\
TurnOffWindowsCopilot=1`.

### Punctuation hotkeys note

Windows `.lnk` "Shortcut key" via the COM API (`IShellLink.Hotkey`)
only accepts letters, digits, and F-keys — punctuation keys
(`Ctrl+Alt+,`, etc.) must be patched into the `.lnk` binary at offset
`0x40` (low byte = VK code, high byte = modifier flags
SHIFT=`0x01`, CONTROL=`0x02`, ALT=`0x04`). After patching, fire
`SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lnkPath, NULL)` to make
explorer re-read the binding. Punctuation hotkeys DO register and
fire when set this way — the COM API simply doesn't expose them.

### I2C collision with MSI's own software

MSI Gaming Intelligence / MSI True Color stack continuously polls the
monitor over DDC/CI. When our exe and MSI's stack hit the I2C bus at
the same time, `SetVCPFeature` returns `false` with last-error
`0xC0262582` (DDC/CI bus error). The exe retries `SetVCPFeature` up
to 10 times with 200 ms backoff; succeeds within 2–3 attempts. The
AOC has no such contention because no AOC software polls its bus
continuously.

### State file

`%TEMP%\toggle-input-<match>.state` persists the last-written input
code as a hex byte (e.g. `0F` or `11`). Used as the fallback "current
state" when DDC/CI read fails. Safe to delete; will be recreated.

## Rebuilding the exe

```powershell
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /out:C:\G-Menu-Toggle\ToggleInput.exe C:\G-Menu-Toggle\ToggleInput.cs
```

No Visual Studio needed — `csc.exe` ships with .NET Framework 4 (already
present on Windows 11).

## Performance notes

- Exe cold-start + DDC/CI round trip: ~250 ms.
- Real-world latency from pressing the Windows hotkey: ~2 s. Most of the
  extra time is `explorer.exe`'s global hotkey handler and not the exe
  itself. If this becomes annoying, switching to **AutoHotkey v2** or
  **PowerToys Keyboard Manager** to bind the same exe is the next move
  — both have sub-100 ms hotkey latency.

## Gotchas

- Windows shortcut hotkeys only fire when the `.lnk` lives in Desktop
  or Start Menu, never in arbitrary folders.
- Hotkey requires a modifier (Ctrl+Alt+ or Ctrl+Shift+ at minimum).
- Won't fire inside fullscreen-exclusive games (borderless is fine).
- DDC/CI must be enabled in the AOC's OSD (it is by default).
- Reserved VBScript names: `wsh` collides with the built-in `WScript`
  alias and causes runtime error `800A01C2`. Use `objShell` instead in
  any WSH `.vbs`.

## Why not reuse G-Menu's own backend?

G-Menu's Electron frontend talks to a bundled C# helper (`G_Menu.exe`)
over **SignalR** on `http://localhost:10020/GMenuHub`. Reusing that
would require reverse-engineering the SignalR hub method names and
payload contract. The direct DDC/CI path is simpler, faster, and works
even if G-Menu is closed.
