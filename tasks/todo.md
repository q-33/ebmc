# EBMC rebrand (approved 2026-11-11)

Goal: no user-visible "MatterControl" name or "M" logo; club EBMC badge instead; app is `EBMC.app`.
Keep: MatterHackers copyright/licence credit (About page, LICENSE, StaticData/License), data folder name,
executable/assembly name, bundle ID, namespaces, config keys.

## Name
- [x] Product name "EBMC" in Tools/EBMC/generate_ebmc_files.py; regenerate Settings.json
- [x] Translation wrapper in main repo replacing "MatterControl" with the product name in localized strings
- [x] Non-localized titles (message boxes, Save As, Settings, terminal log, export filters)
- [x] Tour text, G-code footer comment, parts-sheet PDF metadata
- [x] Tools/macOS/build-app.sh: bundle name EBMC

## Artwork (script under Tools/EBMC/, badge traced from the club favicon)
- [x] App icon: owner chose paper-on-orange
- [x] application.png (1024) and application.ico
- [x] mh.png (title bar), Library/design_apps_icon.png, bedimage.png, Images/mh-logo.png, PartSheetLogo.png
- [x] Extruded EBMC badge mesh: splash/About spinner and default part

## Sample parts
- [x] Remove Phil A Ment, MH Logo, MatterControl - Coin and their bundled thumbnails; default part = badge

## Verification
- [ ] Build; launch; screenshot title bar, welcome, wizard, message box, bed, splash, About, Dock
- [ ] Search build output for leftover visible strings
- [x] Independent Opus review of the diff
- [ ] Not verifiable here: Windows icon/name (compile check only)

# PrusaSlicer incorporation study (requested 2026-11-11)
- [x] Licence analysis: what can legally be reused (code, icons, profiles, UX ideas)
- [x] Technical review: PrusaSlicer 3D view and UX vs this codebase; what is portable
- [x] Recommendation to owner

# Direction agreed 2026-11-11
This app = Pulse XE only. Prusa printers = stock PrusaSlicer. Everything sends to OctoPrint.

## Queue (serial, one working tree; start after the rebrand review lands)
- [ ] Apply rebrand review findings
- [ ] Remove MatterHackers as developer/vendor/supporter from UI, data, packaging; keep licence texts;
      add neutral credit notes (About + NOTICE/EBMC.md)
- [ ] OctoPrint: client, settings (host, API key, test, app-key flow), Send / Send and print, mock server, tests
- [ ] UX modernisation (PrusaSlicer feel, own code only, no agg-sharp changes):
  - [ ] Off-bed part warning + colour (parts are silently dropped from the slice today)
  - [ ] PrusaSlicer mouse/keys option (left-drag rotate, right/middle pan, A arrange, B/Z zoom, 0-6 views)
  - [ ] G-code preview coloured by feature type + legend with time per feature
  - [ ] Hover highlight; themed selection colours
  - [ ] Place-on-face
  - [ ] Tighter arrange
  - [ ] Bed polish (origin axes, subtler grid) and theme pass (spacing/radius/colours within theme JSON)
  - [ ] Fix found in checklist run: clipped layer-view side panel; file panel opens without keyboard focus
- [ ] Later / needs owner: PrusaSlicer club config bundle (needs printer inventory), Pi model

## Rebrand review (2026-11-11): no must-fix. Follow-ups, not in the rebrand commit
- [x] "MC" logo still on the Variable Sheet primitive (StaticData/Stls/sheet_icon.mcx + 2 thumbnails)
- [x] Non-English translations spell the old name differently (58 strings) and are not rebranded (now: app is English-only)
- [ ] Stale GUI automation tests reference removed sample parts (MatterControlUtilities.cs:576, CameraFittingUtilTests, SceneUndoRedoCopyTests)
- [ ] Two sources for the product name (Program.AppName vs Settings.json); PrintHistoryListItem.cs:403 edit has no effect
- [x] build-app.sh stale comment (line 67)
- [ ] generate_brand_assets.py not executable
- [x] Unused upstream logo SVGs still shipped
- [ ] Check in the app: slice-setting help text ("Recover After Failure") shows EBMC, not MatterControl
- [ ] Compile third-party notices for all shipped libraries (NuGet + submodules) and ship them in the app (About + bundle)

## OctoPrint decisions (owner, 2026-11-11)
- Raspberry Pi 4B
- Upload file names get a date-time suffix (members must not overwrite each other's files)
- One shared "members" OctoPrint account with limited permissions
- Levelling for the Pulse XE under OctoPrint: research options, then owner approves any profile/firmware change
- Risk to bench-test first: Smoothieware under OctoPrint (ignore-firmware-errors setting, extruder report)

## Levelling under OctoPrint (owner approved the profile change 2026-11-11; applied in generate_ebmc_files.py)
Printer facts from printer-config/original (owner's SD card copy; never edit, not committed):
Azteeg X5 GT (TMC2660), Smoothieware edge-d82d66f, grid levelling already enabled (3x3 over 180x180,
probe_offsets 0,-15,0, initial_height 15, do_home false), BLTouch M280 S3.0 deploy / S7.0 stow,
Z homes to max, msd_disable true (SD card must be pulled to edit config).
- [x] Profile: print_leveling_required_to_print 0, validate_leveling 0, has_hardware_leveling 1,
      start G-code G28 -> M280 S3.0 -> G32 -> M280 S7.0
- [ ] Bench test after bed thermistor repair, cold first: servo deploy/stow, finger-trigger test before any
      Z move, single G30, then G32; confirm one deploy lasts the whole grid
- [ ] Measure nozzle-to-trigger height (paper test) and add `G30 Z<value>` after G32 (needs owner yes)
- [ ] Resolve probe offset mismatch: firmware 0,-15 vs profile 23,0 (owner to measure on the Mosquito)
- [ ] Later, needs owner yes + SD card: grid 5x5 over more of the 250x220 bed
- [ ] Clear per-Mac baby_step_z_offset when switching to OctoPrint (it is baked into exports)
- [ ] EBMC.md: add the levelling settings to the profile table; correct the Z-offset note
- [ ] Existing installs keep their old copy of the profile: remove and re-add the printer to pick this up

## Done 2026-11-11 (third checkpoint)
- [x] OctoPrint "Send" in the app: settings (on/off, address, API key in per-user store), Send popup, Upload / Upload and Print,
      part-based timestamped names; verified against Tools/EBMC/octoprint_mock.py. Not yet tried against the real Pi.
- [x] Off-bed parts: warning/error, khaki colour in the 3D view, Arrange fix, export/send blocked when nothing is inside
- [x] UX step 1: "Club label" top bar and tabs (designer mockups in the session scratchpad; direction A chosen)
- [ ] UX step 2: icon refresh (needs owner yes to download the Lucide icon set + an SVG rasteriser)
- [ ] Tabs follow-ups: faint labels/hairline in stock Solarized-Light and Classic-Dark themes; stale private test helper
      CloseAllPrinterTabs (Tests/MatterControl.AutomationTests/SliceSettingsTests.cs:774); unit tests for ClubTabStyle geometry
- [ ] Send follow-ups: Stop during slicing waits for the slice; key shown in clear while pasting; no print-history record

## OctoPrint Pi (found 2026-11-11)
OctoprintPulseXE.local, Raspberry Pi 4B 4 GB, OctoPi 1.1.0 / OctoPrint 1.11.8, a club account,
SSH key [key path removed] (owner installed it). Printer = Smoothieboard on the USB serial port.
