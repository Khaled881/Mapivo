# Mapivo

[English](README.md) | [فارسی](README.fa.md)

> Your controller. Every game. Your way.

Mapivo is a Windows controller remapper for individual games. Plug in any gamepad
(Xbox, PlayStation, Switch, generic DirectInput, wheels) and Mapivo makes it work
like an Xbox 360 controller in the game you are about to play — with per-game
profiles, button mapping, deadzones and vibration.

---

## Install

Download **`Mapivo-Setup-1.0.1.2.exe`** from the [Releases](../../releases) page and run it.

- One setup for both **x64** and **ARM64** Windows; it picks the right build automatically.

- The installer asks for your language first (English / فارسی), then Next → Next.
- **No administrator rights needed**: it installs per-user into `%LOCALAPPDATA%\Programs\Mapivo`.
- You can choose any install folder.
- Optional desktop shortcut and a Start Menu shortcut are created.
- If **WebView2 Runtime** is missing it is downloaded and installed automatically.
- The app is self-contained: **.NET 8 does not need to be installed separately**.
- An uninstaller is registered in Windows (Apps & Features).

---

## Features

| Feature | Details |
|---|---|
| Xbox controllers | Full support through XInput |
| PlayStation pads | DualSense, DualSense Edge, DualShock 4, DualShock 3 — native HID, incl. adaptive triggers and lightbar |
| Nintendo Switch pads | Switch Pro and Joy-Con through DirectInput |
| Old / generic pads | Any generic DirectInput gamepad, arcade stick, wheel and flight stick |
| 32-bit and 64-bit games | Both architectures, XInput and DirectInput |
| Per-game profiles | Each game keeps its own map, deadzones and vibration |
| Button mapping | Remap any physical button to any virtual button, with live preview |
| Deadzone tuning | Separate left/right stick and trigger deadzones |
| Vibration test | Low / Medium / High and endless rumble per slot |
| Vibration on old / generic pads | Works through DirectInput force feedback and raw HID reports, not only XInput |
| Copy to game (proxy DLL) | Puts `xinput*.dll` / `dinput8.dll` in the game folder — no driver needed |
| Virtual driver mode | System-wide virtual Xbox pad through ViGEmBus, no DLL in the game folder |
| Instant hot-plug | Connect/unplug is detected immediately through Windows device notifications, no polling or refresh |
| Anti-cheat check | Before copying, the game folder is scanned for EAC, BattlEye, Vanguard, Ricochet, FACEIT, ESEA, GameGuard, XignCode and online APIs; a risk popup shows if found |
| First-run welcome | English/Persian picker, then Start; default is English |
| Two languages | Full English and Persian (RTL) interface |
| Installer | Language choice first, custom folder, desktop and Start Menu shortcuts, icon, uninstaller |
| WebView2 auto-install | Downloaded and installed automatically when missing |
| Self-contained | .NET 8 bundled — no separate runtime install |
| Logs and guide | Built-in logs page and step-by-step guide |

---

## Requirements

| Requirement | Needed for |
|---|---|
| Windows 10/11 (x64 or ARM64) | Everything |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | The UI (installed automatically by the installer if missing) |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) | Virtual driver mode only |

> The installer bundles the .NET 8 runtime, so you do not need to install it.

---

## Build

```bash
dotnet publish src/Mapivo.App/Mapivo.App.csproj -c Release -r win-x64   --self-contained true -o out/publish/x64
dotnet publish src/Mapivo.App/Mapivo.App.csproj -c Release -r win-arm64 --self-contained true -o out/publish/arm64
```

The hook DLLs are compiled separately with Zig:

```bash
tools/build-hook.bat
```

Edit the `ZIG=` line at the top of `tools/build-hook.bat` to point at your
`zig.exe`. Output goes to `out/DLL/x86` and `out/DLL/x64`.

Then build the installer with [Inno Setup](https://jrsoftware.org/isinfo.php) 7:

```bash
"C:\Program Files\Inno Setup 7\ISCC.exe" installer\Mapivo.iss
```

The setup file is written to `out/installer/Mapivo-Setup-1.0.1.2.exe` and contains both the x64 and ARM64 builds.

---

## Online games and anti-cheat

This is the most important thing to read before using Mapivo online.

**Offline and single-player games: no risk.**

Mapivo's **Copy to game** method puts `xinput*.dll` / `dinput8.dll` inside the
game folder. Kernel-level anti-cheat (Vanguard, Easy Anti-Cheat, BattlEye,
Ricochet) scans the game folder for foreign DLLs and may:

- refuse to launch the game (`Untrusted system file`), which is the common case; or
- in rare cases, flag or shadow-ban the account.

Mapivo scans the game folder for anti-cheat signatures and shows a confirmation
popup before copying when it finds them.

**Never use Copy to game in competitive online games.** Instead use the
**Virtual driver** (ViGEmBus) or Steam Input / DS4Windows. Those add a virtual
controller at the system level and put no DLL in the game folder, so anti-cheat
sees normal controller hardware.

Steam itself (VAC) does **not** ban for controller mapping, because translating a
pad to XInput gives no unfair competitive advantage. The risk comes only from the
game's own anti-cheat.

---

## Repository layout

```
src/Mapivo.App     WPF host + WebView2 UI (web/)
src/Mapivo.Core    Controller reading, profiles, virtual pads
src/Mapivo.Hook    Native C hook DLLs (xinput / dinput8)
installer/         Inno Setup script (Mapivo.iss) + Farsi.isl
tools/             Hook build scripts, .def files, controller DB
```

---

## License

MIT — see [LICENSE](LICENSE).
