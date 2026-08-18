// =====================================================================================
// Peer.cs
// =====================================================================================
// One Peer's TCP connection to a single remote BitTorrent peer.
//
// The wire protocol starts with a 68-byte HANDSHAKE:
//
//   |<-- 1 --->|<-- 19 ---->|<-- 8 --->|<-- 20 --->|<-- 20 --->|
//     \x13        "BitTorrent   reserved       infohash           peer-id
//                 protocol"
//
// After the handshake, all messages have the form:
//
//   <4-byte length (big-endian)><1-byte id><payload>
//
// `length` is the byte length of the payload only — the id byte is NOT counted.
// Choke / Unchoke / Interested / NotInterested messages have length 1 (no payload).
// Bitfield has length 1 + (N/8) bytes. Piece messages include an indexed block.
// The KeepAlive message is the special case where length == 0 and there's no id byte.
//
// Spec: https://www.bittorrent.org/beps/bep_0003.html
// =====================================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BitTorrent.Core;

/// <summary>
/// Wire-protocol message IDs. The spec uses a single byte after the length prefix.
/// We expose them as enum constants so the rest of the client can use switch
/// expressions instead of magic numbers.
/// </summary>
public enum PeerMessageId : byte
{
    Choke         = 0,
    Unchoke       = 1,
    Interested    = 2,
    NotInterested = 3,
    Have          = 4,
    Bitfield       = 5,
    Request       = 6,
    Piece         = 7,
    Cancel        = 8,
}

/// <summary>
/// Lightweight incoming-message carrier. For static payloads (Choke, Unchoke,
/// Interested, NotInterested) the data array is empty. Bitfield wraps a byte[].
/// Piece wraps `(index, begin, block)` tuples. Request/Cancel wrap the request
/// descriptor `(index, begin, length)`.
/// </summary>
public sealed record PeerMessage(PeerMessageId Id, byte[] Data);

public sealed class Peer : IAsyncDisposable
{
    // -------------------------------------------------------------------------
    // Constants from the BitTorrent v1 spec.
    // -------------------------------------------------------------------------

    private static readonly byte[] ProtocolIdentifier =
    {
        (byte)19,                                     // string length
        (byte)'B',(byte)'i',(byte)'t',(byte)'T',(byte)'o',(byte)'r',(byte)'r',(byte)'e',
        (byte)'n',(byte)'t',(byte)' ',(byte)'p',(byte)'r',(byte)'o',(byte)'t',(byte)'o',(byte)'c',(byte)'o',(byte)'l'
    };

    public const int HandshakeLength = 68;            // pstrlen(1) + pstr(19) + reserved(8) + info-hash(20) + peer-id(20)

    public const int DefaultBlockSize = 16 * 1024;    // 16 KiB request block

    public const uint ProtocolBase = 0xFEEDBEEFu;    // BEP 10 extension protocol placeholder (unused here)

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>The remote peer's address, used both for connection and logging.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>Our own peer ID (20 bytes). We need it to put into the handshake.</summary>
    public byte[] PeerId { get; }

    /// <summary>The infohash that the remote peer must report to be a valid match.</summary>
    public byte[] InfoHash { get; }

    /// <summary>True after both directions of the handshake have completed successfully.</summary>
    public bool HandshakeCompleted { get; private set; }

    /// <summary>True if the LOCAL client has been choked (cannot request) by the remote.
    /// Starts TRUE; we flip to false when we receive an Unchoke message.</summary>
    public bool IsChokedRemote { get; private set; } = true;

    /// <summary>True if the LOCAL client is choking the remote (will not respond to their requests).
    /// Starts TRUE; we flip to false when we send Unchoke.</summary>
    public bool IsChokingLocal { get; private set; } = true;

    /// <summary>True if the LOCAL client has signalled Interest.</summary>
    public bool IsInterestingLocal { get; private set; }

    /// <summary>True if the REMOTE has signalled Interest.</summary>
    public bool IsInterestedRemote { get; private set; }

    /// <summary>Optional remote peer ID for diagnostics. Available after handshake.</summary>
    public byte[]? RemotePeerId { get; private set; }

    /// <summary>Bitfield of pieces the remote peer says it has. Null until received.</summary>
    public bool[]? RemoteBitfield { get; private set; }

    /// <summary>Piece count, needed to interpret the bitfield payload. Set by the
    /// application AFTER loading the Torrent and BEFORE the bitfield arrives.</summary>
    public int PieceCount { get; set; }

