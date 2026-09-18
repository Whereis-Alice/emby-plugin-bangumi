"""Loopback-only, read-only Emby UI preview with working-tree assets.

Usage: python scripts/preview-episode-ui.py --token-file D:/emby-bgm-bridge/token.json
The real access token stays in this process. A dummy preview token seeds the isolated
preview origin; every upstream GET replaces it with the existing local credential.
All browser writes/playback requests are rejected. Stop the process after UI QA.
"""
import argparse
import json
import re
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import build_opener, ProxyHandler, Request
from urllib.error import HTTPError
from urllib.parse import urlsplit

parser = argparse.ArgumentParser()
parser.add_argument('--token-file', required=True)
parser.add_argument('--port', type=int, default=18196)
parser.add_argument('--item-id', help='Optional Emby item to open instead of Home')
args = parser.parse_args()
credential = json.loads(Path(args.token_file).read_text(encoding='utf-8-sig'))
upstream = 'http://127.0.0.1:8096'
origin = f'http://127.0.0.1:{args.port}'
assets = Path(__file__).resolve().parents[1] / 'src/Emby.Plugins.Bangumi/Web/Assets'
opener = build_opener(ProxyHandler({}))
with opener.open(upstream + '/emby/System/Info/Public') as response:
    info = json.load(response)
server = dict(Id=info['Id'], Name=info['ServerName'], LocalAddress=origin,
              ManualAddress=origin, RemoteAddress=origin, ManualAddressOnly=True,
              LastConnectionMode=2, DateLastAccessed=9999999999999,
              UserId=credential['userId'], AccessToken='preview-only')
server['Users'] = [dict(UserId=credential['userId'], AccessToken='preview-only',
                        DateLastAccessed=9999999999999)]
bootstrap = ('<script>localStorage.setItem("servercredentials3",' +
             json.dumps(json.dumps(dict(Servers=[server]))) + ');</script>')


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass  # Do not log authenticated URLs or credentials.

    def send(self, status, data, content_type):
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        # Emby requests track/version info by POST even before playback. Preview it
        # through the read-only GET variant; never forward a browser mutation.
        if re.fullmatch(r'/emby/Items/\d+/PlaybackInfo', urlsplit(self.path).path):
            self.rfile.read(int(self.headers.get('Content-Length', '0')))
            return self.do_GET()
        self.send(403, b'Read-only UI preview', 'text/plain')

    def do_PUT(self):
        self.send(403, b'Read-only UI preview', 'text/plain')
    do_DELETE = do_PUT
    do_PATCH = do_PUT

    def do_GET(self):
        path = urlsplit(self.path).path
        name = path.rsplit('/', 1)[-1]
        if path.startswith('/emby/Bangumi/Ui/') and name in ('bangumi-ui.js', 'bangumi-ui.css', 'episode-navigator.js'):
            return self.send(200, (assets / name).read_bytes(), 'text/css' if name.endswith('.css') else 'application/javascript')
        if path == '/emby/Bangumi/Ui/Options':
            return self.send(200, b'{"EpisodeNavigator":true}', 'application/json')
        try:
            req = Request(upstream + self.path, headers={'X-Emby-Token': credential['accessToken'], 'Accept-Encoding': 'identity'})
            with opener.open(req, timeout=30) as response:
                data = response.read()
                content_type = response.headers.get('Content-Type', 'application/octet-stream')
                if path == '/web/index.html':
                    data = data.replace(b'<head>', b'<head>' + bootstrap.encode('utf-8'), 1)
                self.send(response.status, data, content_type)
        except HTTPError as error:
            with error:
                self.send(error.code, error.read(), error.headers.get('Content-Type', 'text/plain'))
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            self.send(502, b'Preview upstream unavailable', 'text/plain')


route = f'item?id={int(args.item_id)}&serverId={info["Id"]}' if args.item_id else 'home'
print(f'Read-only preview: {origin}/web/index.html#!/{route}', flush=True)
ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()
