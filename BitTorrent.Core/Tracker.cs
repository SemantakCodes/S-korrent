// =====================================================================================
// Tracker.cs
// =====================================================================================
// Tracker is the BitTorrent HTTP-based peer discovery service. Trackers don't
// transfer any payload data — they only hand out lists of peers that are also
// downloading the same torrent.
//
// Protocol summary:
//   * The client sends an HTTP GET to <announce-url>/announce with a query
//     string carrying the infohash, peer id, port, and current state.
//   * The server replies with a BEncoded dictionary that contains:
//        { "interval": i<int>e,
//          "peers":  <compact binary blob, 6 bytes per peer>,  }
//     where each 6-byte peer record is [4 IPv4 octets][big-endian port].
//
// References:
//   BEP 3 (the original spec)
//   BEP 23 (compact peer format)
//   https://www.bittorrent.org/beps/bep_0003.html
// =====================================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BitTorrent.Core;

namespace BitTorrent.Core;

/// <summary>
/// Why a particular announce was triggered. Trackers use this for statistics
/// and to delay re-announces when paused.
/// </summary>
public enum TrackerEvent
{
    Started,
    Stopped,
    Completed,
    /// <summary>A periodic heartbeat. Period is dictated by the tracker's
    /// "interval" key in the previous response.</summary>
    None,
}

public sealed record TrackerResponse(
    int IntervalSeconds,
    int MinIntervalSeconds,
    IReadOnlyList<IPEndPoint> Peers,
    string? FailureReason,
    int? Complete,
    int? Incomplete);

public sealed class TrackerClient
{
    private readonly HttpClient _http;

    /// <summary>Constructs a tracker client with a private HttpClient.
    /// HttpClient is intended to be reused across announces.</summary>
    public TrackerClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Construct with a caller-provided HttpClient (e.g. for tests).</summary>
    public TrackerClient(HttpClient httpClient) =>
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    // -------------------------------------------------------------------------
    // Public announce
    // -------------------------------------------------------------------------

    /// <summary>
    /// Send an announce request to <paramref name="announceUrl"/>.
    /// Per the spec the URL is the "announce" URL from the torrent + "/announce".
    /// Some tracker host names include the full path; we defer to the caller.
    /// </summary>
    /// <param name="announceUrl">Fully-qualified tracker URL.</param>
    /// <param name="infoHash">Raw 20-byte SHA-1 of the info dictionary.</param>
    /// <param name="peerId">20-byte unique client identifier.</param>
    /// <param name="port">TCP port the client is listening on for incoming
    /// connections. May be 0 if the client cannot accept connections (passive
    /// mode); trackers still hand the peer out to others.</param>
    /// <param name="uploaded">Total bytes uploaded since the start of the
    /// session.</param>
    /// <param name="downloaded">Total bytes downloaded since the start of
    /// the session.</param>
    /// <param name="left">Bytes still pending download. Trackers use this to
    /// derive a peer's completion percentage.</param>
    /// <param name="event">Why this announce was triggered.</param>
    public async Task<TrackerResponse> AnnounceAsync(
        Uri announceUrl,
        byte[] infoHash,
        byte[] peerId,
        ushort port,
        long uploaded,
        long downloaded,
        long left,
        TrackerEvent @event,
        CancellationToken cancellationToken = default)
    {
        if (infoHash is null || infoHash.Length != 20)
            throw new ArgumentException("info_hash must be 20 bytes.", nameof(infoHash));
        if (peerId is null || peerId.Length != 20)
            throw new ArgumentException("peer_id must be 20 bytes.", nameof(peerId));

        // Build the query string. The info_hash and peer_id use the shared
        // PercentEncoding helper so behavior is identical to
        // Torrent.UrlEncodeInfoHash. We split-kv join manually so we keep the
        // allocation pattern predictable.
        var url = BuildAnnounceUrl(announceUrl, infoHash, peerId, port, uploaded, downloaded, left, @event);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // BitTorrent spec requires the explicit "BitTorrent protocol" UA to
        // discourage cache-by-content middleboxes.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BitTorrentProtocol", "0.0.0"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var decoded = BEncoding.Decode(bytes);
        if (decoded is not BEncodedDictionary dict)
            throw new InvalidDataException("Tracker response is not a dictionary.");

        // If the tracker reports a failure, surface it immediately. Many public
        // trackers put neither an "interval" nor a "peers" key on a failed
        // response, so we cannot continue parsing unconditionally.
        string? failure = dict["failure reason"] is BEncodedString f ? f.AsText() : null;
        if (failure is not null)
        {
            return new TrackerResponse(0, 0, Array.Empty<IPEndPoint>(), failure, null, null);
        }

        // Standard keys: interval, min interval, peers, complete, incomplete.
        int interval = dict["interval"] is BEncodedInteger i1 ? (int)i1.Value : 1_800;
        int minInterval = dict["min interval"] is BEncodedInteger i2 ? (int)i2.Value : 0;
        int? complete = dict["complete"] is BEncodedInteger c1 ? (int)c1.Value : null;
        int? incomplete = dict["incomplete"] is BEncodedInteger c2 ? (int)c2.Value : null;

        var peers = ParsePeers(dict);
        return new TrackerResponse(interval, minInterval, peers, null, complete, incomplete);
    }

