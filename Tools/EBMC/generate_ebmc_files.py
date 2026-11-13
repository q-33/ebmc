import json, os, sys, copy
root = sys.argv[1]
J = lambda p: json.load(open(os.path.join(root, p), encoding='utf-8-sig'))
def W(p, d):
    full = os.path.join(root, p); os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, 'w', encoding='utf-8', newline='\n').write(json.dumps(d, indent=2) + '\n')

# ---------- club printer profile ----------
MAKE, MODEL = 'East Bay Makers Club', 'Pulse S-232 Mosquito'
p = J('StaticData/Profiles/Pulse/S-232.printer')
p['ID'] = 'EBMC-Pulse-S-232-Mosquito'
o = p['OemLayer']
o.update({
    'make': MAKE, 'model': MODEL,
    'nozzle_diameter': '0.3',
    'layer_height': '0.15', 'first_layer_height': '0.2',
    'first_layer_extrusion_width': '120%',
    # Speeds doubled on 2026-11-12 after the first good NylonX prints (owner approved). First layer stays
    # at 20 for adhesion; travel is 250, under the firmware's 300 mm/s X/Y limit.
    'first_layer_speed': '20', 'travel_speed': '250',
    'infill_speed': '70', 'perimeter_speed': '70',
    'external_perimeter_speed': '70', 'top_solid_infill_speed': '70',
    'support_material_speed': '80', 'raft_print_speed': '80', 'bridge_speed': '40',
    'retract_length': '2', 'retract_before_travel_avoid': '5',
    'retract_lift': '0.4', 'retract_restart_extra': '0',
    'skirts': '4', 'infill_overlap_perimeter': '20%',
    # Levelling is done by the printer's firmware (Smoothieware grid, G32) so it works when the
    # printer is driven by OctoPrint. Approved by the owner 2026-11-11; not yet bench-tested.
    'print_leveling_required_to_print': '0',
    'validate_leveling': '0',
    'has_hardware_leveling': '1',
    # The club printer is driven by OctoPrint on a Raspberry Pi; jobs are uploaded with Send.
    # The API key is not stored here: each computer gets its own with Get Key in printer settings.
    'octoprint_enabled': '1',
    'octoprint_host': 'http://OctoprintPulseXE.local',
})
# Probe the bed after homing: deploy the BLTouch, run the firmware grid probe, stow it.
# G32 X23 Y15 A180 B180: the firmware is in two-corner grid mode (probe is 23 mm right of the nozzle), so
# the grid start (nozzle X23 Y15) and size (180 x 180) are given on the command.
# S3.3 / S7.43 are the values that move the pin on this printer (checked 2026-11-11; S3.0 from the
# firmware config does not deploy it).
_home = 'G28 ; home all axes\\n'
assert o['start_gcode'].startswith(_home), 'stock start_gcode changed; review the levelling lines'
o['start_gcode'] = (_home
                    + 'M280 S3.3 ; deploy probe\\n'
                    + 'G32 X23 Y15 A180 B180 ; probe bed grid (firmware)\\n'
                    + 'M280 S7.43 ; stow probe\\n'
                    + o['start_gcode'][len(_home):])
heights = {'Coarse': '0.2', 'Standard': '0.15', 'Fine': '0.1'}
for q in p['QualityLayers']:
    q['layer_height'] = heights[q['layer_name']]
nylonx = [m for m in p['MaterialLayers'] if m['layer_name'] == 'NylonX'][0]
nylonx.update({'temperature': '265', 'bed_temperature': '60', 'min_fan_speed': '0', 'max_fan_speed': '0',
               'bridge_fan_speed': '100', 'first_layer_extrusion_width': '110%',
               'retract_restart_extra': '0', 'retract_restart_extra_time_to_apply': '0'})
o['active_material_key'] = nylonx['layer_id']
for k in ('retract_restart_extra', 'retract_restart_extra_time_to_apply'):
    p['StagedUserSettings'].pop(k, None)
W(f'StaticData/Profiles/{MAKE}/{MODEL}.printer', p)

idx = J('StaticData/Profiles/oemprofiles.json')
idx[MAKE] = {MODEL: {'ShortProfileID': 'EBMC-Pulse-S-232-Mosquito', 'ProfileToken': '', 'DeviceToken': ''}}
open(os.path.join(root,'StaticData/Profiles/oemprofiles.json'),'w',encoding='utf-8',newline='').write(json.dumps(idx))  # keep upstream single-line format

