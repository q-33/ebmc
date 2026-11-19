"""Test stand-in for the few flask calls the plugin makes."""


class HTTPException(Exception):
    def __init__(self, code, description=None):
        super().__init__(description)
        self.code = code
        self.description = description


def abort(code, description=None):
    raise HTTPException(code, description)


def jsonify(*args, **kwargs):
    return dict(*args, **kwargs)


def make_response(body, code):
    return {"status": code, "body": body}
