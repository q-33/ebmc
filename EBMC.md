# MatterControl EBMC Edition

A fork of [MatterControl](https://github.com/MatterHackers/MatterControl) set up for the
East Bay Makers Club (https://eastbaymakersclub.com) to run a MatterHackers Pulse XE S-232
fitted with a Slice Engineering Mosquito hotend.

MatterControl is copyright MatterHackers, Inc. and its contributors, and is used under its
BSD-style license (see `LICENSE`). This edition is not produced or supported by MatterHackers.

## What is different from upstream

| Area | Change | Files |
|---|---|---|
| Printer profile | New make "East Bay Makers Club", model "Pulse S-232 Mosquito" | `StaticData/Profiles/East Bay Makers Club/Pulse S-232 Mosquito.printer`, `StaticData/Profiles/oemprofiles.json` |
| Profile list | Bundled makes/models are merged into the cached or server list so the club profile always shows | `MatterControlLib/SettingsManagement/OemSettings.cs` |
| Theme | New "East Bay Makers Club" theme using the club website palette | `StaticData/Themes/System/EBMC/` |
| Default theme | A new user profile starts in the club theme | `MatterControlLib/ApplicationView/AppContext.cs` |
| Product name | "MatterControl EBMC Edition", window title suffix "East Bay Makers Club", shop button hidden | `StaticData/OEMSettings/Settings.json` |
| macOS and Linux | A second host that runs the application without WinForms | `MatterControl.CrossPlatform/`, see `MACOS.md` |
| Slicing | Ordinary meshes are sent to the slicer again (upstream's pinned `agg-sharp` marks them not printable) | `MatterControlLib/DesignTools/PrintableExtensions.cs` |

The data files are produced by `Tools/EBMC/generate_ebmc_files.py` from the stock S-232 profile
and the stock light themes. Re-run it from the repository root after changing a value:

    python Tools/EBMC/generate_ebmc_files.py .

## Printer profile values

Starting point is the factory `Pulse/S-232.printer`. Changed values:

| Setting | Factory S-232 | EBMC profile |
|---|---|---|
| Nozzle diameter | 0.4 mm | 0.3 mm (Slice Vanadium) |
| Layer height / first layer | 0.25 / 0.25 mm | 0.15 / 0.2 mm |
| Quality presets (Coarse / Standard / Fine) | 0.3 / 0.2 / 0.1 mm | 0.2 / 0.15 / 0.1 mm |
| First layer width | 125% | 120% (NylonX preset: 110%) |
| Infill / perimeter / outside perimeter speed | 80 / 50 / 50 mm/s | 35 / 35 / 35 mm/s |
| Top solid infill speed | 35 mm/s | 35 mm/s |
| Travel speed | 300 mm/s | 150 mm/s |
| Retract length | 4.5 mm | 2 mm |
| Minimum avoid travel requiring retraction | 20 mm (default) | 5 mm |
| Z lift | 0 | 0.4 mm |
| Extra length on restart | 0.5 mm | 0 |
| Skirt loops | 2 | 4 |
| Infill overlap | 50% | 20% |
| NylonX nozzle / bed temperature | 255C / 55C | 255C / 60C |
| Default material | none | NylonX |

### Status of these values: provisional

- First-layer adhesion on garolite with glue stick was confirmed with a live Z offset of
  -0.06 mm. Z offset is per machine and per nozzle, so it is not stored in the profile; set it
  with the Z Offset arrows on the print panel during the skirt.
- Retraction (2 mm, 5 mm travel threshold) was chosen to cure stringing but the confirming
  print did not finish, because the bed thermistor circuit failed. Treat it as untested.
- NylonX nozzle temperature is still open between 250C and 265C. 255C is the factory preset
  and the last value a full print completed at.
- A layer shift on tall thin walls was still present on the last completed print.

## Hardware notes for this printer

- Hotend temperature sensor: Slice Engineering 300C thermistor (100K, Semitec 104NT-4 curve).
  Firmware is configured for the Semitec 104GT-2 curve; the display reads about 1C low at
  printing temperatures. Do not set temperatures above 300C.
- Nozzle torque: 1.5 Nm, tightened hot.
- Bed thermistor wiring must be repaired before use (open circuit, 2026-11-10).

## Theme palette

From the CSS variables on eastbaymakersclub.com: blue `#0648D8`, deep blue `#0034A8`,
orange `#FF3D12`, deep orange `#D42609`, green `#087F5B`, yellow `#FFDF69`, ink `#050505`,
soft ink `#38322C`, muted `#71685E`, paper `#F4EDE3`, warm paper `#FBF6ED`, cream `#FFF7EA`.

Not done: the club wordmark is not included (add it only with the club's permission), and the
application font is unchanged (the website uses a monospace face).

## Building

Clone with submodules (`git clone --recurse-submodules`).

**Mac (Apple silicon or Intel) and Linux:** .NET 10 SDK, then `Tools/macOS/build-app.sh --run`
on a Mac or `dotnet run --project MatterControl.CrossPlatform -c Release` anywhere. Details,
and what has and has not been tested, are in `MACOS.md`.

**Windows:** .NET 6 SDK and Visual Studio 2022, open `MatterControl.sln`, build the
`MatterControl` project. The Windows application compiles with these changes but has not
been run.

The club profile, theme and product name were run on Linux through the new host: the profile
appears under "East Bay Makers Club" in printer setup, and the default part slices with its
settings.

An existing user profile keeps its saved theme. To see the club theme on a PC that has run
MatterControl before, pick "East Bay Makers Club" in the theme settings, or use a fresh
Windows account.
