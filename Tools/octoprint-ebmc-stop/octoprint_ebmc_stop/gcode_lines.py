"""The STOP button's "after stop" G-code setting, parsed. Pure Python, no OctoPrint."""

import re

# G, M or T followed by a number (e.g. G28, M104, T0, G28.2). This rejects
# Smoothieware console commands such as "get pos" or "home".
COMMAND_WORD = re.compile(r"^[GMT]\d+(\.\d+)?$")
# Commands whose argument is free text; their case is kept.
MESSAGE_COMMANDS = ("M117", "M118")


def parse_gcode_lines(text):
    """Return (accepted, rejected) lines from the settings text.

    Blank lines and ";" comments are dropped. Lines are upper-cased
    (except the text of M117/M118).
    Lines that do not start with G, M or T followed by a number are rejected:
    Smoothieware would treat them as console commands.
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
