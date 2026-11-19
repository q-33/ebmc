"""Unit tests for the nozzle height parser and limits. Plain Python, no OctoPrint needed:

    python3 -m unittest discover -s Tools/octoprint-ebmc-stop/tests -v
"""

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "nozzle", os.path.join(_HERE, "..", "octoprint_ebmc_stop", "nozzle.py")
)
nozzle = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(nozzle)
_spec2 = importlib.util.spec_from_file_location(
    "gcode_lines", os.path.join(_HERE, "..", "octoprint_ebmc_stop", "gcode_lines.py")
)
gcode_lines = importlib.util.module_from_spec(_spec2)
_spec2.loader.exec_module(gcode_lines)
NAN, INF = float("nan"), float("inf")

# SAMPLE TAKEN FROM THE PRINTER'S SD-CARD BACKUP (config-override, lines 32-33),
# NOT from the live printer's M503 reply, which has not been captured yet.
SD_BACKUP_HOME_OFFSET = [";Home offset (mm):", "M206 X0.00 Y0.00 Z4.50"]

# Built from the Smoothieware printf formats (see nozzle.py), values made up.
SMOOTHIE_M503_CONSTRUCTED = [
    "; config override present: /sd/config-override",
    ";Steps per unit:",
    "M92 X80.00000 Y80.00000 Z400.00000 ",
    ";Home offset (mm):",
    "M206 X0.00 Y0.00 Z5.66 ",
    ";PID settings, i_max, max_pwm:",
    "M301 S0 P38.0000 I1.5000 D50.0000 X255.0000 Y255",
    ";Max temperature setting:",
    "M143 S0 P300.0000",
    ";Max temperature setting:",
    "M143 S1 P100.0000",
    "",
    "ok",
]


class ParseHomeOffset(unittest.TestCase):
    def test_sd_backup_sample(self):
        r = nozzle.parse_m503(SD_BACKUP_HOME_OFFSET)
        self.assertEqual(r["z"], 4.50)
        self.assertIsNone(r["error"])

    def test_constructed_full_reply(self):
        r = nozzle.parse_m503(SMOOTHIE_M503_CONSTRUCTED)
        self.assertEqual(r["z"], 5.66)
        self.assertEqual(r["max_temps"], {0: 300.0, 1: 100.0})

    def test_tolerated_formats(self):
        cases = {
            "M206 X0.00 Y0.00 Z4.50 ": 4.5,
            "M206 Z4.5": 4.5,
            "M206 Z5": 5.0,
            "M206 Z5.123456": 5.123456,
            "M206 X1.0 Z-0.25": -0.25,
            "echo:M206 X0.00 Y0.00 Z4.50": 4.5,
            "echo: M206 Z4.50": 4.5,
            ";M206 Z4.50": 4.5,
            "  m206 y0 z4.50\r\n": 4.5,
            "M206Z4.50": 4.5,
        }
        for line, expected in cases.items():
            with self.subTest(line=line):
                self.assertAlmostEqual(nozzle.parse_home_offset_line(line), expected)

    def test_junk(self):
        for line in [
            "",
            "ok",
            ";Home offset (mm):",
            "M206",
            "M206 X0.00 Y0.00",
            "M206 Zabc",
            "M2060 Z1.00",
            "M206.1 Z1.00",
            "M306 Z1.00",
            "G1 Z5",
            "Homing Offset: X 0.000 Y 0.000 Z 4.500 will take effect next home",
            "M206 A0.00000 B0.00000 C0.00000",
            "Error: unknown",
            None,
        ]:
            with self.subTest(line=line):
                self.assertIsNone(nozzle.parse_home_offset_line(line))

    def test_reply_without_offset_line(self):
        r = nozzle.parse_m503(["; No config override", "M92 X80 Y80 Z400", "ok"])
        self.assertIsNone(r["z"])
        self.assertIn("no home offset line", r["error"])

    def test_two_different_values_is_an_error(self):
        r = nozzle.parse_m503(["M206 Z4.50", "M206 Z4.60"])
        self.assertIsNone(r["z"])
        self.assertIn("more than one", r["error"])

    def test_acks(self):
        self.assertEqual(nozzle.parse_m206_ack("X: 0.000 Y: 0.000 Z: 5.610  will take effect next home"), 5.61)
        self.assertIsNone(nozzle.parse_m206_ack("X: 0.000 Y: 0.000 Z: 5.610"))
        self.assertTrue(nozzle.is_m500_ack("Settings Stored to /sd/config-override"))
        self.assertFalse(nozzle.is_m500_ack("ok"))

    def test_ok_and_error_lines(self):
        for line in ["ok", "ok\r\n", "OK", "ok T:21.0 /0.0 @0", "  ok  "]:
            self.assertTrue(nozzle.is_ok(line), line)
        for line in ["okay", "oknot", "", None, "not ok", "Settings Stored"]:
            self.assertFalse(nozzle.is_ok(line), line)
        for line in ["ERROR: Homing cycle failed - check the max_travel settings", "error:2",
                     "Error: unknown", "!! printer halted", "ALARM: Homing fail",
                     "Entering Alarm/Halt state", "System halted"]:
            self.assertTrue(nozzle.is_error_line(line), line)
        for line in ["ok", "M206 X0.00 Y0.00 Z5.66", ";Home offset (mm):", "Settings Stored to /sd/config-override",
                     "X: 0.000 Y: 0.000 Z: 5.610  will take effect next home"]:
            self.assertFalse(nozzle.is_error_line(line), line)

    def test_clean(self):
        self.assertEqual(nozzle.clean("raise\ntime=x"), "raise?time=x")
        self.assertEqual(nozzle.clean("a\x1b[31mb\x7f"), "a?[31mb?")
        self.assertTrue(nozzle.clean("x" * 500).endswith("..."))

    def test_finite(self):
        self.assertEqual(nozzle.finite("5.66"), 5.66)
        self.assertEqual(nozzle.finite(0), 0.0)
        for v in [NAN, INF, -INF, "nan", "inf", None, "", "5.66; G28", True, False, [], {}]:
            self.assertIsNone(nozzle.finite(v), v)


