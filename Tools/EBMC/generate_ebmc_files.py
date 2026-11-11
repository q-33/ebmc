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
    'first_layer_speed': '20', 'travel_speed': '150',
    'infill_speed': '35', 'perimeter_speed': '35',
    'external_perimeter_speed': '35', 'top_solid_infill_speed': '35',
    'retract_length': '2', 'retract_before_travel_avoid': '5',
    'retract_lift': '0.4', 'retract_restart_extra': '0',
    'skirts': '4', 'infill_overlap_perimeter': '20%',
    # Levelling is done by the printer's firmware (Smoothieware grid, G32) so it works when the
    # printer is driven by OctoPrint. Approved by the owner 2026-11-11; not yet bench-tested.
    'print_leveling_required_to_print': '0',
    'validate_leveling': '0',
    'has_hardware_leveling': '1',
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
nylonx.update({'temperature': '255', 'bed_temperature': '60', 'min_fan_speed': '0', 'max_fan_speed': '0',
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
        'DisabledColor': '#5A534B', 'SplashAccentColor': D_TEXT, 'SectionBackgroundColor': D_RAISED,
    })
    t['EditFieldColors'] = {
        'Focused': field(CREAM, ORANGE, light=MUTED),
        'Hovered': dfield(D_RAISED, ORANGE, text='#FFFFFF'),
        'Inactive': dfield('#262320', D_TAB, light=D_MUTED)}
    t['DropList'] = {
        'Open': dfield(D_TAB, '#00000000'), 'Focused': dfield('#00000000', '#00000000', text='#00000000'),
        'Hovered': dfield(D_RAISED, ORANGE, text='#FFFFFF'), 'Inactive': dfield('#262320', D_TAB)}
    if 'BedGridColors' in t: t['BedGridColors']['Line'] = '#3F3932'
    return t
W('StaticData/Themes/System/EBMC/EBMC-Dark.json',
  recolor_dark(J('StaticData/Themes/System/Modern/Modern-Dark.json')))
menu = J('StaticData/Themes/System/Modern/Modern-Dark-Menu.json')
menu.update({'TextColor': D_TEXT, 'BackgroundColor': D_RAISED, 'PopupBorderColor': D_TAB,
             'RowBorder': D_LINE, 'SectionBackgroundColor': D_BG, 'BedBackgroundColor': D_BG})
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
          # No MatterHackers affiliate/referral code added to links
          'AffiliateCode': ''})
# Drop upstream's branded sample parts from the preloaded list (no C# code reads this key;
# Phil A Ment.stl and MH Logo.stl are also deleted from SampleParts)
s['PreloadedLibraryFiles'] = [f for f in s.get('PreloadedLibraryFiles', []) if f not in (
    'MatterControl - Coin.stl', 'MatterControl - Stand.stl', 'Phil A Ment.stl', 'MH Logo.stl')]
W('StaticData/OEMSettings/Settings.json', s)
print('ok')
