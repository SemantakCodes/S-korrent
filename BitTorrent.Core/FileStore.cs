


using System.Buffers;
using System.Security.Cryptography;

namespace BitTorrent.Core;


public readonly record struct BlockRequest(int PieceIndex, int Offset, int Length)
{
    
    public long ToGlobalByteOffset(int pieceLength) =>
        ((long)PieceIndex * pieceLength) + Offset;
}

public sealed class FileStore : IAsyncDisposable, IDisposable
{
    private readonly Torrent _torrent;
    private readonly string _destinationRoot;
    private readonly FileStream?[] _streams;       
    private readonly object[] _streamLocks;        
    private readonly SHA1 _sha1 = SHA1.Create();   
    private int _disposed;

    
    
    
    public FileStore(Torrent torrent, string destinationRoot)
    {
        _torrent        = torrent ?? throw new ArgumentNullException(nameof(torrent));
        _destinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
        _streams        = new FileStream?[torrent.Files.Count];
        _streamLocks    = new object[torrent.Files.Count];

        
        
        for (int i = 0; i < _streamLocks.Length; i++) _streamLocks[i] = new object();
    }

    
    
    

    
    
    public async ValueTask<int> ReadBlockAsync(BlockRequest request, Memory<byte> buffer,
                                               CancellationToken ct = default)
    {
        EnsureBufferLength(request, buffer.Span);
        return await ReadRangeAsync(
            request.ToGlobalByteOffset(_torrent.PieceLength),
            request.Length,
            buffer,
            ct).ConfigureAwait(false);
    }

    
    public int ReadBlock(BlockRequest request, Span<byte> buffer)
    {
        EnsureBufferLength(request, buffer);
        int total = 0;
        ReadRange(request.ToGlobalByteOffset(_torrent.PieceLength), request.Length, buffer, ref total);
        return total;
    }

    
    
