


using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BitTorrent.Core;

namespace BitTorrent.Core;


public enum TrackerEvent
{
    Started,
    Stopped,
    Completed,
    
    
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

    
    
    public TrackerClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    
    public TrackerClient(HttpClient httpClient) =>
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    
    
    

    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
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

        
        
        
        
        var url = BuildAnnounceUrl(announceUrl, infoHash, peerId, port, uploaded, downloaded, left, @event);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        
        
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BitTorrentProtocol", "0.0.0"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var decoded = BEncoding.Decode(bytes);
        if (decoded is not BEncodedDictionary dict)
            throw new InvalidDataException("Tracker response is not a dictionary.");

        
        
        
        string? failure = dict["failure reason"] is BEncodedString f ? f.AsText() : null;
        if (failure is not null)
        {
            return new TrackerResponse(0, 0, Array.Empty<IPEndPoint>(), failure, null, null);
        }

        
        int interval = dict["interval"] is BEncodedInteger i1 ? (int)i1.Value : 1_800;
        int minInterval = dict["min interval"] is BEncodedInteger i2 ? (int)i2.Value : 0;
        int? complete = dict["complete"] is BEncodedInteger c1 ? (int)c1.Value : null;
        int? incomplete = dict["incomplete"] is BEncodedInteger c2 ? (int)c2.Value : null;

        var peers = ParsePeers(dict);
        return new TrackerResponse(interval, minInterval, peers, null, complete, incomplete);
    }

    
    
    
    private static Uri BuildAnnounceUrl(
        Uri baseUrl, byte[] infoHash, byte[] peerId, ushort port,
        long uploaded, long downloaded, long left, TrackerEvent ev)
    {
        
        
        var sb = new System.Text.StringBuilder(256);
        
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
        AppendKv(sb, ref first, "support.crypto", "1");  
        AppendKv(sb, ref first, "event",      EventToString(ev));
        return new Uri(sb.ToString());
    }

    private static void AppendKv(System.Text.StringBuilder sb, ref bool first, string key, string value)
    {
        if (!first) sb.Append('&');
        sb.Append(key).Append('=').Append(value);
        first = false;
    }

    
    
    

    private static IReadOnlyList<IPEndPoint> ParsePeers(BEncodedDictionary dict)
    {
        var peersNode = dict["peers"];
        if (peersNode is null) return Array.Empty<IPEndPoint>();

        
        
        
        
        
        if (peersNode is BEncodedString peersStr)
            return ParseCompactPeers(peersStr.Value);
        if (peersNode is BEncodedList peersList)
            return ParseModelAList(peersList);

        return Array.Empty<IPEndPoint>();
    }

    
    private static IReadOnlyList<IPEndPoint> ParseCompactPeers(byte[] bytes)
    {
        if (bytes.Length % 6 != 0)
            throw new InvalidDataException("Compact peer list is not a multiple of 6 bytes.");

        if (bytes.Length == 0) return Array.Empty<IPEndPoint>();
        var list = new List<IPEndPoint>(bytes.Length / 6);

        
        var span = bytes.AsSpan();
        for (int i = 0; i < span.Length; i += 6)
        {
            
            var slice = span.Slice(i, 6);
            var ip = new IPAddress(slice.Slice(0, 4));
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(4, 2));
            list.Add(new IPEndPoint(ip, port));
        }
        return list;
    }

    
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

    
    
    

    private static string EventToString(TrackerEvent e) => e switch
    {
        TrackerEvent.Started   => "started",
        TrackerEvent.Stopped   => "stopped",
        TrackerEvent.Completed => "completed",
        _                       => string.Empty,
    };
}



public sealed class UdpTrackerClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Random _random = new();
    private readonly IPEndPoint _endpoint;
    private readonly byte[] _connectionId = new byte[8];
    private bool _connected;

    public UdpTrackerClient(string announceUrl)
    {
        
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

        
        if (!_connected)
        {
            await ConnectAsync(ct);
            if (!_connected)
                throw new InvalidOperationException("UDP tracker connect failed");
        }

        
        return await AnnounceInternalAsync(infoHash, peerId, port, uploaded, downloaded, left, @event, ct);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        const uint ConnectAction = 0;
        var transactionId = (uint)_random.Next();
        
        var request = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(request, 0x41727101980); 
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
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(84), 0); 
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(88), (uint)_random.Next()); 
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(92), -1); 
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

