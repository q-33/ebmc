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
- [ ] "MC" logo still on the Variable Sheet primitive (StaticData/Stls/sheet_icon.mcx + 2 thumbnails)
- [ ] Non-English translations spell the old name differently (58 strings) and are not rebranded
- [ ] Stale GUI automation tests reference removed sample parts (MatterControlUtilities.cs:576, CameraFittingUtilTests, SceneUndoRedoCopyTests)
- [ ] Two sources for the product name (Program.AppName vs Settings.json); PrintHistoryListItem.cs:403 edit has no effect
- [ ] build-app.sh stale comment (line 67); generate_brand_assets.py not executable; unused upstream logo SVGs still shipped
- [ ] Check in the app: slice-setting help text ("Recover After Failure") shows EBMC, not MatterControl
