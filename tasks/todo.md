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
- [x] UX step 2: icon refresh (Lucide 1.72.0 + custom glyphs; committed d31e34dda)
- [ ] Tabs follow-ups: faint labels/hairline in stock Solarized-Light and Classic-Dark themes; stale private test helper
      CloseAllPrinterTabs (Tests/MatterControl.AutomationTests/SliceSettingsTests.cs:774); unit tests for ClubTabStyle geometry
- [ ] Send follow-ups: Stop during slicing waits for the slice; key shown in clear while pasting; no print-history record

## OctoPrint Pi (found 2026-11-11)
OctoprintPulseXE.local, Raspberry Pi 4B 4 GB, OctoPi 1.1.0 / OctoPrint 1.11.8, a club account,
SSH key [key path removed] (owner installed it). Printer = Smoothieboard on the USB serial port.

## Bench results on the Pulse via OctoPrint (2026-11-11, cold, owner at the printer)
Machine: bed moves on Y; gantry carries the hotend on Z; Z homes to the TOP (max).
- [x] OctoPrint connects on the USB serial port @115200; Smoothieware edge-d82d66fNOMSD answers M115/M105/M503; no halt.
      Bed thermistor reads normally (21.7 C).
- [x] BLTouch: `M280 S3.3` deploys, `M280 S7.43` stows. `M280 S3.0` (firmware config value) does NOT deploy.
      Club start G-code corrected to S3.3 / S7.43 (owner approved).
- [x] Finger-trigger test: G30 stopped on a tap. Real G30 at bed centre: pin touched after 214.01 mm.
- [x] Z zero set by paper test at firmware Z -4.5, stored with `M306 Z0` + `M500` (owner approved):
      config-override now has M206 Z4.50; after G28 the firmware reports Z 214.5. Paper check at Z0 confirmed.
      Undo: `M502`. Probe triggers about 0.5 mm above the paper-grip height.
- [ ] Power-cycle the printer and confirm Z 214.5 after homing (config-override persists)
- [ ] G32 grid probe (3x3 over 180x180, firmware probe offset 0,-15); then repeat the paper check with levelling on
- [ ] Hot check: Z zero at 255 C; first-layer test print through OctoPrint
- [ ] Firmware max hotend temp is 400 C (M143 S0); EBMC.md says keep under 300 C — propose lowering (needs owner yes)
- [ ] OctoPrint on the Pi: printer profile set to 250x220x210, nozzle 0.3; serial log on; config backups in
      [path on the Pi] Still default: disconnectOnErrors/sendM112OnError (kept on purpose).

## Bench results, afternoon 2026-11-11 (cold, via OctoPrint, owner at the printer)
Firmware source read at this build's commit (upstream Smoothieware d82d66f): G32 stores heights relative to its
first point and does not move Z zero; a missed probe would travel 2 x max_z; no before/after-probe hooks.
- [x] SD card config (owner approved; original in printer-config/original, installed copy in printer-config/proposed):
      probe_offsets 23,0,0; initial_height 11; only_by_two_corners true; soft endstops on
      (x 0..250, y 0..220, z_min -2.5).
- [x] Stored with M500: M565 X23 Y0 Z0 (probe is 23 mm right of the nozzle), M670 Z6 (probe travel capped at 12 mm),
      M206 Z4.56. Verified the cap ("ZProbe not triggered" after 12 mm with the pin stowed).
- [x] Club start G-code: G28 / M280 S3.3 / G32 X23 Y15 A180 B180 / M280 S7.43 (owner approved; not yet committed).
- [x] G32 grid (repeatable to 0.01): left ~0, middle ~-0.5, right ~-1.0..-1.2 mm => gantry tilted ~1.1 mm over 180 mm.
- [x] Probe triggers 1.20 mm above the paper-grip height (same-spot test at the centre; 1.19 at the front-left).
      The morning paper test was ~0.6 mm high (cause unknown; later two tests agree).