    public async ValueTask WriteBlockAsync(BlockRequest request, ReadOnlyMemory<byte> buffer,
                                           CancellationToken ct = default)
    {
        if (buffer.Length < request.Length)
            throw new ArgumentException("Buffer is smaller than requested block length.", nameof(buffer));
        await WriteRangeAsync(
            request.ToGlobalByteOffset(_torrent.PieceLength),
            buffer.Slice(0, request.Length),
            ct).ConfigureAwait(false);
    }

    
    public void WriteBlock(BlockRequest request, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < request.Length)
            throw new ArgumentException("Buffer is smaller than requested block length.", nameof(buffer));
        int total = buffer.Length;
        WriteRange(request.ToGlobalByteOffset(_torrent.PieceLength), buffer, ref total);
    }

    
    public byte[] AllocateBlockBuffer(BlockRequest request) => new byte[request.Length];

    
    
    
    
    
    
    
    
    
    
    
    
    public async ValueTask<bool> VerifyAsync(int piece, CancellationToken ct = default)
    {
        if ((uint)piece >= (uint)(_torrent.Pieces.Length / 20))
            throw new ArgumentOutOfRangeException(nameof(piece));

        
        long pieceStart = (long)piece * _torrent.PieceLength;
        long pieceEnd   = Math.Min(pieceStart + _torrent.PieceLength, _torrent.TotalLength);
        int  length     = (int)(pieceEnd - pieceStart);

        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadRangeAsync(pieceStart, length, buf.AsMemory(0, length), ct).ConfigureAwait(false);

            
            
            byte[] hash;
            lock (_sha1) hash = _sha1.ComputeHash(buf, 0, length);

            var expected = new byte[20];
            Buffer.BlockCopy(_torrent.Pieces, piece * 20, expected, 0, 20);
            bool ok = hash.AsSpan().SequenceEqual(expected);
            if (ok) _torrent.IsPieceVerified[piece] = true;
            return ok;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    
    public bool Verify(int piece)
    {
        if ((uint)piece >= (uint)(_torrent.Pieces.Length / 20))
            throw new ArgumentOutOfRangeException(nameof(piece));
        long pieceStart = (long)piece * _torrent.PieceLength;
        long pieceEnd   = Math.Min(pieceStart + _torrent.PieceLength, _torrent.TotalLength);
        int length      = (int)(pieceEnd - pieceStart);

        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int total = 0;
            ReadRange(pieceStart, length, buf.AsSpan(0, length), ref total);
            byte[] hash;
            lock (_sha1) hash = _sha1.ComputeHash(buf, 0, length);
            var expected = new byte[20];
            Buffer.BlockCopy(_torrent.Pieces, piece * 20, expected, 0, 20);
            bool ok = hash.AsSpan().SequenceEqual(expected);
            if (ok) _torrent.IsPieceVerified[piece] = true;
            return ok;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    
    
    

    private async ValueTask<int> ReadRangeAsync(long globalOffset, int length, Memory<byte> dest,
                                                CancellationToken ct)
    {
        int total = 0;
        foreach (var file in _torrent.Files)
        {
            if (total >= length) break;

            
            long fileEnd = file.Offset + file.Length;
            if (globalOffset + total >= fileEnd) continue;

            long localOffset = Math.Max(globalOffset, file.Offset) - file.Offset;
            int bytesFromFile = (int)Math.Min(length - total, fileEnd - (globalOffset + total));

            var fs = OpenStream(file);
            var idx = IndexOf(file);
            
            
            lock (_streamLocks[idx])
            {
                fs.Seek(localOffset, SeekOrigin.Begin);
                int read = ReadChunkSyncOrAsync(fs, dest.Slice(total, bytesFromFile), ct);
                total += read;
                if (read < bytesFromFile) break;  
            }
        }
        return total;
    }

    
    
    
    private static int ReadChunkSyncOrAsync(FileStream fs, Memory<byte> slice, CancellationToken ct)
    {
        
        
        
        return fs.ReadAsync(slice, ct).AsTask().GetAwaiter().GetResult();
    }

    private async ValueTask WriteRangeAsync(long globalOffset, ReadOnlyMemory<byte> data,
                                            CancellationToken ct)
    {
        int written = 0;
        while (written < data.Length)
        {
            var file  = LocateFileAtOffset(globalOffset + written);
            var fs    = OpenStream(file);
            var idx   = IndexOf(file);
            long localOffset = globalOffset + written - file.Offset;
            int bytesToWrite = (int)Math.Min(data.Length - written, file.Length - localOffset);

            lock (_streamLocks[idx])
            {
                fs.Seek(localOffset, SeekOrigin.Begin);
                fs.WriteAsync(data.Slice(written, bytesToWrite), ct).AsTask()
                  .GetAwaiter().GetResult();
            }

            written += bytesToWrite;
        }
    }

    
    
    

    private void ReadRange(long globalOffset, int length, Span<byte> dest, ref int total)
    {
        foreach (var file in _torrent.Files)
        {
            if (total >= length) break;

            long fileEnd = file.Offset + file.Length;
            if (globalOffset + total >= fileEnd) continue;

            long localOffset = Math.Max(globalOffset, file.Offset) - file.Offset;
            int bytesFromFile = (int)Math.Min(length - total, fileEnd - (globalOffset + total));

            var fs = OpenStream(file);
            var idx = IndexOf(file);
            lock (_streamLocks[idx])
            {
                fs.Seek(localOffset, SeekOrigin.Begin);
                int read = fs.Read(dest.Slice(total, bytesFromFile));
                total += read;
                if (read < bytesFromFile) break;
            }
        }
    }

    private void WriteRange(long globalOffset, ReadOnlySpan<byte> data, ref int total)
    {
        int written = 0;
        while (written < data.Length)
        {
            var file = LocateFileAtOffset(globalOffset + written);
            var fs   = OpenStream(file);
            var idx  = IndexOf(file);
            long localOffset = globalOffset + written - file.Offset;
            int bytesToWrite = (int)Math.Min(data.Length - written, file.Length - localOffset);

            lock (_streamLocks[idx])
            {
                fs.Seek(localOffset, SeekOrigin.Begin);
                fs.Write(data.Slice(written, bytesToWrite));
            }

            written += bytesToWrite;
        }
        total = written;
    }

    
    
    

    
    
    private TorrentFileEntry LocateFileAtOffset(long globalOffset)
    {
        
        
        foreach (var f in _torrent.Files)
            if (globalOffset >= f.Offset && globalOffset < f.Offset + f.Length)
                return f;
        throw new InvalidOperationException($"No file contains global offset {globalOffset}.");
    }

    
    private int IndexOf(TorrentFileEntry target)
    {
        for (int i = 0; i < _torrent.Files.Count; i++)
            if (ReferenceEquals(_torrent.Files[i], target)) return i;
        throw new InvalidOperationException("File not found in torrent.");
    }

    
    
    
    private FileStream OpenStream(TorrentFileEntry file)
    {
        var idx = IndexOf(file);
        if (_streams[idx] is not null) return _streams[idx]!;

        lock (_streamLocks[idx])
        {
            if (_streams[idx] is not null) return _streams[idx]!;
            string fullPath = ResolveOnDiskPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            
            
            
            
            var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                                     bufferSize: 64 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (fs.Length < file.Length) fs.SetLength(file.Length);
            _streams[idx] = fs;
            return fs;
        }
    }

    
    private string ResolveOnDiskPath(TorrentFileEntry file)
    {
        
        if (_torrent.IsSingleFile)
            return Path.Combine(_destinationRoot, _torrent.Name);

        
        
        return Path.Combine(_destinationRoot, _torrent.Name, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    
    
    

    private static void EnsureBufferLength(BlockRequest request, Span<byte> buffer)
    {
        if (request.Length <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Block length must be positive.");
        if (buffer.Length < request.Length)
            throw new ArgumentException("Buffer is smaller than requested block length.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(FileStore));
    }

    
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var fs in _streams) fs?.Dispose();
        _sha1.Dispose();
    }

    
    
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

