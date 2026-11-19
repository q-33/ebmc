# EBMC Brand (OctoPrint plugin)

Shows the East Bay Makers Club badge in OctoPrint's web interface where OctoPrint shows its
own logo, and gives the interface the colours of the club website (eastbaymakersclub.com).
It changes looks only: no printer commands, no API, no settings, and no sizes or spacing.
The name "OctoPrint", its About dialog, credits and licence texts are left as they are. It
works with or without the EBMC Stop plugin and can be removed on its own; disabling it
restores stock OctoPrint.

What changes:

- **Top bar:** the badge replaces the green tentacle beside the instance name, drawn in
  cream on the club's ink band.
- **Browser tab and home-screen icons:** the club's app icon (orange tile, cream badge).
  OctoPrint's "Colorize favicon" setting still tints it when a bar colour is chosen.
- **Temperature tab:** a faint badge replaces the octopus behind the graph.
- **Login page:** the badge sits above "Please log in", aligned with it (OctoPrint has no
  logo there), in the club colours.
- **First-run wizard, last page:** the badge replaces the tentacle picture.
- **Colours and shapes:** see "Colours and shapes" below.

## Colours and shapes

Taken from the club website's own CSS and used the way the site uses them:

| OctoPrint | Club look |
|---|---|
| Top bar | solid ink band, cream name, links and badge, an orange line along the bottom; always, whatever colour is chosen in Settings > Appearance |
| Page ground | paper `#F4EDE3` with the site's faint ink grid |
| Sidebar panels (Connection, State, Files, ...) | ink heading bars with cream text; warm-paper body; 2px ink outline; hard offset shadow |
| Tab content, dialogs, menus, popovers, notices | warm paper `#FBF6ED`, ink outlines, hard offset shadows, 2px corners |
| Plain buttons | warm paper, ink outline and text, small hard shadow; pressed: yellow |
| Primary buttons (Print, Upload, Save, confirm ticks, Log in), links, progress bars, focus rings, dropdown hover | blue `#0648D8` (hover deep blue `#0034A8`), cream text `#FFF7EA` |
| Highlights: selected file, active settings menu item and pills, table header rows | yellow `#FFDF69` with ink text |
| Active tab | an orange `#FF3D12` line on top; orange is used nowhere else except the top bar's line |
| Terminal, tooltips, labels, the webcam box | ink with cream text |
| Temperature graph | ink frame, soft-ink labels, faint ink grid; the lines keep OctoPrint's colours (hot end red/pink, bed blue/light blue); event marks in palette (cancel stays red) |
| Notices | success: green edge on a green tint; info: deep blue on a blue tint; warning: on a yellow tint |
| Danger (STOP, delete/cancel confirmations, error notices) | OctoPrint's own red, never recoloured (danger buttons only get the 2px corners and the hard shadow; error notice text is a darker red so it reads) |

Every colour is a CSS variable in `static/css/ebmc_palette.css`, generated from the same
palette as the desktop app's badge artwork. Colours that OctoPrint draws on canvases (the
temperature graph's frame and grid, the G-code viewer's grid) are set from those variables by
`static/js/ebmc_brand.js`. The site's typeface (Berkeley Mono) is a commercial font, so
OctoPrint keeps its own font. The G-code viewer's bed area stays white: OctoPrint's viewer
paints it white in code with no option to change it.

Not changed:
- The login page's browser-tab icon stays OctoPrint's: that page loads no plugin scripts.
- The recovery page and the separate "application key" approval page show no logo and are
  left as they are.
- The loading screen and the About dialog show no OctoPrint logo, so nothing changes there.

## Install on the printer's Raspberry Pi

Do this when no print is running.

1. Copy the `octoprint-ebmc-brand` folder to the Pi, for example into the home folder of
   the user that runs OctoPrint.
2. Install it into OctoPrint's own Python environment (on OctoPi, `~/oprint`):

   ```
   ~/oprint/bin/pip install ~/octoprint-ebmc-brand
   ```

3. Restart OctoPrint:

   ```
   sudo service octoprint restart
   ```

4. Reload the page in the browser, bypassing the cache (Ctrl+F5, or Cmd+Shift+R on a Mac).
   Browsers keep favicons a long time; if the tab still shows the tentacle, close the
   tab and open it again.

## Uninstall

Either use Settings > Plugin Manager (find "EBMC Brand", click the bin icon, then restart
OctoPrint when asked), or on the Pi:

```
~/oprint/bin/pip uninstall OctoPrint-EbmcBrand
sudo service octoprint restart
```

Then reload the page in the browser, bypassing the cache.

## The images and colours

All images in `octoprint_ebmc_brand/static/img/` and `static/css/ebmc_palette.css` are generated from the club badge
(`Tools/EBMC/assets/ebmc-badge-source.png`), traced the same way as the MatterControl app's
icons, so the marks match, and the palette is the club's. The script also prints the contrast
ratio of every colour pairing the stylesheets use. Do not edit them by hand; from the
repository root run:

```
python3 Tools/EBMC/generate_octoprint_brand.py .
```

It needs the Python packages listed at the top of that script.

The EBMC badge belongs to the East Bay Makers Club and is used here with the club's permission.
It is not covered by the software licence of this repository or of this plugin's code; that
applies to the badge image, the files generated from it and the badge meshes.

## How it works (for maintainers)

- Top bar: `static/css/ebmc_brand.css` hides OctoPrint's background image on
  `#navbar .navbar-inner .brand span` and draws `img/badge.svg` as a CSS mask in
  `currentColor` on `::before`, at OctoPrint's 20 x 20 px.
- Graph: the same stylesheet replaces the background image of `#temperature-graph`.
- Colours and shapes: `static/css/ebmc_brand.css` restyles Bootstrap 2 and OctoPrint
  selectors (`body`, `a`, `.btn`, `.btn-primary`, `.nav-tabs`, `.nav-pills`, `.nav-list`,
  `.accordion-group`/`.accordion-heading`, `.table`, `.well`, `pre`, `.progress .bar`,
  `.dropdown-menu`, `.modal`, `.alert-*` which PNotify uses, `.label`/`.badge`,
  `.tooltip`/`.popover`, `.slider`, form fields, `#navbar .navbar-inner` with every
  Appearance colour, `#files .gcode_files .entry`, `#term`, `#webcam_img_container`,
  `.temperature-mark-*`). `.btn-danger` keeps its colours.
- Canvas colours: `static/js/ebmc_brand.js` adds the palette's grid colours to the
  temperature graph's flot options and calls the G-code viewer's public
  `GCODE.renderer.setOption({colorGrid})`.
- Icons: `static/js/ebmc_brand.js` points the `<link>` elements in the page head
  (`shortcut icon`, `mask-icon`, `mask-icon-theme`, `apple-touch-icon`, `manifest`) at this
  plugin's files when the plugin bundle loads. OctoPrint has no supported hook for these
  links. Its appearance view model then builds the favicon from `mask-icon-theme`.
- Login page: the `octoprint.theming.login` hook adds `static/css/ebmc_palette.css` and
  `static/css/ebmc_brand_login.css`.
