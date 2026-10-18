using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A private, loopback-only tracker and BitTorrent peer. No public swarm or copyrighted fixture.
internal sealed class LocalSwarm : IDisposable
{
    private readonly TcpListener _tracker = new TcpListener(IPAddress.Loopback, 0);
    private readonly TcpListener _peer = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    internal readonly byte[] Payload;
    internal readonly byte[] Torrent;
    internal readonly byte[] InfoHash;
    internal readonly string Name;
    internal int DelayMs = 12;
    internal long Transferred;
    internal int TrackerRequests;
    internal readonly ConcurrentDictionary<string, ConcurrentBag<int>> PieceRequests = new ConcurrentDictionary<string, ConcurrentBag<int>>();
    private const int PieceLength = 65536;

    internal LocalSwarm(int length, string name, bool multiple = false, bool isPrivate = true, bool includeTracker = true)
    {
        Name = name;
        Payload = Enumerable.Range(0, length).Select(i => (byte)((i * 31 + i / 137) % 251)).ToArray();
        _tracker.Start(); _peer.Start();
        byte[] pieces;
        using (var sha = SHA1.Create())
            pieces = Enumerable.Range(0, (length + PieceLength - 1) / PieceLength)
                .SelectMany(i => sha.ComputeHash(Payload, i * PieceLength, Math.Min(PieceLength, length - i * PieceLength))).ToArray();
        var info = new SortedDictionary<string, object> {
            ["name"] = name, ["piece length"] = PieceLength, ["pieces"] = pieces, ["private"] = isPrivate ? 1 : 0
        };
        if (multiple)
            info["files"] = new object[] {
                new SortedDictionary<string, object> { ["length"] = length / 2, ["path"] = new object[] { "a.bin" } },
                new SortedDictionary<string, object> { ["length"] = length - length / 2, ["path"] = new object[] { "子目录", "b.bin" } }
            };
        else info["length"] = length;
        byte[] encodedInfo = Encode(info);
        using (var sha = SHA1.Create()) InfoHash = sha.ComputeHash(encodedInfo);
        Torrent = Encode(new SortedDictionary<string, object> {
            ["announce"] = includeTracker ? TrackerUrl : "",
            ["info"] = info
        });
        Task.Run(() => TrackerLoop());
        Task.Run(() => PeerLoop());
    }

    internal string TrackerUrl => "http://127.0.0.1:" + ((IPEndPoint)_tracker.LocalEndpoint).Port + "/announce";

    internal string Magnet => "magnet:?xt=urn:btih:" + BitConverter.ToString(InfoHash).Replace("-", "").ToLowerInvariant() +
        "&dn=" + Uri.EscapeDataString(Name) + "&tr=" + Uri.EscapeDataString("http://127.0.0.1:" + ((IPEndPoint)_tracker.LocalEndpoint).Port + "/announce");