    /// <summary>Compose the announce URL with proper key/value separators.
    /// We rewrite the .Query portion to dodge Uri's percent-encoding which
    /// would re-encode our already-encoded hashes.</summary>
    private static Uri BuildAnnounceUrl(
        Uri baseUrl, byte[] infoHash, byte[] peerId, ushort port,
        long uploaded, long downloaded, long left, TrackerEvent ev)
    {
        // Use a StringBuilder so we allocate exactly once. 21 entries is a
        // reasonable upper bound for the typical tracker query.
        var sb = new System.Text.StringBuilder(256);
        // Base URL minus any existing query/fragment, then ?
        var s = baseUrl.ToString();
        int q = s.IndexOf('?');
        int hash = s.IndexOf('#');
        int end = (q < 0 ? s.Length : q);
        end = (hash >= 0 && hash < end ? hash : end);
        sb.Append(s, 0, end).Append('?');
        bool first = true;
        AppendKv(sb, ref first, "info_hash",  PercentEncoding.Encode(infoHash));
        AppendKv(sb, ref first, "peer_id",    PercentEncoding.Encode(peerId));
        AppendKv(sb, ref first, "port",       port.ToString());
        AppendKv(sb, ref first, "uploaded",   uploaded.ToString());
        AppendKv(sb, ref first, "downloaded", downloaded.ToString());
        AppendKv(sb, ref first, "left",       left.ToString());
        AppendKv(sb, ref first, "compact",    "1");
        AppendKv(sb, ref first, "support.crypto", "1");  // BEP 14 hint
        AppendKv(sb, ref first, "event",      EventToString(ev));
        return new Uri(sb.ToString());
    }

    private static void AppendKv(System.Text.StringBuilder sb, ref bool first, string key, string value)
    {
        if (!first) sb.Append('&');
        sb.Append(key).Append('=').Append(value);
        first = false;
    }

    // -------------------------------------------------------------------------
    // Peer list parsing
    // -------------------------------------------------------------------------

    private static IReadOnlyList<IPEndPoint> ParsePeers(BEncodedDictionary dict)
    {
        var peersNode = dict["peers"];
        if (peersNode is null) return Array.Empty<IPEndPoint>();

        // Two formats exist:
        //   - "peers": binary string, 6 bytes per IPv4 peer. This is the compact
        //     form we requested with ?compact=1.
        //   - "peers": list of dictionaries (legacy "model A" form). Each
        //     dictionary has "ip" + "port".
        if (peersNode is BEncodedString peersStr)
            return ParseCompactPeers(peersStr.Value);
        if (peersNode is BEncodedList peersList)
            return ParseModelAList(peersList);

        return Array.Empty<IPEndPoint>();
    }

