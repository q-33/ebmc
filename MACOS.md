# MatterControl EBMC Edition on a Mac (Apple silicon)

The Windows application is built on WinForms, which is why upstream MatterControl has no
current Mac build. This repository adds a second way to start the same application:
`MatterControl.CrossPlatform`, which opens the window with GLFW and draws with OpenGL
instead. It has no Windows dependencies and targets macOS (arm64 and Intel) and Linux.

## Status - read this first

| | |
|---|---|
| Builds with the .NET 10 SDK | Yes (built on Linux x64; cross-published for `osx-arm64`) |
| Runs on Linux x64 | Yes - tested end to end, see "What was tested" |
| Runs on a Mac | **Not yet tried.** No Mac was available while this was written |
| Windows application still compiles | Yes (compile check only, not run) |

The code that is specific to macOS is small and listed under "First run on a Mac" so it
can be checked off. Expect to find something on the first real run; please report it.

## What you need

- A Mac with Apple silicon (M1 or later) or Intel
- The **.NET 10 SDK** for macOS: <https://dotnet.microsoft.com/download/dotnet/10.0>
  (choose the Arm64 installer for an M-series Mac)
- Git (`xcode-select --install` provides it)

Nothing else. GLFW comes from NuGet, SQLite and OpenGL are part of macOS.

## Get the code

```sh
git clone --recurse-submodules https://github.com/q-33/ebmc.git
cd ebmc
```

If you cloned without `--recurse-submodules`: `git submodule update --init --recursive`

## Build the app (recommended)

```sh
Tools/macOS/build-app.sh --run
```

This creates `dist/MatterControl EBMC.app` and opens it. Drag it to Applications if you
like. The first build downloads packages and takes a few minutes.

The app bundle matters on a Retina display: its `Info.plist` tells macOS to give the
window a full resolution framebuffer.

## Or run straight from source

```sh
dotnet run --project MatterControl.CrossPlatform -c Release
```

Quicker while changing code. If the window looks soft on a Retina display when started
this way, use the app bundle; the application adapts to whichever resolution macOS
actually hands it.

`dotnet run --project MatterControl.CrossPlatform -c Release -- --info` prints the
runtime, processor architecture and data folder without opening a window.

## First run on a Mac - what to check

These are the parts that could not be exercised without a Mac. In rough order of how
soon you will meet them:

1. **The window opens** and the interface is sharp and correctly sized on the built-in
   display. (GLFW window, legacy OpenGL 2.1 context, Retina scale measured from the
   real framebuffer.)
2. **Mouse position matches the pointer** - buttons highlight under the cursor, not
   beside it. (Mouse coordinates are scaled by the Retina factor.)
3. **Command shortcuts**: Command+Z, Command+C / V, Command+S. Command is mapped to the
   shortcuts the Windows version puts on Ctrl.
4. **Open / save dialogs** appear (Add to Bed > Add System File, Export). They are the
   system's own panels, shown through `/usr/bin/osascript`. The main window ignores
   input until the panel is answered, so if the window seems stuck and no panel is
   visible, look for one hidden behind it - and please report that.
5. **Drag the window to an external monitor.** The interface should rebuild itself at
   the new scale after about half a second.
6. **Printer connection.** Plug in the printer; the port list should show
   `/dev/tty.usbmodem...`. Connect, read temperatures, jog an axis.
7. **Slice and print** a small part. The computer should not go to sleep during the
   print (`caffeinate` is started for its duration).
8. **Quit** with Command+Q and with the red button; reopen and check that the window
   comes back where it was.

If the application stops with an error, the report is printed to the terminal and
appended to `crash.log` in the data folder (`--info` shows where; normally
`~/Library/Application Support/MatterControl`).

## Good to know

- **Settings and printers** live in the data folder above. Nothing is shared with a
  Windows installation; set the printer up again or copy the `.printer` file across.
- **Slicing** runs inside the application rather than as a separate program.
- **One window.** Dialogs are drawn as panels inside the main window.
- **Not wired up on macOS yet**: opening a file by double-clicking it in Finder, the
  single-instance check (a second launch starts a second copy), the screen colour
  picker, the camera, and software rendering (`FORCE_SOFTWARE_RENDERING`).