- [x] With levelling on at Z0: paper GOOD at the centre (125,110); at the right (200,110) it needed Z -0.30.
      => levelling under-corrects by ~0.3 mm at the far right; left and centre are right. Likely the carriage
      rolls along a twisted/tilted gantry (probe-to-nozzle height changes with X). Mechanical fix needed.
- [ ] Level the X gantry mechanically, re-run G32 (target < 0.2 mm), repeat paper checks at centre and right
- [ ] Until then keep prints in the left/centre of the bed (X below ~150)
- [ ] Hot check (255 C) and a small first-layer test print through Send
- [ ] OctoPrint: PortLister 0.1.11 installed; autoconnect on; custom "Z offset (EBMC)" controls added.
      NOTE the "Set Z zero here and save" button stores zero at the nozzle's current spot; with levelling on, the
      right value is set from the probe at the grid reference point (see above) — reword or remove the button.

## CORRECTION and final Z zero (2026-11-11, late afternoon)
The "right side is 0.3 mm high" finding above was wrong: it compared a lighter paper feel at the centre with a
firmer one on the right. Re-tested with the same feel: centre good at Z -0.20, right (200,110) good at Z -0.30,
so levelling is right to ~0.1 mm across the bed. The owner's preferred paper feel is ~0.2 mm lower than the
earlier tests, i.e. the probe triggers ~1.40 mm above that height.
- [x] Final stored offset: M206 Z4.76 (owner approved), M500. Confirmed by paper at the centre, levelling on, cold.
      After G28 the firmware reports Z ~214.76. Right side is ~0.1 mm high (cold).
- [ ] Gantry is still tilted ~1.1 mm over 180 mm (levelling compensates); level it mechanically when convenient
- [ ] No restriction needed on where parts sit; watch the first layer on the far right
- [ ] Hot check at 255 C and a small first-layer test print through Send
- [ ] Decide what to do with the "Set Z zero here and save" control (misleading with levelling on)
- [ ] Commit the start G-code corrections (S3.3/S7.43, G32 X23 Y15 A180 B180) when the owner says

## Hot test and re-zero (2026-11-11 evening)
- [x] First test print through Send to the real OctoPrint (Cube_20261111-160000.gcode, 265 C / 60 C): upload, bed heat,
      home, G32 (all nine points), purge line and skirt all ran. Owner cancelled during the skirt: nozzle too high.
      Cancel took ~40 s (Smoothie finishes buffered moves before answering M84); heaters then went off.
- [x] NylonX nozzle default set to 265 C (owner), generator + EBMC.md updated (not committed yet).
- [x] Re-zero at printing conditions (bed 60 C soaked, nozzle 150 C, grid active, bed centre): owner set the height
      0.8 mm lower than the cold calibration. Stored M206 Z5.56 (owner approved); confirmed by paper at Z0.
      Cause of the 0.8 mm difference from the cold paper tests is not established.
