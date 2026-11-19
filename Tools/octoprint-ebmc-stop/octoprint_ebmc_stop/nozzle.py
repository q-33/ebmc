"""Nozzle height (firmware home offset, M206 Z) helpers. Pure Python, no OctoPrint.

Formats, from the Smoothieware source at the printer's build (edge-d82d66f):
  * M503 / M500, src/modules/tools/endstops/Endstops.cpp:
        printf(";Home offset (mm):\\nM206 ");  then "%c%1.2f " per homing axis, then "\\n"
    so the value line looks like "M206 X0.00 Y0.00 Z4.50 " (two decimals).
  * M206, same file: "%c: %5.3f " per homing axis, then " will take effect next home\\n",
    e.g. "X: 0.000 Y: 0.000 Z: 4.500  will take effect next home".
  * M500, src/modules/communication/GcodeDispatch.cpp:
        "Settings Stored to %s\\r\\nok\\r\\n"
  * M503 / M500, src/modules/tools/temperaturecontrol/TemperatureControl.cpp:
        ";Max temperature setting:\\nM143 S%d P%1.4f\\n"   (S is the heater's pool index)
  * A failed homing, Endstops.cpp: "ERROR: Homing cycle failed - check the max_travel
    settings" (or "ALARM: Homing fail" in grbl mode), and the firmware enters HALT.

The parsers are tolerant: optional "echo:" or ";" in front, any number of
decimals, X and Y present or absent.

On this printer Z homes to MAX: after homing the position is gamma_max + home_offset, so a
larger M206 Z puts the nozzle lower. Raising the nozzle therefore DECREASES M206 Z
(setting raise_decreases_m206, default true).
"""

import math
import re

# No single action may move more than this, whatever the steps setting says.
MAX_STEP_PER_ACTION_MM = 0.2

_PREFIX = r"^\s*(?:echo:\s*)?(?:;\s*)?"
_M206_LINE = re.compile(_PREFIX + r"M206(?![0-9.])(?P<args>.*)$", re.IGNORECASE)
_M143_LINE = re.compile(_PREFIX + r"M143(?![0-9.])(?P<args>.*)$", re.IGNORECASE)
_NUMBER = r"([-+]?\d+(?:\.\d+)?)"
_PARAM_Z = re.compile(r"(?:^|\s)Z\s*" + _NUMBER + r"(?=\s|$)", re.IGNORECASE)
_PARAM_S = re.compile(r"(?:^|\s)S\s*" + _NUMBER + r"(?=\s|$)", re.IGNORECASE)
_PARAM_P = re.compile(r"(?:^|\s)P\s*" + _NUMBER + r"(?=\s|$)", re.IGNORECASE)
_M206_ACK = re.compile(r"(?:^|\s)Z:\s*" + _NUMBER + r".*will take effect next home", re.IGNORECASE)
_M500_ACK = re.compile(r"^\s*(?:echo:\s*)?Settings Stored", re.IGNORECASE)
_OK = re.compile(r"^\s*ok(?:\s|$)", re.IGNORECASE)
_ERROR = re.compile(r"^\s*(?:error|!!)|alarm|halt", re.IGNORECASE)
_CONTROL = re.compile(r"[\x00-\x1f\x7f]")


def parse_home_offset_line(line):
    """Z value from an "M206 ... Z<n>" settings line, or None."""
    m = _M206_LINE.match(line or "")
    if not m:
        return None
    z = _PARAM_Z.search(m.group("args"))
    return float(z.group(1)) if z else None


def parse_max_temp_line(line):
    """(index, max_temp) from an "M143 S<i> P<t>" settings line, or None."""
    m = _M143_LINE.match(line or "")
    if not m:
        return None
    s = _PARAM_S.search(m.group("args"))
    p = _PARAM_P.search(m.group("args"))
    if not (s and p):
        return None
    return int(float(s.group(1))), float(p.group(1))


def parse_m206_ack(line):
    """Z value from Smoothieware's reply to M206 ("... Z: 5.610  will take effect next home")."""
    m = _M206_ACK.search(line or "")
    return float(m.group(1)) if m else None


def is_m500_ack(line):
    return bool(_M500_ACK.match(line or ""))


def is_ok(line):
    """An acknowledgement line: "ok", or "ok" followed by a space and more text."""
    return bool(_OK.match(line or ""))


def is_error_line(line):
    """A line that means the printer reported a problem (error, !!, ALARM, halt)."""
    return bool(_ERROR.search(line or ""))


def clean(value, limit=60):
    """Request text made safe for one log line: no control characters, bounded length."""
    text = _CONTROL.sub("?", str(value))
    return text if len(text) <= limit else text[:limit] + "..."


