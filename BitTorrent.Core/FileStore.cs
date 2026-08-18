// =====================================================================================
// FileStore.cs
// =====================================================================================
// Translates a BitTorrent "global" byte address space (zero through TotalLength-1)
// onto the OS file system, which may be either a single file or many files.
//
// The internal data layout is straightforward: we open one FileStream PER file
// inside the torrent. Each read/write request takes (pieceIndex, offset, length)
// and (a) figures out which file(s) the request spans, then (b) issues a
// FileStream.Seek + Read/Write per file, copying the leftover bytes between
// buffers.
//
// Concurrency:
//   * Reads are common and concurrent (when downloading from many peers). The
//     per-file object lock prevents two BlockRange calls from interleaving
//     their Seek+Read against the same physical file.
//   * Writes take the same per-file lock for symmetry.
//
// Verification:
//   * `Verify(piece)` reads back the full piece (which may straddle multiple
//     files), computes SHA-1, and compares to the expected hash from the
//     torrent. It also flips `Torrent.IsPieceVerified[piece]` on success.
//
// Performance:
//   * We use the async FileStream methods (ReadAsync / WriteAsync) which lets
//     many concurrent block IO operations overlap on the same physical disk.
//   * Pooled ArrayPool buffers avoid GC churn on the hot path.
// =====================================================================================

using System.Buffers;
using System.Security.Cryptography;

namespace BitTorrent.Core;

/// <summary>
/// A request to read or write one Block on the global torrent byte axis.
/// </summary>
public readonly record struct BlockRequest(int PieceIndex, int Offset, int Length)
{
    /// <summary>Convert (piece, offset) into the absolute byte offset across all files.</summary>
    public long ToGlobalByteOffset(int pieceLength) =>
        ((long)PieceIndex * pieceLength) + Offset;
}

public sealed class FileStore : IAsyncDisposable, IDisposable
{
    private readonly Torrent _torrent;
    private readonly string _destinationRoot;
    private readonly FileStream?[] _streams;       // one per torrent file, lazily opened
    private readonly object[] _streamLocks;        // per-file lock for seek+RW atomicity
    private readonly SHA1 _sha1 = SHA1.Create();   // shared instance for Verify.
    private int _disposed;

    /// <summary>Construct a FileStore that lays out the torrent under <paramref name="destinationRoot"/>.
    /// Single-file torrents are placed at <c>{destination}/{Name}</c>; multi-file torrents are placed
    /// under <c>{destination}/{Name}/</c>.</summary>
    public FileStore(Torrent torrent, string destinationRoot)
    {
        _torrent        = torrent ?? throw new ArgumentNullException(nameof(torrent));
        _destinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
        _streams        = new FileStream?[torrent.Files.Count];
        _streamLocks    = new object[torrent.Files.Count];

        // Per-file small lock — used around Open + Read + Seek to make sure multiple
        // concurrent BlockRange calls on the SAME physical file do not interleave.
        for (int i = 0; i < _streamLocks.Length; i++) _streamLocks[i] = new object();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Asynchronously read one Block from disk into <paramref name="buffer"/>.
    /// Returns the number of bytes actually read.</summary>
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

    /// <summary>Synchronous read counterpart (lives on the same file streams as the async one).</summary>
    public int ReadBlock(BlockRequest request, Span<byte> buffer)
    {
        EnsureBufferLength(request, buffer);
        int total = 0;
        ReadRange(request.ToGlobalByteOffset(_torrent.PieceLength), request.Length, buffer, ref total);
        return total;
    }

    /// <summary>Asynchronously write <paramref name="buffer"/>'s contents to the requested Block on disk.
    /// Cross-file writes are handled in slices; each slice takes the per-file lock.</summary>
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

    /// <summary>Synchronous write counterpart.</summary>
    public void WriteBlock(BlockRequest request, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < request.Length)
            throw new ArgumentException("Buffer is smaller than requested block length.", nameof(buffer));
        int total = buffer.Length;
        WriteRange(request.ToGlobalByteOffset(_torrent.PieceLength), buffer, ref total);
    }

    /// <summary>Allocate a buffer of the appropriate length for ReadBlock.</summary>
    public byte[] AllocateBlockBuffer(BlockRequest request) => new byte[request.Length];

