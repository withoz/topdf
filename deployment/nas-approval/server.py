import base64
import hashlib
import hmac
import html
import json
import os
import re
import secrets
import shutil
import sqlite3
import ssl
import threading
import time
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs

DATA = Path(os.getenv("TOPDF_DATA", "data"))
TLS = Path(os.getenv("TOPDF_TLS", "tls"))
TOKEN = re.compile(r"[0-9a-f]{64}\Z")
STATES = ("pending", "approved", "revoked")
RELEASES = DATA / "updates"
MAX_INSTALLER = 1024 * 1024 * 1024
VERSION = re.compile(r"(?:0|[1-9][0-9]{0,4})(?:\.(?:0|[1-9][0-9]{0,4})){2}\Z")


def release():
    try:
        return json.loads((RELEASES / "latest.json").read_text(encoding="ascii"))
    except FileNotFoundError:
        return None


def version_parts(version):
    return tuple(map(int, version.split(".")))


def load_admin():
    path = DATA / "admin-password.txt"
    if not path.exists():
        with path.open("x", encoding="ascii") as stream:
            stream.write(secrets.token_urlsafe(30) + "\n")
    password = path.read_text(encoding="ascii").strip()
    if len(password) < 24:
        raise ValueError("admin-password.txt must contain at least 24 characters")
    return password


def connect():
    db = sqlite3.connect(DATA / "approval.sqlite3", timeout=5)
    db.row_factory = sqlite3.Row
    return db


def initialize():
    DATA.mkdir(parents=True, exist_ok=True)
    with closing(connect()) as db, db:
        db.execute("""CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY, code TEXT UNIQUE NOT NULL, pc TEXT NOT NULL,
            username TEXT NOT NULL, status TEXT NOT NULL CHECK(status IN ('pending','approved','revoked')),
            first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, changed INTEGER NOT NULL,
            ip TEXT NOT NULL
        )""")
    return load_admin()