    private readonly TcpClient _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1); // NetworkStream isn't thread-safe for concurrent writes.

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Connect asynchronously to the supplied peer. The handshake is not
    /// performed yet; callers must invoke <see cref="PerformHandshakeAsync"/>
    /// before exchanging any other messages.</summary>
    public Peer(IPEndPoint endPoint, byte[] infoHash, byte[] peerId)
    {
        EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
        InfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        PeerId   = peerId   ?? throw new ArgumentNullException(nameof(peerId));
        if (infoHash.Length != 20) throw new ArgumentException("infoHash must be 20 bytes.", nameof(infoHash));
        if (peerId.Length   != 20) throw new ArgumentException("peerId must be 20 bytes.",   nameof(peerId));

        _client = new TcpClient { NoDelay = true }; // Nagle off — small messages.
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Open the underlying TCP connection to the remote peer.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(EndPoint.Address, EndPoint.Port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    /// <summary>
    /// Execute the 68-byte handshake in both directions. After this returns,
    /// <see cref="HandshakeCompleted"/> is true and we are ready to exchange
    /// BitTorrent wire-protocol messages.
    /// </summary>
    public async Task PerformHandshakeAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Call ConnectAsync first.");

        // ---- Transmit our handshake. ----
        var outBuf = BuildHandshakeBytes(PeerId, InfoHash);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(outBuf, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        // ---- Receive the remote handshake. ----
        var inBuf = new byte[HandshakeLength];
        await ReadExactlyAsync(_stream, inBuf, 0, HandshakeLength, ct).ConfigureAwait(false);

        // ---- Validate. ----
        // First byte is the protocol-string length which must equal the length
        // of our identifier; second 19 bytes must match exactly.
        if (!inBuf.AsSpan(0, ProtocolIdentifier.Length).SequenceEqual(ProtocolIdentifier))
            throw new InvalidDataException("Peer protocol identifier mismatch.");
        // The 8 reserved bytes are unused in v1; BEP 10 extension protocol sets
        // some of them but we don't implement extensions in this v1.0 client.
        // Skip check; just ignore them.

        // Remote infohash must equal ours.
        if (!inBuf.AsSpan(28, 20).SequenceEqual(InfoHash))
            throw new InvalidDataException("Peer announced a different infohash.");

        RemotePeerId = new byte[20];
        Buffer.BlockCopy(inBuf, 48, RemotePeerId, 0, 20);

        HandshakeCompleted = true;
    }

    // -------------------------------------------------------------------------
    // Byte-level send/receive
    // -------------------------------------------------------------------------

    /// <summary>Send a fixed-length message with the given ID and payload.</summary>
    public async Task SendMessageAsync(PeerMessageId id, byte[]? payload = null,
                                       CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        payload ??= Array.Empty<byte>();
        int len = payload.Length;
        if (len + 1 > int.MaxValue)
            throw new ArgumentException("Message payload too large.");

        // 4-byte big-endian length followed by 1-byte id and the payload.
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(), len + 1); // +1 accounts for the id byte.
        header[4] = (byte)id;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Send a 4-byte length zero keep-alive message.</summary>
    public async Task SendKeepAliveAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        var header = new byte[4]; // zero length
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(header, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Read one protocol message from the network. Returns null on keep-alive.</summary>
    public async Task<PeerMessage?> ReadMessageAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        // 1) Length prefix
        var lengthBuf = new byte[4];
        await ReadExactlyAsync(_stream, lengthBuf, 0, 4, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);

        // 2) Length 0 means KeepAlive — return null to signal "no payload".
        if (length == 0) return null;

        // 3) ID + payload
        var idBuf = new byte[1];
        await ReadExactlyAsync(_stream, idBuf, 0, 1, ct).ConfigureAwait(false);
        var id = (PeerMessageId)idBuf[0];

        // Body is `length - 1` bytes long (the id itself is excluded).
        int bodyLen = length - 1;
        var body = new byte[bodyLen];
        if (bodyLen > 0)
            await ReadExactlyAsync(_stream, body, 0, bodyLen, ct).ConfigureAwait(false);

        // 4) Update local bitfield if applicable.
        return new PeerMessage(id, body);
    }

    /// <summary>Dispatch a single incoming message into the local state variables.</summary>
    public void Handle(PeerMessage msg)
    {
        switch (msg.Id)
        {
            case PeerMessageId.Choke:
                IsChokedRemote = true;          // We got Choked.
                break;
            case PeerMessageId.Unchoke:
                IsChokedRemote = false;         // We got Unchoked.
                break;
            case PeerMessageId.Interested:
                IsInterestedRemote = true;      // Remote wants our pieces.
                break;
            case PeerMessageId.NotInterested:
                IsInterestedRemote = false;
                break;
            case PeerMessageId.Bitfield:
                if (PieceCount <= 0)
                    throw new InvalidOperationException("Set Peer.PieceCount before receiving Bitfield.");
                if (msg.Data.Length < (PieceCount + 7) / 8)
                    throw new InvalidDataException("Bitfield shorter than expected.");
                RemoteBitfield = new bool[PieceCount];
                for (int i = 0; i < PieceCount; i++)
                {
                    if ((msg.Data[i >> 3] & (1 << (7 - (i & 7)))) != 0)
                        RemoteBitfield[i] = true;
                }
                break;
            // Have / Request / Piece / Cancel updates are not state-machine
            // relevant here; the orchestrator polls those on demand.
        }
    }

    // -------------------------------------------------------------------------
    // Convenience skeletons for every message type a v1 client might send.
    // They keep the call sites readable; underlying work is delegated to
    // SendMessageAsync above.
    // -------------------------------------------------------------------------

    public Task SendChokeAsync(CancellationToken ct = default)
    {
        IsChokingLocal = true;
        return SendMessageAsync(PeerMessageId.Choke, null, ct);
    }

    public Task SendUnchokeAsync(CancellationToken ct = default)
    {
        IsChokingLocal = false;
        return SendMessageAsync(PeerMessageId.Unchoke, null, ct);
    }

    public Task SendInterestedAsync(CancellationToken ct = default)
    {
        IsInterestingLocal = true;
        return SendMessageAsync(PeerMessageId.Interested, null, ct);
    }

    public Task SendNotInterestedAsync(CancellationToken ct = default)
    {
        IsInterestingLocal = false;
        return SendMessageAsync(PeerMessageId.NotInterested, null, ct);
    }

    /// <summary>Send our local bitfield. Each bit tells the remote whether we have that piece.</summary>
    public Task SendBitfieldAsync(bool[] havePieces, CancellationToken ct = default)
    {
        int byteCount = (havePieces.Length + 7) / 8;
        var payload = new byte[byteCount];
        for (int i = 0; i < havePieces.Length; i++)
            if (havePieces[i])
                payload[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return SendMessageAsync(PeerMessageId.Bitfield, payload, ct);
    }

    /// <summary>Send a "Have" announcement for one piece.</summary>
    public Task SendHaveAsync(int pieceIndex, CancellationToken ct = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);
        return SendMessageAsync(PeerMessageId.Have, payload, ct);
    }

    /// <summary>Send a request for a 16 KiB block.</summary>
    public Task SendRequestAsync(int pieceIndex, int offset, int length, CancellationToken ct = default)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0),  pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4),  offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8),  length);
        return SendMessageAsync(PeerMessageId.Request, payload, ct);
    }

    /// <summary>Send a Piece response containing an indexed block.</summary>
    public Task SendPieceAsync(int pieceIndex, int offset, byte[] block, CancellationToken ct = default)
    {
        var payload = new byte[8 + block.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0),  pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4),  offset);
        Buffer.BlockCopy(block, 0, payload, 8, block.Length);
        return SendMessageAsync(PeerMessageId.Piece, payload, ct);
    }

    /// <summary>Cancel a previously-issued Request.</summary>
    public Task SendCancelAsync(int pieceIndex, int offset, int length, CancellationToken ct = default)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);
        return SendMessageAsync(PeerMessageId.Cancel, payload, ct);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    /// <summary>Allocate the 68-byte handshake buffer.</summary>
    private static byte[] BuildHandshakeBytes(byte[] peerId, byte[] infoHash)
    {
        var buf = new byte[HandshakeLength];
        // 1) Protocol identifier (20 bytes).
        Buffer.BlockCopy(ProtocolIdentifier, 0, buf, 0, ProtocolIdentifier.Length);
        // 2) 8 reserved bytes (already zero from `new byte[]`).
        // 3) infohash at offset 28.
        Buffer.BlockCopy(infoHash, 0, buf, 28, 20);
        // 4) peer-id at offset 48.
        Buffer.BlockCopy(peerId, 0, buf, 48, 20);
        return buf;
    }

    /// <summary>
    /// Read EXACTLY <paramref name="count"/> bytes — never less. Recovers from
    /// short reads because NetworkStream itself does not guarantee a single
    /// ReadAsync call returns the full requested amount.
    /// </summary>
    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buf, int offset, int count,
                                                CancellationToken ct)
    {
        while (count > 0)
        {
            int read = await stream.ReadAsync(buf.AsMemory(offset, count), ct).ConfigureAwait(false);
            if (read <= 0) throw new EndOfStreamException("Peer closed connection prematurely.");
            offset += read;
            count  -= read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _stream?.Dispose();
            _client.Dispose();
            _writeLock.Dispose();
        }
        catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
