"""Test stand-in for octoprint.server.current_user."""


class _User:
    def get_name(self):
        return "tester"


current_user = _User()