def check_device(token, pc, username, ip):
    if not isinstance(token, str) or not TOKEN.fullmatch(token):
        raise ValueError("Invalid device token")
    identity = hashlib.sha256(bytes.fromhex(token)).hexdigest()
    now = int(time.time())
    with closing(connect()) as db, db:
        db.execute("BEGIN IMMEDIATE")
        device = db.execute("SELECT code,status FROM devices WHERE id=?", (identity,)).fetchone()
        if device:
            db.execute("UPDATE devices SET last_seen=?,ip=? WHERE id=?", (now, ip, identity))
            return dict(device)
        if db.execute("SELECT count(*) FROM devices").fetchone()[0] >= 500:
            raise OverflowError("Device limit reached")
        code = secrets.token_hex(5).upper()
        db.execute("INSERT INTO devices VALUES(?,?,?,?,?,?,?,?,?)",
                   (identity, code, pc[:80], username[:80], "pending", now, now, now, ip))
        return {"code": code, "status": "pending"}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send_data(self, status, body, content_type, nonce=None, **headers):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        policy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; form-action 'self'"
        if nonce:
            policy += f"; script-src 'nonce-{nonce}'; connect-src 'self'"
        self.send_header("Content-Security-Policy", policy)
        for key, value in headers.items():
            self.send_header(key.replace("_", "-"), value)
        self.end_headers()
        self.wfile.write(body)

    def json(self, status, body):
        self.send_data(status, json.dumps(body).encode(), "application/json")

    def admin(self):
        expected = "Basic " + base64.b64encode(f"admin:{self.server.admin_password}".encode()).decode()
        if hmac.compare_digest(self.headers.get("Authorization", "").encode(), expected.encode()):
            return True
        self.send_data(401, b"Administrator login required", "text/plain", WWW_Authenticate='Basic realm="TOPDF approval"')
        return False

    def do_GET(self):
        if self.path != "/admin":
            return self.json(404, {"error": "Not found"})
        if not self.admin():
            return
        with closing(connect()) as db:
            rows = db.execute("SELECT * FROM devices ORDER BY status='pending' DESC, first_seen DESC").fetchall()
        labels = {"pending": "승인 대기", "approved": "승인됨", "revoked": "회수됨"}
        escape = html.escape
        def row(item):
            code = escape(item["code"])
            return (f'<tr><td><strong>{code}</strong></td><td>{escape(item["pc"])}</td>'
                    f'<td>{escape(item["username"])}</td><td>{escape(item["ip"])}</td>'
                    f'<td>{labels[item["status"]]}</td><td><form method="post" action="/admin/update">'
                    f'<input type="hidden" name="csrf" value="{self.server.csrf}">'
                    f'<input type="hidden" name="code" value="{code}">'
                    '<button name="action" value="approved">승인</button> '
                    '<button name="action" value="revoked">회수</button></form></td></tr>')
        latest = release()
        version = html.escape(latest["version"]) if latest else "없음"
        nonce = secrets.token_hex(16)
        update_panel = f"""<h2>TOPDF 업데이트</h2><p>현재 배포 버전 {version}</p>
            <form id="publish" data-csrf="{self.server.csrf}">
            <label>NAS 승인판 설치 파일 <input id="installer" type="file" accept=".exe" required></label>
            <button>업데이트 배포</button></form><p id="result" role="status"></p>
            <script nonce="{nonce}">
            document.getElementById('publish').addEventListener('submit', async (event) => {{
                event.preventDefault();
                const form = event.currentTarget;
                const button = form.querySelector('button');
                const output = document.getElementById('result');
                const file = document.getElementById('installer').files[0];
                if (!file || !confirm(file.name + '을 배포하시겠습니까?')) return;
                button.disabled = true;
                output.textContent = '업로드 중…';
                try {{
                    const response = await fetch('/admin/publish', {{method: 'POST', body: file,
                        headers: {{'X-Installer-Name': file.name, 'X-Topdf-CSRF': form.dataset.csrf}}}});
                    if (!response.ok) throw new Error('배포 실패 (' + response.status + ')');
                    location.reload();
                }} catch (error) {{ output.textContent = error.message; }}
                finally {{ button.disabled = false; }}
            }});
            </script>"""
        page = ('<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width">'
                '<title>TOPDF 승인 관리</title><style>body{font:16px system-ui;margin:2rem;color:#173047}'
                'table{border-collapse:collapse;width:100%}td,th{padding:.6rem;border-bottom:1px solid #ccd5dc;text-align:left}'
                'button{padding:.35rem .65rem}td:nth-child(5){white-space:nowrap}</style>'
                '<h1>TOPDF 승인 관리</h1><p>팀원 화면에 표시된 승인 코드와 일치하는 PC를 승인하세요.</p>' + update_panel +
                '<table><thead><tr><th>승인 코드</th><th>PC</th><th>Windows 사용자</th><th>접속 IP</th><th>상태</th><th>관리</th>'
                '</tr></thead><tbody>' + "".join(map(row, rows)) + '</tbody></table></html>')
        self.send_data(200, page.encode(), "text/html; charset=utf-8", nonce=nonce)

    def publish(self, length):
        if not self.admin():
            return
        if not hmac.compare_digest(self.headers.get("X-Topdf-CSRF", "").encode(), self.server.csrf.encode()):
            return self.json(403, {"error": "Invalid form token"})
        name = self.headers.get("X-Installer-Name", "")
        match = re.fullmatch(r"TOPDF-Setup-([0-9.]+)-NAS-Approval-win-x64\.exe", name)
        if not match or not VERSION.fullmatch(match[1]) or not 2 < length <= MAX_INSTALLER:
            return self.json(400, {"error": "Invalid NAS approval installer"})
        version = match[1]
        RELEASES.mkdir(exist_ok=True)
        temporary = RELEASES / (secrets.token_hex(16) + ".tmp")
        manifest = temporary.with_suffix(".json.tmp")
        digest = hashlib.sha256()
        try:
            with temporary.open("xb") as stream:
                remaining = length
                while remaining:
                    block = self.rfile.read(min(1024 * 1024, remaining))
                    if not block:
                        return self.json(400, {"error": "Incomplete installer"})
                    if remaining == length and not block.startswith(b"MZ"):
                        return self.json(400, {"error": "Invalid Windows installer"})
                    stream.write(block)
                    digest.update(block)
                    remaining -= len(block)
            with self.server.publish_lock:
                latest = release()
                if latest and version_parts(version) <= version_parts(latest["version"]):
                    return self.json(409, {"error": "Version must be newer"})
                info = dict(version=version, filename=name, sha256=digest.hexdigest(), size=length)
                manifest.write_text(json.dumps(info), encoding="ascii")
                temporary.replace(RELEASES / (info["sha256"] + ".exe"))
                manifest.replace(RELEASES / "latest.json")
            self.json(201, info)
        finally:
            temporary.unlink(missing_ok=True)
            manifest.unlink(missing_ok=True)

    def read_update_body(self):
        encoding = self.headers.get("Transfer-Encoding")
        lengths = self.headers.get_all("Content-Length", [])
        if encoding is None:
            if len(lengths) != 1 or not 0 < (length := int(lengths[0])) <= 4096:
                raise ValueError("Invalid request length")
            body = self.rfile.read(length)
            if len(body) != length:
                raise ValueError("Incomplete request")
            return body
        if self.headers.get_all("Transfer-Encoding") != [encoding] or encoding.lower() != "chunked" or lengths:
            raise ValueError("Invalid transfer encoding")
        body = bytearray()
        while True:
            line = self.rfile.readline(18)
            if not re.fullmatch(rb"[0-9a-fA-F]{1,16}\r\n", line):
                raise ValueError("Invalid chunk size")
            size = int(line, 16)
            if size == 0:
                if self.rfile.read(2) != b"\r\n" or not body:
                    raise ValueError("Invalid chunk terminator")
                return body
            if len(body) + size > 4096:
                raise ValueError("Invalid request length")
            chunk = self.rfile.read(size)
            if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                raise ValueError("Incomplete chunk")
            body.extend(chunk)

    def device_update(self):
        body = json.loads(self.read_update_body())
        token = body.get("token") if isinstance(body, dict) else None
        if not isinstance(token, str) or not TOKEN.fullmatch(token):
            return self.json(403, {"error": "Approval required"})
        identity = hashlib.sha256(bytes.fromhex(token)).hexdigest()
        with closing(connect()) as db:
            approved = db.execute("SELECT 1 FROM devices WHERE id=? AND status='approved'", (identity,)).fetchone()
        if not approved:
            return self.json(403, {"error": "Approval required"})
        latest = release()
        if self.path == "/device/update":
            return self.json(200, latest)
        if not latest or body.get("sha256") != latest["sha256"]:
            return self.json(409, {"error": "Check for updates again"})
        with (RELEASES / (latest["sha256"] + ".exe")).open("rb") as stream:
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(latest["size"]))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            shutil.copyfileobj(stream, self.wfile, 1024 * 1024)

    def do_POST(self):
        try:
            if self.path in ("/device/update", "/device/download"):
                return self.device_update()
            length = int(self.headers.get("Content-Length", "0"))
            if self.path == "/admin/publish":
                return self.publish(length)
            if not 0 < length <= 4096:
                return self.json(400, {"error": "Invalid request length"})
            if self.path == "/device/check":
                body = json.loads(self.rfile.read(length))
                if not isinstance(body, dict):
                    raise ValueError("Invalid device data")
                pc, username = body.get("pc"), body.get("username")
                if not isinstance(pc, str) or not isinstance(username, str):
                    raise ValueError("Invalid device data")
                return self.json(200, check_device(body.get("token"), pc, username, self.client_address[0]))
            if self.path == "/admin/update":
                if not self.admin():
                    return
                body = parse_qs(self.rfile.read(length).decode(), max_num_fields=5)
                if not hmac.compare_digest(body.get("csrf", [""])[0].encode(), self.server.csrf.encode()):
                    return self.json(403, {"error": "Invalid form token"})
                code, action = body.get("code", [""])[0], body.get("action", [""])[0]
                if action not in STATES[1:] or not re.fullmatch("[0-9A-F]{10}", code):
                    return self.json(400, {"error": "Invalid action"})
                with closing(connect()) as db, db:
                    db.execute("UPDATE devices SET status=?,changed=? WHERE code=?", (action, int(time.time()), code))
                return self.send_data(303, b"", "text/plain", Location="/admin")
            return self.json(404, {"error": "Not found"})
        except (ValueError, UnicodeError):
            self.json(400, {"error": "Invalid request"})
        except OverflowError:
            self.json(503, {"error": "Device limit reached"})
        except (ConnectionError, TimeoutError):
            self.close_connection = True


class Server(ThreadingHTTPServer):
    def get_request(self):
        conn, addr = super().get_request()
        conn.settimeout(5)
        return conn, addr


def main():
    os.umask(0o077)
    password = initialize()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(TLS / "server.crt", TLS / "server.key")
    with Server((os.getenv("TOPDF_BIND", "0.0.0.0"), 8743), Handler) as server:
        server.admin_password = password
        server.csrf = secrets.token_hex(32)
        server.publish_lock = threading.Lock()
        server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
        server.serve_forever()


if __name__ == "__main__":
    main()
