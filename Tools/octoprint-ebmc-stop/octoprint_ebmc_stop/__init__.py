"""EBMC Stop: one button that stops the current print and homes the printer.

The button lives under the Temperature tab. Pressing it (after a confirmation
dialog in the browser) calls this plugin's simple API with the command "stop".

Server side:
  * the printer must be connected and either idle, printing, paused or already
    cancelling; anything else (e.g. a file upload to the printer's SD card) is
    refused with 409 and nothing is sent;
  * a printing or paused job is cancelled with OctoPrint's own cancel;
  * the plugin then waits until the printer is truly idle again (state
    OPERATIONAL, not printing, paused or cancelling). OctoPrint fires
    PrintCancelled *before* it queues its own "after print job is cancelled"
    script and only returns to OPERATIONAL after that script has been sent, so
    waiting for the idle state (not the event) keeps the two from interleaving;
  * only then are the heaters switched off (optional) and the "after stop"
    G-code sent. If the printer does not become idle in time, nothing is sent
    and the browser is told.
"""

import re
import threading
import time

import flask

import octoprint.plugin
from octoprint.access.permissions import Permissions
from octoprint.events import Events

IDLE_TIMEOUT_S = 60.0
POLL_S = 0.5
HEATERS_OFF = ["M104 S0", "M140 S0"]
DEFAULT_AFTER_STOP = "G28"
# G, M or T followed by a number (e.g. G28, M104, T0, G28.2). This rejects
# Smoothieware console commands such as "get pos" or "home".
COMMAND_WORD = re.compile(r"^[GMT]\d+(\.\d+)?$")
# Commands whose argument is free text; their case is kept.
MESSAGE_COMMANDS = ("M117", "M118")


def parse_gcode_lines(text):
    """Return (accepted, rejected) lines from the settings text.

    Blank lines and ";" comments are dropped. Lines are upper-cased
    (except the text of M117/M118).
    Lines that do not start with G, M or T are rejected: Smoothieware would
    treat them as console commands.
    """
    accepted, rejected = [], []
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith(";"):
            continue
        parts = line.split(None, 1)
        parts[0] = parts[0].upper()
        if len(parts) > 1 and parts[0] not in MESSAGE_COMMANDS:
            # Parameter letters too: "g28 x y" must not become "G28 x y".
            parts[1] = parts[1].upper()
        line = " ".join(parts)
        if COMMAND_WORD.match(parts[0]):
            accepted.append(line)
        else:
            rejected.append(raw.strip())
    return accepted, rejected