class Limits(unittest.TestCase):
    STEPS = [0.02, 0.05, 0.1]

    def check(self, before, direction, step, ref=5.66, window=0.3, raise_decreases=True):
        return nozzle.check_adjust(before, direction, step, self.STEPS, ref, window, raise_decreases)

    def test_sign_rule_this_printer(self):
        self.assertEqual(self.check(5.66, "raise", 0.05), (5.61, None))
        self.assertEqual(self.check(5.61, "lower", 0.1), (5.71, None))

    def test_sign_rule_inverted(self):
        self.assertEqual(self.check(5.66, "raise", 0.05, raise_decreases=False), (5.71, None))
        self.assertEqual(self.check(5.66, "lower", 0.1, raise_decreases=False), (5.56, None))

    def test_refusals(self):
        self.assertIn("not one of the allowed", self.check(5.66, "raise", 0.03)[1])
        self.assertIn("not a number", self.check(5.66, "raise", "x")[1])
        self.assertIn("not a number", self.check(5.66, "raise", None)[1])
        self.assertIn("Direction", self.check(5.66, "up", 0.05)[1])
        self.assertIn("No reference", self.check(5.66, "raise", 0.05, ref=None)[1])
        self.assertIn("outside the allowed window", self.check(5.95, "lower", 0.1)[1])
        self.assertIn("negative", self.check(0.03, "raise", 0.05, ref=0.0, window=1.0)[1])

    def test_step_over_per_action_limit(self):
        new, reason = nozzle.check_adjust(5.66, "raise", 0.3, [0.02, 0.05, 0.1, 0.3], 5.66, 1.0, True)
        self.assertIsNone(new)
        self.assertIn("0.2 mm allowed in one action", reason)

    def test_window_edge_allowed(self):
        self.assertEqual(self.check(5.86, "lower", 0.1), (5.96, None))

    def test_window_low_edge(self):
        self.assertEqual(self.check(5.41, "raise", 0.05), (5.36, None))   # exactly reference - 0.3
        self.assertIn("outside the allowed window", self.check(5.38, "raise", 0.05)[1])  # 5.33

    def test_non_finite_and_negative_inputs(self):
        for step in [NAN, INF, -0.05, 0, -INF, "nan"]:
            new, reason = nozzle.check_adjust(5.66, "raise", step, [0.02, 0.05, 0.1, -0.05, 0], 5.66, 0.3, True)
            self.assertIsNone(new, step)
        for before in [NAN, INF]:
            self.assertIsNone(self.check(before, "raise", 0.05)[0])
        self.assertIsNone(self.check(5.66, "raise", 0.05, ref=NAN)[0])
        self.assertIsNone(self.check(5.66, "raise", 0.05, window=NAN)[0])
        self.assertIsNone(self.check(5.66, "raise", 0.05, window=-1)[0])
        self.assertIsNone(nozzle.parse_m503(["M206 Z5.5", "M206 Z5.6"])["z"])


class Settings(unittest.TestCase):
    def test_valid(self):
        cfg, err = nozzle.validate_settings("5.46", 0.3, [0.02, 0.05, 0.1])
        self.assertIsNone(err)
        self.assertEqual(cfg, {"reference_z": 5.46, "max_total_mm": 0.3, "steps": [0.02, 0.05, 0.1]})

    def test_unset_reference_is_allowed_but_none(self):
        for ref in [None, "", "  "]:
            cfg, err = nozzle.validate_settings(ref, 0.3, [0.05])
            self.assertIsNone(err)
            self.assertIsNone(cfg["reference_z"])

    def test_invalid(self):
        bad = [
            ("nan", 0.3, [0.05]), ("inf", 0.3, [0.05]), ("abc", 0.3, [0.05]), (-1, 0.3, [0.05]),
            (5.46, NAN, [0.05]), (5.46, INF, [0.05]), (5.46, -0.1, [0.05]), (5.46, "x", [0.05]),
            (5.46, 0.3, []), (5.46, 0.3, None), (5.46, 0.3, "0.05"), (5.46, 0.3, [-0.05]),
            (5.46, 0.3, [0]), (5.46, 0.3, [0.25]), (5.46, 0.3, [NAN]), (5.46, 0.3, [True]),
        ]
        for ref, window, steps in bad:
            cfg, err = nozzle.validate_settings(ref, window, steps)
            self.assertIsNone(cfg, (ref, window, steps))
            self.assertTrue(err)


class GcodeLines(unittest.TestCase):
    def test_parse(self):
        accepted, rejected = gcode_lines.parse_gcode_lines(
            "g28 x y\nget pos\n; comment\n\nhome\n  m117 Hello There  \nT0\nM\nG28.2\nM104 S0"
        )
        self.assertEqual(accepted, ["G28 X Y", "M117 Hello There", "T0", "G28.2", "M104 S0"])
        self.assertEqual(rejected, ["get pos", "home", "M"])

    def test_empty(self):
        self.assertEqual(gcode_lines.parse_gcode_lines(None), ([], []))
        self.assertEqual(gcode_lines.parse_gcode_lines(""), ([], []))


if __name__ == "__main__":
    unittest.main()
