# EBMC Stop (OctoPrint plugin)

Adds one large red button, **STOP PRINT and HOME ALL AXES**, at the bottom of
OctoPrint's Temperature tab (under the graph and the temperature table), and a
nozzle height control in the Control tab (see "Nozzle height" below).

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

**STOP always wins.** It is accepted even while a nozzle height read or change
(below) is running: the nozzle sequence is told to stop, sends nothing further,
and reports exactly what was sent, what the printer acknowledged and whether the
new value was saved. STOP's own G-code is sent only after the nozzle sequence has
let go and the printer is idle, so the two never mix.

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

## Nozzle height (Control tab)

A small panel at the bottom of the Control tab, "Nozzle height above the bed".
The nozzle height is the firmware's home offset, `M206 Z`.

**The sign rule on this printer: to raise the nozzle, make the number smaller.**
Z homes upwards to Z max, so a larger `M206 Z` puts the nozzle lower, closer to
the bed. The panel works in words (Raise / Lower) so nobody has to remember this.
The rule is the `raise_decreases_m206` setting (default `true`). It is not in the
settings dialog, so a stray click cannot flip it; for a printer that works the
other way, set it in OctoPrint's `config.yaml` (with OctoPrint stopped):

```yaml
plugins:
  ebmc_stop:
    raise_decreases_m206: false
```

- **Read** sends `M503` and shows the stored value, plus the maximum heater
  temperatures the firmware reports (`M143` lines in the same reply). If the
  reply has no home offset line, it says so; it never guesses.
- **Raise nozzle / Lower nozzle**, with a step of 0.02, 0.05 or 0.1 mm. The
  panel reads the value first, then asks once, for example: "RAISE the nozzle by
  0.05 mm? The nozzle will sit 0.05 mm HIGHER above the bed. Stored value M206 Z
  will change from 5.66 to 5.61. The printer will save this and home."
- On Yes the plugin re-reads the value and refuses if the stored value or the
  new value is not what the dialog showed. It then sends `M206 Z<new>` (Z only;
  X and Y are left as they are), `M500` (save), `G28` (home) and `M503` (read
  back), one at a time: each is sent only after the printer has acknowledged the
  previous one, and only if the printer is still idle. If the printer stops
  answering, replies with an error (`error`, `!!`, `ALARM`, `halt`, for example
  Smoothieware's "ERROR: Homing cycle failed"), or a print is started meanwhile,
  nothing further is sent and the message says what was sent, acknowledged and
  saved. After an error the printer may be halted and needs attention.
- The result shows before, after and the read-back. If the read-back is not
  what was sent (to 0.01 mm), it says MISMATCH, stays on screen, and is logged:
  check before printing.

It works only when the printer is connected and idle: never while printing,
paused, cancelling or sending a file to the SD card, and never while another
read, change or STOP is in progress. STOP itself is never refused because of
the nozzle control (see "STOP always wins" above).
It needs OctoPrint's **Control** permission; read-only and logged-out users are
refused.

Limits (Settings > EBMC Stop):

- Steps: only 0.02, 0.05 and 0.1 mm (the `nozzle_steps_mm` setting, edited in
  `config.yaml`; each must be above 0 and at most 0.2 mm). Never more than 0.2 mm
  in one action.
- Invalid settings (a reference or window that is not a number, a step outside
  0 to 0.2 mm) mean changes are refused with a message; nothing falls back silently.
- **Reference M206 Z** and **Window**: every new value must stay within
  reference ± window (default window 0.3 mm). While no reference is entered,
  values can be read but **not changed**; the panel shows the value just read
  so an administrator can confirm it and type it in. This keeps a run of small
  steps from drifting far from a known good value.
- A negative value is refused.

**`M500` saves ALL the firmware's current settings** to `config-override` on the
printer's SD card, not just the home offset: also any value the last print's
G-code or the Terminal changed since power-on (for example accelerations `M204`,
jerk `M205`, speeds `M203`, filament diameter `M200 D`, retraction `M207`/`M208`,
PID `M301`). Change the nozzle height on a freshly reset or power-cycled printer.
Before anything is sent, the plugin writes the printer's full `M503` reply to
`nozzle_height.log`, so the earlier saved state can be put back by hand.

Every read and every change, including refused ones, is logged with time, user,
direction, step, before, after, read-back, what was sent and acknowledged, and
the outcome, to `octoprint.log` and to
`nozzle_height.log` in the plugin's data folder (on OctoPi:
`~/.octoprint/data/ebmc_stop/nozzle_height.log`).

The parser tests run without OctoPrint:

```
python3 -m unittest discover -s tests -v
```

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
- Nozzle height: simple API commands `nozzle_height` (read) and `nozzle_adjust`
  (`{"direction": "raise"|"lower", "step_mm": 0.05, "expected_before": 5.66}`),
  checked against `Permissions.CONTROL`. Replies are read through the
  `octoprint.comm.protocol.gcode.received` hook. The reply formats come from the
  Smoothieware source at the printer's build (see `nozzle.py`); the live
  printer's `M503` reply has not been captured yet.
- No extra dependencies, no update checks, no tracking.
