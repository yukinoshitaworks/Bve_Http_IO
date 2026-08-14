"""
BveEX_RockOn_115_Http 用の橋渡しサーバー。

- POST /bve/snapshot : BVEプラグインから車両状態(JSON)を受け取り、コンソールに表示する。
- GET  /bve/commands  : BVEプラグインが毎Tick取得する、現在のハンドル指令(reverser/power/brake)を返す。
- POST /bve/commands  : Node-REDダッシュボードなど外部から、ハンドル指令を更新する。
                        送られてきたキーだけを上書きする(部分更新)。

起動:
    python bve_http_server.py

127.0.0.1:5000 (ループバックのみ) で待ち受ける。
BVEプラグインもNode-REDも同じPC内からしかアクセスしないため、
0.0.0.0(全ネットワークインターフェース)で待ち受けると、同じLAN上の
他の端末から無認証でハンドル操作コマンドを送り込めてしまう。
"""

import json
import threading
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 5000

_lock = threading.Lock()
_commands = {"reverser": 0, "power": 0, "brake": 0}


class Handler(BaseHTTPRequestHandler):
    def _send_json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json_body(self):
        length = int(self.headers.get("Content-Length", 0))
        if length == 0:
            return {}
        raw = self.rfile.read(length)
        return json.loads(raw.decode("utf-8"))

    def do_GET(self):
        if self.path == "/bve/commands":
            with _lock:
                current = dict(_commands)
            self._send_json(200, current)
        else:
            self._send_json(404, {"error": "not found"})

    def do_POST(self):
        if self.path == "/bve/snapshot":
            try:
                data = self._read_json_body()
            except (ValueError, UnicodeDecodeError) as ex:
                self._send_json(400, {"error": f"invalid json: {ex}"})
                return

            timestamp = datetime.now().strftime("%H:%M:%S")
            print(f"[{timestamp}] snapshot: {data}")
            self._send_json(200, {"status": "ok"})

        elif self.path == "/bve/commands":
            try:
                data = self._read_json_body()
            except (ValueError, UnicodeDecodeError) as ex:
                self._send_json(400, {"error": f"invalid json: {ex}"})
                return

            with _lock:
                for key in ("reverser", "power", "brake"):
                    if key in data:
                        _commands[key] = int(data[key])
                current = dict(_commands)

            print(f"command updated -> {current}")
            self._send_json(200, current)

        else:
            self._send_json(404, {"error": "not found"})

    def log_message(self, format, *args):
        # デフォルトの毎リクエストログは省略し、上の print だけで様子を確認する
        pass


class SingleInstanceHTTPServer(ThreadingHTTPServer):
    # Windows の SO_REUSEADDR は「既にLISTEN中の同一ポートへの多重バインド」まで許してしまい、
    # 2つのサーバーが同じポートを奪い合って通信が不安定になる(BVE側でタイムアウト/キャンセルが
    # ランダムに発生する)。多重起動を検知できるよう、ここでは無効化して bind 失敗を起こす。
    allow_reuse_address = False


def main():
    try:
        server = SingleInstanceHTTPServer((HOST, PORT), Handler)
    except OSError as ex:
        print(f"[FATAL] Failed to bind {HOST}:{PORT} - another instance is likely already running: {ex}")
        raise SystemExit(1)

    print(f"Listening on http://{HOST}:{PORT}")
    print("  POST /bve/snapshot  <- BVE plugin")
    print("  GET  /bve/commands  <- BVE plugin")
    print("  POST /bve/commands  <- Node-RED dashboard etc.")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
