# Changelog

## 1.0.1.1

### Fixed

- **Game crashes on launch after "Copy to game" (WWE 2K15 and similar)** —
  the PE reader read the import descriptor at the wrong offsets, so the exe
  was treated as importing nothing and every `xinput*.dll` plus `dinput8.dll`
  was dropped into the game folder. Some game loaders reject that set at
  startup. The import table is now read correctly, and only the DLLs the exe
  actually imports are copied. A launcher exe is resolved to its sibling game
  exe first, so importing from a launcher works too.
- **Hook crash at startup** — the per-slot summary log passed a single
  character instead of a string to `wsprintfA`, which faulted inside
  `user32!wvsprintfA`. The line is now built in single-argument steps.
- **Missing rumble capability** — a pad reached through the system XInput
  driver did not advertise force feedback, so games skipped vibration
  entirely. The capabilities are reported with the FFB bit set while keeping
  the driver's real motor resolutions.
- **Virtual-pad input delay** — the virtual pad was refreshed only every
  10 ms, so on some machines a button press reached the game one frame or
  more late. The refresh interval is now 5 ms and the loop keeps a fixed
  deadline instead of drifting, which removes the accumulated delay.
- **Sleep granularity** — the driver asks Windows for a 1 ms timer while it
  runs, so the 5 ms cadence is actually met instead of sliding to the default
  15.6 ms system tick.

### Notes

- The virtual-pad thread now runs at the process's normal priority. Raising it
  was tried and made things worse when the game itself was raised: on a slower
  CPU the input thread lost the race against the game's own threads, which
  added the very delay the fix was meant to remove.
- Everything from 1.0.1 is included.

## 1.0.1

### Added

- **In-app update check** — the Settings page now shows the installed version
  and a "Check for updates" button. It reads the latest release from GitHub
  and reports whether a newer build exists.
- **One-click in-app update** — when a newer version exists, pressing
  "Download update" fetches the setup, installs it silently, and reopens
  Mapivo automatically on the new version. No manual download or file copying.
- **Update prompt on launch** — Mapivo checks for updates at startup. If a
  newer build is available a dialog offers **Download update** or **Later**,
  so the update is never forced.
- **Version shown in the UI** — the real version (1.0.1) now appears in the
  sidebar footer and the Settings page, and in the assembly metadata.

- **Trigger mapping on DirectInput pads** — triggers reported as raw buttons
  (`b6`/`b7` on many pads) can now be bound from the mapping dropdown. The
  device profile defaults are merged with the user map, so triggers work on a
  fresh game profile without editing anything.
- **Mapping source list** — the source dropdown now offers all canonical
  buttons, the analog axis sources (`lx+`, `ly-`, `rx+`, `ry-`, …) and, on a
  DirectInput pad, raw buttons `b0..b31`.
- **"No profile" state in the dashboard** — after deleting the selected
  profile, the game stays in the list and is shown as having no profile.
- **Pressed highlight** — pressing a button now lights the pad preview with a
  brighter, saturated blue and a glow so it is obvious which control is being
  pressed.

### Fixed

- **L3/R3 highlight position** — the press indicator now sits centered on the
  correct stick circle instead of being offset.
- **Pad after minimize** — the pad no longer drops out when the window is
  minimized and restored (DirectInput cooperative level now uses
  `DISCL_BACKGROUND | DISCL_NONEXCLUSIVE`).
- **XInput rumble when the hook DLLs are copied into the game folder** —
  in some games the rumble reached the pad but the motor would not spin
  ("tries but is stuck"). The hook now mirrors the weak channel onto the big
  motor and gives every idle→active transition a short full-power kick to
  break stiction, then settles on the requested strength. The same kick/mirror
  also applies on the virtual-driver path. DirectInput rumble already worked.
- **Modal action row spacing** — a divider and consistent gap were added above
  the action buttons so "Cancel" and the primary button no longer run together.

### Notes

- Rumble is re-sent while active so pads that treat each report as one-shot
  keep spinning, and a stop report is sent when the effect ends.
- Verified on the Release build; the in-app update flow (check, download,
  install, reopen) was tested end to end.
