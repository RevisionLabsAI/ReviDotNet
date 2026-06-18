"""Tiny static file server for the Forge agent-instance mockups.

Serves this directory with `Cache-Control: no-store` so the live preview always
reflects what's on disk (plain `python -m http.server` lets the browser cache
HTML/CSS/JS and show stale versions after edits).
"""
import http.server
import os
import socketserver

PORT = 4599
DIRECTORY = os.path.dirname(os.path.abspath(__file__))


class NoCacheHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=DIRECTORY, **kwargs)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")
        self.send_header("Expires", "0")
        super().end_headers()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    with Server(("127.0.0.1", PORT), NoCacheHandler) as httpd:
        print(f"Serving {DIRECTORY} at http://127.0.0.1:{PORT} (no-store)")
        httpd.serve_forever()
