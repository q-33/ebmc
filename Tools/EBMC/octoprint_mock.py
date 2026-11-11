#!/usr/bin/env python3
"""A small mock of the OctoPrint REST API, for testing EBMC's OctoPrint support without a Raspberry Pi.

Python 3 standard library only (works on 3.9 and on 3.13+, which no longer has the ``cgi`` module).
It listens on 127.0.0.1 only. Uploaded files are kept in a temporary directory that is deleted on exit.

Usage
-----
    python3 Tools/EBMC/octoprint_mock.py --port 5050 --key testkey \
        [--slow-upload-kbps N] [--state closed|operational|printing]

What it implements (see https://docs.octoprint.org/en/master/api/):

* ``X-Api-Key`` is required on ``/api/*`` (403 with ``{"error": ...}`` otherwise). The Application Keys
  endpoints are unauthenticated, as in OctoPrint.
* ``GET /api/version``, ``GET /api/server``
* ``POST /api/files/local`` - multipart upload with parts ``file``, ``select``, ``print``. Content-Length is
  required (411 without it, or when the body is chunked). The name is sanitised much like OctoPrint does
  (directories dropped, unsafe characters removed, accents stripped, whitespace turned into ``_``).
  415 unless .gcode/.gco/.g/.stl, 409 if the name is the file being printed. 201 with a Location header.
  ``effectiveSelect``/``effectivePrint`` are true only when requested AND the printer is operational and idle;
  in that case printing that file starts. Asking to print while the printer is closed is not an error.
* ``GET /api/job``, ``POST /api/job`` (``start``/``cancel``; 409 when not applicable; 204)
* ``GET /api/printer`` - 409 when the printer is not connected, otherwise state flags and temperatures
* ``GET /api/connection``, ``POST /api/connection`` (``connect`` becomes Operational after about 2 s;
  ``disconnect``; 400 for a port or baud rate that is not offered)
* ``GET /plugin/appkeys/probe`` (204), ``POST /plugin/appkeys/request`` (201),
  ``GET /plugin/appkeys/request/<token>`` (202 until approved, then 200 with the server's key once;
  404 if denied or if it was not polled for more than 5 s)

Unauthenticated test controls:

* ``POST /mock/approve/<token>``, ``POST /mock/deny/<token>``
* ``POST /mock/state?printer=closed|operational|printing``
* ``GET /mock/files`` - saved uploads with sizes and SHA-256

Every request is logged on one line to stderr; API keys are never printed.

curl walkthrough
----------------
    python3 Tools/EBMC/octoprint_mock.py --port 5050 --key testkey &
    B=http://127.0.0.1:5050

    curl -i -H 'X-Api-Key: testkey' $B/api/version          # 200
    curl -i -H 'X-Api-Key: wrong'   $B/api/version          # 403

    printf 'G28\\nG1 X10\\n' > /tmp/cube.gcode
    curl -i -H 'X-Api-Key: testkey' -F 'file=@/tmp/cube.gcode' -F select=true -F print=true \
        $B/api/files/local                                   # 201, effectivePrint false (printer closed)
    curl -i -X POST "$B/mock/state?printer=operational"
    curl -i -H 'X-Api-Key: testkey' -F 'file=@/tmp/cube.gcode' -F print=true \
        $B/api/files/local                                   # 201, effectivePrint true, now printing
    curl -i -H 'X-Api-Key: testkey' -F 'file=@/tmp/cube.gcode' $B/api/files/local   # 409, file in use
    curl -s $B/mock/files

    # Application Keys workflow
    curl -i $B/plugin/appkeys/probe                          # 204
    T=$(curl -s -X POST -H 'Content-Type: application/json' -d '{"app":"EBMC"}' \
        $B/plugin/appkeys/request | python3 -c 'import json,sys; print(json.load(sys.stdin)["app_token"])')
    curl -i $B/plugin/appkeys/request/$T                     # 202
    curl -i -X POST $B/mock/approve/$T
    curl -i $B/plugin/appkeys/request/$T                     # 200 {"api_key": "testkey"}

    kill %1
"""

