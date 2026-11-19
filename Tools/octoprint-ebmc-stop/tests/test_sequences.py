"""Sequence tests for the STOP button and the nozzle height control, with OctoPrint and
flask replaced by the stand-ins in tests/stubs and a scripted Smoothieware-like printer.

    python3 -m unittest discover -s Tools/octoprint-ebmc-stop/tests -v
"""

import logging
import os
import shutil
import sys
import tempfile
import threading
import time
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, "stubs"))
sys.path.insert(1, os.path.join(_HERE, ".."))

import flask  # noqa: E402  (the stub)
import octoprint_ebmc_stop as plugin_module  # noqa: E402
from octoprint.access.permissions import Permissions  # noqa: E402
from octoprint.events import Events  # noqa: E402

logging.basicConfig(level=logging.CRITICAL)


class FakePrinter:
    """Answers one command at a time, like a serial printer behind OctoPrint: the plugin's
    "sending" hook fires when a command is written, replies go through its received hook."""

    def __init__(self, plugin, z=5.66):
        self.plugin = plugin
        self.z = z
        self.state = "OPERATIONAL"
        self.delays = {}  # command word -> seconds before the reply
        self.home_fails = False
        self.events = []  # (time, kind, text)
        self.queue = []
        self.cv = threading.Condition()
        threading.Thread(target=self._worker, daemon=True).start()

    # OctoPrint printer API used by the plugin
    def is_operational(self):
        return self.state not in ("OFFLINE", "CLOSED")

    def get_state_id(self):
        return self.state

    def get_state_string(self):
        return {"OPERATIONAL": "Operational", "PRINTING": "Printing", "PAUSED": "Paused",
                "CANCELLING": "Cancelling", "OFFLINE": "Offline"}.get(self.state, self.state)

    def is_printing(self):
        return self.state in ("PRINTING", "CANCELLING", "STARTING")

    def is_paused(self):
        return self.state == "PAUSED"

    def is_pausing(self):
        return False

    def is_cancelling(self):
        return self.state == "CANCELLING"

    def cancel_print(self, tags=None, user=None):
        self.log("cancel", "")
        self.state = "CANCELLING"

        def done():
            time.sleep(0.2)
            self.state = "OPERATIONAL"
            self.plugin.on_event(Events.PRINTER_STATE_CHANGED, {"state_id": "OPERATIONAL"})

        threading.Thread(target=done, daemon=True).start()

    def commands(self, cmds, tags=None):
        if isinstance(cmds, str):
            cmds = [cmds]
        with self.cv:
            for c in cmds:
                self.log("handed", c)
                self.queue.append((c, set(tags or ())))
            self.cv.notify()

    # helpers
    def log(self, kind, text):
        self.events.append((time.monotonic(), kind, text))

    def kinds(self, kind):
        return [t for _, k, t in self.events if k == kind]

    def _reply(self, line):
        self.log("recv", line)
        self.plugin.on_gcode_received(None, line + "\r\n")

    def _worker(self):
        while True:
            with self.cv:
                while not self.queue:
                    self.cv.wait()
                cmd, tags = self.queue.pop(0)
            self.plugin.on_gcode_sending(None, "sending", cmd, None, None, tags=tags)
            self.log("sent", cmd)
            time.sleep(self.delays.get(cmd.split()[0], 0.01))
            if cmd == "M503":
                for line in ["; config override present: /sd/config-override", ";Home offset (mm):",
                             "M206 X0.00 Y0.00 Z%.2f " % self.z, ";Max temperature setting:",
                             "M143 S0 P300.0000", ""]:
                    self._reply(line)
            elif cmd.startswith("M206"):
                self.z = float(cmd.split("Z")[1])
                self._reply("X: 0.000 Y: 0.000 Z: %5.3f  will take effect next home" % self.z)
            elif cmd == "M500":
                self._reply("Settings Stored to /sd/config-override")
            elif cmd == "G28" and self.home_fails:
                self._reply("ERROR: Homing cycle failed - check the max_travel settings")
            self._reply("ok")


class FakeSettings:
    def __init__(self, values):
        self.values = values

    def get(self, path):
        return self.values[path[0]]

    def get_boolean(self, path):
        return bool(self.values[path[0]])


class Messages:
    def __init__(self):
        self.items = []

    def send_plugin_message(self, ident, data):
        self.items.append(data)

    def last(self, prefix):
        found = [m for m in self.items if m["type"].startswith(prefix)]
        return found[-1] if found else None


