"""EBMC Stop: a STOP PRINT and HOME button, and a nozzle height control.

STOP PRINT and HOME ALL AXES (Temperature tab), API command "stop". STOP always wins:
  * it is accepted even while a nozzle height read or change is running; it then raises
    an abort flag that the nozzle code checks before every send and while waiting, so the
    nozzle sequence sends nothing further and reports what was and was not sent;
  * the printer must be connected and either idle, printing, paused or already
    cancelling; anything else (e.g. a file upload to the printer's SD card) is
    refused with 409 and nothing is sent; a second STOP while one runs is refused;
  * a printing or paused job is cancelled at once with OctoPrint's own cancel;
  * the plugin then waits until the nozzle code has let go and the printer is truly idle
    (state OPERATIONAL, not printing, paused or cancelling). OctoPrint fires
    PrintCancelled *before* it queues its own "after print job is cancelled" script and
    only returns to OPERATIONAL after that script has been sent, so waiting for the idle
    state (not the event) keeps the two from interleaving;
  * only then are the heaters switched off (optional) and the "after stop" G-code sent.
    If that does not happen in time, nothing is sent and the browser is told.

Nozzle height (Control tab), API commands "nozzle_height" and "nozzle_adjust": the
firmware home offset M206 Z, read with M503 and changed with M206, M500, G28, then read
back with M503. Only from the idle state; one command at a time, each sent only after the
printer has acknowledged the previous one, with the idle state checked before every send;
any error/!!/ALARM/halt reply stops the sequence. See nozzle.py for formats and limits.
"""

import datetime
import itertools
import os
import threading
import time

import flask

import octoprint.plugin
from octoprint.access.permissions import Permissions
from octoprint.events import Events

from . import nozzle
from .gcode_lines import parse_gcode_lines

IDLE_TIMEOUT_S = 60.0
POLL_S = 0.5
HEATERS_OFF = ["M104 S0", "M140 S0"]
DEFAULT_AFTER_STOP = "G28"

# Nozzle height: how long to wait for the printer to acknowledge each command.
READ_TIMEOUT_S = 10.0
M206_TIMEOUT_S = 10.0
M500_TIMEOUT_S = 20.0
G28_TIMEOUT_S = 120.0
NOZZLE_LOG = "nozzle_height.log"

SD_UPLOAD_STATES = ("Sending file to SD", "Starting to send file to SD")
TRANSFER_CLEAR_STATES = ("OPERATIONAL", "OFFLINE", "CLOSED", "ERROR", "CLOSED_WITH_ERROR")


class _Step:
    """One nozzle command in flight: lines received after it was handed to the serial port."""

    def __init__(self, tag):
        self.tag = tag
        self.armed = False  # set by the "sending" hook just before the line is written
        self.lines = []
        self.done = threading.Event()


class _Abort(Exception):
    def __init__(self, reason, halted=False):
        super().__init__(reason)
        self.reason = reason
        self.halted = halted


class _Progress:
    """What the nozzle sequence has handed over and what the printer has acknowledged."""

    def __init__(self):
        self.sent = []  # written to the serial port
        self.queued = []  # handed to OctoPrint, not yet written when we stopped waiting
        self.confirmed = []  # acknowledged by the printer