# ---------- club theme (palette read from eastbaymakersclub.com CSS variables) ----------
BLUE, BLUE_DEEP, ORANGE, ORANGE_DEEP = '#0648D8', '#0034A8', '#FF3D12', '#D42609'
GREEN, YELLOW, INK, SOFT_INK, MUTED = '#087F5B', '#FFDF69', '#050505', '#38322C', '#71685E'
PAPER, PAPER_WARM, CREAM = '#F4EDE3', '#FBF6ED', '#FFF7EA'
LINE, LINE_SOFT = '#D9CFBF', '#E6DDCE'
def field(bg, border, text=INK, light=None):
    d = {'BackgroundColor': bg, 'ForegroundColor': '#00000000', 'BorderColor': border, 'TextColor': text}
    if light: d['LightTextColor'] = light
    return d
def recolor(t, bg, tabbar, inactive):
    t = copy.deepcopy(t)
    t.update({
        'TextColor': INK,
        'PresetColors': {'MaterialPreset': ORANGE, 'QualityPreset': YELLOW, 'UserOverride': BLUE + '96'},
        'IsDarkTheme': False,
        'BackgroundColor': bg, 'TabBarBackground': tabbar, 'InactiveTabColor': inactive,
        'InteractionLayerOverlayColor': bg + 'F0', 'SplitterBackground': LINE,
        'AccentMimimalOverlay': BLUE + '50', 'PrimaryAccentColor': BLUE,
        'BedColor': PAPER, 'BedBackgroundColor': LINE_SOFT, 'PrinterBedTextColor': MUTED,
        'BorderColor': SOFT_INK, 'BorderColor40': MUTED, 'BorderColor20': LINE,
        'PopupBorderColor': LINE, 'RowBorder': LINE_SOFT,
        'DisabledColor': MUTED, 'SplashAccentColor': PAPER, 'SectionBackgroundColor': PAPER,
    })
    t['EditFieldColors'] = {
        'Focused': field(CREAM, ORANGE, light=MUTED),
        'Hovered': field(CREAM, ORANGE, text='#00000000'),
        'Inactive': field(CREAM, LINE, light=MUTED)}
    t['DropList'] = {
        'Open': field(LINE, '#00000000'), 'Focused': field(LINE_SOFT, ORANGE),
        'Hovered': field(LINE_SOFT, ORANGE), 'Inactive': field(CREAM, LINE)}
    if 'BedGridColors' in t: t['BedGridColors']['Line'] = LINE
    return t
W('StaticData/Themes/System/EBMC/EBMC-Paper.json',
  recolor(J('StaticData/Themes/System/Light/Light-White.json'), PAPER_WARM, PAPER, LINE_SOFT))
W('StaticData/Themes/System/EBMC/EBMC-Paper-Menu.json',
  recolor(J('StaticData/Themes/System/Solarized/Solarized-Light-Menu.json'), PAPER, LINE, LINE_SOFT))
W('StaticData/Themes/System/EBMC/EBMC-Paper.themeset', {
    'Name': 'East Bay Makers Club Light', 'DefaultColorIndex': 0,
    'AccentColors': [BLUE, BLUE_DEEP, ORANGE, ORANGE_DEEP, GREEN, SOFT_INK],
    'ThemeID': 'EBMC-Paper', 'MenuThemeID': 'EBMC-Paper-Menu'})

# ---------- club dark theme (the default): the same palette on the site's ink colours ----------
D_BG, D_TAB, D_RAISED, D_LINE = '#1F1C19', '#151311', '#2B2723', '#3D3731'
D_TEXT, D_MUTED = '#EDE6DA', '#9A9186'
D_FIELD = '#262320'
# a focused field is a little lighter than an idle one (and than a hovered one, #2B2723), so focus is not mistaken for hover
D_FIELD_FOCUS = '#2E2A26'
# Disabled text: 3.6:1 on the workspace and 3.1:1 on menus (#5A534B was 2.2:1), still well below the
# muted text (#9A9186, 5.5:1) so it reads as disabled. Used for disabled text, borders and palette glyphs.
D_DISABLED = '#7A7268'
SKY, MINT = '#6C9BFF', '#35B98B'   # the club blue and green, lightened to stay readable on a dark ground
def dfield(bg, border, text=D_TEXT, light=None):
    d = {'BackgroundColor': bg, 'ForegroundColor': '#00000000', 'BorderColor': border, 'TextColor': text}
    if light: d['LightTextColor'] = light
    return d
