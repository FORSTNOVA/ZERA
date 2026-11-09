import http.server, hashlib, json, os, re, sys, time
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]/'scratch/mobile-fixture'
ROOT.mkdir(exist_ok=True)
DATA=bytes(i%251 for i in range(2*1024*1024+127))
(ROOT/'payload.bin').write_bytes(DATA)
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path=='/health':
            data=b'ok'; self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data);return
        if not self.path.startswith(('/payload.bin','/dav/payload.bin','/slow/payload.bin','/hold/payload.bin')):self.send_error(404);return
        if self.path.startswith('/dav/') and self.headers.get('Authorization')!='Basic dGVzdDpzZWNyZXQ=':self.send_error(401);return
        start,end=0,len(DATA)-1
        if self.headers.get('Range'):
            match=re.fullmatch(r'bytes=(\d+)-(\d*)',self.headers['Range']);start=int(match[1]);end=min(end,int(match[2] or end))
            self.send_response(206);self.send_header('Content-Range',f'bytes {start}-{end}/{len(DATA)}')
        else:self.send_response(200)
        self.send_header('Accept-Ranges','bytes');self.send_header('Content-Length',str(end-start+1));self.send_header('Content-Type','application/octet-stream');self.end_headers()
        for at in range(start,end+1,8192):
            try:self.wfile.write(DATA[at:min(at+8192,end+1)]);self.wfile.flush();time.sleep(.15 if self.path.startswith('/hold/') else .03 if self.path.startswith('/slow/') else .003)
            except (BrokenPipeError,ConnectionResetError):return
    do_HEAD=do_GET
    def do_PROPFIND(self):
        if self.headers.get('Authorization')!='Basic dGVzdDpzZWNyZXQ=':self.send_error(401);return
        self.rfile.read(int(self.headers.get('Content-Length',0)))
        xml=f'''<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:displayname>fixture</d:displayname><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response><d:response><d:href>/dav/payload.bin</d:href><d:propstat><d:prop><d:displayname>payload.bin</d:displayname><d:getcontentlength>{len(DATA)}</d:getcontentlength><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>'''.encode()
        self.send_response(207);self.send_header('Content-Type','application/xml');self.send_header('Content-Length',str(len(xml)));self.end_headers();self.wfile.write(xml)
    def log_message(self,*args):pass
print(json.dumps({'port':18880,'size':len(DATA),'sha256':hashlib.sha256(DATA).hexdigest()}),flush=True)
http.server.ThreadingHTTPServer(('127.0.0.1',18880),Handler).serve_forever()