def finite(value):
    """float(value) if it is a finite number (bools are not), else None."""
    if isinstance(value, bool):
        return None
    try:
        v = float(value)
    except (TypeError, ValueError):
        return None
    return v if math.isfinite(v) else None


def parse_m503(lines):
    """Parse collected M503 reply lines.

    Returns {"z": float or None, "error": str or None, "max_temps": {index: temp}}.
    Never guesses: no M206 line with a Z value -> z is None and error says so;
    two different Z values -> error.
    """
    zs = [z for z in (parse_home_offset_line(l) for l in lines) if z is not None]
    temps = {}
    for line in lines:
        t = parse_max_temp_line(line)
        if t is not None:
            temps[t[0]] = t[1]
    result = {"z": None, "error": None, "max_temps": temps}
    if not zs:
        result["error"] = "The printer's reply to M503 had no home offset line (M206 ... Z...)."
    elif len(set(round(z, 4) for z in zs)) > 1:
        result["error"] = "The printer's reply to M503 had more than one different M206 Z value: " + ", ".join(
            f"{z:g}" for z in zs
        )
    elif not math.isfinite(zs[0]):
        result["error"] = "The printer reported a home offset that is not a number."
    else:
        result["z"] = zs[0]
    return result


def validate_settings(reference_z, max_total_mm, steps):
    """Check the nozzle settings. Returns ({"reference_z", "max_total_mm", "steps"}, None)
    or (None, reason). reference_z may be unset (None or ""): then "reference_z" is None
    and changes are refused separately. Nothing falls back silently."""
    if reference_z is None or (isinstance(reference_z, str) and not reference_z.strip()):
        ref = None
    else:
        ref = finite(reference_z)
        if ref is None or ref < 0:
            return None, f"The reference value in the settings ({clean(reference_z)}) is not a valid number."
    window = finite(max_total_mm)
    if window is None or window < 0:
        return None, f"The window in the settings ({clean(max_total_mm)}) is not a valid number."
    if not isinstance(steps, (list, tuple)) or not steps:
        return None, "The step sizes in the settings are missing or not a list."
    clean_steps = []
    for s in steps:
        v = finite(s)
        if v is None or not (0 < v <= MAX_STEP_PER_ACTION_MM + 1e-9):
            return None, (
                f"Step size {clean(s)} in the settings is not between 0 and "
                f"{MAX_STEP_PER_ACTION_MM:g} mm."
            )
        clean_steps.append(round(v, 3))
    return {"reference_z": ref, "max_total_mm": window, "steps": clean_steps}, None


def target_value(before, direction, step_mm, raise_decreases_m206):
    """New M206 Z for a raise/lower of the nozzle, rounded to 0.01 mm."""
    if direction not in ("raise", "lower"):
        raise ValueError("direction must be 'raise' or 'lower'")
    decrease = (direction == "raise") == bool(raise_decreases_m206)
    new = before - step_mm if decrease else before + step_mm
    return round(new + 0.0, 2)


def check_adjust(before, direction, step_mm, allowed_steps, reference_z, max_total_mm, raise_decreases_m206):
    """Validate an adjustment. Returns (new_value, None) or (None, reason)."""
    if direction not in ("raise", "lower"):
        return None, "Direction must be 'raise' or 'lower'."
    step = finite(step_mm)
    if step is None:
        return None, "The step is not a number."
    allowed = [round(float(s), 3) for s in (allowed_steps or [])]
    if round(step, 3) not in allowed:
        return None, f"Step {step:g} mm is not one of the allowed steps ({', '.join(f'{s:g}' for s in allowed)} mm)."
    if not (0 < step <= MAX_STEP_PER_ACTION_MM + 1e-9):
        return None, f"Step {step:g} mm is not between 0 and the {MAX_STEP_PER_ACTION_MM:g} mm allowed in one action."
    if reference_z is None:
        return None, (
            "No reference value has been confirmed. An administrator must enter the reference "
            "M206 Z in Settings > EBMC Stop before the nozzle height can be changed."
        )
    before_v, ref, window = finite(before), finite(reference_z), finite(max_total_mm)
    if before_v is None or ref is None or window is None or window < 0:
        return None, "The current value, the reference or the window is not a valid number."
    new = target_value(before_v, direction, step, raise_decreases_m206)
    if new < 0:
        return None, f"The new value M206 Z{new:.2f} would be negative."
    low, high = round(ref - window, 2), round(ref + window, 2)
    if new < low - 1e-9 or new > high + 1e-9:
        return None, (
            f"The new value M206 Z{new:.2f} would be outside the allowed window "
            f"{low:.2f} to {high:.2f} (reference {ref:.2f} \u00b1 {window:g} mm)."
        )
    return new, None