def recolor_dark(t):
    t = copy.deepcopy(t)
    t.update({
        'TextColor': D_TEXT,
        'PresetColors': {'MaterialPreset': ORANGE, 'QualityPreset': YELLOW, 'UserOverride': SKY + '96'},
        'IsDarkTheme': True,
        'BackgroundColor': D_BG, 'TabBarBackground': D_TAB, 'InactiveTabColor': D_RAISED,
        'InteractionLayerOverlayColor': D_BG + 'F0', 'SplitterBackground': D_TAB,
        'AccentMimimalOverlay': ORANGE + '50', 'PrimaryAccentColor': ORANGE,
        'BedColor': '#625B53', 'BedBackgroundColor': '#262320', 'PrinterBedTextColor': '#161412',
        'BorderColor': D_TEXT, 'BorderColor40': D_LINE, 'BorderColor20': D_LINE,
        'PopupBorderColor': D_TAB, 'RowBorder': D_LINE,
        'DisabledColor': D_DISABLED, 'SplashAccentColor': D_TEXT, 'SectionBackgroundColor': D_RAISED,
        # read by ThemeConfig (settable); used by buttons, menu buttons and the printer bar (stock 3)
        'ButtonRadius': 6,
    })
    # a focused field stays dark (stock: cream #FFF7EA), marked by the accent border
    t['EditFieldColors'] = {
        'Focused': dfield(D_FIELD_FOCUS, ORANGE, light=D_MUTED),
        'Hovered': dfield(D_RAISED, ORANGE, text='#FFFFFF'),
        'Inactive': dfield(D_FIELD, D_TAB, light=D_MUTED)}
    t['DropList'] = {
        'Open': dfield(D_TAB, '#00000000'), 'Focused': dfield('#00000000', '#00000000', text='#00000000'),
        'Hovered': dfield(D_RAISED, ORANGE, text='#FFFFFF'), 'Inactive': dfield(D_FIELD, D_TAB)}
    if 'BedGridColors' in t: t['BedGridColors']['Line'] = '#3F3932'
    # inherited from Modern-Dark but not a ThemeConfig property (ignored when the theme loads)
    t.pop('HotendLimitsBackgroundColor', None)
    return t
W('StaticData/Themes/System/EBMC/EBMC-Dark.json',
  recolor_dark(J('StaticData/Themes/System/Modern/Modern-Dark.json')))
# Menu theme (popup menus and popovers such as the temperature and render-option panels). Every key the stock
# file carries is set to a dark club value; the stock greys (TabBar #B1B1B1, Disabled #FBFBFB, Border #C8C8C8,
# focused field #CCCCCC) made disabled items look enabled and separators (BorderColor20) invisible.
menu = J('StaticData/Themes/System/Modern/Modern-Dark-Menu.json')
menu.update({'TextColor': D_TEXT, 'BackgroundColor': D_RAISED,
             'PresetColors': {'MaterialPreset': ORANGE, 'QualityPreset': YELLOW, 'UserOverride': SKY + '96'},
             'TabBarBackground': D_TAB, 'InactiveTabColor': D_BG,
             'InteractionLayerOverlayColor': D_RAISED + 'F0', 'SplitterBackground': D_LINE,
             # replaced at start-up by the accent colour (ThemeSet.SetAccentColor)
             'AccentMimimalOverlay': ORANGE + '50', 'PrimaryAccentColor': ORANGE,
             'LightTextColor': D_MUTED,
             # BorderColor20 is the menu separator line
             'BorderColor': D_LINE, 'BorderColor40': D_LINE, 'BorderColor20': D_LINE,
             'PopupBorderColor': D_TAB, 'RowBorder': D_LINE,
             'DisabledColor': D_DISABLED, 'SplashAccentColor': D_TEXT,
             'SectionBackgroundColor': D_BG, 'BedBackgroundColor': D_BG,
             'ButtonRadius': 6})
menu['EditFieldColors'] = {
    'Focused': dfield(D_FIELD_FOCUS, ORANGE, light=D_MUTED),
    'Hovered': dfield(D_FIELD, ORANGE, light=D_MUTED),
    'Inactive': dfield(D_FIELD, D_LINE, light=D_MUTED)}
menu['DropList'] = {
    'Open': dfield(D_TAB, '#00000000'), 'Focused': dfield(D_FIELD, ORANGE),
    'Hovered': dfield(D_FIELD, ORANGE), 'Inactive': dfield(D_FIELD, D_LINE)}