- Lesson: keep paper off the bed during G32 — a sheet under the first point made the grid fail ("Probe failed to
  complete") and that message does not stop a print.
- Hot grid (60 C) differs from cold by 0.1-0.2 mm in the middle; the start G-code probes after bed heat-up, so
  prints use the hot shape.
- PortLister reconnect failed once after a power cycle (garbled first line -> OctoPrint sent M112 and closed);
  manual Connect worked. Not yet made more tolerant.
- [ ] Second test print with Z offset 5.56; watch purge line and first layer
- [ ] Commit the 265 C default when the owner says
- [x] Second test print (Cube_20261111-172506.gcode, Z offset 5.56): owner reports the first layer looks great.
- [x] Club profile: Send to OctoPrint on by default with http://OctoprintPulseXE.local (owner approved).

# WHERE WE LEFT OFF (2026-11-11, end of day) — read this first next session
Branch macos-arm64, 6 commits ahead of origin (not pushed): d61e2c877 rebrand, 150a56bd1 MatterHackers removal +
English-only + OctoPrint client + levelling, 4e5f23017 Send + off-bed warning + tabs, 30107202e probe/grid
G-code, e65a7c71d NylonX 265 C + Send on by default.

Working on the printer (all confirmed today):
- Slice in the app -> Send > Upload and Print -> OctoPrint on the Pi -> bed heat, home, 9-point probe, print.
- First completed print through this chain: Cube_20261111-172506.gcode, NylonX 265/60, 45 min, owner: "looks great".
- Z offset M206 Z5.56 (set hot), probe offset X23, probe cap M670 Z6, soft endstops; see EBMC.md
  "Printing through OctoPrint" and the bench sections above.

Icon refresh (UX step 2) is committed as d31e34dda: 34 Lucide 1.72.0 + 14 custom glyphs, built by
Tools/EBMC/build_icons.py from Tools/EBMC/icons/map.json. Seen in the running app before the commit.

CHANGED 2026-11-11 — no Beginner screen in this app. Owner: the Pulse XE "will only be for advanced users
needing nylon or advanced materials". Beginners (kids/adults with a downloaded model) use stock PrusaSlicer
on the club's Prusa printers. Owner chose "PrusaSlicer for beginners". So:
- This app: keep only the maintainer lock on printer and material settings (ordinary members cannot change
  them; material fixed to what the profile says is loaded). Design under way, plan to be shown to the owner.
- Beginners: a club PrusaSlicer settings bundle (Simple mode, club presets, printers set up to send).
  Research under way. Needs from the owner: Prusa printer list, nozzles, materials, PrusaLink vs OctoPrint.

MAINTAINER LOCK — plan approved by owner 2026-11-11 ("go, PIN, speeds editable, hide Controls and Terminal"):
- [ ] L1 rule + PIN + switch: SettingsLock / MaintainerPin / MaintainerMessages (pure, unit-tested), Maintainer
      service, PIN page, OEM flag RequireMaintainerForPrinterSettings via the generator, Settings row. No gates.
- [ ] L1 independent review
- [ ] L2 gates: settings writes (SettingsContext), greyed rows + row menus, material dropdown + preset editing,
      Printer Options items, Controls/Terminal hidden, Printers tab delete/add, rename, import, Part Settings
      material overrides ignored, OctoPrint key buttons. Checked in the app locked and unlocked.
- [ ] L2 independent review
- [ ] L3 wording, EBMC.md section ("a guard, not security"), real setup on a club Mac
Decisions: per-computer PIN (hashed in user settings, session-only unlock, app starts locked); speeds stay
per-print/editable; quality presets selectable by all, edited by maintainers only; adding a printer allowed
only when none exists.

TWO INTERFACES — owner 2026-11-11: "one for simply interacting with the printer but also one where I could
start building out my own CAD features" (open-source, feature-rich, a Fusion 360 alternative).
- Print interface = printer tabs: no shapes rail, toolbar trimmed to printing tools, maintainer lock.
- Design interface = design tabs ("+"): keeps the rail and every modelling tool; CAD features go here.
- [ ] Shapes rail off printer tabs (OEM switch ShowShapesRailOnPrinterTabs), kept on design tabs
- [ ] Printer-tab toolbar trimmed (OEM switch TrimPrinterTabToolbar); owner said "trim the toolbar"
- [ ] CAD design study (kernel, sketch solver, .NET interop, roadmap, build-vs-adopt) — read-only research,
      started in parallel. No CAD code until the owner has seen the study. Note: today's modelling is
      mesh-based; Fusion-style CAD needs a solid-modelling kernel + constraint solver.
Review fixes folded into L2: SetPin refused while locked + Change PIN asks for the current PIN; material rule
uses the ACTIVE material preset only; tests against the real club profile.

STATE 2026-11-12 (evening):
- Owner: "Right now, the PulseXE and app is for me only" (testing, observing the club); later members who want
  nylon / exotic materials. A print sent from the app through OctoPrint worked today.
- OctoPrint fixes today: ModemManager disabled on the Pi (it probed the printer port and garbled the first
  line), "send M112 on error" off (it halted Smoothie after any error), Preheat (EBMC) buttons + temperature
  presets PLA 210/55, PETG 240/75, NylonX 265/60, NylonG 265/60. Backups in [path on the Pi]
- Printer: one halt mid-start at 15:32, "MINTEMP or MAXTEMP triggered on B" (bed sensor) as the bed moved to
  Y5; suspected bed thermistor wiring; owner says the printer "is now fixed" — ask what was done; wiggle test
  not run.
- UNCOMMITTED, built, 334 tests, NOT yet seen running since the fix round and not re-reviewed: maintainer lock
  (L1+L2+fixes), shapes rail switch, printer-tab toolbar + right-click trim.
- IN PROGRESS: shapes-and-tools palette, direction A of tasks/design/shapes-rail-mockups.html (owner chose "A").
- NEXT, owner said "Start with group 1" (safety): (1) refuse G-code not made for this printer / maintainer-only,
  (2) bed-clear + material confirmation before every remote start incl. Ctrl+P, (3) confirm before a design
  tab's Print replaces the printer bed. Starts when the palette build is finished (same files, one build).
- Walk-in/code analysis delivered 2026-11-12 (three reviews). Groups 2-5 not started: first-ten-minutes fixes,
  Club Starters (+ the Release "UserHasPermission => false" persistence blocker to confirm), shared-computer
  reset, engineering (CI, Mac tests, signing, profile updates, mXparser licence, dead-code deletion,
  .gitignore printer-config/ and .DS_Store — owner not yet answered).
- CAD study delivered 2026-11-11 (OCCT + libslvs + own C shim; Stage 0 spike is the go/no-go). Not saved to a
  file; owner has not said whether to run the spike.
- PrusaSlicer bundle research delivered 2026-11-11 (2.9.7, config bundle, PrusaLink recommended). Waiting on the
  owner for printer list, nozzles, materials, PrusaLink yes/no.

Open items, roughly in priority order:
1. Maintainer lock + PrusaSlicer club bundle (above). Push the branch only when the owner says.
2. UX design list, next areas: floating view-control cluster (now mixes old/new icons), printer action row
   buttons, left shapes rail, Library tiles, right-hand panel tabs. Designer mockup direction A "Club label".
3. UX behaviour list: PrusaSlicer mouse/keys option, G-code preview coloured by feature + time legend, hover
   highlight, place-on-face, tighter arrange, bed axes. Earlier checklist bugs: clipped layer-view side panel,
   file dialog opens without keyboard focus.
4. Printer: level the X gantry mechanically (1.1 mm tilt, levelling compensates); firmware max hotend temp is
   400 C vs 300 C thermistor limit; PortLister reconnect failed once after a power cycle (manual Connect works);
   OctoPrint Cancel takes ~40 s on Smoothie.
5. App follow-ups: third-party notices for all shipped libraries; Variable Sheet model assets not unpacked on a
   clean install; stock-theme contrast for the new tabs; Send: Stop during slicing waits for the slice.
6. PrusaSlicer club config bundle for the Prusa printers (needs the club's printer list; the network shows a
   "Prusa MK3.5S" and a "Prusa XL").
7. MACOS.md still says running on a Mac is "Not yet tried" — update with today's results.

Access for next time (nothing secret is stored in this repo):
- OctoPrint: http://OctoprintPulseXE.local, a club account. SSH key on the owner's Mac:
  [key path removed] API key for Claude's use: [path removed] (application key; revoke in OctoPrint > Settings > Application Keys). OctoPrint config backups on the Pi:
  [path on the Pi] Serial log is ON ([path on the Pi]).
- Printer SD card backups: printer-config/ in the repo folder (untracked on purpose).
- Mock OctoPrint for tests: python3 Tools/EBMC/octoprint_mock.py --port 5050 --key testkey

Design reference kept in the repo folder (untracked): tasks/design/ebmc-ux-mockups.html (designer's three
directions; A was chosen) and the icon before/after sheets (icons-contact-sheet-3x.png = first pass,
icons-contact-sheet-v2-3x.png = redrawn glyphs). The session scratchpad they came from may be gone next time.
