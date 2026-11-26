# Third-party notices

TVControlForWin is licensed under AGPL-3.0-or-later (see [LICENSE](LICENSE)). It includes
code from, and is distributed together with, the projects below. Their licenses apply to
those parts.

## Code included in the source

### Microsoft PowerToys

`Controls/AlwaysActiveDesktopAcrylicBackdrop.cs` is ported from PowerToys'
`Common.UI.Controls`. <https://github.com/microsoft/PowerToys>

Copyright (c) Microsoft Corporation. All rights reserved. Licensed under the MIT License
(text below).

## Libraries shipped with the app

| Library | Copyright | License |
|---|---|---|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | (c) .NET Foundation and Contributors | MIT |
| [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | (c) havendv and contributors | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) | (c) .NET Foundation and Contributors | MIT |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | (c) Microsoft Corporation | Microsoft Software License Terms for the Windows App SDK (the package's `license.txt`); source under MIT |

The .NET runtime and the Windows App SDK carry their own third-party notices, published in
their repositories.

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.

## Protocol references

No code was taken from these, but they documented the protocols the app speaks:
[bscpylgtv](https://github.com/chros73/bscpylgtv),
[ColorControl](https://github.com/Maassoft/ColorControl) and
[lgtv2](https://github.com/hobbyquaker/lgtv2) for LG webOS, and
[androidtvremote2](https://github.com/tronikos/androidtvremote2) for the Android TV Remote
protocol.