W('StaticData/Themes/System/EBMC/EBMC-Dark-Menu.json', menu)
W('StaticData/Themes/System/EBMC/EBMC-Dark.themeset', {
    'Name': 'East Bay Makers Club Dark', 'DefaultColorIndex': 0,
    'AccentColors': [ORANGE, YELLOW, SKY, MINT, ORANGE_DEEP, '#C9BFB0'],
    'ThemeID': 'EBMC-Dark', 'MenuThemeID': 'EBMC-Dark-Menu'})

# ---------- product settings ----------
s = J('StaticData/OEMSettings/Settings.json')
s.update({'ShowShopButton': False, 'UnregisteredProductName': 'EBMC',
          'RegisteredProductName': 'EBMC', 'WindowTitleExtra': 'East Bay Makers Club',
          # Printer setup offers only these makes (the club's own printer)
          'PrinterWhiteList': [MAKE],
          # No Upgrade/Store tabs, update check, sign-in, Community links or filament shop button
          'ShowVendorServices': False,
          # The interface tour stays in the main menu but no longer opens at every start
          'ShowTourOnStartup': False,
          # No first-run agreement page (MatterHackers' EULA for their own builds)
          'ShowLicenseAgreement': False,
          # Printer and material settings need the club maintainer PIN; per-print settings stay open
          'RequireMaintainerForPrinterSettings': True,
          # Ask "Ready to print?" (bed empty, material loaded, temperatures) before every print started via OctoPrint
          'ConfirmBedBeforeRemotePrint': True,
          # The shapes and tools palette replaces the stock shapes rail (stock: 'classic')
          'ShapesPaletteStyle': 'palette',
          # One theme: the club's dark theme with its default accent colour; no theme or colour pickers
          'LockedThemeSet': 'EBMC-Dark',
          # New QR Code parts point at the club's website (stock: MatterHackers' site)
          'QrCodeDefaultText': 'https://eastbaymakersclub.com',
          # Printer tabs show the palette's print-oriented buttons (add model, test shapes, calibration, prepare)
          'ShowShapesRailOnPrinterTabs': True,
          # Printer tabs keep only the toolbar tools printing needs (design tabs keep every tool)
          'TrimPrinterTabToolbar': True,
          # Offer to scale a model file that is tiny (made in inches?) or bigger than the bed when it is added
          'OfferModelSizeFix': True,
          # Exact solids (Open CASCADE kernel) on design tabs, STEP open/export; needs the kernel library in the app
          'EnableSolidModeling': True,
          # Interface typeface: Nunito (StaticData/Fonts/Nunito_*.ttf, SIL OFL) instead of Liberation Sans;
          # empty keeps the stock font. Text parts (3D text objects) keep their own font setting
          'DefaultUiFont': 'Nunito',
          # The "Simple · Advanced" switch in the top bar; a new user starts in Simple. A mode only changes which
          # tools, panels and settings rows are shown, never a setting value or how a print is sliced or sent
          'ShowUiModeSwitch': True,
          'DefaultUiMode': 'Simple',
          # Club shapes (Rounded Box, Tube, Polygon Prism, Star, Heart, Slot Plate, Gear) and the Club Starters
          # (fill-in designs) on design tabs; ordinary parts whose output is a normal mesh
          'EnableClubShapes': True,
          # A new design tab opens empty with a "Start a design" card; the club badge becomes one of the starters
          'NewDesignStartsEmpty': True,
          # Advanced-mode design tools: command search (Cmd/Ctrl + K), typed Transform fields, Mirror Copy and the
          # keyboard shortcuts sheet. They only change the design through the undo buffer; nothing prints or slices
          'EnableAdvancedTools': True,
          # Club dialog template (verb buttons, Enter never picks a destructive one) and menu style (sections, shortcut
          # hints, danger items last), and toasts for short notices. Same actions and gating as the stock menus
          'ClubDialogsAndMenus': True,
          # No MatterHackers affiliate/referral code added to links
          'AffiliateCode': ''})
# Drop upstream's branded sample parts from the preloaded list (no C# code reads this key;
# Phil A Ment.stl and MH Logo.stl are also deleted from SampleParts)
s['PreloadedLibraryFiles'] = [f for f in s.get('PreloadedLibraryFiles', []) if f not in (
    'MatterControl - Coin.stl', 'MatterControl - Stand.stl', 'Phil A Ment.stl', 'MH Logo.stl')]
W('StaticData/OEMSettings/Settings.json', s)
print('ok')