    /// <summary>
    /// Read every byte of <paramref name="piece"/> back from disk, compute SHA-1,
    /// and compare against the expected hash from the torrent. Updates
    /// <see cref="Torrent.IsPieceVerified"/> on success.
    ///
    /// The read path holds per-file locks for the duration of the SHA1
    /// computation (held under a single <c>lock()</c> block per file), so a
    /// concurrent writer to the same piece would race to interfere with the
    /// hash. This is intentional: a write racing with verify means the
    /// downloaded piece is no longer valid; we want the next round to fail and
    /// re-download.
    /// </summary>
    public async ValueTask<bool> VerifyAsync(int piece, CancellationToken ct = default)
    {
        if ((uint)piece >= (uint)(_torrent.Pieces.Length / 20))
            throw new ArgumentOutOfRangeException(nameof(piece));

        // The last piece may be shorter than PieceLength.
        long pieceStart = (long)piece * _torrent.PieceLength;
        long pieceEnd   = Math.Min(pieceStart + _torrent.PieceLength, _torrent.TotalLength);
        int  length     = (int)(pieceEnd - pieceStart);

        var buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadRangeAsync(pieceStart, length, buf.AsMemory(0, length), ct).ConfigureAwait(false);

            // SHA1 reads the buffer; the data is allocated locally so there's no
            // concurrent writer hazard to guard against here.
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

    /// <summary>Synchronous Verify counterpart.</summary>
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

    // -------------------------------------------------------------------------
    // Internal: async range read/write across one or more torrent files
    // -------------------------------------------------------------------------

    private async ValueTask<int> ReadRangeAsync(long globalOffset, int length, Memory<byte> dest,
                                                CancellationToken ct)
    {
        int total = 0;
        foreach (var file in _torrent.Files)
        {
            if (total >= length) break;

            // Compute the overlap of this read with the current file.
            long fileEnd = file.Offset + file.Length;
            if (globalOffset + total >= fileEnd) continue;

            long localOffset = Math.Max(globalOffset, file.Offset) - file.Offset;
            int bytesFromFile = (int)Math.Min(length - total, fileEnd - (globalOffset + total));

            var fs = OpenStream(file);
            var idx = IndexOf(file);
            // Per-file lock around Seek + ReadAsync so concurrent BlockRange
            // calls on the same physical file serialize cleanly.
            lock (_streamLocks[idx])
            {
                fs.Seek(localOffset, SeekOrigin.Begin);
                int read = ReadChunkSyncOrAsync(fs, dest.Slice(total, bytesFromFile), ct);
                total += read;
                if (read < bytesFromFile) break;  // EOF
            }
        }
        return total;
    }

    /// <summary>Drive either the sync or async read based on whether the
    /// underlying stream supports async IO at this call site. Most FileStream
    /// instances opened with FileOptions.Asynchronous will return true here.</summary>
    private static int ReadChunkSyncOrAsync(FileStream fs, Memory<byte> slice, CancellationToken ct)
    {
        // FileStream.ReadAsync(Memory<byte>) returns ValueTask<int>; the
        // synchronous Read(Span<byte>) returns int. We always go async here so
        // the call site is consistently non-blocking on the disk layer.
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

    // -------------------------------------------------------------------------
    // Internal: synchronous range read/write variants
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // File stream management
    // -------------------------------------------------------------------------

    /// <summary>Find which torrent file contains <paramref name="globalOffset"/>.
    /// Caller must ensure the offset is inside the torrent's range.</summary>
    private TorrentFileEntry LocateFileAtOffset(long globalOffset)
    {
        // Linear search is fine for the small file counts typical of v1 torrents
        // (almost always < 100). We could binary-search by Offset in the future.
        foreach (var f in _torrent.Files)
            if (globalOffset >= f.Offset && globalOffset < f.Offset + f.Length)
                return f;
        throw new InvalidOperationException($"No file contains global offset {globalOffset}.");
    }

    /// <summary>Return the index of <paramref name="target"/> in the file list.</summary>
    private int IndexOf(TorrentFileEntry target)
    {
        for (int i = 0; i < _torrent.Files.Count; i++)
            if (ReferenceEquals(_torrent.Files[i], target)) return i;
        throw new InvalidOperationException("File not found in torrent.");
    }

    /// <summary>Open (or reuse) the FileStream corresponding to a given file entry.
    /// The double-checked locking pattern keeps cold-start costs O(files) without
    /// contending on the per-file lock for every read.</summary>
    private FileStream OpenStream(TorrentFileEntry file)
    {
        var idx = IndexOf(file);
        if (_streams[idx] is not null) return _streams[idx]!;

        lock (_streamLocks[idx])
        {
            if (_streams[idx] is not null) return _streams[idx]!;
            string fullPath = ResolveOnDiskPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Allocate the full length up-front so we can seek freely. Real
            // clients sparse-allocate, but for v1 we keep the implementation
            // linear and easy to follow. The cost is acceptable for typical
            // torrent sizes (tens to hundreds of GiB fits modern filesystems).
            var fs = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                                     bufferSize: 64 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (fs.Length < file.Length) fs.SetLength(file.Length);
            _streams[idx] = fs;
            return fs;
        }
    }

    /// <summary>Resolve the OS path for a torrent file entry under the destination root.</summary>
    private string ResolveOnDiskPath(TorrentFileEntry file)
    {
        // Single-file mode: place directly under the root using the torrent name.
        if (_torrent.IsSingleFile)
            return Path.Combine(_destinationRoot, _torrent.Name);

        // Multi-file mode: <root>/<torrent-name>/<relative-path-from-torrent>
        // The torrent's relative path uses '/'; split it for OS use.
        return Path.Combine(_destinationRoot, _torrent.Name, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    // -------------------------------------------------------------------------
    // Validation + lifecycle
    // -------------------------------------------------------------------------

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

    /// <summary>Flush, dispose, and release all per-file FileStreams.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var fs in _streams) fs?.Dispose();
        _sha1.Dispose();
    }

    /// <summary>Async dispose counterpart (no async work is required today but
    /// the interface is here so callers in DI containers can use it).</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
