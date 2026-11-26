# Developing TVControlForWin

A WinUI 3 (.NET 8) desktop app, unpackaged and self-contained. This page covers building
it, how it talks to each kind of TV, where things live, and how a release is made.

- [Building](#building)
- [Trying a build without touching your real setup](#trying-a-build-without-touching-your-real-setup)
- [How it talks to the TV](#how-it-talks-to-the-tv)
- [Adding a TV brand](#adding-a-tv-brand)
- [Project layout](#project-layout)
- [Updates](#updates)
- [Making a release](#making-a-release)

## Building

**You need**

- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Visual Studio 2022 or newer with the **.NET desktop development** workload. It provides
  the WinUI build tasks; the .NET SDK alone is not enough.

**Build and run**

Open `TVControlForWin.sln` in Visual Studio and press **F5**, or from a terminal:

```powershell
pwsh -File build.ps1 -Configuration Debug -Run
```

Use `build.ps1` (Visual Studio's MSBuild), not `dotnet build`: WinUI's resource step needs
a task that only ships with Visual Studio, so `dotnet build` fails with `MSB4062`.

**Package** the portable zip and the installer into `dist\`:

```powershell
pwsh -File build.ps1 -Configuration Release -Package
```

The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`). Without it the zip is still produced.

## Trying a build without touching your real setup

The app is single-instance and keeps its data in `%LOCALAPPDATA%\TVControlForWin`. To run a
second copy on throwaway data next to the one you use:

```powershell
$env:TVCONTROL_DATA_DIR = "C:\temp\tvcontrol-trial"
.\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\TVControlForWin.exe --page power
```

A copy started this way leaves the "start with Windows" entry alone and doesn't check for
updates by itself. `--page` opens straight on `home`, `schedule`, `power`, `hotkeys`, `osd`
or `settings`.

## How it talks to the TV

### LG webOS

webOS speaks **SSAP over a WebSocket** at `wss://<tv>:3001`, falling back to `ws://<tv>:3000`.

1. **Pair.** A registration handshake makes the TV show an accept prompt and return a
   `client-key`, which is saved and reused so it never prompts again.
2. **Set brightness.** `ssap://com.webos.settingsservice/setSystemSettings` with
   `{ "category": "picture", "settings": { "backlight": "<0-100>" } }`, falling back to
   `ssap://settings/setSystemSettings` on firmware that rejects the internal URI.
3. **Read brightness.** `ssap://settings/getSystemSettings`.
4. **Power.** `ssap://system/turnOff`, and `…tvpower/power/turnOffScreen` / `turnOnScreen`
   to blank and restore just the panel. `…tvpower/power/getPowerState` reports `Active`,
   `Screen Off` or `Suspend`, and doubles as a liveness probe: a TV that loses power
   doesn't close the WebSocket, so without asking, the app would keep saying "connected".
5. **Wake.** A TV that's off can't be reached over SSAP, so waking is a Wake-on-LAN packet
   to the MAC address learned (via ARP) while the TV was reachable.

References: bscpylgtv, ColorControl, lgtv2.

### Google TV / Android TV

These speak the **Android TV Remote protocol v2**: protobuf messages over TLS, the same
thing Google's phone remote uses.

1. **Pair** (port 6467). The app connects with a self-signed client certificate and the TV
   shows a 6-character code. The app answers with a SHA-256 over both certificates' public
   keys plus the code. From then on the TV trusts that certificate, which is saved with
   the TV.
2. **Control** (port 6466). The TV reports whether it's on or in standby, and pings every
   few seconds; silence means the connection is dead. Power is `KEYCODE_POWER`, a *toggle*,
   so it's only sent when the TV's reported state says which way it will go.
3. **Wake.** The power key if the TV kept its connection in standby, plus Wake-on-LAN for
   the ones that didn't.

No developer mode is needed for any of that. Reference: tronikos/androidtvremote2.

### Brightness on TCL Google TVs (ADB)

The remote protocol carries no picture settings, and Android's own display-brightness
setting does nothing on these sets. The panel is driven by TCL's picture pipeline, behind
a system service called `tcl_tv_display`. The TV's own slider calls
`setBacklight(applyType, value, actType)` on it (binder transaction 100; `getBacklight()`
is 101), and an ADB shell can make the same call:

```
service call tcl_tv_display 100 i32 1 i32 <0-100> i32 2
```

The app speaks ADB itself (`Services/AdbClient.cs`: the TCP protocol, RSA authentication,
one shell command at a time), so nothing extra is installed.

Those transaction numbers are positions in TCL's interface and could differ on another
firmware. So the app checks the service's interface name, checks that the value it reads
is a plausible 0 to 100, and reads the first write of each connection back. If that
doesn't match, brightness is switched off for that TV rather than risk calling something
else. Worked out and tested on a 65C765 (Android 14).

## Adding a TV brand

Every TV is driven through `ITvController` (`Services/ITvController.cs`): status, pairing,
power, brightness. The rest of the app doesn't know which brand it's talking to.

- **A new kind of TV** (its own protocol): add a `TvKind`, implement `ITvController`, and
  teach `TvControllerFactory` to create it and to recognise it by the ports it listens on.
  `SsdpDiscovery` finds TVs on the network.
- **Brightness for another Google TV brand**: the connection, setup and safety net already
  exist in `AdbBrightness`. What's missing is that brand's equivalent of
  `AdbTclBacklight`: how to read and set the backlight from an ADB shell. `adb logcat`
  while moving the TV's own brightness slider is how the TCL call was found.

Whatever you add, read values back before trusting a write. A call that "succeeds" is not
the same as a panel that changed.

## Project layout

| Path | Purpose |
|------|---------|
| `Services/ITvController.cs` | What every TV driver offers, plus brand detection |
| `Services/WebOsClient.cs`, `WebOsController.cs`, `SsapPayloads.cs` | LG: the WebSocket, and per-TV lifecycle, reconnect, power, brightness |
| `Services/AndroidTvRemoteClient.cs`, `AndroidTvController.cs`, `ProtoWire.cs` | Google TV: TLS, code pairing, the remote connection, power |
| `Services/AdbClient.cs`, `AdbBrightness.cs`, `AdbTclBacklight.cs` | Google TV brightness over ADB |
| `Services/RuleTargets.cs` | Which TVs a schedule, power rule or hotkey applies to (pure logic) |
| `Services/ScheduleService.cs`, `PowerEventService.cs`, `HotkeyService.cs` | Schedules, Windows power events, global hotkeys |
| `Services/SsdpDiscovery.cs`, `WakeOnLan.cs`, `MonitorService.cs` | Finding TVs, waking them, matching them to Windows screens |
| `Services/UpdateService.cs`, `AppInfo.cs` | Finding, verifying and installing app updates |
| `Services/DeviceStore.cs`, `AppPaths.cs` | Settings as JSON in `%LOCALAPPDATA%\TVControlForWin` |
| `ViewModels/` | `MainViewModel`, `TvViewModel`, `UpdateViewModel` and friends (CommunityToolkit.Mvvm) |
| `Views/` | The window, its six pages, and the brightness overlay |
| `Controls/` | `SettingCard`, `InfoTip`, the overlay's acrylic backdrop |
| `installer.iss`, `build.ps1` | Installer script and build script |

## Updates

`UpdateService` asks GitHub for the repository's latest release (drafts and pre-releases
don't count). An update is only installed if:

- the release has a file named `TVControlForWin-<version>-setup.exe`,
- it comes from the repository's release downloads over HTTPS, and
- it matches the size and SHA-256 hash GitHub lists for it.

To install, the app starts one hidden command prompt that runs the installer, waits for
it, and starts the app again whatever the outcome. Then the app exits. If the installer
fails, the old version comes back, and that release isn't tried unattended again.

**Rehearsing an update** without publishing anything: a trial run (see above) accepts
`TVCONTROL_UPDATE_FEED` (the address of a stand-in for GitHub's "latest release" answer)
and `TVCONTROL_INSTALL_KIND` (`Portable`, `PerUser` or `PerMachine`). Both are ignored
unless `TVCONTROL_DATA_DIR` is set too.

## Making a release

1. Set `Version`, `AssemblyVersion` and `FileVersion` in `TVControlForWin.csproj`.
2. `pwsh -File build.ps1 -Configuration Release -Package`
3. Publish a GitHub release tagged `v<version>` (for example `v3.0.1`) with both files
   from `dist\`, named exactly as the build wrote them. The updater looks for that
   installer name.

Never run the installer through to the end just to test it, not even into a temporary
folder: it registers itself as the installed copy, and the next real install will try to
go to the same place.
