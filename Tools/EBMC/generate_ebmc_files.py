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
})
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
    'Name': 'East Bay Makers Club', 'DefaultColorIndex': 0,
    'AccentColors': [BLUE, BLUE_DEEP, ORANGE, ORANGE_DEEP, GREEN, SOFT_INK],
    'ThemeID': 'EBMC-Paper', 'MenuThemeID': 'EBMC-Paper-Menu'})

# ---------- product settings ----------
s = J('StaticData/OEMSettings/Settings.json')
s.update({'ShowShopButton': False, 'UnregisteredProductName': 'MatterControl EBMC Edition',
          'RegisteredProductName': 'MatterControl EBMC Edition', 'WindowTitleExtra': 'East Bay Makers Club'})
W('StaticData/OEMSettings/Settings.json', s)
print('ok')