class EbmcStopPlugin(
    octoprint.plugin.SettingsPlugin,
    octoprint.plugin.AssetPlugin,
    octoprint.plugin.TemplatePlugin,
    octoprint.plugin.SimpleApiPlugin,
    octoprint.plugin.EventHandlerPlugin,
):
    def __init__(self):
        self._lock = threading.Lock()
        self._stop_busy = False
        self._nozzle_busy = False
        self._nozzle_free = threading.Event()
        self._nozzle_free.set()
        self._abort = threading.Event()
        self._state_changed = threading.Event()
        self._transferring = False
        self._step = None
        self._step_ids = itertools.count(1)
        self._log_lock = threading.Lock()

    # ~~ SettingsPlugin

    def get_settings_defaults(self):
        return {
            "heaters_off": True,
            "after_stop_gcode": DEFAULT_AFTER_STOP,
            "nozzle_steps_mm": [0.02, 0.05, 0.1],
            "raise_decreases_m206": True,
            "reference_z": None,
            "max_total_mm": 0.3,
        }

    # ~~ AssetPlugin

    def get_assets(self):
        return {
            "js": ["js/ebmc_stop.js", "js/ebmc_nozzle.js"],
            "css": ["css/ebmc_stop.css"],
        }

    # ~~ TemplatePlugin

    def get_template_configs(self):
        return [
            {"type": "generic", "template": "ebmc_stop.jinja2"},
            {"type": "generic", "template": "ebmc_nozzle.jinja2"},
            {"type": "settings", "name": "EBMC Stop", "custom_bindings": False},
        ]

    def is_template_autoescaped(self):
        return True

    # ~~ EventHandlerPlugin

    def on_event(self, event, payload):
        if event == Events.PRINTER_STATE_CHANGED:
            # Only a wake-up for waiting threads; the printer state is what counts.
            self._state_changed.set()
            if (payload or {}).get("state_id") in TRANSFER_CLEAR_STATES:
                self._transferring = False
        elif event == Events.TRANSFER_STARTED:
            self._transferring = True
        elif event in (Events.TRANSFER_DONE, Events.TRANSFER_FAILED, Events.DISCONNECTED, Events.ERROR):
            # A disconnect during an upload fires neither TransferDone nor TransferFailed.
            self._transferring = False

    # ~~ octoprint.comm.protocol.gcode.sending / .received hooks

    def on_gcode_sending(self, comm, phase, cmd, cmd_type, gcode, *args, **kwargs):
        step = self._step
        if step is not None and step.tag in (kwargs.get("tags") or ()):
            step.armed = True
        return None

    def on_gcode_received(self, comm, line, *args, **kwargs):
        step = self._step
        if step is not None and step.armed and not step.done.is_set():
            text = (line or "").strip()
            step.lines.append(text)
            if nozzle.is_ok(text) or nozzle.is_error_line(text):
                step.done.set()
        return line

    # ~~ SimpleApiPlugin

    def is_api_protected(self):
        return True

    def get_api_commands(self):
        return {"stop": [], "nozzle_height": [], "nozzle_adjust": ["direction", "step_mm"]}

    def on_api_command(self, command, data):
        if command == "stop":
            return self._api_stop()
        if command == "nozzle_height":
            return self._api_nozzle_read()
        if command == "nozzle_adjust":
            return self._api_nozzle_adjust(data)
        return flask.abort(400, description="Unknown command")

    # ~~ who is busy: STOP always wins over the nozzle control

    def _acquire_stop(self):
        with self._lock:
            if self._stop_busy:
                return "A stop is already in progress"
            self._stop_busy = True
            return None

    def _release_stop(self):
        with self._lock:
            self._stop_busy = False

    def _acquire_nozzle(self):
        with self._lock:
            if self._stop_busy:
                return "A STOP is in progress"
            if self._nozzle_busy:
                return "A nozzle height change or read is in progress"
            self._nozzle_busy = True
            self._abort.clear()
            self._nozzle_free.clear()
            return None

    def _release_nozzle(self):
        with self._lock:
            self._nozzle_busy = False
            self._nozzle_free.set()

    # ~~ STOP PRINT and HOME

    def _api_stop(self):
        # Same permission OctoPrint's own job API requires for "cancel".
        if not Permissions.PRINT.can():
            return flask.abort(403, description="Insufficient permissions")

        if not self._printer.is_operational():
            self._logger.warning("Stop refused: the printer is not connected")
            return flask.abort(409, description="Printer is not connected")

        other = self._acquire_stop()
        if other is not None:
            return flask.abort(409, description=other)

        try:
            return self._start_stop()
        except Exception:
            self._release_stop()
            raise

    def _start_stop(self):
        user = self._user()
        state = self._printer.get_state_id()
        if self._is_sd_upload():
            # An upload to the printer's SD card runs in the STARTING/PRINTING states, and
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
            # flask.abort raises; _api_stop releases the flag on the way out.
            return flask.abort(
                409, description=f"Printer is busy ({state}); nothing was sent"
            )

        with self._lock:
            nozzle_running = self._nozzle_busy
            if nozzle_running:
                self._abort.set()
        if nozzle_running:
            self._logger.warning(f"Stop requested by {user}: stopping the nozzle height sequence")

        self._state_changed.clear()
        if job_active:
            self._logger.info(f"Stop requested by {user}: cancelling the job ({state})")
            self._printer.cancel_print(tags=self._tags(), user=user)
        elif cancelling:
            self._logger.info(f"Stop requested by {user}: a cancel is already running")
        else:
            self._logger.info(f"Stop requested by {user}: no job to cancel")

        threading.Thread(target=self._finish_stop, name="ebmc_stop", daemon=True).start()
        return flask.jsonify(cancelling=job_active or cancelling, nozzle_stopped=nozzle_running)

    def _finish_stop(self):
        try:
            deadline = time.monotonic() + IDLE_TIMEOUT_S
            if not self._nozzle_free.wait(IDLE_TIMEOUT_S):
                msg = (
                    f"The nozzle height sequence did not stop within {IDLE_TIMEOUT_S:.0f} s. "
                    "Nothing was sent; the printer has NOT been homed."
                )
                self._logger.error(msg)
                self._notify("error", msg)
                return
            if not self._wait_for_idle(max(0.0, deadline - time.monotonic())):
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
            self._release_stop()

    # ~~ nozzle height: API

    def _nozzle_refusal(self):
        """Reason the nozzle height may not be read or changed now, or None."""
        if not self._printer.is_operational():
            return "Printer is not connected"
        if self._is_sd_upload():
            return "A file is being sent to the printer's SD card"
        if not self._is_idle():
            return f"The printer is not idle ({self._printer.get_state_string()})"
        return None

    def _api_nozzle_read(self):
        user = self._user()
        entry = {"action": "read", "user": user}

        def refuse(code, reason):
            self._log_nozzle(entry, "refused: " + reason)
            return flask.abort(code, description=reason.rstrip(".") + ". Nothing was sent.")

        # Sends M503; the same panel moves the printer: OctoPrint's CONTROL permission.
        if not Permissions.CONTROL.can():
            return refuse(403, "Insufficient permissions")
        reason = self._nozzle_refusal()
        if reason:
            return refuse(409, reason)
        other = self._acquire_nozzle()
        if other is not None:
            return refuse(409, other)
        progress = _Progress()
        try:
            result = self._read_offset(progress)
        except _Abort as exc:
            self._log_nozzle(entry, "failed: " + exc.reason)
            return flask.make_response(flask.jsonify(error=exc.reason), 409)
        finally:
            self._release_nozzle()
        if result["z"] is None:
            self._log_nozzle(entry, "failed: " + result["error"])
            return flask.make_response(flask.jsonify(error=result["error"], lines=result["lines"]), 502)
        entry["before"] = result["z"]
        self._log_nozzle(entry, "ok")
        return flask.jsonify(self._read_response(result))

    def _api_nozzle_adjust(self, data):
        user = self._user()
        direction = data.get("direction")
        step = data.get("step_mm")
        entry = {"action": "adjust", "user": user, "direction": direction, "step_mm": step}

        def refuse(code, reason):
            self._log_nozzle(entry, "refused: " + reason)
            # A read (M503) may already have been sent; nothing was changed.
            return flask.abort(code, description=reason.rstrip(".") + ". Nothing was changed.")

        if not Permissions.CONTROL.can():
            return refuse(403, "Insufficient permissions")
        config, reason = self._nozzle_settings()
        if reason:
            return refuse(409, reason)
        # Cheap checks first (direction, step), so a bad request sends nothing at all.
        _, reason = nozzle.check_adjust(5.0, direction, step, config["steps"], 5.0, 1e9, True)
        if reason:
            return refuse(400, reason)
        expected_before = nozzle.finite(data.get("expected_before"))
        expected_target = nozzle.finite(data.get("expected_target"))
        if expected_before is None or expected_target is None:
            return refuse(400, "expected_before and expected_target are required and must be numbers")
        if config["reference_z"] is None:
            return refuse(
                409,
                "No reference value has been confirmed. An administrator must enter the reference "
                "M206 Z in Settings > EBMC Stop before the nozzle height can be changed",
            )
        reason = self._nozzle_refusal()
        if reason:
            return refuse(409, reason)
        other = self._acquire_nozzle()
        if other is not None:
            return refuse(409, other)

        started = False
        try:
            progress = _Progress()
            try:
                result = self._read_offset(progress)
            except _Abort as exc:
                return refuse(409, exc.reason)
            if result["z"] is None:
                return refuse(502, result["error"])
            before = result["z"]
            entry["before"] = before
            if abs(expected_before - before) > 0.005:
                return refuse(
                    409,
                    f"The stored value is now M206 Z{before:.2f}, not {expected_before:.2f} "
                    "as shown when you confirmed. Read it again",
                )
            new, reason = nozzle.check_adjust(
                before, direction, step, config["steps"], config["reference_z"],
                config["max_total_mm"], self._raise_decreases(),
            )
            if reason:
                return refuse(409, reason)
            if abs(expected_target - new) > 0.005:
                return refuse(
                    409,
                    f"The new value would be M206 Z{new:.2f}, not {expected_target:.2f} as shown "
                    "when you confirmed (the settings may have changed). Read it again",
                )
            entry["target"] = new
            # Keep the whole earlier saved state: M500 will overwrite it.
            self._log_nozzle(entry, "starting; pre-change M503 reply follows", reply=result["all_lines"])
            threading.Thread(
                target=self._finish_adjust, args=(entry,), name="ebmc_nozzle", daemon=True
            ).start()
            started = True
        finally:
            # The thread releases the nozzle flag when it is done; every other path here.
            if not started:
                self._release_nozzle()
        self._logger.info(
            f"Nozzle height change by {nozzle.clean(user)}: {direction} {nozzle.finite(step):g} mm, "
            f"M206 Z{before:.2f} -> Z{new:.2f}"
        )
        return flask.jsonify(before=before, target=new, direction=direction, step_mm=nozzle.finite(step))

    # ~~ nozzle height: the sequence

    def _finish_adjust(self, entry):
        before, new = entry["before"], entry["target"]
        progress = _Progress()
        try:
            lines = self._send_one("M206 Z%.2f" % new, M206_TIMEOUT_S, "M206", progress)
            acks = [z for z in (nozzle.parse_m206_ack(l) for l in lines) if z is not None]
            if not acks:
                raise _Abort("The printer did not report the new home offset after M206")
            if abs(acks[-1] - new) > 0.005:
                raise _Abort(f"After M206 the printer reports Z {acks[-1]:.3f}, not {new:.2f}")

            self._notify("nozzle_progress", "Saving the new value (M500)...")
            lines = self._send_one("M500", M500_TIMEOUT_S, "M500", progress)
            if not any(nozzle.is_m500_ack(l) for l in lines):
                raise _Abort("The printer did not report 'Settings Stored' after M500")

            self._notify("nozzle_progress", "Homing (G28)...")
            self._send_one("G28", G28_TIMEOUT_S, "homing (G28)", progress)

            self._notify("nozzle_progress", "Reading back (M503)...")
            readback = self._read_offset(progress, what="the read-back (M503)")
            entry["readback"] = readback["z"]
            if readback["z"] is None:
                raise _Abort("Read-back failed: " + readback["error"])

            if abs(readback["z"] - new) > 0.005:
                msg = (
                    f"MISMATCH: sent M206 Z{new:.2f} and saved it, but the printer now reports "
                    f"M206 Z{readback['z']:.2f} (before: Z{before:.2f}). Check the printer's "
                    "settings before printing."
                )
                self._log_nozzle(entry, "MISMATCH", progress=progress)
                self._logger.error("Nozzle height " + msg)
                self._notify("nozzle_error", msg)
                return

            self._log_nozzle(entry, "ok", progress=progress)
            self._notify(
                "nozzle_done",
                f"Nozzle {'raised' if entry['direction'] == 'raise' else 'lowered'} by "
                f"{nozzle.finite(entry['step_mm']):g} mm. Stored value M206 Z{before:.2f} -> Z{new:.2f}; "
                f"read back Z{readback['z']:.2f} (matches). Saved and homed.",
                before=before, after=new, readback=readback["z"],
                max_temps=readback["max_temps"],
            )
        except _Abort as exc:
            msg = self._abort_message(exc, progress, before, new)
            self._log_nozzle(entry, "failed: " + exc.reason, progress=progress)
            self._logger.error("Nozzle height change failed: " + msg)
            self._notify("nozzle_error", msg)
        except Exception:
            self._logger.exception("Nozzle height change failed")
            self._log_nozzle(entry, "failed: internal error", progress=progress)
            self._notify(
                "nozzle_error",
                "Nozzle height change failed with an internal error; see octoprint.log. "
                "Check the printer before printing.",
            )
        finally:
            self._release_nozzle()

    def _send_one(self, command, timeout, what, progress):
        """Send one command and wait for the printer's acknowledgement.

        Refuses to send if STOP was pressed or the printer is not idle. Returns the lines
        received for this command. Raises _Abort on STOP, timeout or an error reply.
        """
        if self._abort.is_set():
            raise _Abort(f"Stopped by STOP before {what}")
        reason = self._nozzle_refusal()
        if reason:
            raise _Abort(f"{reason} before {what}")
        step = _Step(f"ebmc_stop:step:{next(self._step_ids)}")
        self._step = step
        try:
            self._printer.commands([command], tags=self._tags() | {step.tag})
            deadline = time.monotonic() + timeout
            while not step.done.wait(0.1):
                if self._abort.is_set():
                    raise _Abort(f"Stopped by STOP while waiting for {what}")
                if time.monotonic() > deadline:
                    raise _Abort(f"The printer did not acknowledge {what} within {timeout:.0f} s")
        finally:
            self._step = None
            if step.armed:
                progress.sent.append(command)
            else:
                progress.queued.append(command)
        errors = [l for l in step.lines if nozzle.is_error_line(l)]
        if errors:
            raise _Abort(f"The printer reported a problem after {what}: {nozzle.clean(errors[0], 120)}", halted=True)
        progress.confirmed.append(command)
        return list(step.lines)

    def _read_offset(self, progress, what="M503"):
        lines = self._send_one("M503", READ_TIMEOUT_S, what, progress)
        result = nozzle.parse_m503(lines)
        result["all_lines"] = lines
        result["lines"] = lines[:60]
        return result

    @staticmethod
    def _abort_message(exc, progress, before, new):
        msg = exc.reason + ". "
        if exc.halted:
            msg += (
                "The printer may be HALTED and needs attention before anything else "
                "(Smoothieware stays halted until it is reset or sent M999). "
            )
        if not progress.sent and not progress.queued:
            return msg + f"Nothing was sent; the stored value is still M206 Z{before:.2f}."
        unconfirmed = [c for c in progress.sent if c not in progress.confirmed]
        msg += "Sent and acknowledged: " + (", ".join(progress.confirmed) or "nothing") + ". "
        if unconfirmed:
            msg += "Sent but NOT acknowledged: " + ", ".join(unconfirmed) + ". "
        if progress.queued:
            msg += "Handed to OctoPrint but not yet sent (it will still go out): " + ", ".join(progress.queued) + ". "
        msg += "Nothing further was sent. "
        if "M500" in progress.confirmed:
            msg += f"M206 Z{new:.2f} WAS saved. "
            if "G28" not in progress.confirmed:
                msg += "Homing was not confirmed. "
            msg += "Read the value again and check before printing."
        elif "M500" in progress.sent or "M500" in progress.queued:
            msg += (
                f"M206 Z{new:.2f} is probably live; it is UNKNOWN whether it was saved (the saved "
                f"value was Z{before:.2f}). Read the value again and check before printing."
            )
        else:
            msg += (
                f"M206 Z{new:.2f} may be live but was NOT saved; the saved value is still "
                f"Z{before:.2f}. Read the value again or power-cycle the printer before printing."
            )
        return msg

    # ~~ nozzle height: settings, response, log

    def _nozzle_settings(self):
        return nozzle.validate_settings(
            self._settings.get(["reference_z"]),
            self._settings.get(["max_total_mm"]),
            self._settings.get(["nozzle_steps_mm"]),
        )

    def _raise_decreases(self):
        return self._settings.get_boolean(["raise_decreases_m206"])

    def _read_response(self, result):
        z = result["z"]
        config, settings_error = self._nozzle_settings()
        preview = {}
        if config:
            for direction in ("raise", "lower"):
                for s in config["steps"]:
                    new, reason = nozzle.check_adjust(
                        z, direction, s, config["steps"], config["reference_z"],
                        config["max_total_mm"], self._raise_decreases(),
                    )
                    preview[f"{direction}:{s:g}"] = {
                        "new": nozzle.target_value(z, direction, s, self._raise_decreases()),
                        "allowed": reason is None,
                        "reason": reason,
                    }
        return {
            "z": z,
            "max_temps": result["max_temps"],
            "steps": config["steps"] if config else [],
            "raise_decreases_m206": self._raise_decreases(),
            "reference_z": config["reference_z"] if config else None,
            "settings_error": settings_error,
            "suggested_reference": z if config and config["reference_z"] is None else None,
            "max_total_mm": config["max_total_mm"] if config else None,
            "preview": preview,
        }

    def _log_nozzle(self, entry, outcome, progress=None, reply=None):
        fields = {
            "time": datetime.datetime.now().isoformat(timespec="seconds"),
            "action": entry.get("action"),
            "user": entry.get("user"),
            "direction": entry.get("direction"),
            "step_mm": entry.get("step_mm"),
            "before": entry.get("before"),
            "after": entry.get("target"),
            "readback": entry.get("readback"),
            "outcome": outcome,
        }
        if progress is not None:
            fields["sent"] = " | ".join(progress.sent) or "nothing"
            fields["acknowledged"] = " | ".join(progress.confirmed) or "nothing"
            if progress.queued:
                fields["queued"] = " | ".join(progress.queued)
        line = " ".join(
            f"{k}={nozzle.clean(v, 300)}" for k, v in fields.items() if v is not None or k == "outcome"
        )
        self._logger.info("Nozzle height: " + line)
        try:
            path = os.path.join(self.get_plugin_data_folder(), NOZZLE_LOG)
            with self._log_lock, open(path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
                for r in reply or []:
                    f.write("    | " + nozzle.clean(r, 300) + "\n")
        except Exception:
            self._logger.exception("Could not write the nozzle height log")

    # ~~ shared internals

    @staticmethod
    def _user():
        from octoprint.server import current_user

        return current_user.get_name() if current_user else None

    def _tags(self):
        return {"source:plugin", f"plugin:{self._identifier}"}

    def _is_sd_upload(self):
        # OctoPrint has no public "is streaming" call. Transfer events track it, and the
        # state text covers the moment before the event is delivered (util/comm.py:
        # STARTING + streaming -> "Starting to send file to SD", PRINTING + streaming ->
        # "Sending file to SD").
        return self._transferring or self._printer.get_state_string() in SD_UPLOAD_STATES

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

    def _notify(self, kind, text, **extra):
        data = {"type": kind, "text": text}
        data.update(extra)
        self._plugin_manager.send_plugin_message(self._identifier, data)


__plugin_name__ = "EBMC Stop"
__plugin_version__ = "1.2.0"
__plugin_description__ = (
    "A STOP PRINT and HOME button under the Temperature tab, and a nozzle height control in the Control tab."
)
__plugin_pythoncompat__ = ">=3.7,<4"


def __plugin_load__():
    global __plugin_implementation__
    global __plugin_hooks__
    __plugin_implementation__ = EbmcStopPlugin()
    __plugin_hooks__ = {
        "octoprint.comm.protocol.gcode.sending": __plugin_implementation__.on_gcode_sending,
        "octoprint.comm.protocol.gcode.received": __plugin_implementation__.on_gcode_received,
    }
