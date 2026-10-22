# Managed display backend checks

Run with the .NET 10 SDK on Windows, macOS, or Linux:

```text
dotnet run --project tests/ScreenFix.Display.Tests/ScreenFix.Display.Tests.csproj
```

This dependency-free console harness links the three production backend files.
It exercises only pure managed transformations through reflection. It never
constructs `DisplayService`, loads `user32.dll`, or changes any display setting.
Failures return a nonzero process exit code.

Coverage includes SDK structure sizes and union offsets; legacy and virtual mode
indices; source resizing with left, right, above, below, and offset neighbors;
primary-origin normalization; preservation of every target signal and 240 Hz;
clone/crop/orientation rejection; detection of driver substitutions; and JSON
recovery data with invalid-version/index rejection.

The ladder that applies a size (CCD, classic CCD, ChangeDisplaySettings) is covered only through its pure parts:
the order-independent gap closing, the check of a classic mode change, the choice of methods, the wording of
driver refusals (including Windows error 1610), and the DISPLAY_DEVICE structure size. The calls to Windows
themselves can only be verified on real hardware.

`DISPLAYCONFIG_SOURCE_MODE.position` uses global desktop coordinates. In
contrast, `DISPLAYCONFIG_DESKTOP_IMAGE_INFO.DesktopImageRegion` describes a
region inside its path source and must fit within `PathSourceSize`. A normal
second screen at global `X = 1920` therefore still uses the local image region
`(0, 0, width, height)`. Tests explicitly cover positive and negative global
positions. The backend deliberately rejects complex local crops and virtual
clone surfaces instead of guessing their transformation.

Microsoft references:

- [Source mode and desktop coordinates](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_source_mode)
- [Desktop image region and path source size](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_desktop_image_info)
- [Source virtual-mode bitfield indices](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_source_info)
- [Target virtual-mode bitfield indices](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_target_info)

These checks validate calculations and binary layout. Actual driver acceptance,
physical centering, refresh retention, recovery, and fullscreen game behavior
still require a Windows hardware test.