    private async Task TrackerLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tracker.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(async () => {
                using (client)
                try
                {
                    var stream = client.GetStream();
                    var request = new List<byte>();
                    while (request.Count < 65536)
                    {
                        byte[] one = new byte[1];
                        if (await stream.ReadAsync(one, 0, 1) == 0) return;
                        request.Add(one[0]);
                        if (request.Count >= 4 && Encoding.ASCII.GetString(request.Skip(request.Count - 4).ToArray()) == "\r\n\r\n") break;
                    }
                    int port = ((IPEndPoint)_peer.LocalEndpoint).Port;
                    Interlocked.Increment(ref TrackerRequests);
                    var body = Encode(new SortedDictionary<string, object> {
                        ["interval"] = 1, ["complete"] = 1, ["incomplete"] = 0,
                        ["peers"] = new byte[] { 127, 0, 0, 1, (byte)(port >> 8), (byte)port }
                    });
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                }
                catch { }
            });
        }
    }

    private async Task PeerLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _peer.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => ServePeer(client));
        }
    }

    private async Task ServePeer(TcpClient client)
    {
        using (client)
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            byte[] handshake = await Read(stream, 68);
            if (handshake[0] != 19 || !handshake.Skip(28).Take(20).SequenceEqual(InfoHash)) return;
            var requests = PieceRequests.GetOrAdd(Guid.NewGuid().ToString("N"), _ => new ConcurrentBag<int>());
            byte[] reply = new byte[68];
            reply[0] = 19;
            Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(reply, 1);
            // Advertise BEP 10 metadata support; no public discovery is used by this private torrent.
            reply[25] = 0x10;
            InfoHash.CopyTo(reply, 28);
            Encoding.ASCII.GetBytes("-ZRTEST-123456789012").CopyTo(reply, 48);
            await stream.WriteAsync(reply, 0, reply.Length);
            int count = (Payload.Length + PieceLength - 1) / PieceLength;
            byte[] bitfield = new byte[(count + 7) / 8];
            for (int i = 0; i < count; i++) bitfield[i / 8] |= (byte)(0x80 >> (i % 8));
            await Message(stream, 5, bitfield);
            await Message(stream, 1, new byte[0]);
            byte[] info = ExtractInfo(Torrent);
            await Message(stream, 20, new byte[] { 0 }.Concat(Encode(new SortedDictionary<string, object> {
                ["m"] = new SortedDictionary<string, object> { ["ut_metadata"] = 1 }, ["metadata_size"] = info.Length
            })).ToArray());
            int clientMetadataId = 1;
            while (!_stop.IsCancellationRequested)
            {
                int length = BigInt(await Read(stream, 4), 0);
                if (length == 0) continue;
                if (length < 1 || length > 1024 * 1024) return;
                byte[] message = await Read(stream, length);
                if (message[0] == 6 && length == 13)
                {
                    int piece = BigInt(message, 1), offset = BigInt(message, 5), size = BigInt(message, 9);
                    requests.Add(piece);
                    int start = piece * PieceLength + offset;
                    if (start < 0 || size < 1 || size > 65536 || start + size > Payload.Length) return;
                    await Task.Delay(DelayMs);
                    await Message(stream, 7, message.Skip(1).Take(8).Concat(Payload.Skip(start).Take(size)).ToArray());
                    Interlocked.Add(ref Transferred, size);
                }
                else if (message[0] == 20 && message.Length > 2)
                {
                    if (message[1] == 0)
                    {
                        string header = Encoding.ASCII.GetString(message, 2, message.Length - 2);
                        var match = System.Text.RegularExpressions.Regex.Match(header, "11:ut_metadatai([0-9]+)e");
                        if (match.Success) clientMetadataId = int.Parse(match.Groups[1].Value);
                    }
                    else if (message[1] == 1)
                    {
                        string request = Encoding.ASCII.GetString(message, 2, message.Length - 2);
                        var match = System.Text.RegularExpressions.Regex.Match(request, "5:piecei([0-9]+)e");
                        if (!match.Success || !request.Contains("8:msg_typei0e")) continue;
                        int piece = int.Parse(match.Groups[1].Value);
                        byte[] header = Encode(new SortedDictionary<string, object> { ["msg_type"] = 1, ["piece"] = piece, ["total_size"] = info.Length });
                        await Message(stream, 20, new byte[] { (byte)clientMetadataId }.Concat(header).Concat(info.Skip(piece * 16384).Take(16384)).ToArray());
                    }
                }
            }
        }
        catch { }
    }

    private static byte[] ExtractInfo(byte[] torrent)
    {
        byte[] marker = Encoding.ASCII.GetBytes("4:info");
        for (int i = 0; i < torrent.Length - marker.Length; i++)
            if (torrent.Skip(i).Take(marker.Length).SequenceEqual(marker))
                return torrent.Skip(i + marker.Length).Take(torrent.Length - i - marker.Length - 1).ToArray();
        throw new Exception("Fixture info missing");
    }

    private static async Task<byte[]> Read(NetworkStream stream, int length)
    {
        byte[] result = new byte[length];
        for (int at = 0; at < length;)
        {
            int read = await stream.ReadAsync(result, at, length - at);
            if (read == 0) throw new EndOfStreamException();
            at += read;
        }
        return result;
    }
    private static int BigInt(byte[] data, int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
    private static async Task Message(NetworkStream stream, byte id, byte[] data)
    {
        int length = data.Length + 1;
        byte[] prefix = { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, id };
        await stream.WriteAsync(prefix, 0, prefix.Length);
        await stream.WriteAsync(data, 0, data.Length);
    }
    internal static byte[] Encode(object value)
    {
        using (var output = new MemoryStream())
        {
            Action<string> text = s => { byte[] b = Encoding.ASCII.GetBytes(s); output.Write(b, 0, b.Length); };
            if (value is string s) value = Encoding.UTF8.GetBytes(s);
            if (value is byte[] bytes) { text(bytes.Length + ":"); output.Write(bytes, 0, bytes.Length); }
            else if (value is SortedDictionary<string, object> dict)
            {
                text("d");
                foreach (var entry in dict) { byte[] k = Encode(entry.Key), v = Encode(entry.Value); output.Write(k, 0, k.Length); output.Write(v, 0, v.Length); }
                text("e");
            }
            else if (value is object[] list) { text("l"); foreach (var item in list) { byte[] b = Encode(item); output.Write(b, 0, b.Length); } text("e"); }
            else text("i" + value + "e");
            return output.ToArray();
        }
    }
    public void Dispose() { _stop.Cancel(); _tracker.Stop(); _peer.Stop(); }
}
