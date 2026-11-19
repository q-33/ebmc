"""Test stand-in for octoprint.events (names as in OctoPrint 1.11)."""


class Events:
    PRINTER_STATE_CHANGED = "PrinterStateChanged"
    TRANSFER_STARTED = "TransferStarted"
    TRANSFER_DONE = "TransferDone"
    TRANSFER_FAILED = "TransferFailed"
    DISCONNECTED = "Disconnected"
    ERROR = "Error"
