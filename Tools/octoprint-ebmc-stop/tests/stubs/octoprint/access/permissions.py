"""Test stand-in for octoprint.access.permissions."""


class _Permission:
    def __init__(self):
        self.allowed = True

    def can(self):
        return self.allowed


class Permissions:
    PRINT = _Permission()
    CONTROL = _Permission()
