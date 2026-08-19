using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BitTorrent.Core;

namespace BitTorrent.WPF.Services;


public sealed class LocalTestTracker : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ushort _port;
    private readonly Dictionary<string, List<PeerInfo>> _torrents = new();
    private readonly object _lock = new();
    private bool _disposed;

    public LocalTestTracker(ushort port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        while (!_disposed && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (_disposed) { break; }
            catch (ObjectDisposedException) when (_disposed) { break; }
            catch {  }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var request = ctx.Request;
            var response = ctx.Response;

            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != "/announce")
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            var query = ParseQueryString(request.Url?.Query ?? "");
            var infoHashParam = query.GetValueOrDefault("info_hash");
            var peerIdParam = query.GetValueOrDefault("peer_id");
            var portParam = query.GetValueOrDefault("port");
            var leftParam = query.GetValueOrDefault("left");
            var eventParam = query.GetValueOrDefault("event");
            var compactParam = query.GetValueOrDefault("compact");

            if (string.IsNullOrEmpty(infoHashParam) || string.IsNullOrEmpty(peerIdParam) || string.IsNullOrEmpty(portParam))
            {
                response.StatusCode = 400;
                response.Close();
                return;
            }

            
            var infoHash = PercentEncoding.Decode(infoHashParam);
            if (infoHash.Length != 20)
            {
                response.StatusCode = 400;
                response.Close();
                return;
            }

            var infoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant();
            var peerId = PercentEncoding.Decode(peerIdParam);
            var port = ushort.TryParse(portParam, out var p) ? p : (ushort)6881;
            var left = long.TryParse(leftParam, out var l) ? l : 0;
            var peerIp = request.RemoteEndPoint?.Address ?? IPAddress.Loopback;
            var peerEndpoint = new IPEndPoint(peerIp, port);

            lock (_lock)
            {
                if (!_torrents.TryGetValue(infoHashHex, out var peers))
                {
                    peers = new List<PeerInfo>();
                    _torrents[infoHashHex] = peers;
                }

                
                var existing = peers.FirstOrDefault(x => x.PeerId.SequenceEqual(peerId));
                if (existing != null)
                {
                    existing.Endpoint = peerEndpoint;
                    existing.LastSeen = DateTime.UtcNow;
                    existing.Left = left;
                }
                else
                {
                    peers.Add(new PeerInfo
                    {
                        PeerId = peerId,
                        Endpoint = peerEndpoint,
                        LastSeen = DateTime.UtcNow,
                        Left = left
                    });
                }

                
                peers.RemoveAll(x => DateTime.UtcNow - x.LastSeen > TimeSpan.FromMinutes(5));

                
                var responsePeers = peers
                    .Where(x => !x.Endpoint.Equals(peerEndpoint))
                    .Select(x => x.Endpoint)
                    .ToList();

                
                var peerBytes = new List<byte>();
                foreach (var ep in responsePeers)
                {
                    if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var ipBytes = ep.Address.GetAddressBytes();
                        peerBytes.AddRange(ipBytes);
                        var portBytes = new byte[2];
                        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)ep.Port);
                        peerBytes.AddRange(portBytes);
                    }
                }

                
                var responseDict = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
                {
                    [new BEncodedString(Encoding.UTF8.GetBytes("interval"))] = new BEncodedInteger(1800),
                    [new BEncodedString(Encoding.UTF8.GetBytes("min interval"))] = new BEncodedInteger(900),
                    [new BEncodedString(Encoding.UTF8.GetBytes("peers"))] = new BEncodedString(peerBytes.ToArray()),
                    [new BEncodedString(Encoding.UTF8.GetBytes("complete"))] = new BEncodedInteger(peers.Count(p => p.Left == 0)),
                    [new BEncodedString(Encoding.UTF8.GetBytes("incomplete"))] = new BEncodedInteger(peers.Count(p => p.Left > 0)),
                });

                var responseBytes = BEncoding.Encode(responseDict);

                response.StatusCode = 200;
                response.ContentType = "text/plain";
                response.ContentLength64 = responseBytes.Length;
                response.OutputStream.Write(responseBytes);
                response.Close();
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener?.Stop();
        _listener?.Close();
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(query)) return result;
        
        var parts = query.TrimStart('?').Split('&');
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
            {
                result[kv[0]] = kv[1];
            }
        }
        return result;
    }

    private sealed class PeerInfo
    {
        public byte[] PeerId { get; set; } = Array.Empty<byte>();
        public IPEndPoint Endpoint { get; set; } = null!;
        public DateTime LastSeen { get; set; }
        public long Left { get; set; }
    }
}


file static class PercentEncoding
{
    public static byte[] Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return Array.Empty<byte>();
        
        var bytes = new List<byte>();
        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length)
            {
                var hex = encoded.Substring(i + 1, 2);
                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    bytes.Add(b);
                    i += 2;
                    continue;
                }
            }
            else if (encoded[i] == '+')
            {
                bytes.Add((byte)' ');
                continue;
            }
            bytes.Add((byte)encoded[i]);
        }
        return bytes.ToArray();
    }
}