    /// <summary>Decode the 6-bytes-per-peer binary form mandated by BEP 23.</summary>
    private static IReadOnlyList<IPEndPoint> ParseCompactPeers(byte[] bytes)
    {
        if (bytes.Length % 6 != 0)
            throw new InvalidDataException("Compact peer list is not a multiple of 6 bytes.");

        if (bytes.Length == 0) return Array.Empty<IPEndPoint>();
        var list = new List<IPEndPoint>(bytes.Length / 6);

        // Treat the buffer as a span for fast big-endian reads.
        var span = bytes.AsSpan();
        for (int i = 0; i < span.Length; i += 6)
        {
            // Bytes 0..3 = IPv4 octets. Bytes 4..5 = port in network byte order.
            var slice = span.Slice(i, 6);
            var ip = new IPAddress(slice.Slice(0, 4));
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(4, 2));
            list.Add(new IPEndPoint(ip, port));
        }
        return list;
    }

    /// <summary>Legacy "model A" form: list of dictionaries with "ip"/"port".</summary>
    private static IReadOnlyList<IPEndPoint> ParseModelAList(BEncodedList list)
    {
        var result = new List<IPEndPoint>(list.Value.Count);
        foreach (var item in list.Value)
        {
            if (item is not BEncodedDictionary dict) continue;
            var ipNode = dict["ip"];
            var portNode = dict["port"];
            if (ipNode is BEncodedString ipStr && portNode is BEncodedInteger portInt)
            {
                if (IPAddress.TryParse(ipStr.AsText(), out var ip))
                    result.Add(new IPEndPoint(ip, (int)portInt.Value));
            }
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Event helper
    // -------------------------------------------------------------------------

    private static string EventToString(TrackerEvent e) => e switch
    {
        TrackerEvent.Started   => "started",
        TrackerEvent.Stopped   => "stopped",
        TrackerEvent.Completed => "completed",
        _                       => string.Empty,
    };
}

// =====================================================================================
// UDP Tracker (BEP 15)
// =====================================================================================
// UDP tracker protocol uses a simple request/response over UDP:
// 1. Connect request -> get connection_id
// 2. Announce request with connection_id -> get peer list
// =====================================================================================

public sealed class UdpTrackerClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Random _random = new();
    private readonly IPEndPoint _endpoint;
    private readonly byte[] _connectionId = new byte[8];
    private bool _connected;

    public UdpTrackerClient(string announceUrl)
    {
        // Parse udp://host:port or udp://host:port/path
        var uri = new Uri(announceUrl);
        if (uri.Scheme != "udp")
            throw new ArgumentException("URL must use udp:// scheme", nameof(announceUrl));
        
        var ip = Dns.GetHostAddresses(uri.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _endpoint = new IPEndPoint(ip, uri.Port);
        _udp = new UdpClient { Client = { ReceiveTimeout = 15000 } };
    }

    public async Task<TrackerResponse> AnnounceAsync(
        byte[] infoHash,
        byte[] peerId,
        ushort port,
        long uploaded,
        long downloaded,
        long left,
        TrackerEvent @event,
        CancellationToken ct = default)
    {
        if (infoHash is null || infoHash.Length != 20)
            throw new ArgumentException("info_hash must be 20 bytes.", nameof(infoHash));
        if (peerId is null || peerId.Length != 20)
            throw new ArgumentException("peer_id must be 20 bytes.", nameof(peerId));

        // Step 1: Connect
        if (!_connected)
        {
            await ConnectAsync(ct);
            if (!_connected)
                throw new InvalidOperationException("UDP tracker connect failed");
        }

        // Step 2: Announce
        return await AnnounceInternalAsync(infoHash, peerId, port, uploaded, downloaded, left, @event, ct);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        const uint ConnectAction = 0;
        var transactionId = (uint)_random.Next();
        
        var request = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(request, 0x41727101980); // Magic constant
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), ConnectAction);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(12), transactionId);

        await _udp.SendAsync(request, _endpoint);
        
        var receiveTask = _udp.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(15000, ct));
        if (completed != receiveTask) throw new TimeoutException("UDP connect timeout");
        
        var response = receiveTask.Result;
        if (response.Buffer.Length < 16) throw new InvalidDataException("Invalid connect response");
        
        var action = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer);
        var respTransId = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(4));
        if (action != 0 || respTransId != transactionId) throw new InvalidDataException("Invalid connect response");
        
        Buffer.BlockCopy(response.Buffer, 8, _connectionId, 0, 8);
        _connected = true;
    }

    private async Task<TrackerResponse> AnnounceInternalAsync(
        byte[] infoHash, byte[] peerId, ushort port,
        long uploaded, long downloaded, long left, TrackerEvent @event,
        CancellationToken ct)
    {
        const uint AnnounceAction = 1;
        var transactionId = (uint)_random.Next();
        
        var request = new byte[98];
        Buffer.BlockCopy(_connectionId, 0, request, 0, 8);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), AnnounceAction);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(12), transactionId);
        Buffer.BlockCopy(infoHash, 0, request, 16, 20);
        Buffer.BlockCopy(peerId, 0, request, 36, 20);
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(56), downloaded);
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(64), left);
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(72), uploaded);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(80), EventToInt(@event));
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(84), 0); // IP = 0 (default)
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(88), (uint)_random.Next()); // Key
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(92), -1); // num_want = -1 (default)
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(96), port);

        await _udp.SendAsync(request, _endpoint);
        
        var receiveTask = _udp.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(15000, ct));
        if (completed != receiveTask) throw new TimeoutException("UDP announce timeout");
        
        var response = receiveTask.Result;
        if (response.Buffer.Length < 20) throw new InvalidDataException("Invalid announce response");
        
        var action = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer);
        var respTransId = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(4));
        if (action != 1 || respTransId != transactionId) throw new InvalidDataException("Invalid announce response");
        
        var interval = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(8));
        var leechers = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(12));
        var seeders = BinaryPrimitives.ReadUInt32BigEndian(response.Buffer.AsSpan(16));
        
        var peers = ParseCompactPeersUdp(response.Buffer.AsSpan(20));
        
        return new TrackerResponse(
            (int)interval,
            0,
            peers,
            null,
            (int)seeders,
            (int)leechers);
    }

    private static uint EventToInt(TrackerEvent e) => e switch
    {
        TrackerEvent.Started => 2,
        TrackerEvent.Stopped => 3,
        TrackerEvent.Completed => 1,
        _ => 0
    };

    private static IReadOnlyList<IPEndPoint> ParseCompactPeersUdp(ReadOnlySpan<byte> bytes)
    {
        var list = new List<IPEndPoint>();
        for (int i = 0; i + 5 < bytes.Length; i += 6)
        {
            var slice = bytes.Slice(i, 6);
            var ip = new IPAddress(slice.Slice(0, 4));
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(4, 2));
            list.Add(new IPEndPoint(ip, port));
        }
        return list;
    }

    public void Dispose()
    {
        _udp?.Dispose();
    }
}
