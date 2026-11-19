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
OctoPrint runs on a Raspberry Pi 4B 4 GB (OctoPi 1.1.0 / OctoPrint 1.11.8) on the club network. Address, login
and key locations are NOT recorded in this public file; they are in the owner's private notes. Printer = Smoothieboard over USB serial.

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
      a backups folder on the Pi. Still default: disconnectOnErrors/sendM112OnError (kept on purpose).

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

# Members & Projects, stage 2: the Members tab (2026-11-16, uncommitted)
- [x] EnableClubMembers (stock off, club on); MembersSession + MembersSessionCore (open on unlock, move to a new PIN
      before it is stored, save and close before lock and on exit, drafts in memory, save 1 s after the last change);
      Members tab with quick add, search, list, detail and state views; Restore honours the PIN check with the vault
      open; tests (MembersSessionTests, MembersWiringTests, one vault regression test).
- [x] Fix round after an independent review: PIN change allowed only when the file moves (or the new PIN opens it,
      or there is no file), "Change the PIN anyway" as a worded choice; typed fields saved after a pause, each on its
      own; failed save at lock told in a box, held, warned about at exit; drafts cleared at lock; second-window PIN
      check; open after the rebuild; no UI-thread waits for background saves.
- [ ] On-screen check with invented names only (see the stage 2 report); no screenshots of real data.
- [ ] Next stages: projects (3), export/import/backups page (4), design links (5).

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
  presets PLA 210/55, PETG 240/75, NylonX 265/60, NylonG 265/60. Config backed up on the Pi first.
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

IMPROVEMENT PROGRAMME — owner 2026-11-12: "Commit the work so far. After that ... think extra hard about what
we could do to improve the app, and do it ... don't do anything that will change/alter printing itself since
it's working great ... simplicity for a beginning user ... Fusion360 like tools for an advanced user ... Test
them and integrate ... as UX designer ... more tools, cleaning up menus, icons, pop-ups ... improve the
beginner mode as well as the advanced mode."
Committed first: 6f235b466 (lock, palette, print safety, single theme). Branch 7 ahead of origin, NOT pushed.
HARD RULE for this programme: no change to slicing, G-code output, start/end G-code, the export stream chain,
OctoPrintSender's upload/start behaviour, the club profile's slice/printer settings, or anything on the Pi or
printer. Design side, UI, wording, menus, icons, dialogs, Library, Help only. Commit only when the owner asks.
Plan (one build tree, so code batches run one after another; designers/reviewers are read-only in parallel):
- [ ] R  Independent review of the last fix round + theme lock (already committed); fix findings.
- [ ] UX UX audit + mockups: Simple/Advanced modes, menus, icons, pop-ups, properties panel, Library, Help.
- [ ] A1 Design-tool fixes: Subtract uses holes; boolean/rebuild failures reported; Duplicate beside; Lay Flat,
        Plane Cut and scale-handle crashes; text size in mm; Measure dX/dY/dZ; Plane Cut keep top/bottom;
        load failures reported; wrong-size/units check on load; macOS file dialog focus.
- [ ] A2 New shapes (rounded plate, tube, polygon/hex prism, star, heart, slot, gear) and Club Starters
        (name tag, sign, box with lid, hook, phone stand, knob, cable clip, plant label, QR tag) in the palette.
- [ ] B  Simple / Advanced mode (app-wide switch) per the UX spec; menus, pop-ups and icon clean-up.
- [ ] C  Fusion-style tools that fit a mesh modeller: command search, position/size fields, mirror copy,
        pattern tools, section view, place-on-face, SVG import, parameters table surfaced.
- [ ] Each batch: unit tests + Release build, on-screen check by the orchestrator, independent Opus review.
NOT in this programme without a separate yes: the exact-solid CAD kernel spike (needs downloading/building
OpenCascade), the Release "UserHasPermission" persistence change (it changes which objects reach the slicer),
anything on the print path.