class EbmcStopPlugin(
    octoprint.plugin.SettingsPlugin,
    octoprint.plugin.AssetPlugin,
    octoprint.plugin.TemplatePlugin,
    octoprint.plugin.SimpleApiPlugin,
    octoprint.plugin.EventHandlerPlugin,
):
    def __init__(self):
        self._busy_lock = threading.Lock()
        self._busy = False
        self._state_changed = threading.Event()
        self._transferring = False

    # ~~ SettingsPlugin

    def get_settings_defaults(self):
        return {
            "heaters_off": True,
            "after_stop_gcode": DEFAULT_AFTER_STOP,
        }

    # ~~ AssetPlugin

    def get_assets(self):
        return {"js": ["js/ebmc_stop.js"], "css": ["css/ebmc_stop.css"]}

    # ~~ TemplatePlugin

    def get_template_configs(self):
        return [
            {"type": "generic", "template": "ebmc_stop.jinja2"},
            {"type": "settings", "name": "EBMC Stop", "custom_bindings": False},
        ]

    def is_template_autoescaped(self):
        return True

    # ~~ EventHandlerPlugin

    def on_event(self, event, payload):
        # Only a wake-up for the waiting thread; the printer state is what counts.
        if event == Events.PRINTER_STATE_CHANGED:
            self._state_changed.set()
        elif event == Events.TRANSFER_STARTED:
            self._transferring = True
        elif event in (Events.TRANSFER_DONE, Events.TRANSFER_FAILED):
            self._transferring = False

    # ~~ SimpleApiPlugin

    def is_api_protected(self):
        return True

    def get_api_commands(self):
        return {"stop": []}

    def on_api_command(self, command, data):
        if command != "stop":
            return flask.abort(400, description="Unknown command")

        # Same permission OctoPrint's own job API requires for "cancel".
        if not Permissions.PRINT.can():
            return flask.abort(403, description="Insufficient permissions")

        if not self._printer.is_operational():
            self._logger.warning("Stop refused: the printer is not connected")
            return flask.abort(409, description="Printer is not connected")

        with self._busy_lock:
            if self._busy:
                return flask.abort(409, description="A stop is already in progress")
            self._busy = True

        try:
            return self._start_stop()
        except Exception:
            with self._busy_lock:
                self._busy = False
            raise

    # ~~ internals

    def _start_stop(self):
        from octoprint.server import current_user

        user = current_user.get_name() if current_user else None
        state = self._printer.get_state_id()
        if self._is_sd_upload():
            # An upload to the printer's SD card runs in the PRINTING state, and
            # cancel_print() would abort and delete it. Not what this button is for.
            self._logger.warning("Stop refused: a file is being sent to the SD card")
            return flask.abort(
                409, description="A file is being sent to the printer's SD card; nothing was sent"
            )
        cancelling = self._printer.is_cancelling()
        job_active = not cancelling and (
            self._printer.is_printing() or self._printer.is_paused()
        )

        if not (state == "OPERATIONAL" or cancelling or job_active):
            self._logger.warning(f"Stop refused by state {state}, nothing sent")
            # flask.abort raises; on_api_command clears _busy on the way out.
            return flask.abort(
                409, description=f"Printer is busy ({state}); nothing was sent"
            )

        self._state_changed.clear()
        if job_active:
            self._logger.info(f"Stop requested by {user}: cancelling the job ({state})")
            self._printer.cancel_print(tags=self._tags(), user=user)
        elif cancelling:
            self._logger.info(f"Stop requested by {user}: a cancel is already running")
        else:
            self._logger.info(f"Stop requested by {user}: no job to cancel")

        threading.Thread(target=self._finish_stop, name="ebmc_stop", daemon=True).start()
        return flask.jsonify(cancelling=job_active or cancelling)

    def _tags(self):
        return {"source:plugin", f"plugin:{self._identifier}"}

    def _is_sd_upload(self):
        # OctoPrint has no public "is streaming" call. Transfer events track it,
        # and the state text covers the moment before the event is delivered
        # (util/comm.py: PRINTING + streaming -> "Sending file to SD").
        return self._transferring or self._printer.get_state_string() == "Sending file to SD"

    def _is_idle(self):
        p = self._printer
        return (
            p.get_state_id() == "OPERATIONAL"
            and not p.is_printing()
            and not p.is_paused()
            and not p.is_pausing()
            and not p.is_cancelling()
        )

    def _wait_for_idle(self, timeout):
        # Woken early by PrinterStateChanged, re-checked every POLL_S anyway.
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self._is_idle():
                return True
            self._state_changed.wait(POLL_S)
            self._state_changed.clear()
        return self._is_idle()

    def _notify(self, kind, text):
        self._plugin_manager.send_plugin_message(
            self._identifier, {"type": kind, "text": text}
        )

    def _finish_stop(self):
        try:
            if not self._wait_for_idle(IDLE_TIMEOUT_S):
                state = self._printer.get_state_id()
                msg = (
                    f"The printer did not become idle within {IDLE_TIMEOUT_S:.0f} s "
                    f"(state {state}). Nothing was sent; it has NOT been homed."
                )
                self._logger.error(msg)
                self._notify("error", msg)
                return

            lines = []
            if self._settings.get_boolean(["heaters_off"]):
                lines.extend(HEATERS_OFF)
            accepted, rejected = parse_gcode_lines(self._settings.get(["after_stop_gcode"]))
            for line in rejected:
                self._logger.warning(f"Skipping after-stop line that is not G/M/T: {line!r}")
            lines.extend(accepted)

            self._logger.info("Sending after-stop G-code: " + " | ".join(lines))
            self._printer.commands(lines, tags=self._tags())
            text = "Printer stopped. Sent: " + ", ".join(lines)
            if rejected:
                text += ". Skipped invalid lines: " + ", ".join(rejected)
            self._notify("sent", text)
        except Exception:
            self._logger.exception("Stop and home failed")
            self._notify("error", "Stop and home failed; see octoprint.log.")
        finally:
            with self._busy_lock:
                self._busy = False


__plugin_name__ = "EBMC Stop"
__plugin_version__ = "1.0.0"
__plugin_description__ = (
    "A large red button under the Temperature tab that stops the print and homes the printer."
)
__plugin_pythoncompat__ = ">=3.7,<4"
__plugin_implementation__ = EbmcStopPlugin()
