# win-hardware-utils

Small Windows utilities for talking to peripherals directly when the vendor
software is too clicky, too heavy, or just doesn't expose what you need.

Each subfolder is an independent project with its own README and build
instructions. Everything is hand-rolled C# compiled with the `csc.exe` that
ships with .NET Framework 4 (no Visual Studio, no SDK install needed beyond
what Windows already has).

## Projects

| Folder | What it does |
|---|---|
| [`monitor-toggle/`](monitor-toggle/) | Toggle a monitor's input source (DP ↔ HDMI) via DDC/CI from a single hotkey. Built because AOC's G-Menu and similar OEM tools don't expose input selection in their hotkey editor. Supports a hybrid mode that falls back to synthesising the monitor's own input-cycle hotkey when DDC/CI is one-way blocked (e.g. an MSI in HDMI mode). |
| [`battery-tray/`](battery-tray/) | One tray icon showing battery levels for a wireless mouse, keyboard, and gamepad simultaneously — so you don't need three vendor apps in the system tray. Currently wired up for Logitech (HID++ 2.0), Corsair (BLE GATT), and Flydigi (named-pipe + reverse-engineered protobuf). |

## Building

Every project compiles with the in-box .NET 4 compiler:

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ...
```

See each project's README for exact flags.

## Design notes

A few non-obvious bits worth knowing if you're forking this:

- **Windows shortcut hotkeys** (`.lnk` "Shortcut key") only fire when the
  shortcut lives in Desktop or Start Menu. Punctuation keys aren't accepted
  via `IShellLink.Hotkey` but DO work when patched into the `.lnk` binary at
  offset `0x40` (low byte = VK, high byte = modifier flags).
- **`csc.exe v4.0.30319`** is C# 5 by default. No tuples, no `out var`, no
  underscore digit separators, no auto-property initializers.
- **`NotifyIcon.Text` has a 63-character cap.** Setting longer text throws
  silently and the icon never updates.
- **WinRT BLE GATT** can be called from .NET Framework 4.8 via the unified
  `Windows.winmd` at
  `C:\Program Files (x86)\Windows Kits\10\UnionMetadata\<sdk>\Windows.winmd`
  plus the facade `System.Runtime.dll` and `System.Runtime.InteropServices.WindowsRuntime.dll`
  from the reference assemblies path.

## License

Personal use. Vendor SDKs (Corsair iCUE SDK DLL, etc.) are NOT redistributed
here — fetch them yourself from their respective vendors / GitHub releases.