import argparse
import email.parser
import email.policy
import hashlib
import json
import os
import re
import secrets
import sys
import tempfile
import threading
import time
import unicodedata
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, quote, unquote, urlsplit

ALLOWED_EXTENSIONS = (".gcode", ".gco", ".g", ".stl")
APPKEY_STALE_SECONDS = 5.0
CONNECT_DELAY_SECONDS = 2.0
MOCK_PRINT_SECONDS = 600.0
PORTS = ["/dev/ttyACM0", "/dev/ttyUSB0"]
BAUDRATES = [250000, 230400, 115200]


def sanitize_name(name):
    """Roughly what OctoPrint's local storage does to an uploaded file name."""
    name = (name or "").replace("\\", "/").split("/")[-1]
    name = unicodedata.normalize("NFKD", name).encode("ascii", "ignore").decode("ascii")
    name = re.sub(r'[?%*:|"<>;$&\x00-\x1f\x7f]', "", name)
    name = re.sub(r"\s+", "_", name.strip())
    return name.lstrip(".")


class MockState:
    def __init__(self, key, printer, upload_dir, slow_kbps):
        self.key = key
        self.lock = threading.Lock()
        self.upload_dir = upload_dir
        self.slow_kbps = slow_kbps
        self.connect_at = None
        self.port = None
        self.baudrate = None
        self.selected = None
        self.print_started = None
        self.appkeys = {}
        self.set_printer(printer)

    # Callers hold the lock for everything below

    def set_printer(self, printer):
        self.printer = printer
        if printer == "closed":
            self.port = self.baudrate = None
            self.selected = None
            self.print_started = None
        else:
            self.port, self.baudrate = PORTS[0], BAUDRATES[0]
        if printer == "operational":
            self.print_started = None
        if printer == "printing":
            if self.selected is None:
                self.selected = "mock_job.gcode"
            self.print_started = time.time()

    def tick(self):
        if self.printer == "connecting" and time.time() >= self.connect_at:
            self.set_printer("operational")

    def state_text(self):
        return {"closed": "Closed", "connecting": "Connecting", "operational": "Operational", "printing": "Printing"}[self.printer]

    def printing_file(self):
        return self.selected if self.printer == "printing" else None


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "OctoPrintMock/1.0"
    state = None  # set in main()

    # ---------- plumbing ----------

    def log_request(self, code="-", size="-"):
        pass  # replaced by our own single line in send()

    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (time.strftime("%H:%M:%S"), fmt % args))

    def key_label(self):
        supplied = self.headers.get("X-Api-Key")
        if supplied is None:
            return "key=none"
        return "key=ok" if supplied == self.state.key else "key=WRONG(%d chars)" % len(supplied)

    def safe_path(self):
        parts = urlsplit(self.path)
        query = re.sub(r"(?i)(apikey=)[^&]*", r"\1***", parts.query)
        return parts.path + ("?" + query if query else "")

    def send(self, status, body=None, headers=None, close=False):
        data = b""
        if body is not None:
            data = json.dumps(body).encode("utf-8")
        self.send_response(status)
        if body is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        if close:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        if data and self.command != "HEAD":
            self.wfile.write(data)
        self.log_message("%s %s -> %d %s", self.command, self.safe_path(), status, self.key_label())

    def error(self, status, message, close=False):
        self.send(status, {"error": message}, close=close)

    def read_body(self, throttle=False):
        length = self.headers.get("Content-Length")
        if length is None:
            return None
        remaining = int(length)
        chunks = []
        kbps = self.state.slow_kbps if throttle else None
        while remaining > 0:
            chunk = self.rfile.read(min(remaining, 4096))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
            if kbps:
                time.sleep(len(chunk) / (kbps * 1024.0))
        return b"".join(chunks)

    def json_body(self):
        raw = self.read_body() or b""
        try:
            value = json.loads(raw.decode("utf-8") or "null")
        except ValueError:
            return None
        return value if isinstance(value, dict) else None

    def authorized(self):
        return self.headers.get("X-Api-Key") == self.state.key

    def base_url(self):
        return "http://%s" % (self.headers.get("Host") or "%s:%d" % self.server.server_address[:2])

    # ---------- routing ----------

    def do_GET(self):
        self.route("GET")

    def do_POST(self):
        self.route("POST")

    def route(self, method):
        path = urlsplit(self.path).path
        st = self.state

        if path.startswith("/api/"):
            if not self.authorized():
                # Drain a body we can measure; otherwise drop the connection
                if self.headers.get("Content-Length") is not None:
                    self.read_body()
                    return self.error(403, "Invalid or missing API key")
                return self.error(403, "Invalid or missing API key", close=method == "POST")

        with st.lock:
            st.tick()

        routes = {
            ("GET", "/api/version"): self.get_version,
            ("GET", "/api/server"): self.get_server,
            ("POST", "/api/files/local"): self.post_upload,
            ("GET", "/api/job"): self.get_job,
            ("POST", "/api/job"): self.post_job,
            ("GET", "/api/printer"): self.get_printer,
            ("GET", "/api/connection"): self.get_connection,
            ("POST", "/api/connection"): self.post_connection,
            ("GET", "/plugin/appkeys/probe"): self.get_probe,
            ("POST", "/plugin/appkeys/request"): self.post_appkey_request,
            ("POST", "/mock/state"): self.post_mock_state,
            ("GET", "/mock/files"): self.get_mock_files,
        }
        handler = routes.get((method, path))
        if handler:
            return handler()

        match = re.match(r"^/plugin/appkeys/request/([^/]+)$", path)
        if match and method == "GET":
            return self.get_appkey_decision(unquote(match.group(1)))
        match = re.match(r"^/mock/(approve|deny)/([^/]+)$", path)
        if match and method == "POST":
            return self.post_mock_decision(match.group(1), unquote(match.group(2)))

        if method == "POST":
            self.read_body()
        self.error(404, "Not found")

    # ---------- /api ----------

    def get_version(self):
        self.send(200, {"api": "0.1", "server": "1.11.8", "text": "OctoPrint 1.11.8"})

    def get_server(self):
        self.send(200, {"version": "1.11.8", "safemode": None})

    def post_upload(self):
        st = self.state
        if self.headers.get("Content-Length") is None or "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            return self.error(411, "Content-Length required", close=True)

        content_type = self.headers.get("Content-Type", "")
        body = self.read_body(throttle=True)

        ctype = email.parser.BytesHeaderParser(policy=email.policy.HTTP).parsebytes(
            b"Content-Type: " + content_type.encode("latin-1") + b"\r\n\r\n")
        boundary = ctype.get_param("boundary")
        if ctype.get_content_type() != "multipart/form-data" or not boundary:
            return self.error(400, "Expected multipart/form-data")

        fields, file_name, file_data = {}, None, None
        for name, filename, data in parse_multipart(body, boundary.encode("latin-1")):
            if name == "file" and filename is not None:
                file_name, file_data = filename, data
            elif name:
                fields[name] = data.decode("utf-8", "replace").strip()

        if file_data is None:
            return self.error(400, "No file included")

        name = sanitize_name(file_name)
        if not name:
            return self.error(400, "Invalid file name")
        if not name.lower().endswith(ALLOWED_EXTENSIONS):
            return self.error(415, "Could not upload the file \"%s\", invalid type" % name)

        want_print = fields.get("print", "false").lower() == "true"
        want_select = want_print or fields.get("select", "false").lower() == "true"

        with st.lock:
            if st.printing_file() == name:
                return self.error(409, "Trying to overwrite file that is currently being printed: %s" % name)

            with open(os.path.join(st.upload_dir, name), "wb") as f:
                f.write(file_data)

            idle = st.printer == "operational"
            effective_select = want_select and idle
            effective_print = want_print and idle
            if effective_select:
                st.selected = name
            if effective_print:
                st.set_printer("printing")

        base = self.base_url()
        resource = "%s/api/files/local/%s" % (base, quote(name))
        self.send(201, {
            "files": {"local": {
                "name": name,
                "origin": "local",
                "refs": {"resource": resource, "download": "%s/downloads/files/local/%s" % (base, quote(name))},
            }},
            "done": True,
            "effectiveSelect": effective_select,
            "effectivePrint": effective_print,
        }, headers={"Location": resource})

    def get_job(self):
        st = self.state
        with st.lock:
            name = st.selected
            size = None
            if name and os.path.exists(os.path.join(st.upload_dir, name)):
                size = os.path.getsize(os.path.join(st.upload_dir, name))
            completion = print_time = time_left = None
            if st.printer == "printing" and st.print_started:
                print_time = time.time() - st.print_started
                completion = min(100.0, 100.0 * print_time / MOCK_PRINT_SECONDS)
                time_left = max(0, int(MOCK_PRINT_SECONDS - print_time))
                print_time = int(print_time)
            body = {
                "job": {
                    "file": {"name": name, "origin": "local" if name else None, "size": size, "date": None},
                    "estimatedPrintTime": MOCK_PRINT_SECONDS if name else None,
                },
                "progress": {"completion": completion, "filepos": None, "printTime": print_time, "printTimeLeft": time_left},
                "state": st.state_text() if st.printer != "closed" else "Offline",
            }
        self.send(200, body)

    def post_job(self):
        st = self.state
        body = self.json_body()
        if body is None or "command" not in body:
            return self.error(400, "Expected a JSON body with a command")
        command = body["command"]
        with st.lock:
            if command == "start":
                if st.printer != "operational" or not st.selected:
                    return self.error(409, "Printer is not operational or no file is selected")
                st.set_printer("printing")
            elif command == "cancel":
                if st.printer != "printing":
                    return self.error(409, "No active print job to cancel")
                st.set_printer("operational")
            else:
                return self.error(400, "Unknown command: %s" % command)
        self.send(204)

    def get_printer(self):
        st = self.state
        with st.lock:
            if st.printer in ("closed", "connecting"):
                return self.error(409, "Printer is not operational")
            printing = st.printer == "printing"
            body = {
                "state": {
                    "text": st.state_text(),
                    "flags": {
                        "operational": True, "paused": False, "printing": printing, "cancelling": False,
                        "pausing": False, "sdReady": False, "error": False, "ready": not printing,
                        "closedOrError": False,
                    },
                },
                "temperature": {
                    "tool0": {"actual": 214.6 if printing else 23.8, "target": 215.0 if printing else 0.0, "offset": 0},
                    "bed": {"actual": 59.8 if printing else 22.1, "target": 60.0 if printing else 0.0, "offset": 0},
                },
            }
        self.send(200, body)

    def get_connection(self):
        st = self.state
        with st.lock:
            body = {
                "current": {
                    "state": st.state_text(),
                    "port": st.port,
                    "baudrate": st.baudrate,
                    "printerProfile": "_default",
                },
                "options": {
                    "ports": PORTS,
                    "baudrates": BAUDRATES,
                    "printerProfiles": [{"name": "Default", "id": "_default"}],
                    "portPreference": None,
                    "baudratePreference": None,
                    "printerProfilePreference": "_default",
                    "autoconnect": False,
                },
            }
        self.send(200, body)

    def post_connection(self):
        st = self.state
        body = self.json_body()
        if body is None or "command" not in body:
            return self.error(400, "Expected a JSON body with a command")
        command = body["command"]
        with st.lock:
            if command == "connect":
                port = body.get("port", PORTS[0])
                baud = body.get("baudrate", BAUDRATES[0])
                if port not in PORTS:
                    return self.error(400, "Invalid port: %s" % port)
                if baud not in BAUDRATES:
                    return self.error(400, "Invalid baudrate: %s" % baud)
                if st.printer == "closed":
                    st.printer = "connecting"
                    st.connect_at = time.time() + CONNECT_DELAY_SECONDS
                    st.port, st.baudrate = port, baud
            elif command == "disconnect":
                st.set_printer("closed")
            else:
                return self.error(400, "Unknown command: %s" % command)
        self.send(204)

    # ---------- application keys ----------

    def get_probe(self):
        self.send(204)

    def post_appkey_request(self):
        body = self.json_body()
        if body is None or not body.get("app"):
            return self.error(400, "app is required")
        token = secrets.token_urlsafe(16)
        with self.state.lock:
            self.state.appkeys[token] = {"app": body["app"], "user": body.get("user"), "status": "pending", "last_poll": time.time()}
        base = self.base_url()
        self.log_message("appkeys: request from app %r, token %s", body["app"], token)
        self.send(201, {"app_token": token, "auth_dialog": "%s/plugin/appkeys/auth/%s" % (base, token)},
                  headers={"Location": "%s/plugin/appkeys/request/%s" % (base, token)})

    def get_appkey_decision(self, token):
        st = self.state
        with st.lock:
            entry = st.appkeys.get(token)
            now = time.time()
            if entry is None:
                return self.send(404)
            if now - entry["last_poll"] > APPKEY_STALE_SECONDS:
                del st.appkeys[token]
                return self.send(404)
            entry["last_poll"] = now
            if entry["status"] == "pending":
                return self.send(202)
            del st.appkeys[token]
            if entry["status"] == "denied":
                return self.send(404)
            key = st.key
        self.send(200, {"api_key": key})

    # ---------- test controls ----------

    def post_mock_decision(self, decision, token):
        self.read_body()
        with self.state.lock:
            entry = self.state.appkeys.get(token)
            if entry is None:
                return self.error(404, "Unknown or expired token")
            entry["status"] = "approved" if decision == "approve" else "denied"
        self.send(204)

    def post_mock_state(self):
        self.read_body()
        printer = parse_qs(urlsplit(self.path).query).get("printer", [None])[0]
        if printer not in ("closed", "operational", "printing"):
            return self.error(400, "printer must be closed, operational or printing")
        with self.state.lock:
            self.state.set_printer(printer)
        self.send(204)

    def get_mock_files(self):
        files = []
        with self.state.lock:
            for name in sorted(os.listdir(self.state.upload_dir)):
                with open(os.path.join(self.state.upload_dir, name), "rb") as f:
                    data = f.read()
                files.append({"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            current = {"printer": self.state.printer, "selected": self.state.selected}
        self.send(200, {"files": files, "state": current})


def parse_multipart(body, boundary):
    """Yields (name, filename, data) for each part of a multipart/form-data body."""
    delimiter = b"--" + boundary
    header_parser = email.parser.BytesHeaderParser(policy=email.policy.HTTP)
    segments = body.split(b"\r\n" + delimiter)
    # The first delimiter has no CRLF before it
    first = segments[0]
    if first.startswith(delimiter):
        segments[0] = first[len(delimiter):]
    for segment in segments:
        if segment.startswith(b"--"):
            break
        if segment.startswith(b"\r\n"):
            segment = segment[2:]
        head, sep, data = segment.partition(b"\r\n\r\n")
        if not sep:
            continue
        headers = header_parser.parsebytes(head + b"\r\n\r\n")
        name = headers.get_param("name", header="content-disposition")
        filename = headers.get_filename()
        yield name, filename, data


def main():
    parser = argparse.ArgumentParser(description="Mock OctoPrint server for EBMC testing (binds 127.0.0.1).")
    parser.add_argument("--port", type=int, default=5050)
    parser.add_argument("--key", default="testkey", help="the API key clients must send in X-Api-Key")
    parser.add_argument("--slow-upload-kbps", type=float, default=None, help="throttle reading uploads to N KiB/s")
    parser.add_argument("--state", choices=("closed", "operational", "printing"), default="closed")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="octoprint-mock-") as upload_dir:
        Handler.state = MockState(args.key, args.state, upload_dir, args.slow_upload_kbps)
        server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
        server.daemon_threads = True
        sys.stderr.write("OctoPrint mock on http://127.0.0.1:%d (printer %s, uploads in %s)\n"
                         % (server.server_address[1], args.state, upload_dir))
        sys.stderr.flush()
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()


if __name__ == "__main__":
    main()
