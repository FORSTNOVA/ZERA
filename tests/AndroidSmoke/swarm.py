"""A synthetic, loopback-only BT v1 swarm; run with the bundled Windows Python."""
import hashlib, http.server, json, struct, sys
from pathlib import Path
project=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(project/'src/ZERA.Core/Runtime/bt-split'))
import libtorrent as lt
root=project/'scratch/mobile-swarm';root.mkdir(parents=True,exist_ok=True)
data=bytes(i%251 for i in range(2*1024*1024+127));(root/'payload.bin').write_bytes(data)
fs=lt.file_storage();fs.add_file('payload.bin',len(data))
t=lt.create_torrent(fs,32768,lt.create_torrent.v1_only);t.add_tracker('http://127.0.0.1:18881/announce')
lt.set_piece_hashes(t,str(root));torrent=lt.bencode(t.generate());(root/'fixture.torrent').write_bytes(torrent)
info=lt.torrent_info(torrent)
session=lt.session({'listen_interfaces':'127.0.0.1:18900','enable_dht':False,'enable_lsd':False,'enable_upnp':False,'enable_natpmp':False,'enable_outgoing_utp':False,'enable_incoming_utp':False,'allow_multiple_connections_per_ip':True})
handle=session.add_torrent({'ti':info,'save_path':str(root),'flags':lt.torrent_flags.seed_mode})
private=lt.create_torrent(fs,32768,lt.create_torrent.v1_only);private.set_priv(True);private.add_tracker('http://127.0.0.1:18881/announce');lt.set_piece_hashes(private,str(root))
private_data=lt.bencode(private.generate());(root/'private.torrent').write_bytes(private_data)
private_handle=session.add_torrent({'ti':lt.torrent_info(private_data),'save_path':str(root),'flags':lt.torrent_flags.seed_mode})
class Tracker(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path=='/status':
            status=handle.status()
            body=json.dumps({'state':str(status.state),'seeding':status.is_seeding,'paused':bool(status.flags&lt.torrent_flags.paused),'peers':status.num_peers,'progress':status.progress,'error':str(status.errc),'alerts':[a.message() for a in session.pop_alerts()]}).encode()
            self.send_response(200);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body);return
        body=lt.bencode({'interval':1,'complete':1,'incomplete':0,'peers':bytes([127,0,0,1])+struct.pack('!H',18900)})
        self.send_response(200);self.send_header('Content-Type','application/x-bittorrent');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def log_message(self,*args):pass
print(json.dumps({'torrent':str(root/'fixture.torrent'),'infoHash':str(info.info_hash()),'sha256':hashlib.sha256(data).hexdigest()}),flush=True)
http.server.ThreadingHTTPServer(('127.0.0.1',18881),Tracker).serve_forever()
