using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Download;
using ZERA.Core.Panels;

internal sealed class DavFixture : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        public string Root;
        public string Mode = "ok";
        public int Authorized, BadHeaders, NonzeroRanges;
        public long Sent;
        public readonly byte[] Data = Enumerable.Range(0, 4 * 1024 * 1024 + 31).Select(i => (byte)(i * 7 % 251)).ToArray();
        private const string Auth = "Basic dXNlcjpTRUNSRVQtUEFTUw==";
        public DavFixture()
        {
            _listener.Start(); Root = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/dav/";
            _ = Task.Run(async () => {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            });
        }
        private string Props(string href, string properties, int code = 200) => "<x:response><x:href>" + href + "</x:href><x:propstat><x:prop>" + properties + "</x:prop><x:status>HTTP/1.1 " + code + " OK</x:status></x:propstat></x:response>";
        private async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); var header = new MemoryStream(); var one = new byte[1];
                while (header.Length < 65536)
                {
                    if (await stream.ReadAsync(one, 0, 1) == 0) return;
                    header.WriteByte(one[0]);
                    if (header.Length >= 4 && Encoding.ASCII.GetString(header.GetBuffer(), (int)header.Length - 4, 4) == "\r\n\r\n") break;
                }
                string request = Encoding.ASCII.GetString(header.ToArray());
                var lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None); string method = lines[0].Split(' ')[0];
                string lengthHeader = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthHeader != null)
                {
                    int remaining = int.Parse(lengthHeader.Substring(lengthHeader.IndexOf(':') + 1));
                    var discard = new byte[4096];
                    while (remaining > 0) { int read = await stream.ReadAsync(discard, 0, Math.Min(discard.Length, remaining)); if (read == 0) return; remaining -= read; }
                }
                if (request.IndexOf("quark", StringComparison.OrdinalIgnoreCase) >= 0 || request.IndexOf("Cookie:", StringComparison.OrdinalIgnoreCase) >= 0) Interlocked.Increment(ref BadHeaders);
                if (Mode != "anonymous" && !request.Contains("Authorization: " + Auth)) { await Reply(stream, 401, new byte[0], false); return; }
                Interlocked.Increment(ref Authorized);
                if (Mode == "redirect") { await Reply(stream, 302, new byte[0], false, "Location: http://127.0.0.1:1/elsewhere\r\n"); return; }
                if (method == "PROPFIND")
                {
                    string xml = "<x:multistatus xmlns:x=\"DAV:\">" + Props("/dav/", "<x:resourcetype><x:collection/></x:resourcetype><x:quota-used-bytes>10</x:quota-used-bytes><x:quota-available-bytes>90</x:quota-available-bytes>");
                    if (request.Contains("Depth: 1")) xml += Props("/dav/%E4%B8%AD%E6%96%87%20%23.bin", "<x:resourcetype/><x:getcontentlength>" + Data.Length + "</x:getcontentlength>") +
                        Props("/dav/folder/", "<x:resourcetype><x:collection/></x:resourcetype>") + Props("/dav/missing", "<x:getcontentlength>99</x:getcontentlength>", 404) +
                        Props("http://example.com/stolen", "<x:resourcetype/>") + Props("/outside/file", "<x:resourcetype/>") + Props("/dav/deep/file", "<x:resourcetype/>");
                    xml += "</x:multistatus>";
                    if (Mode == "xml") xml = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><x:multistatus xmlns:x=\"DAV:\">&e;</x:multistatus>";
                    if (Mode == "html") { await Reply(stream, 200, Encoding.UTF8.GetBytes("<html>Login</html>"), false); return; }
                    await Reply(stream, 207, Encoding.UTF8.GetBytes(xml), false); return;
                }
                long start = 0, end = Data.Length - 1;
                string range = lines.FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
                if (range != null)
                {
                    var values = range.Substring(range.IndexOf('=') + 1).Split('-');
                    start = long.Parse(values[0]); if (values[1].Length > 0) end = long.Parse(values[1]);
                    end = Math.Min(end, Data.Length - 1); if (start > 0) Interlocked.Increment(ref NonzeroRanges);
                }
                string extra = "Accept-Ranges: bytes\r\n" + (range == null ? "" : "Content-Range: bytes " + start + "-" + end + "/" + Data.Length + "\r\n");
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (range == null ? "200 OK" : "206 Partial Content") + "\r\nContent-Length: " + (end - start + 1) + "\r\nConnection: close\r\n" + extra + "\r\n");
                await stream.WriteAsync(head, 0, head.Length);
                if (method == "HEAD") return;
                for (long pos = start; pos <= end;)
                {
                    int count = (int)Math.Min(16384, end - pos + 1);
                    await stream.WriteAsync(Data, (int)pos, count); pos += count; Interlocked.Add(ref Sent, count);
                    await Task.Delay(3);
                }
            }
            catch { }
        }
        private static async Task Reply(NetworkStream stream, int status, byte[] body, bool head, string extra = "")
        {
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" + extra + "\r\n");
            await stream.WriteAsync(header, 0, header.Length); if (!head) await stream.WriteAsync(body, 0, body.Length);
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }
