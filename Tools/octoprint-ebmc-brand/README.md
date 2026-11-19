# EBMC Brand (OctoPrint plugin)

Shows the East Bay Makers Club badge in OctoPrint's web interface where OctoPrint shows its
own logo, and gives the interface the colours of the club website (eastbaymakersclub.com).
It changes looks only: no printer commands, no API, no settings, and no sizes or spacing.
The name "OctoPrint", its About dialog, credits and licence texts are left as they are. It
works with or without the EBMC Stop plugin and can be removed on its own; disabling it
restores stock OctoPrint.

What changes:

- **Top bar:** the badge replaces the green tentacle beside the instance name. It is drawn
  in the top bar's own text colour. With Settings > Appearance colour "default" the bar
  looks like the club site's header (paper, ink badge and name, soft-ink links that turn
  blue). If the owner picks another Appearance colour, OctoPrint's coloured bar is used
  instead and the badge follows its text colour (dark on light bars, light on dark).
- **Browser tab and home-screen icons:** the club's app icon (orange tile, cream badge).
  OctoPrint's "Colorize favicon" setting still tints it when a bar colour is chosen.
- **Temperature tab:** a faint badge replaces the octopus behind the graph.
- **Login page:** the badge sits above "Please log in", aligned with it (OctoPrint has no
  logo there), in the club colours.
- **First-run wizard, last page:** the badge replaces the tentacle picture.
- **Colours:** see "Colours" below.

## Colours

Taken from the club website's own CSS and used the way the site uses them:

| OctoPrint | Club colour |
|---|---|
| Page ground | paper `#F4EDE3` |
| Text | ink `#050505` |
| Panels, tabs' content, dialogs, menus, default buttons | warm paper `#FBF6ED`, borders ink at 24 % |
| Links, primary buttons (Print, Upload, Save, Log in), progress bars, focus rings, selected menu items | blue `#0648D8` (hover deep blue `#0034A8`), cream text `#FFF7EA` |
| Active tab | a thin orange `#FF3D12` line on top (the site's orange marker), nothing else orange |
| Secondary text, placeholders | muted `#71685E` |
| Notices | success: green edge on a green tint; info: deep blue on a blue tint; warning: on a yellow tint |
| Danger (STOP, delete/cancel confirmations, error notices) | OctoPrint's own red, unchanged (error notice text a darker red so it reads) |

Every colour is a CSS variable in `static/css/ebmc_palette.css`, generated from the same
palette as the desktop app's badge artwork. The site's typeface (Berkeley Mono) is a
commercial font, so OctoPrint keeps its own font. The webcam view, the G-code viewer's
drawing area and its layer slider keep OctoPrint's look.

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
- Colours: `static/css/ebmc_brand.css` recolours Bootstrap 2 and OctoPrint selectors
  (`body`, `a`, `.btn`, `.btn-primary`, `.nav-tabs`, `.nav-pills`, `.accordion-group`,
  `.table`, `.well`, `pre`, `.progress .bar`, `.dropdown-menu`, `.nav-list`, `.modal`,
  `.alert-*` which PNotify uses, form focus, `#navbar .navbar-inner.default`,
  `#files .gcode_files .entry`) with colours only. `.btn-danger` is never touched.
- Icons: `static/js/ebmc_brand.js` points the `<link>` elements in the page head
  (`shortcut icon`, `mask-icon`, `mask-icon-theme`, `apple-touch-icon`, `manifest`) at this
  plugin's files when the plugin bundle loads. OctoPrint has no supported hook for these
  links. Its appearance view model then builds the favicon from `mask-icon-theme`.
- Login page: the `octoprint.theming.login` hook adds `static/css/ebmc_palette.css` and
  `static/css/ebmc_brand_login.css`.