- **OpenGL on macOS is deprecated by Apple** but still ships, including on Apple
  silicon. MatterControl draws with the old fixed-function pipeline, so it needs the
  legacy 2.1 context that macOS still provides. If Apple removes OpenGL in a future
  macOS release this host stops working until the renderer is ported.
- **Sharing the app**: it is ad hoc signed, which is fine on the Mac that built it. A
  copy sent to another Mac is quarantined; the recipient right-clicks it and chooses
  Open the first time. Proper distribution needs an Apple Developer ID and notarization.

Environment variables for troubleshooting:

| Variable | Purpose |
|---|---|
| `MATTERCONTROL_GLFW` | Full path of a `libglfw.3.dylib` to use instead of the bundled one |
| `MATTERCONTROL_SQLITE` | Full path of a `libsqlite3.dylib` to use |
| `MATTERCONTROL_STATICDATA` | Full path of the `StaticData` folder |

## What was tested, and how

On Ubuntu 24.04 x64 with a virtual display and Mesa's software OpenGL, driving the real
application with synthetic mouse and keyboard input:

- First start: licence page, welcome page, printer setup with the East Bay Makers Club
  profile, typing in the search box
- 3D view: model, bed, selection outline, view cube; rotate, zoom, reset view
- Slicing the default part: 400 layers of G-code with the club settings
  (0.3 mm nozzle, 0.15 mm layers, 255 C / 60 C), layer view
- Native file dialog round trip (zenity on Linux), adding an STL to the bed
- Undo / redo with Ctrl+Z / Ctrl+Y
- Window resize, saved size and position restored on the next start, clean exit
- Connecting to the built-in printer emulator and reading temperatures
- A display scaled to 200 %
- Idle CPU use about 1 %

Not tested anywhere: a print on real hardware from this build, and everything in the
"First run on a Mac" list.

## What changed outside the new project

- `MatterControlLib` now targets `net6.0` instead of `net6.0-windows`. The three places
  that used Windows-only APIs were changed: QR codes are rendered without
  `System.Drawing`, the registry lookup for external programs and the screen colour
  picker are skipped off Windows.
- **Slicing fix (affects Windows too).** The `agg-sharp` revision that upstream pins was
  taken in the middle of a rewrite; in it every ordinary mesh reports that it is not
  printable, so pressing Slice did nothing. `MatterControlLib/DesignTools/PrintableExtensions.cs`
  corrects that without touching the submodule.
- The user data folder is created if it does not exist (a new Linux account has no
  `~/.local/share`).
- The display scale is included wherever the text size setting was applied.
- `SixLabors.ImageSharp` is pinned to 2.1.13 in `Directory.Build.targets` (known
  advisories remain; the fixed 4.x line needs a newer .NET than the libraries target).
- `MatterControl.csproj` excludes the new folder from its source glob.

## How the host is put together

| File (`MatterControl.CrossPlatform/`) | Job |
|---|---|
| `Program.cs` | Startup, mirroring the Windows `Program.cs` |
| `Platform/GlfwPlatformWindow.cs` | The window, input, event loop, Retina handling |
| `Platform/NativeGL.cs` | OpenGL calls, looked up through GLFW at run time |
| `Platform/NativeLibraries.cs` | Finds GLFW and SQLite for the current system |
| `Platform/DesktopFileDialogProvider.cs` | Open / save / folder dialogs |
| `Platform/UiThreadSynchronizationContext.cs` | Brings `await` continuations back to the UI thread, as WinForms does |
| `Platform/KeepAwake.cs` | Stops the computer sleeping during a print |
| `Platform/GlfwClipboard.cs`, `DesktopPlatformFeatures.cs`, `DesktopOsInformationProvider.cs`, `GlfwWindowProvider.cs` | The remaining platform services |

The same project runs on Linux x64 with `dotnet run`. GLFW is bundled there too; file
dialogs use `zenity`.
