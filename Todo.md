# TVControlForWin — TODO

Ideas / next steps after **v3.0**.

## Distribution & polish
- [x] **App icon** — replace the placeholder sun with a real designed icon.
- [ ] **Code signing** — get rid of the SmartScreen "unrecognized app" warning on first run.
- [x] **Installer** — an Inno Setup `setup.exe` (Start-menu shortcut, uninstall entry) as an
      alternative to the portable zip. See `installer.iss` / `build.ps1 -Package`.
- [x] Make the release tag and the in-app version read identically — both are `v2.0.0`.

## Features
- [x] **SSDP auto-discovery** — find LG TVs on the LAN so users don't have to type IPs.
- [x] **Power automation** — turn the TVs on/off with the PC (startup, sleep, shutdown, display sleep).
- [x] **Google TV / Android TV** (TCL, Sony, Philips…) — discovery, code pairing, power on/off
      over the Android TV Remote protocol. Tested on a TCL 65C765.
- [x] **Brightness on TCL Google TVs** — over ADB (USB debugging on the TV), through TCL's
      `tcl_tv_display` service. Tested on a 65C765 (Android 14).
- [ ] **Brightness on other Google TV brands** (Sony, Philips, Hisense…) — each keeps its picture
      controls somewhere different. The recipe that found TCL's: record `adb logcat` while moving
      the TV's own brightness slider, then look for the service call behind it.
- [ ] **More brands** — Samsung (Tizen), Roku, Fire TV, Vizio each need their own driver behind
      `ITvController`.
- [ ] **Extra picture sliders** — contrast / black level / energy-saving (OLED brightness limiter).
- [ ] **Sunrise–sunset auto-dim** — schedule brightness to the sun instead of fixed clock times.
- [x] **Per-TV schedule targeting** — each schedule picks its TVs (or all of them).
- [x] **Per-TV power rules** — a TV follows the shared rules or has its own.
- [x] **Hotkey target** — the TV under the cursor, all TVs, or one fixed TV.
- [x] **Renamed** from LGforWin to TVControlForWin (settings carry over on first run).
- [x] **Automatic updates** — daily check against GitHub releases, background install for
      per-user installs (Settings → Updates).
- [x] **GitHub repository renamed** to TVControlForWin, first releases cut, README rewritten
      and screenshots retaken. Licence is now AGPL-3.0-or-later.
- [ ] **Scheduled power** — turn a TV off or on at a set time, alongside brightness schedules.

## Maybe / later

- [ ] Surface a TV's power state in the tray icon / tooltip, not just the Home card.
- [ ] MSIX packaging (Store-style install + auto-update).
- [ ] "Identify screens" helper on the Home page to make monitor pairing foolproof.
- [ ] Optional per-TV dedicated hotkey combos (in addition to cursor targeting).