class Base(unittest.TestCase):
    def setUp(self):
        Permissions.PRINT.allowed = True
        Permissions.CONTROL.allowed = True
        self.tmp = tempfile.mkdtemp()
        p = plugin_module.EbmcStopPlugin()
        p._identifier = "ebmc_stop"
        p._logger = logging.getLogger("test")
        settings = p.get_settings_defaults()
        settings["reference_z"] = "5.66"
        p._settings = FakeSettings(settings)
        p._plugin_manager = Messages()
        p.get_plugin_data_folder = lambda: self.tmp
        p._printer = FakePrinter(p)
        self.p = p
        self.printer = p._printer

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def call(self, command, data=None):
        try:
            return self.p.on_api_command(command, data or {})
        except flask.HTTPException as e:
            return {"status": e.code, "error": e.description}

    def adjust(self, direction="raise", step=0.05, before=5.66, target=5.61):
        data = {"direction": direction, "step_mm": step}
        if before is not None:
            data["expected_before"] = before
        if target is not None:
            data["expected_target"] = target
        return self.call("nozzle_adjust", data)

    def wait_for(self, predicate, timeout=5.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if predicate():
                return True
            time.sleep(0.02)
        return False

    def nozzle_result(self):
        self.assertTrue(self.wait_for(lambda: self.p._plugin_manager.last("nozzle_done")
                                      or self.p._plugin_manager.last("nozzle_error")))
        return self.p._plugin_manager.last("nozzle_done") or self.p._plugin_manager.last("nozzle_error")

    def log_text(self):
        with open(os.path.join(self.tmp, plugin_module.NOZZLE_LOG)) as f:
            return f.read()


class NozzleSequence(Base):
    def test_raise_order_and_acknowledgements(self):
        r = self.adjust()
        self.assertEqual(r, {"before": 5.66, "target": 5.61, "direction": "raise", "step_mm": 0.05})
        self.assertEqual(self.nozzle_result()["type"], "nozzle_done")
        self.assertEqual(self.printer.kinds("sent"), ["M503", "M206 Z5.61", "M500", "G28", "M503"])
        # never two commands ahead: each command handed over only after the previous "ok"
        ev = self.printer.events
        handed = [(t, x) for t, k, x in ev if k == "handed"]
        oks = [t for t, k, x in ev if k == "recv" and x == "ok"]
        for i in range(1, len(handed)):
            self.assertTrue(any(handed[i - 1][0] < o < handed[i][0] for o in oks),
                            "%s handed before %s was acknowledged" % (handed[i][1], handed[i - 1][1]))
        log = self.log_text()
        self.assertIn("pre-change M503 reply follows", log)
        self.assertIn("    | M206 X0.00 Y0.00 Z5.66", log)
        self.assertIn("outcome=ok", log)

    def test_homing_error_is_a_failure(self):
        self.printer.home_fails = True
        self.adjust()
        result = self.nozzle_result()
        self.assertEqual(result["type"], "nozzle_error")
        self.assertIn("Homing cycle failed", result["text"])
        self.assertIn("HALTED", result["text"])
        self.assertIn("WAS saved", result["text"])
        self.assertEqual(self.printer.kinds("sent"), ["M503", "M206 Z5.61", "M500", "G28"])

    def test_expected_values_required_and_finite(self):
        for before, target in [(None, 5.61), (5.66, None), (float("nan"), 5.61), (5.66, float("inf")),
                               ("5.66; G28", 5.61), (True, 5.61)]:
            r = self.adjust(before=before, target=target)
            self.assertEqual(r["status"], 400, (before, target, r))
        self.assertEqual(self.printer.kinds("handed"), [])

    def test_expected_values_compared(self):
        self.assertEqual(self.adjust(before=5.70)["status"], 409)
        r = self.adjust(target=5.71)  # direction flipped between read and confirm
        self.assertEqual(r["status"], 409)
        self.assertIn("not 5.71", r["error"])
        self.assertEqual(self.printer.kinds("handed"), ["M503", "M503"])

    def test_print_started_mid_sequence(self):
        self.printer.delays["M500"] = 0.3

        def start_print():
            self.wait_for(lambda: "M500" in self.printer.kinds("sent"))
            self.printer.state = "PRINTING"

        threading.Thread(target=start_print, daemon=True).start()
        self.adjust()
        result = self.nozzle_result()
        self.assertEqual(result["type"], "nozzle_error")
        self.assertIn("not idle", result["text"])
        self.assertNotIn("G28", self.printer.kinds("handed"))

    def test_invalid_settings_refuse(self):
        for key, value in [("reference_z", "nan"), ("reference_z", "inf"), ("max_total_mm", float("nan")),
                           ("nozzle_steps_mm", [0.05, -0.05]), ("nozzle_steps_mm", [0.05, 0.3]),
                           ("nozzle_steps_mm", [])]:
            saved = self.p._settings.values[key]
            self.p._settings.values[key] = value
            r = self.adjust()
            self.assertEqual(r["status"], 409, (key, value, r))
            self.p._settings.values[key] = saved
        self.assertEqual(self.printer.kinds("handed"), [])

    def test_log_line_cannot_be_forged(self):
        self.call("nozzle_adjust", {"direction": "raise\ntime=FORGED outcome=ok", "step_mm": 0.05,
                                    "expected_before": 5.66, "expected_target": 5.61})
        lines = self.log_text().splitlines()
        self.assertEqual(len(lines), 1)
        self.assertIn("direction=raise?time=FORGED", lines[0])

    def test_reads_are_logged(self):
        self.assertEqual(self.call("nozzle_height")["z"], 5.66)
        Permissions.CONTROL.allowed = False
        self.assertEqual(self.call("nozzle_height")["status"], 403)
        log = self.log_text()
        self.assertIn("action=read", log)
        self.assertIn("outcome=ok", log)
        self.assertIn("refused: Insufficient permissions", log)


class StopWins(Base):
    def stop_during(self, word):
        self.printer.delays[word] = 0.5
        self.adjust()
        self.assertTrue(self.wait_for(lambda: any(s.split()[0] == word for s in self.printer.kinds("sent")
                                                  if s != "M503") or (word == "M503" and
                                                  self.printer.kinds("sent").count("M503") == 2)))
        r = self.call("stop")
        self.assertEqual(r, {"cancelling": False, "nozzle_stopped": True})
        result = self.nozzle_result()
        self.assertEqual(result["type"], "nozzle_error")
        self.assertIn("Stopped by STOP", result["text"])
        self.assertTrue(self.wait_for(lambda: self.p._plugin_manager.last("sent")))
        handed = self.printer.kinds("handed")
        stop_at = handed.index("M104 S0")
        self.assertEqual(handed[stop_at:], ["M104 S0", "M140 S0", "G28"])
        nozzle_cmds = handed[:stop_at]
        self.assertEqual(nozzle_cmds[-1].split()[0], word)  # nothing after the step that was running
        return result, nozzle_cmds

    def test_stop_during_m206(self):
        result, cmds = self.stop_during("M206")
        self.assertEqual(cmds, ["M503", "M206 Z5.61"])
        self.assertIn("NOT saved", result["text"])

    def test_stop_during_m500(self):
        result, cmds = self.stop_during("M500")
        self.assertEqual(cmds, ["M503", "M206 Z5.61", "M500"])
        self.assertIn("UNKNOWN whether it was saved", result["text"])

    def test_stop_during_g28(self):
        result, cmds = self.stop_during("G28")
        self.assertEqual(cmds, ["M503", "M206 Z5.61", "M500", "G28"])
        self.assertIn("WAS saved", result["text"])
        self.assertIn("Homing was not confirmed", result["text"])

    def test_stop_during_readback(self):
        result, cmds = self.stop_during("M503")
        self.assertEqual(cmds, ["M503", "M206 Z5.61", "M500", "G28", "M503"])
        self.assertIn("WAS saved", result["text"])

    def test_stop_during_read(self):
        self.printer.delays["M503"] = 0.5
        out = {}
        t = threading.Thread(target=lambda: out.setdefault("r", self.call("nozzle_height")))
        t.start()
        self.wait_for(lambda: "M503" in self.printer.kinds("sent"))
        self.assertEqual(self.call("stop")["nozzle_stopped"], True)
        t.join(3)
        self.assertEqual(out["r"]["status"], 409)
        self.assertIn("Stopped by STOP", out["r"]["body"]["error"])

    def test_stop_with_job_cancels_and_refusals(self):
        self.printer.state = "PRINTING"
        self.assertEqual(self.call("stop"), {"cancelling": True, "nozzle_stopped": False})
        self.assertEqual(self.call("stop")["status"], 409)  # second STOP
        self.assertEqual(self.call("nozzle_height")["status"], 409)  # nozzle while STOP runs
        self.assertTrue(self.wait_for(lambda: self.p._plugin_manager.last("sent")))
        self.assertEqual(self.printer.kinds("cancel"), [""])
        self.assertEqual(self.printer.kinds("handed"), ["M104 S0", "M140 S0", "G28"])


class TransferFlag(Base):
    def test_cleared_on_disconnect_and_state(self):
        for event, payload in [(Events.DISCONNECTED, {}), (Events.ERROR, {}),
                               (Events.PRINTER_STATE_CHANGED, {"state_id": "OFFLINE"}),
                               (Events.PRINTER_STATE_CHANGED, {"state_id": "OPERATIONAL"})]:
            self.p.on_event(Events.TRANSFER_STARTED, {})
            self.assertTrue(self.p._is_sd_upload())
            self.p.on_event(event, payload)
            self.assertFalse(self.p._is_sd_upload(), event)

    def test_starting_upload_state_refuses_stop(self):
        self.printer.get_state_string = lambda: "Starting to send file to SD"
        self.printer.state = "STARTING"
        r = self.call("stop")
        self.assertEqual(r["status"], 409)
        self.assertEqual(self.printer.kinds("cancel"), [])


if __name__ == "__main__":
    unittest.main()
