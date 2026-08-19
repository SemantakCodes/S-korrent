using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BitTorrent.Core;

int failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) failures++;
}

if (args.Length > 0)
{
    PrintTorrentInfo(args[0]);
    return 0;
}

await RunSelfTestAsync();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;


void PrintTorrentInfo(string path)
{
    var torrent = Torrent.LoadFromFile(path);
    Console.WriteLine($"Name:             {torrent.Name}");
    Console.WriteLine($"Mode:             {(torrent.IsSingleFile ? "single file" : "multi-file")}");
    Console.WriteLine($"Total length:     {torrent.TotalLength:N0} bytes");
    Console.WriteLine($"Piece length:     {torrent.PieceLength:N0} bytes");
    Console.WriteLine($"Piece count:      {torrent.Pieces.Length / 20}");
    Console.WriteLine($"Announce:         {torrent.AnnounceUrl}");
    Console.WriteLine($"InfoHash (hex):   {torrent.InfoHashString}");
    Console.WriteLine($"InfoHash (url):   {torrent.UrlEncodeInfoHash()}");
    Console.WriteLine();
    Console.WriteLine("Files:");
    foreach (var f in torrent.Files)
        Console.WriteLine($"  {f.Length,14:N0}  {f.RelativePath}");
}


async Task RunSelfTestAsync()
{
    Console.WriteLine("== BitTorrent.Core self-test ==");
    Console.WriteLine();

    
    var dict = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
    {
        [new BEncodedString(Encoding.UTF8.GetBytes("cow"))] = new BEncodedString(Encoding.UTF8.GetBytes("moo")),
        [new BEncodedString(Encoding.UTF8.GetBytes("spam"))] = new BEncodedString(Encoding.UTF8.GetBytes("eggs")),
        [new BEncodedString(Encoding.UTF8.GetBytes("num"))] = new BEncodedInteger(42),
        [new BEncodedString(Encoding.UTF8.GetBytes("neg"))] = new BEncodedInteger(-3),
    });
    string encStr = Encoding.UTF8.GetString(BEncoding.Encode(dict));
    string expected = "d3:cow3:moo3:negi-3e3:numi42e4:spam4:eggse";
    Check("BEncode canonical output", encStr == expected);
    var dec = BEncoding.Decode(BEncoding.Encode(dict));
    Check("BEncode round-trip", dec is BEncodedDictionary dd && dd["cow"] is BEncodedString cs && cs.AsText() == "moo");

    
    Check("PercentEncoding lowercase hex",
        PercentEncoding.Encode(new byte[] { (byte)'a', 0xE4, (byte)'~', 0xFF }) == "a%e4~%ff");

    
    byte[] payloadBytes = Encoding.UTF8.GetBytes("hello bit torrent world, this is a payload piece for testing.");
    byte[] piecesBlob = new byte[20];
    Buffer.BlockCopy(SHA1.HashData(payloadBytes), 0, piecesBlob, 0, 20);

    var info = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
    {
        [new BEncodedString(Encoding.UTF8.GetBytes("length"))] = new BEncodedInteger(payloadBytes.Length),
        [new BEncodedString(Encoding.UTF8.GetBytes("name"))] = new BEncodedString(Encoding.UTF8.GetBytes("testfile.bin")),
        [new BEncodedString(Encoding.UTF8.GetBytes("piece length"))] = new BEncodedInteger(16384),
        [new BEncodedString(Encoding.UTF8.GetBytes("pieces"))] = new BEncodedString(piecesBlob),
    });
    var root = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
    {
        [new BEncodedString(Encoding.UTF8.GetBytes("announce"))] = new BEncodedString(Encoding.UTF8.GetBytes("http://127.0.0.1:5555/announce")),
        [new BEncodedString(Encoding.UTF8.GetBytes("info"))] = info,
    });

    Torrent tor = Torrent.LoadFromBytes(BEncoding.Encode(root));
    byte[] expectedInfoHash = SHA1.HashData(BEncoding.Encode(info));
    Check("Torrent single-file", tor.IsSingleFile && tor.Files.Count == 1 && tor.Name == "testfile.bin");
    Check("Torrent infohash == sha1(info dict)", tor.InfoHash.AsSpan().SequenceEqual(expectedInfoHash));

    string dest = Path.Combine(Path.GetTempPath(), "bt_filestore_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dest);
    try
    {
        using var store = new FileStore(tor, dest);
        var req = new BlockRequest(0, 0, payloadBytes.Length);
        await store.WriteBlockAsync(req, payloadBytes);
        var buf = store.AllocateBlockBuffer(req);
        int n = await store.ReadBlockAsync(req, buf);
        Check("FileStore round-trip", n == payloadBytes.Length && buf.AsSpan(0, n).SequenceEqual(payloadBytes));
        Check("FileStore Verify SHA-1", await store.VerifyAsync(0));
    }
    finally
    {
        Directory.Delete(dest, true);
    }

    
    byte[] peerId = Encoding.UTF8.GetBytes("ABCDEFGHIJKLMNOPQRST");
    byte[] ih = new byte[20];
    new Random(1).NextBytes(ih);

    IPEndPoint ep = new(IPAddress.Loopback, 55999);
    var server = new TcpListener(IPAddress.Loopback, 55999);
    server.Start();
    var peerTask = Task.Run(async () =>
    {
        try
        {
            var peer = new Peer(ep, ih, peerId);
            await peer.ConnectAsync();
            await peer.PerformHandshakeAsync();
            await peer.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Peer client error: {ex.Message}");
            failures++;
        }
    });
    var client = await server.AcceptTcpClientAsync();
    NetworkStream ns = client.GetStream();
    byte[] handshake = new byte[Peer.HandshakeLength];
    int read = 0;
    while (read < handshake.Length)
        read += await ns.ReadAsync(handshake.AsMemory(read, handshake.Length - read));
    bool protoOk = handshake[0] == 19 && handshake.AsSpan(1, 19).SequenceEqual("BitTorrent protocol"u8);
    await ns.WriteAsync(handshake);
    await peerTask;
    client.Dispose();
    server.Stop();
    Check("Peer handshake exchange", protoOk);

    
    var tracker = new TrackerClient();
    var responder = Task.Run(async () =>
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:59999/");
        listener.Start();
        var ctx = await listener.GetContextAsync();
        string q = ctx.Request.Url!.Query;
        Check("Tracker query has info_hash", q.Contains("info_hash="));
        Check("Tracker query has compact=1", q.Contains("compact=1"));
        byte[] body = Encoding.ASCII.GetBytes("d8:intervali1800e5:peers6:")
            .Concat(new byte[] { 0x7F, 0x00, 0x00, 0x01, 0x1F, 0x90, (byte)'e' }).ToArray();
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
        listener.Stop();
    });
    var resp = await tracker.AnnounceAsync(new Uri("http://127.0.0.1:59999/announce"),
        ih, peerId, 6881, 0, 0, 1000, TrackerEvent.Started);
    Check("Tracker interval parsed", resp.IntervalSeconds == 1800);
    Check("Tracker peers parsed (compact)", resp.Peers.Count == 1 && resp.Peers[0].Port == 8080);
    await responder;

    Console.WriteLine();
}