RESUME POINT (written 2026-11-12 ~22:00; owner: "That looks good, keep going"; usage limits nearly reached)
Branch macos-arm64 at c908cc7bf (history scrubbed of access details; 7 ahead of origin; NOT pushed; do not push
or commit without the owner's word). Uncommitted in the tree, all built and unit-tested (469 tests, 0 errors):
  A1 design-tool fixes (DesignTools/EBMC/*, Subtract/Combine/PlaneCut/Text/Measure/Duplicate, ModelSizeCheck,
  load-failure notice, macOS file panel focus, typos), the print-safety re-check before export, palette queue
  fixes, native/occt/ (OpenCascade 8.0.1 shim + .NET test; spike PASSED 13/13; _build/ is git-ignored).
  A1 seen on screen: Duplicate beside, Solid/Hole buttons, hole insert, save-prompt wording. Rest unchecked.
  A1 has NOT had its independent review yet.
In flight when this was written: "Integrate OpenCascade solids into app" implementer (Solid Box / Cylinder /
  plate with hole, STEP import/export, OEM EnableSolidModeling). If its report is missing after a restart, look
  at git status for MatterControlLib/DesignTools/Solid/ and re-brief a fresh implementer from native/occt/README.md.
Approved order after that (owner saw tasks/design/ux-modes-mockups.html and said keep going):
  1. on-screen check + independent review of A1 and the solids batch; fix findings
  2. visual clean-up: P1 icons, Nunito default font, dialog + menu styles, tooltips/toast, theme JSON fixes,
     wording/typos, DockingTabControl int-cast fix (UX report "safe now" items 1-10)
  3. Simple / Advanced switch per the mockups (default Simple; drives SliceSettingsViewDetail)
  4. A2 new shapes + Club Starters; empty-tab start card
  5. advanced tools: command search, typed transform panel, mirror copy, section view, shortcuts sheet
LEFT OUT unless the owner asks (print-adjacent): design-tab Print becoming "Send to printer tab" without
  starting; Simple printer panel with quality/strength; eye-toggle hiding (until it is known whether hidden
  parts are sliced); the Release UserHasPermission persistence change.
Still waiting on the owner: what fixed the printer (bed sensor halt 2026-11-12 15:32); G-code rule (their usual
  job is a .gcode file, refused while locked); PrusaSlicer bundle answers; purge of old commits from the reflog.

STATE 2026-11-13 ~03:20. THE IMPROVEMENT PROGRAMME'S PLANNED WORK IS COMPLETE AND REVIEWED.
Committed: ed018e809 (8 ahead of origin, NOT pushed). UNCOMMITTED, built, 954 tests, 0 errors (Release and
Debug), bundle in dist/ rebuilt after the last fix round: Simple/Advanced mode; visual review fixes; solids
fix rounds 2-3; seven shapes, nine Club Starters, start card; advanced tools (Cmd+K search, Transform section,
Mirror Copy, shortcuts sheet); dialog template + safe Enter defaults + regrouped menus + toasts; all review
fix rounds, including the final one (Debug re-add crash, MissingMeshWatch, DefaultButton -1).
Final review: no must-fix; print path untouched except the owner-approved speeds; no private data in the tree.
WAITING ON THE OWNER:
  1. ON-SCREEN PASS — none of batch 4, batch 5, the dialogs/menus or the later fix rounds has been seen
     running (display asleep since ~01:30). Do this first when the owner says "check".
  2. Commit (owner's word needed). 3. Sign-off on the "Cmd + P" hint text in OctoPrintSendPopupMenu.cs.
  4. New speeds: unlock + Printer Options > Update settings on the installed printer; test cube.
  5. OpenCASCADE contact + copyright holder; release checklist before sharing any binary.
The nudge cron was deleted when this was written; nothing is scheduled.
Open follow-ups (not started): Transform section should edit parametric objects' own size fields; section
  view (feasible, S); Help/tour rewrite; P2/P3 icons + tumble cube; Library container renames; PrusaSlicer
  bundle (waiting on owner); CI on GitHub; signing/notarisation; mXparser licence; toasts all use the info
  icon; whether a window rebuild during a print (AllowsWindowRebuild) disturbs anything — untested live.

TEMPERATURE LIMITS — owner 2026-11-13: "Set the max temp to 300", "The max bed temp should be 100".
Staged firmware config (printer-config/proposed/config, untracked): hotend max_temp 400 -> 300, bed max_temp
140 -> 100, each with a .bak copy beside it. WRITTEN TO THE PRINTER'S SD CARD 2026-11-13 13:19 through OctoPrint (config-set sd; read back: hotend 300,
bed 100; before: 400 / 140). The firmware reads its config at start-up, so the limits take effect after the
NEXT printer reset or power cycle — not yet done (printer was hot at 265/60 and idle). After the reset:
re-read both values and note it here. App: Temperature Tower and Set Temperature parts now cap at 300 (were 400).
On-screen pass 2026-11-13 09:00: start card, starters, palette families, club menus, Mirror Copy, Cmd+D,
shortcuts sheet, dialog template seen working. Defects found and fixed afterwards (976 tests, NOT re-checked
on screen): Find a tool popup had zero width; start card position/text; Size field appended typed text to
the old value ("79.7100.126" read as 79.71*0.126); clipped properties title; badge tile tint.

2026-11-13 13:27: new speeds applied to the INSTALLED printer on the owner's Mac through Printer Options >
Update settings (owner unlocked the maintainer lock): infill/top/perimeters 70, support/raft 80, bridges 40,
travel 250, first layer 20; confirmed in the saved profile, no speed overrides. Not yet printed at these
speeds. Firmware limits (hotend 300, bed 100) are on the SD card but need a printer reset to take effect.

SPEEDS — owner approved 2026-11-12 ("I agree with your suggestions. Except, let's keep the first layer at
20"): club profile infill/perimeters/top 35->70, support/raft 40->80, bridges 20->40, travel 150->250 (firmware
X/Y cap is 300), first layer stays 20; retract 40 and min speed 15 unchanged. Done in the generator and
regenerated (uncommitted). The installed printer on the owner's Mac keeps its own copy: owner must unlock and
use Printer Options > Update Settings. Not yet printed at these speeds. This is the ONE approved exception to
the programme's "nothing changes printing" rule; reviewers must expect these 8 OemLayer keys to differ.

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
- OctoPrint and the Pi: address, account, SSH key and API key locations are kept out of this public file (owner's
  private notes / Claude memory "ebmc-resume-pointer"). OctoPrint config backups and the serial log are on the Pi.
- Printer SD card backups: printer-config/ in the repo folder (untracked on purpose).
- Mock OctoPrint for tests: python3 Tools/EBMC/octoprint_mock.py --port 5050 --key testkey

Design reference kept in the repo folder (untracked): tasks/design/ebmc-ux-mockups.html (designer's three
directions; A was chosen) and the icon before/after sheets (icons-contact-sheet-3x.png = first pass,
icons-contact-sheet-v2-3x.png = redrawn glyphs). The session scratchpad they came from may be gone next time.

# Export for laser, stage A (2026-11-14, uncommitted)
- [x] OEM switch EnableLaserExport (stock off, club on); pure outline core in MatterControlLib/DesignTools/Laser/
      (footprint, slice at height, SVG and DXF writers); SVG/DXF exporters; Export for laser page; command search
      and club menus; tests (LaserOutlineTests, LaserWriterTests, LaserWiringTests).
- [x] Measured: 100,800-triangle sphere footprint about 0.2 s, 301k-triangle height field 0.46 s, badge STL 17 ms.
- [ ] On-screen check (plan step A9): 20 mm cube via Find a tool > laser; Plate with Hole and a text sign as SVG
      and DXF; open in a browser/Inkscape and LightBurn and measure; owner checks XCS/xTool Studio size and that
      the lines import as cut lines; printer-tab Export dialog still lists G-code as before.
- [ ] Owner questions: XCS vs LightBurn, red vs black lines, whether R12 DXF imports cleanly (R2000 exists in code).
- Sample files: run `LASER_SAMPLES_DIR=<folder> dotnet test Tests/EBMC.Tests --filter Name=WriteSamples`.

# Export for laser, stage B: G-code for laser (.nc) (2026-11-14, uncommitted)
- [x] Pure core LaserGcode.cs (machine profile, job, rules, writer, data loader); machines.json and an empty
      materials.json from the generator; EnableLaserGcode (stock off, club on), maintainer-unlocked only; third format
      on the Export for laser page; LaserGcodeTests.
- [ ] Measure on the xTool S1 before anyone cuts with this file: G-code dialect (does it accept .nc at all), S range,
      M3/M4, max speeds, work area, Y origin. Until then the S1 profile is a placeholder (verified: false).
- [ ] Owner approves the first material presets (none are shipped).
- [ ] On-screen check of the G-code section of the page (maintainer unlocked and locked).


# RESUME HERE (2026-11-16, afternoon) — read this first next session

State of the repo: branch `macos-arm64`, level with GitHub at `0e9107104`. Everything below is local and
UNCOMMITTED. Commit or push only when the owner asks.

## Members & Projects (in progress)
- Stage 1 (pure core, encrypted file, `MatterControlLib/ClubMembers/Core/`): done, reviewed five times, judged fit
  to build on.
- Stage 2 (Members tab, quick add, list, detail panel, notes log, lock and PIN-change wiring): built, reviewed three
  times. Last review: nothing blocks real data. 1345 tests pass. The app bundle in `dist/` was rebuilt with it.
- NEXT: on-screen check with INVENTED names only (the review could not run the app). Needs the owner to unlock
  the maintainer lock; the tab only exists while unlocked. Screen control lacked the macOS Screen Recording
  permission on 2026-11-16; the owner was restarting to fix that.
  Checklist: tab appears on unlock ("Opening the members list…") and goes on lock; quick add + Enter; search;
  contact, date met, tags, notes log; typing survives a Simple/Advanced switch; notes typed then force-quit after
  ~2 s are kept; lock with typed text shows a count; Change PIN (normal, and the "anyway" wording); delete
  confirmation; wrong-PIN and unreadable views; printing and slicing unchanged.
- Three low findings left from the last review (not blocking): a misleading notice when files saved under
  different PINs are mixed; the "PIN not stored" advice is wrong after a restart; the "changes made here" counter
  over-counts edits later undone.
- Not started: stage 3 (projects), stage 4 (export/import, restore UI), stage 5 (design-file links).
- Owner decisions so far: encrypt with the maintainer PIN; machines are free-text tags; archive by default;
  tab named "Members", shown in both modes; the "PIN changed in another window" check applies always.
- Before real names go in: suggest a 6–8 digit PIN. Two copies of the app can run at once on macOS.
- Privacy rule: member data lives only in the app's per-user data folder. Invented names only in the repo.

## Export for laser (parked by the owner on 2026-11-16)
- Stages A (SVG/DXF) and B (laser G-code, `.nc`) are built and reviewed; stage C (direct send) not started.
- Not confirmed on screen: the save step, and no laser software has opened the files.
- Members and laser changes share six files but are kept in separate hunks so they can be committed apart.

## Printer
- Retraction thresholds 5 → 1 mm are committed, pushed and applied to the installed printer profile; not yet
  print-tested (the stringing on jar-lid threads). Drying the spool was the other suggestion.
- Still open from before: read back the firmware temperature limits after a printer power-cycle; print-test the
  doubled speeds.

## Outside the repo (owner's Claude Code setup, 2026-11-16)
- A status line (context bar plus 5-hour and weekly subscription usage) and an "Orchestration" section in the
  owner's global instructions were added, with Opus as the default sub-agent model.

# PLAN (2026-11-16, evening): maintainer lock off, Members on its own passphrase — APPROVED by the owner 2026-11-16

Owner decisions (2026-11-16): turn the maintainer lock off in the club build; Members gets its own passphrase.

Spec: `RequireMaintainerForPrinterSettings` becomes false, so no PIN is asked anywhere and printer and material
settings are open; the lock code stays in the repo, switched off. The Members tab is always in the tab bar when
`EnableClubMembers` is on. Opening it asks for a Members passphrase that encrypts the file exactly as the PIN did
(the encrypted-file code takes any text as the key, so the file format does not change). First use sets the
passphrase (typed twice, 8 characters or more, with a line saying a forgotten passphrase cannot be recovered).
The tab has "Lock" and "Change passphrase…". Nothing about the passphrase is stored outside the file. The old
PIN-change wiring between the lock and Members is deleted rather than kept behind the switch, because keeping both
doubles the session state machine. A file made under the old PIN still opens when that PIN is typed.

Earlier on 2026-11-16 (done, on screen not yet confirmed): four display fixes (rows skipped by the layout so the
quick-add bar and the log section were clipped; list date in UTC; lock notice wrapped too wide). 1349 tests pass.

Steps
- [x] 1. Lock off: `StaticData/OEMSettings/Settings.json` and `Tools/EBMC/generate_ebmc_files.py`.
- [x] 2. Core, no UI (`ClubMembers/Core/MembersRules.cs`, `MembersVault.cs`, `Session/MembersSessionCore.cs`):
      passphrase rules, create-list, unlock that never creates a list, change passphrase, restore check; delete the
      PIN-change code and the Opening and WrongPin states; reword the sentences. Tests rewritten to match.
- [x] 3. Session glue (`ClubMembers/MembersSession.cs`): tab shown whenever the feature is on; Lock, Open,
      CreateList, ChangePassphrase; no reference to the maintainer.
- [x] 4. Unhook the maintainer (`Maintainer.cs`, `MaintainerPinStore.cs`, `MaintainerPinPage.cs`): no reference to
      Members; remove "Change the PIN anyway" and "Load the list again".
- [x] 5. Tab UI (`MembersTabPage.cs`, `MemberDetailPanel.cs`): Set and Enter views, Lock and Change passphrase on
      the open view, and the three review findings (error text that never clears, one-line notes box, texts cut
      off when wrapped, the two rows still anchored the old way).
- [x] 6. Laser G-code: owner chose to switch `EnableLaserGcode` off in the club build (SVG and DXF stay).
- [x] 7. `EBMC.md`: Members row, laser row, a line that the lock is off in the club build.
- [x] 8. Independent review of the whole change, full test run, rebuild the app.
- [x] 9. On-screen check with invented names and a throwaway test passphrase: set, wrong, right, lock, change,
      Simple/Advanced, force-quit keeps notes, log, Archive, delete confirmation, no lock items left in menus,
      printer settings editable, printing and slicing unchanged.

What turning the lock off also opens (stock MatterControl behaviour, flagged): opened .gcode files and plates with
Set Temperature / Send G-Code parts are no longer refused; anyone can edit the OctoPrint key and printer settings.
"Ready to print?" before a remote print still asks.

Test data: the three invented members made today are in a file keyed to the old PIN; it will be moved aside (not
deleted) before the on-screen check, with the owner's yes.

Status 2026-11-16 18:30: steps 1-7 built; 1350 tests pass; `dist/EBMC.app` rebuilt with them. Step 8 (independent
review) running. Step 9 (on-screen check) NOT done: the Mac's screen was locked, so the app could not open a
window. The old test members folder was moved aside (same per-user data folder, name ending
`-set-aside-2026-11-16-old-pin`), with the owner's yes. All of this is uncommitted.

Status 2026-11-16 18:45: independent review found nothing blocking real names. Its two medium findings (a mistyped
passphrase after a failed-save lock dropped the held key; passphrases left in the masked fields' undo history and
copyable) and the small ones are fixed. 1353 tests pass. `dist/EBMC.app` rebuilt with all of it.
RESUME HERE: step 9, the on-screen check, is the only step left; it needs the Mac's screen unlocked. Use a
throwaway test passphrase and invented names. Also confirm on screen: Cmd+Z in a cleared passphrase field brings
nothing back; Cmd+C / Cmd+X do nothing there (the right-click Cut/Copy is known to remain: it is a shared widget).
Known and left: the app-wide message box wraps text too wide on Retina screens (`StyledMessageBoxWindow.cs` ~275,
a one-word fix the owner has not asked for; the Members notice works around it).

Status 2026-11-16 19:00: step 9 done on screen (macOS, invented names, throwaway passphrase). Seen working: no
PIN or lock item anywhere (Printer Options menu); Members tab always present; Set view (mismatch and too-short
refused, Cmd+Z brings nothing back, file written at once); quick add with first note; local date in the list; log
row, entries, Archive / Unarchive, Show archived, red delete confirmation and delete; date error appears and
clears; three-line notes box; notes typed 3 s before a force-quit kept; Enter view after restart; wrong
passphrase refused; Change passphrase (wrong current refused; old one then fails, new one opens); drafts and the
open list survive Simple/Advanced; Lock with typed text shows the count notice, wrapped correctly; slicing works.
NOT checked on screen: sending a print (shared printer, not started); Cmd+C / Cmd+X blocking in passphrase
fields; two app windows at once; restore from backup; laser SVG/DXF export (parked).
Small things seen, not fixed: the "passphrase was changed" notice stays through a Lock until dismissed.
Review section: what surprised — rows anchored `Center | Fit` inside a top-to-bottom Fit column are skipped by
agg-sharp's layout, which three code reviews and 1345 tests missed; only running the app showed it. Remember:
run the app before calling UI work reviewed. CORRECTION 2026-11-16 19:05: the owner has started adding REAL members to the
list on this Mac. Do NOT delete or move the `Members` folder. That list is still under the throwaway test
passphrase from the on-screen check, which appears in the session transcript: the owner should change it with
Change passphrase. No more test typing into that list; on-screen tests need a separate data folder. The older
set-aside folder (old PIN, invented names only) can go with the owner's yes.

# PLAN (2026-11-16, 19:10): member importance and role badges — design chosen by the owner

Owner's choices: an importance level per member (none, 1, 2, 3 stars) plus an optional short role typed by hand
and shown as a badge (for example a founder is 3 stars with the badge "Founder"); the list is ordered by
importance first, then most recent activity as today; badges and colours on the list rows and in the member
panel; a filter above the list for "this importance or higher".

Spec: two new fields on a member, `Importance` (0 to 3, default 0) and `Role` (one short line, default empty).
Files written before this change open unchanged, with every member at 0 and no role. The role is searchable.
Changing either is an ordinary edit. The filter is a view setting and is not stored in the file.

- [x] 1. Core: fields, rules, ordering, search, export; old files still open; tests.
- [x] 2. UI: picker and role field in the member panel; stars, colour and role badge on list rows; filter.
- [x] 3. `EBMC.md` Members row.
- [x] 4. Independent review (file compatibility first), full tests.
- [x] 5. On-screen check in a SEPARATE scratch data folder, never the owner's real list.

Status 2026-11-16 19:55 (importance and roles): built, 1373 tests pass, independent review found nothing blocking
and judged the owner's existing file safe to open (the first save keeps version 1 copies in `.prev` and the day's
backup). Seen on screen in a separate test copy on a scratch data folder: picker, role field and badge, stars and
stripe on rows, importance-first order, star filter, search by role. Review follow-ups (role validation gaps,
clamped sort for out-of-range values, empty-list wording, star outline clipping, boundary tests) are with the
implementer. NOT yet in `dist/EBMC.app`: the owner's app is running from it, so the bundle is rebuilt only when
the owner says to quit it. The members file format is now version 2: the build now in `dist/` will refuse a file
this build has saved, so do not open the real list with the new build until the owner has the new app.
Added outside the feature (owner to confirm): `EBMC_DATA_DIR` in `ApplicationDataStorage.cs`, an environment
variable naming another folder for ALL user data, so a build can be tried without touching the real settings and
members list; unset = normal behaviour, a non-absolute value stops the app. How to test on screen: publish to a
scratch folder, wrap it as an app with a DIFFERENT bundle id, start it with `open -n <app> --env
EBMC_DATA_DIR=<scratch>`, and confirm with `lsof` that it opened the scratch `MatterControl.db`. Setting HOME
does NOT redirect the data folder on macOS (tried: the test copy opened the real settings database for about a
minute; the members file was not opened or changed).
Open question from the owner (19:22): stay on the MatterControl fork or move. Recommendation given: keep the fork
for printing, move Members and other club-admin features to a separate small app reusing `ClubMembers/Core` and
`Session`; waiting for the owner's answer.

Status 2026-11-16 20:00: review follow-ups for importance done (1382 tests pass). On the owner's instruction the
app was quit normally and `dist/EBMC.app` rebuilt with importance, roles and the follow-ups; it is running again
and waiting at the Members passphrase. A safety copy of the members folder as it was before the first version 2
save is beside it, named `Members-copy-before-version-2-2026-11-16`. Not yet seen: the owner's existing list
opening in the new build (the owner enters the passphrase). Still open: change the throwaway passphrase; the
fork-or-separate-app question; nothing is committed.

Status 2026-11-18 13:20: the owner's existing list opened in the rebuilt app (seen on screen: filter row, three
entries, one of them the invented test entry "Juniper Vale" left from the first on-screen check; the owner can
delete it). On the owner's instruction the members passphrase was changed in the app to one the owner chose, and
confirmed by Lock then Open with the new one. The throwaway passphrase no longer opens the current file; it still
opens the older copies (`.prev`, backups from before today, `Members-copy-before-version-2-2026-11-16`).
Owner asked to load a "MicroDuck" file into the app for nylon parts: no file with that name was found on this Mac
(home folder, Downloads, Desktop, Documents, iCloud Drive, app library); waiting for its location.
Remaining on-screen checks (Cmd+C/X blocking, restore from backup, light theme, star outline, Settings page and
Find a tool lock entries) were started in the scratch test copy and NOT done; the test copy was closed.

Status 2026-11-18 16:20 (failed NylonX print): small parts came loose mid-print. The slice used bed 60 C, nozzle
260 C, no brim. The 60 comes from the NylonX preset inside the installed Pulse profile itself (bed 60, nozzle 260),
which outranks the app's separate NylonX material file (75 C on garolite). With the owner's yes, three overrides
were set in the app's slice settings (user layer; profile backed up first as `.printer.bak-2026-11-18-1618`):
bed 75 C, nozzle 265 C, brim on with 8 loops. Not yet re-sliced or printed. Nozzle on the Pulse is 0.3 mm.
2026-11-18 16:25: on the owner's yes, the club profile's NylonX preset bed temperature is now 75 C in the repo
(profile file, generator, `EBMC.md`). The installed profile is unchanged apart from the three overrides above; a
fresh install or re-import picks up the new preset. Not committed.
2026-11-18 18:17: second NylonX print (bed 75, nozzle 265, no brim, 0.2 mm layers) held and finished; surface
grainy, side-hole tops ragged. On the owner's instruction, overrides set for the next print with dry NylonG:
layer height 0.15, bed 80 C, outside perimeter speed 35 mm/s (profile backed up as `.printer.bak-2026-11-18-1816`).
Material preset in the app is still NylonX. Not yet sliced or printed.
2026-11-18 18:57: on the owner's instruction, Z lift on retraction set from 0.4 to 0 in the app (override; profile
backed up as `.printer.bak-2026-11-18-1855`). Owner also asked to raise the default Z height by 0.1 mm: NOT done,
it is the firmware offset (M206 Z5.56 -> Z5.66, M500, home); shown to the owner, waiting for a yes and an idle printer.
2026-11-18 19:02: MISTAKE on the firmware Z offset. The owner asked to raise the nozzle 0.1 mm; I sent M206 Z5.66 +
M500 + G28 (read back Z5.66, firmware Z 215.66 after homing). That is the WRONG direction: on this printer (homes
to max) a larger M206 Z makes the nozzle go LOWER (2026-11-11: "nozzle too high" was fixed by raising 4.76 to 5.56).
The nozzle is now 0.1 mm LOWER than before. Correct value for +0.1 mm is M206 Z5.46 (then M500, G28); to undo only,
M206 Z5.56. The correction was not sent (blocked pending the owner's yes). DO NOT PRINT until this is fixed.
Rule: on this printer, to RAISE the nozzle, DECREASE M206 Z.
Also set on the owner's instruction: nozzle temperature 260 C for the NylonG spool (app, effective value 260).
