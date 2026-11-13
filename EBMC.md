# MatterControl EBMC Edition

A fork of [MatterControl](https://github.com/MatterHackers/MatterControl) set up for the
East Bay Makers Club (https://eastbaymakersclub.com) to run a MatterHackers Pulse XE S-232
fitted with a Slice Engineering Mosquito hotend.

MatterControl is copyright MatterHackers, Inc. and its contributors. See `NOTICE.md` for the
credits and the licences this edition is distributed under (BSD 2-Clause for MatterControl, see
`LICENSE`; GNU AGPL v3 for the bundled MatterSlice). This edition is not produced or supported
by MatterHackers.

## What is different from upstream

| Area | Change | Files |
|---|---|---|
| Printer profile | New make "East Bay Makers Club", model "Pulse S-232 Mosquito" | `StaticData/Profiles/East Bay Makers Club/Pulse S-232 Mosquito.printer`, `StaticData/Profiles/oemprofiles.json` |
| Profile list | Bundled makes/models are merged into the cached or server list so the club profile always shows | `MatterControlLib/SettingsManagement/OemSettings.cs` |
| Theme | "East Bay Makers Club Dark" (the default) and "East Bay Makers Club Light", both from the club website palette | `StaticData/Themes/System/EBMC/` |
| Default theme | A new user profile starts in the dark club theme | `MatterControlLib/ApplicationView/AppContext.cs` |
| Vendor services removed | No Upgrade or Store tab, update check, account sign-in, Community links, "Shop filament" button or Pro upsell; the interface tour no longer opens at every start; the first-run vendor agreement page is skipped. Controlled by `ShowVendorServices`, `ShowTourOnStartup` and `ShowLicenseAgreement` in the OEM settings (set any to `true` to restore) | `StaticData/OEMSettings/Settings.json`, `MatterControlLib/PartPreviewWindow/MainViewWidget.cs`, `MatterControlLib/ApplicationView/BrandMenuButton.cs`, `MatterControlLib/ApplicationView/Application.cs` and three others |
| MatterHackers references | About shows "East Bay Makers Club edition", an acknowledgement of MatterControl and MatterHackers, and MatterControl's own BSD licence beside the bundled ones; MatterHackers help, store and forum links, remote images and product panels are removed or hidden while `ShowVendorServices` is off (links and image or text fetches from in-app help text and dialogs are blocked; code paths used only by stock, non-club printer profiles with a vendor SKU can still contact the vendor's product service); the affiliate code is cleared; only the club's own assemblies name the club as company (the agg-sharp and MatterSlice submodule assemblies keep their own). Credits are in `NOTICE.md` | `MatterControlLib/DialogPages/AboutPage.cs`, `MatterControlLib/ApplicationView/ApplicationController.cs`, `MatterControlLib/History/PrintHistoryEditor.cs`, `MatterControlLib/Library/Widgets/AddMaterialWidget.cs`, `StaticData/License/`, `Directory.Build.targets`, `NOTICE.md` and others |
| Printer list | Printer setup offers only the club printer; the stock "Setup New Pulse" shortcut is hidden. Controlled by `PrinterWhiteList` in the OEM settings | `StaticData/OEMSettings/Settings.json`, `MatterControlLib/Library/Widgets/AddPrinterWidget.cs`, `MatterControlLib/DialogPages/StartupPage.cs`, `MatterControlLib/SettingsManagement/OemSettings.cs` |
| Product name | "EBMC", window title suffix "East Bay Makers Club", shop button hidden | `StaticData/OEMSettings/Settings.json` |
| Name in the interface | "MatterControl" is replaced by the product name in every translated string and in dialog titles, the G-code footer, startup errors and the Mac bundle (`EBMC.app`). The app is English-only: the other translations and the Language setting were removed | `MatterControlLib/ApplicationView/BrandedTranslationMap.cs`, `MatterControlLib/ApplicationView/ApplicationController.cs`, `MatterControl.CrossPlatform/Program.cs`, `Tools/macOS/build-app.sh` and about ten dialog files |
| Logos and icon | The club's EBMC badge replaces the "M" logos: app icon, dialog title bars, bed watermark, splash and About spinner, parts-sheet footer | `Tools/EBMC/generate_brand_assets.py` writes `StaticData/Icons/application.png`, `application.ico`, `StaticData/Icons/mh.png`, `StaticData/OEMSettings/bedimage.png`, `StaticData/Stls/EBMC Badge.stl` and others |
| Toolbar and menu icons | The top-bar search, brand menu, printer action row and 3D-view toolbar icons are one outline family: 34 Lucide icons (2-unit stroke on a 24-unit grid), and 14 icons Lucide does not have that were drawn for EBMC in the same style; all are black on transparent at 64 px (56 px for the 14 pt ones) and tinted to the theme text colour; eight scene operations that drew their icons untinted are now tinted. `map.json` lists every replaced file and its source; licence in `StaticData/License/Lucide.txt` | `Tools/EBMC/build_icons.py`, `Tools/EBMC/icons/` (`map.json`, `lucide/`, `custom/`), `StaticData/Icons/*` (49 files), `MatterControlLib/ApplicationView/SceneOperations.cs`, `StaticData/License/` |
| Sample parts | The astronaut, "MH Logo" and coin sample parts are removed; the default part on a new bed is a printable EBMC badge | `StaticData/OEMSettings/SampleParts/EBMC Badge.stl`, `MatterControlLib/PartPreviewWindow/View3D/SceneActions.cs` |
| macOS and Linux | A second host that runs the application without WinForms | `MatterControl.CrossPlatform/`, see `MACOS.md` |
| Exact solids | Design tabs get Solid Box, Solid Cylinder and Plate with Hole (a "Solids" palette family and the Primitives list): exact rounded or bevelled edges and a clean through-hole, made by Open CASCADE in a separate native library (`native/occt/`); STEP files open as parts and solids export as STEP. Controlled by `EnableSolidModeling` in the OEM settings, and only shown when the library is present. The objects reach the slicer as ordinary meshes. **Before any copy of the app that includes the solid modelling library is given to anyone, in this order:** (1) push the branch that contains `native/occt/` to the public repository; (2) attach the OCCT source archive (tag `V8_0_1`, commit `b8f597c677811d1f9f4d8a97f5ae2825c0353a42`) and the `native/occt/` folder to the same release that carries the app; (3) fill in the contact line in `StaticData/License/OpenCASCADE.txt` (LGPL 2.1 section 4; see `native/occt/README.md`). None of these is done yet. | `MatterControlLib/DesignTools/Solid/`, `MatterControlLib/Library/Export/StepExport.cs`, `MatterControlLib/Library/ContentProviders/StepContentProvider.cs`, `native/occt/`, `StaticData/License/OpenCASCADE.txt` |
| Simple · Advanced | A switch at the right of the top bar. Simple (where a new user starts) shows fewer palette buttons, toolbar tools and view controls, and the Simple slice-settings list; Advanced shows everything as before. The Slice Settings "View" level follows the mode and picking a level there switches the mode. A mode never changes a setting value, the bed or how a print is sliced or sent; the maintainer lock works the same in both. In Simple the Printer settings panel lists only the simple section, so maintainers should switch to Advanced to see every printer setting. Controlled by `ShowUiModeSwitch` and `DefaultUiMode` in the OEM settings | `MatterControlLib/ApplicationView/UiModes.cs`, `MatterControl.Printing/Settings/UiMode.cs`, `MatterControlLib/PartPreviewWindow/UiModeSwitch.cs`, `PaletteCatalog.cs`, `SceneOperations.cs`, `ViewToolBarControls.cs`, `View3DWidget.cs`, `SliceSettingsWidget.cs` |
| Club shapes and starters | Design tabs get Rounded Box, Slot Plate (Box shapes), Tube (Round shapes), Polygon Prism, Star, Heart and Gear ("More shapes", Advanced only), and nine Club Starters, fill-in designs with a few plain fields: Name Tag, Sign, Box with Lid, Wall Hook, Phone Stand, Knob, Cable Clip, Plant Label and QR Tag (a "Starters" palette family, a "Club Starters" library folder and the classic rail). Each is an ordinary part rebuilt from its fields and saved with them; its output is a normal closed mesh built from 2D outlines (text by the text part's own font code). A new design tab opens empty with a "Start a design" card (add a shape, use a starter, open a file; the club badge is one of the starters; "Don't show this again"). Controlled by `EnableClubShapes` and `NewDesignStartsEmpty` in the OEM settings | `MatterControlLib/DesignTools/ClubShapes/`, `MatterControlLib/DesignTools/EBMC/Club*.cs`, `MatterControlLib/DesignTools/Primitives/` (seven shape files), `ClubStartersContainer.cs`, `PaletteCatalog.cs`, `DesignStartCard.cs`, `MainViewWidget.cs` |
| Advanced tools | Command search ("Find a tool": Cmd/Ctrl + K anywhere, S in the 3D view) lists the tools, shapes, starters and a fixed set of file and edit actions, matches Fusion and Tinkercad words (fillet, chamfer, union, extrude, shell…) and never lists Print, Send, Upload or Slice; a Transform section at the top of the properties panel takes typed centre, bottom, size and rotation (one undo step each, arithmetic such as 20+5); Mirror Copy adds a mirrored copy next to the selection; Cmd/Ctrl + D duplicates; "Keyboard shortcuts" in the main menu (or ?) lists every shortcut. Everything but Cmd/Ctrl + K and D is Advanced mode only. They change the design through the undo buffer like the other tools and never slice, send or print. Controlled by `EnableAdvancedTools` in the OEM settings | `MatterControlLib/ApplicationView/AdvancedTools.cs`, `ShortcutTable.cs`, `ShortcutsPage.cs`, `MatterControlLib/PartPreviewWindow/CommandSearch/`, `TransformSection.cs`, `View3D/Actions/MirrorCopy.cs`, `MatterControlLib/DesignTools/EBMC/{CommandSearchRules,TransformRules,MirrorCopyRules}.cs` |
| Dialogs and menus | One dialog template: the dialog's own title with a severity tile, a footer bar with buttons named with verbs ("Cancel print" / "Keep printing", "Delete printer" / "Keep"…), the main action on the right; a destructive button is red and Enter presses the safe one (Cancel print, Delete printer, Remove, Clear history, Disconnect, Forget key, Replace the bed, Reset to club defaults, exit while printing; the hardware-error box now defaults to "Stay paused"). The Bed, printer ⋯ (Printer options), brand, 3D right-click, tab and Library menus are regrouped with section headers, icons, shortcut hints, separators and danger items last; locked items stay visible with the lock reason. The Add button opens the file dialog (the Library is on its arrow). Short notices show as a toast at the bottom left. Every item and button runs the same action with the same enable rule as before. Controlled by `ClubDialogsAndMenus` in the OEM settings | `MatterControlLib/ApplicationView/ClubUi/`, `MatterControlLib/SetupWizard/DialogPage.cs`, `MatterControlLib/ControlElements/StyledMessageBoxWindow.cs`, the menu files (`PrinterActionsBar.cs`, `ViewToolBarControls.cs`, `BrandMenuButton.cs`, `ApplicationController.cs`, `View3DWidget.cs`, `MainViewWidget.cs`, `LibraryWidget.cs`) |
| Slicing | Ordinary meshes are sent to the slicer again (upstream's pinned `agg-sharp` marks them not printable) | `MatterControlLib/DesignTools/PrintableExtensions.cs` |

The data files are produced by `Tools/EBMC/generate_ebmc_files.py` from the stock S-232 profile
and the stock light themes. Re-run it from the repository root after changing a value:

    python Tools/EBMC/generate_ebmc_files.py .

The badge artwork (icons, bed watermark, badge meshes) is produced by
`Tools/EBMC/generate_brand_assets.py` from `Tools/EBMC/assets/ebmc-badge-source.png`. It needs a
few Python packages; they are listed at the top of the script.

The EBMC badge belongs to the East Bay Makers Club and is used here with the club's permission.
It is not covered by the software licence in `LICENSE`; that applies to the badge image, the
files generated from it and the badge meshes.

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
| NylonX nozzle / bed temperature | 255C / 55C | 265C / 60C |
| Default material | none | NylonX |

### Status of these values: provisional

- First-layer adhesion on garolite with glue stick was confirmed with a live Z offset of
  -0.06 mm. Z offset is per machine and per nozzle, so it is not stored in the profile; set it
  with the Z Offset arrows on the print panel during the skirt.
- Retraction (2 mm, 5 mm travel threshold) was chosen to cure stringing but the confirming
  print did not finish, because the bed thermistor circuit failed. Treat it as untested.
- NylonX nozzle temperature is 265C, set by the club on 2026-11-11 (the factory preset is 255C).
  It has not yet been confirmed by a completed print at this value.
- A layer shift on tall thin walls was still present on the last completed print.

## Printing through OctoPrint

The club printer is connected by USB to a Raspberry Pi 4 running OctoPrint (`http://OctoprintPulseXE.local`).
The club profile turns on "Send jobs to OctoPrint" with that address, so the printer bar shows **Send** in
place of Connect and Print. On each computer, open Printer Settings > General and use **Get Key** once; you
approve the request in OctoPrint's web page. The key is kept in that computer's user settings, never in the
printer profile.

Send > Upload and Print slices the bed, uploads the G-code and starts it. The start G-code homes, deploys the
BLTouch (`M280 S3.3`), probes nine points (`G32 X23 Y15 A180 B180`), stows the probe (`M280 S7.43`) and prints.
Keep the bed clear while it probes. OctoPrint's Cancel can take up to 40 seconds on this firmware; use the
power switch in an emergency.

Settings that live on the printer, not in this repository (set 2026-11-11):

| Where | Setting |
|---|---|
| SD card `config` | probe offset 23,0; two-corner grid mode; probe start height 11; software travel limits (X 0-250, Y 0-220, Z floor -2.5) |
| Stored with `M500` | `M565 X23 Y0 Z0`, `M670 Z6` (probe travel capped at 12 mm), `M206 Z5.56` (Z offset) |

The Z offset was set with the bed at 60C, the nozzle at 150C and levelling active, and confirmed by a good
first layer. To change it, send `M206 Z<value>` then `M500` from OctoPrint's Terminal and home. The gantry is
tilted about 1.1 mm across the bed; the firmware levelling compensates for it.

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

The dark theme uses warm near-black grounds built from the ink colours (`#1F1C19`, `#151311`,
`#2B2723`), paper-tinted text (`#EDE6DA`) and the club orange as its accent. The club blue and
green are too dark to read on that ground, so its accent choices include lightened versions
(`#6C9BFF`, `#35B98B`); those two are not from the website.

Not done: the full "East Bay Makers Club" wordmark is not included (only the EBMC badge is).

The interface typeface is Nunito (`DefaultUiFont` in the OEM settings; empty restores Liberation Sans).
Text parts keep their own font setting.

## Building

Clone with submodules (`git clone --recurse-submodules`).

**Mac (Apple silicon or Intel) and Linux:** .NET 10 SDK, then `Tools/macOS/build-app.sh --run`
on a Mac or `dotnet run --project MatterControl.CrossPlatform -c Release` anywhere. Details,
and what has and has not been tested, are in `MACOS.md`.

**Windows:** .NET 6 SDK and Visual Studio 2022, open `MatterControl.sln`, build the
`MatterControl` project. The Windows application compiles with these changes but has not
been run.

The club profile, theme and product name were run on Linux through the new host: the profile
appears under "East Bay Makers Club" in printer setup, and the default part of that time (the astronaut, since replaced by the EBMC badge) sliced with
its settings. The rebranded build was run on an Apple silicon Mac; the badge part has not been
sliced or printed.

An existing user profile keeps its saved theme. To see the club theme on a PC that has run
MatterControl before, pick "East Bay Makers Club" in the theme settings, or use a fresh
Windows account.
