using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BitTorrent.Core;
using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF.Services;

public sealed class DownloadEngine
{
    private readonly byte[] _peerId;
    private readonly ushort _listenPort;
    private readonly Random _random = new();

    public event Action<string>? LogMessage;

    public DownloadEngine()
    {
        _peerId = GeneratePeerId();
        _listenPort = 6881;
    }

    private void Log(string msg) => LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public async Task StartDownloadAsync(TorrentViewModel torrentVm, CancellationToken ct)
    {
        var torrent = torrentVm.TorrentInfo.Torrent;
        var destinationPath = Path.Combine(
            torrentVm.TorrentInfo.IsSingleFile 
                ? Path.GetDirectoryName(torrentVm.TorrentInfo.Files.First().RelativePath) ?? ""
                : Path.Combine(torrentVm.TorrentInfo.Name));

        // For simplicity, we use the download path directly
        var storePath = Path.GetDirectoryName(torrentVm.TorrentInfo.Files.First().RelativePath) ?? "";
        var fullStorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BitTorrent");

        using var fileStore = new FileStore(torrent, fullStorePath);
        torrentVm.Status = "Connecting to tracker...";
        Log($"Starting download for: {torrentVm.TorrentInfo.Name}");
        Log($"InfoHash: {torrentVm.TorrentInfo.InfoHashHex}");
        Log($"Announce URL: {torrentVm.TorrentInfo.AnnounceUrl}");

        // Get peers from tracker
        var peers = await GetPeersAsync(torrent, ct);
        if (peers.Count == 0)
        {
            torrentVm.Status = "No peers found";
            Log("ERROR: No peers returned from any tracker");
            return;
        }

        torrentVm.Status = $"Found {peers.Count} peers. Connecting...";
        Log($"Got {peers.Count} peers from trackers");

        // Start peer connections and download
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
        var tasks = new List<Task>();

        foreach (var peerEndpoint in peers)
        {
            if (ct.IsCancellationRequested) break;
            
            await semaphore.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ConnectAndDownloadPeerAsync(peerEndpoint, torrent, fileStore, torrentVm, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
        
        if (!ct.IsCancellationRequested)
        {
            // Verify all pieces
            torrentVm.Status = "Verifying...";
            for (int i = 0; i < torrentVm.TorrentInfo.PieceCount; i++)
            {
                if (await fileStore.VerifyAsync(i))
                {
                    torrentVm.UpdateProgress(i, torrentVm.Pieces[i].BlocksTotal, true);
                }
            }
            torrentVm.Status = "Complete";
        }
    }

    private async Task<List<IPEndPoint>> GetPeersAsync(Torrent torrent, CancellationToken ct)
    {
        var peers = new List<IPEndPoint>();

        if (torrent.AnnounceUrl == null)
        {
            Log("No announce URL in torrent");
            return peers;
        }

        // Collect all announce URLs (primary + announce-list if present)
        var announceUrls = new List<Uri> { torrent.AnnounceUrl };
        
        // Note: The Core library doesn't expose announce-list yet, so we only use primary

        var tracker = new TrackerClient();
        
        foreach (var url in announceUrls)
        {
            if (ct.IsCancellationRequested) break;
            
            try
            {
                Log($"Announcing to: {url}");
                var response = await tracker.AnnounceAsync(
                    url,
                    torrent.InfoHash,
                    _peerId,
                    _listenPort,
                    0, 0, torrent.TotalLength,
                    TrackerEvent.Started,
                    ct);

                Log($"Tracker response: interval={response.IntervalSeconds}s, peers={response.Peers.Count}, complete={response.Complete}, incomplete={response.Incomplete}");
                
                if (response.FailureReason != null)
                {
                    Log($"Tracker error: {response.FailureReason}");
                    continue;
                }

                peers.AddRange(response.Peers);
            }
            catch (Exception ex)
            {
                Log($"Tracker announce failed for {url}: {ex.Message}");
            }
        }

        return peers.Distinct().ToList();
    }

    private async Task ConnectAndDownloadPeerAsync(
        IPEndPoint endpoint, 
        Torrent torrent, 
        FileStore fileStore,
        TorrentViewModel torrentVm,
        CancellationToken ct)
    {
        var peer = new Peer(endpoint, torrent.InfoHash, _peerId);
        
        try
        {
            Log($"Connecting to peer: {endpoint}");
            await peer.ConnectAsync(ct);
            Log($"TCP connected to: {endpoint}");
            await peer.PerformHandshakeAsync(ct);
            Log($"Handshake OK with: {endpoint}");
            
            var peerVm = new PeerViewModel(endpoint.ToString(), 
                peer.RemotePeerId != null ? Encoding.UTF8.GetString(peer.RemotePeerId) : null);
            peerVm.IsConnected = true;
            torrentVm.UpdatePeer(peerVm);

            // Send interested
            await peer.SendInterestedAsync(ct);
            peerVm.IsInterested = true;
            Log($"Sent Interested to: {endpoint}");

            // Wait for unchoke
            var unchoked = await WaitForUnchokeAsync(peer, ct);
            if (!unchoked)
            {
                peerVm.Status = "Choked (timeout)";
                Log($"Peer {endpoint} did not unchoke (timeout)");
                return;
            }

            peerVm.Status = "Downloading";
            peerVm.IsChoked = false;
            Log($"Peer {endpoint} unchoked us");

            // Download pieces
            await DownloadPiecesAsync(peer, torrent, fileStore, torrentVm, peerVm, ct);
        }
        catch (Exception ex)
        {
            Log($"Peer {endpoint} error: {ex.Message}");
            var peerVm = new PeerViewModel(endpoint.ToString()) { Status = $"Error: {ex.Message}" };
            torrentVm.UpdatePeer(peerVm);
        }
        finally
        {
            await peer.DisposeAsync();
        }
    }

    private async Task<bool> WaitForUnchokeAsync(Peer peer, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - start < timeout)
        {
            var msg = await peer.ReadMessageAsync(ct);
            if (msg == null) continue;

            if (msg.Id == PeerMessageId.Unchoke)
                return true;
            if (msg.Id == PeerMessageId.Choke)
                continue;
        }
        return false;
    }

    private async Task DownloadPiecesAsync(
        Peer peer,
        Torrent torrent,
        FileStore fileStore,
        TorrentViewModel torrentVm,
        PeerViewModel peerVm,
        CancellationToken ct)
    {
        const int blockSize = 16384;
        var pieceCount = torrentVm.TorrentInfo.PieceCount;
        var random = new Random();

        while (!ct.IsCancellationRequested)
        {
            // Find a piece we need
            var neededPieces = torrentVm.Pieces
                .Where(p => !p.IsComplete && !p.IsDownloading)
                .ToList();

            if (neededPieces.Count == 0)
            {
                peerVm.Status = "Seeding";
                break;
            }

            var piece = neededPieces[random.Next(neededPieces.Count)];
            piece.IsDownloading = true;

            try
            {
                await DownloadPieceAsync(peer, torrent, fileStore, torrentVm, piece, peerVm, ct);
                piece.IsDownloading = false;
                
                // Verify
                if (await fileStore.VerifyAsync(piece.Index))
                {
                    piece.IsVerified = true;
                    torrentVm.UpdateProgress(piece.Index, piece.BlocksTotal, true);
                }
            }
            catch (Exception ex)
            {
                piece.IsDownloading = false;
                peerVm.Status = $"Error: {ex.Message}";
                break;
            }
        }
    }

    private async Task DownloadPieceAsync(
        Peer peer,
        Torrent torrent,
        FileStore fileStore,
        TorrentViewModel torrentVm,
        PieceViewModel piece,
        PeerViewModel peerVm,
        CancellationToken ct)
    {
        const int blockSize = 16384;
        long pieceStart = (long)piece.Index * torrent.PieceLength;
        long pieceEnd = Math.Min(pieceStart + torrent.PieceLength, torrent.TotalLength);
        int pieceLength = (int)(pieceEnd - pieceStart);

        for (int blockOffset = 0; blockOffset < pieceLength; blockOffset += blockSize)
        {
            if (ct.IsCancellationRequested) break;

            int currentBlockSize = Math.Min(blockSize, pieceLength - blockOffset);
            var request = new BlockRequest(piece.Index, blockOffset, currentBlockSize);
            var buffer = fileStore.AllocateBlockBuffer(request);

            // Request block
            await peer.SendRequestAsync(piece.Index, blockOffset, currentBlockSize, ct);

            // Read response
            var msg = await peer.ReadMessageAsync(ct);
            if (msg == null || msg.Id != PeerMessageId.Piece)
            {
                blockOffset -= blockSize; // Retry
                continue;
            }

            // Parse piece message: index (4), begin (4), block
            if (msg.Data.Length < 8 + currentBlockSize) continue;

            int receivedIndex = BinaryPrimitives.ReadInt32BigEndian(msg.Data);
            int receivedBegin = BinaryPrimitives.ReadInt32BigEndian(msg.Data.AsSpan(4));
            
            if (receivedIndex != piece.Index || receivedBegin != blockOffset) continue;

            var blockData = msg.Data.AsSpan(8, currentBlockSize);
            blockData.CopyTo(buffer);

            // Write to disk
            await fileStore.WriteBlockAsync(request, buffer, ct);
            
            piece.BlocksReceived++;
            torrentVm.UpdateProgress(piece.Index, piece.BlocksReceived);
            
            // Update speed
            peerVm.DownloadSpeed = currentBlockSize / 1024.0; // KB/s rough
        }
    }

    private static byte[] GeneratePeerId()
    {
        var id = new byte[20];
        id[0] = (byte)'-';
        id[1] = (byte)'B';
        id[2] = (byte)'T';
        id[3] = (byte)'0';
        id[4] = (byte)'0';
        id[5] = (byte)'1';
        id[6] = (byte)'-';
        new Random().NextBytes(id.AsSpan(7));
        return id;
    }
}