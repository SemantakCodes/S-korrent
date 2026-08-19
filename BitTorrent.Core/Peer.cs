


using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BitTorrent.Core;


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


public sealed record PeerMessage(PeerMessageId Id, byte[] Data);

public sealed class Peer : IAsyncDisposable
{
    
    
    

    private static readonly byte[] ProtocolIdentifier =
    {
        (byte)19,                                     
        (byte)'B',(byte)'i',(byte)'t',(byte)'T',(byte)'o',(byte)'r',(byte)'r',(byte)'e',
        (byte)'n',(byte)'t',(byte)' ',(byte)'p',(byte)'r',(byte)'o',(byte)'t',(byte)'o',(byte)'c',(byte)'o',(byte)'l'
    };

    public const int HandshakeLength = 68;            

    public const int DefaultBlockSize = 16 * 1024;    

    public const uint ProtocolBase = 0xFEEDBEEFu;    

    
    
    

    
    public IPEndPoint EndPoint { get; }

    
    public byte[] PeerId { get; }

    
    public byte[] InfoHash { get; }

    
    public bool HandshakeCompleted { get; private set; }

    
    
    public bool IsChokedRemote { get; private set; } = true;

    
    
    public bool IsChokingLocal { get; private set; } = true;

    
    public bool IsInterestingLocal { get; private set; }

    
    public bool IsInterestedRemote { get; private set; }

    
    public byte[]? RemotePeerId { get; private set; }

    
    public bool[]? RemoteBitfield { get; private set; }

    
    
    public int PieceCount { get; set; }

    private readonly TcpClient _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1); 

    
    
    

    
    
    
    public Peer(IPEndPoint endPoint, byte[] infoHash, byte[] peerId)
    {
        EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
        InfoHash = infoHash ?? throw new ArgumentNullException(nameof(infoHash));
        PeerId   = peerId   ?? throw new ArgumentNullException(nameof(peerId));
        if (infoHash.Length != 20) throw new ArgumentException("infoHash must be 20 bytes.", nameof(infoHash));
        if (peerId.Length   != 20) throw new ArgumentException("peerId must be 20 bytes.",   nameof(peerId));

        _client = new TcpClient { NoDelay = true }; 
    }

    
    
    

    
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(EndPoint.Address, EndPoint.Port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    
    
    
    
    
    public async Task PerformHandshakeAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Call ConnectAsync first.");

        
        var outBuf = BuildHandshakeBytes(PeerId, InfoHash);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(outBuf, ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }

        
        var inBuf = new byte[HandshakeLength];
        await ReadExactlyAsync(_stream, inBuf, 0, HandshakeLength, ct).ConfigureAwait(false);

        
        
        
        if (!inBuf.AsSpan(0, ProtocolIdentifier.Length).SequenceEqual(ProtocolIdentifier))
            throw new InvalidDataException("Peer protocol identifier mismatch.");
        
        
        

        
        if (!inBuf.AsSpan(28, 20).SequenceEqual(InfoHash))
            throw new InvalidDataException("Peer announced a different infohash.");

        RemotePeerId = new byte[20];
        Buffer.BlockCopy(inBuf, 48, RemotePeerId, 0, 20);

        HandshakeCompleted = true;
    }

    
    
    

    
    public async Task SendMessageAsync(PeerMessageId id, byte[]? payload = null,
                                       CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        payload ??= Array.Empty<byte>();
        int len = payload.Length;
        if (len + 1 > int.MaxValue)
            throw new ArgumentException("Message payload too large.");

        
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(), len + 1); 
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

    
    public async Task SendKeepAliveAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        var header = new byte[4]; 
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(header, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    
    public async Task<PeerMessage?> ReadMessageAsync(CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        
        var lengthBuf = new byte[4];
        await ReadExactlyAsync(_stream, lengthBuf, 0, 4, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);

        
        if (length == 0) return null;

        
        var idBuf = new byte[1];
        await ReadExactlyAsync(_stream, idBuf, 0, 1, ct).ConfigureAwait(false);
        var id = (PeerMessageId)idBuf[0];

        
        int bodyLen = length - 1;
        var body = new byte[bodyLen];
        if (bodyLen > 0)
            await ReadExactlyAsync(_stream, body, 0, bodyLen, ct).ConfigureAwait(false);

        
        return new PeerMessage(id, body);
    }

    
    public void Handle(PeerMessage msg)
    {
        switch (msg.Id)
        {
            case PeerMessageId.Choke:
                IsChokedRemote = true;          
                break;
            case PeerMessageId.Unchoke:
                IsChokedRemote = false;         
                break;
            case PeerMessageId.Interested:
                IsInterestedRemote = true;      
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
            
            
        }
    }

    
    
    
    
    

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

    
    public Task SendBitfieldAsync(bool[] havePieces, CancellationToken ct = default)
    {
        int byteCount = (havePieces.Length + 7) / 8;
        var payload = new byte[byteCount];
        for (int i = 0; i < havePieces.Length; i++)
            if (havePieces[i])
                payload[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        return SendMessageAsync(PeerMessageId.Bitfield, payload, ct);
    }

    
    public Task SendHaveAsync(int pieceIndex, CancellationToken ct = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, pieceIndex);
        return SendMessageAsync(PeerMessageId.Have, payload, ct);
    }

    
    public Task SendRequestAsync(int pieceIndex, int offset, int length, CancellationToken ct = default)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0),  pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4),  offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8),  length);
        return SendMessageAsync(PeerMessageId.Request, payload, ct);
    }

    
    public Task SendPieceAsync(int pieceIndex, int offset, byte[] block, CancellationToken ct = default)
    {
        var payload = new byte[8 + block.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0),  pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4),  offset);
        Buffer.BlockCopy(block, 0, payload, 8, block.Length);
        return SendMessageAsync(PeerMessageId.Piece, payload, ct);
    }

    
    public Task SendCancelAsync(int pieceIndex, int offset, int length, CancellationToken ct = default)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), length);
        return SendMessageAsync(PeerMessageId.Cancel, payload, ct);
    }

    
    
    

    
    private static byte[] BuildHandshakeBytes(byte[] peerId, byte[] infoHash)
    {
        var buf = new byte[HandshakeLength];
        
        Buffer.BlockCopy(ProtocolIdentifier, 0, buf, 0, ProtocolIdentifier.Length);
        
        
        Buffer.BlockCopy(infoHash, 0, buf, 28, 20);
        
        Buffer.BlockCopy(peerId, 0, buf, 48, 20);
        return buf;
    }

    
    
    
    
    
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
        catch {  }
        await Task.CompletedTask;
    }
}

