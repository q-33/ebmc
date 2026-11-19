# EBMC Stop (OctoPrint plugin)

Adds one large red button, **STOP PRINT and HOME ALL AXES**, at the bottom of
OctoPrint's Temperature tab (under the graph and the temperature table).

What happens when you press it:

1. A dialog asks you to confirm. Nothing happens if you say No.
2. If a print is running or paused, OctoPrint cancels it the normal way.
   OctoPrint then sends its own "After print job is cancelled" script
   (Settings > GCODE Scripts).
3. The plugin waits until the printer is idle again (connected, not printing,
   not paused, not cancelling). That only happens after OctoPrint's cancel
   script has been sent, so the two never mix. The browser shows "Stopping..."
   while it waits.
4. The heaters are switched off (`M104 S0`, `M140 S0`), unless you turned that
   off in the settings.
5. The "after stop" G-code is sent and the browser shows "Homing". The default
   is just:

   ```
   G28
   ```

If the printer is not idle within 60 seconds, **nothing** is sent, the printer
is not homed, and the browser shows an error that stays until you close it.

The request is refused (nothing sent) when the printer is not connected, or is
doing something other than printing, being paused, cancelling or sitting idle
(for example uploading a file to the printer's SD card). Only users who are
allowed to cancel prints (OctoPrint's "Print" permission) can use it; read-only
and logged-out users see it greyed out. Pressing it again while a stop is in
progress is refused.

## Why the default has no "lift Z first"

On this printer (Smoothieware, `gamma_homing_direction home_to_max`,
`homing_order ZYX`) homing moves Z **up** to the top first, away from the part,
before X and Y move. So `G28` alone already lifts the nozzle clear. An extra
relative lift (`G91` / `G1 Z10` / `G90`) is not needed, and near the top of
travel it would drive into the end stop (there is no software Z maximum).

**If you use this plugin on a printer that homes Z downwards onto the bed, do
not use this default:** plain `G28` there can drive the nozzle into the part.
Use something like `G91`, `G1 Z10 F600`, `G90`, `G28 X Y` instead.

## Settings

OctoPrint Settings > EBMC Stop:

- **Switch heaters off first**: on by default.
- **After-stop G-code**: one command per line. Lines starting with `;` are
  ignored, and each line is upper-cased (except the text of `M117`/`M118`
  messages), because Smoothieware ignores lower-case parameter letters. Lines that do not start with
  G, M or T followed by a number (for example `G28`, `M104`, `T0`) are skipped
  and logged, because Smoothieware would run them as console commands.

## Install on the printer's Raspberry Pi

1. Copy the `octoprint-ebmc-stop` folder to the Pi, for example into the home
   folder of the user that runs OctoPrint.
2. Log in to the Pi (SSH) and install it into OctoPrint's own Python environment.
   On OctoPi that environment is `~/oprint`:

   ```
   ~/oprint/bin/pip install ~/octoprint-ebmc-stop
   ```

3. Restart OctoPrint:

   ```
   sudo service octoprint restart
   ```

4. Reload the OctoPrint page in the browser (Ctrl+F5 / Cmd+Shift+R). The red
   button is at the bottom of the Temperature tab, and "EBMC Stop" appears in
   Settings and in Settings > Plugin Manager.

## Uninstall

Either use Settings > Plugin Manager in OctoPrint (find "EBMC Stop", click the
bin icon, then restart OctoPrint when asked), or on the Pi:

```
~/oprint/bin/pip uninstall OctoPrint-EbmcStop
sudo service octoprint restart
```

Then reload the page in the browser.

## How it works (for maintainers)

- Button: a `generic` template that the plugin's view model moves into the
  Temperature tab (`#temp`) after OctoPrint has bound all view models.
- Server: `SimpleApiPlugin` command `stop` at `POST /api/plugin/ebmc_stop`,
  protected (`is_api_protected`) and checked against `Permissions.PRINT`.
  Cancel uses `printer.cancel_print()`. The plugin does not send on the
  `PrintCancelled` event, because OctoPrint fires that event before it queues
  its cancel script. It waits for the printer state to be `OPERATIONAL` and not
  printing, paused or cancelling. A `PrinterStateChanged` event wakes it early,
  and it re-checks every 0.5 s for up to 60 s. Progress and errors reach the
  browser as plugin messages.
- No extra dependencies, no update checks, no tracking.
