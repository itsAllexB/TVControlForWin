# Contributing to TVControlForWin

Thanks for wanting to help. Contributions of every size are welcome, and the most useful
one is getting more TVs to work.

## Ways to help

### Tell us about your TV

No code needed. [Open an issue](https://github.com/itsAllexB/TVControlForWin/issues) and
say:

- the brand, model and year, and the software it runs (webOS, Google TV, Android TV…);
- what works and what doesn't: finding it, pairing, on and off, brightness;
- anything you had to change on the TV to make it work.

"Everything works on my LG G4" is just as useful as a problem. It tells the next person
with that TV what to expect.

### Add brightness for another Google TV brand

On and off already work on every Google TV and Android TV. Brightness works on TCL only,
because each brand keeps its picture settings behind a different private call.

The connection, the one-time setup on the TV and the safety checks already exist
(`Services/AdbBrightness.cs`). What's missing for your brand is its equivalent of
`Services/AdbTclBacklight.cs`: how to read and set the backlight from an ADB shell.

How the TCL call was found, and how you could find yours:

1. Turn on USB debugging on the TV and connect with `adb connect <tv-ip>`.
2. Run `adb logcat`, then move the TV's own brightness slider and watch what scrolls by.
   The service that handles the slider usually names itself.
3. Look it up with `adb shell service list`, and try reading the current value from it.
4. Only then try setting a value, and watch the screen. A call that reports success is
   not the same as a panel that changed.

If you get that far but don't want to write the code, an issue with what you found is
already most of the work.

### Add a new kind of TV

A TV with its own protocol (Samsung, Roku, Fire TV…) needs a driver. Every TV is driven
through one interface, `ITvController`, so the rest of the app doesn't have to change.
[docs/development.md](docs/development.md#adding-a-tv-brand) explains where a driver plugs in.

### Fix a bug or improve something

Issues and pull requests are both fine. For anything bigger than a fix, an issue first
saves you from building something that doesn't fit.

## Building

```powershell
pwsh -File build.ps1 -Configuration Debug -Run
```

You need the .NET 8 SDK and Visual Studio with the **.NET desktop development** workload.
Use the script, not `dotnet build`. More in [docs/development.md](docs/development.md),
including how to run a second copy on throwaway data next to the one you use.

## Pull requests

- Keep each one to a single change.
- Say which TV you tested on, and what you could not test.
- Text on screen stays short. Longer explanations go behind an "i" button
  (`Controls/InfoTip`).
- When the app changes something on a TV, read it back. Don't trust a call just because
  it returned.
- New source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.

## License

TVControlForWin is licensed under [AGPL-3.0-or-later](LICENSE). By contributing, you agree
that your contribution is licensed the same way.
