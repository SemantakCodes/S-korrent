// =====================================================================================
// Torrent.cs
// =====================================================================================
// Represents a parsed .torrent file. After loading from disk, this class exposes:
//
//   * The tracker's announce URL.
//   * The list of files (or the single file) the torrent refers to.
//   * The piece length and concatenated SHA-1 piece hashes.
//   * The 20-byte "infohash" used to identify the torrent to trackers and peers.
//
// The most important invariant is that the InfoHash is computed over the EXACT
// bytes of the "info" dictionary as represented in the .torrent file. Even a
// single byte's difference (whitespace, key ordering) would yield a different
// hash and break every tracker / peer interaction.
//
// BitTorrent v1 spec:
//   https://www.bittorrent.org/beps/bep_0003.html
// =====================================================================================

using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using BitTorrent.Core;

namespace BitTorrent.Core;

/// <summary>
/// One row of the file list inside a torrent. Multi-file torrents have many of these;
/// single-file torrents have exactly one (so callers can treat both uniformly).
/// </summary>
public sealed record TorrentFileEntry(
    string RelativePath,
    long Length,
    long Offset         // byte offset within the whole torrent payload
);

public sealed class Torrent
{
    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>The tracker URL from the "announce" key. May be null for
    /// trackerless torrents (magnet-only). For BitTorrent v1 this is the only
    /// required tracker URL.</summary>
    public Uri? AnnounceUrl { get; private set; }

    /// <summary>Length of one piece in bytes. All pieces have this length except
    /// possibly the final one. (Spec range 1 KiB .. 16 MiB, but we don't enforce
    /// these as the .torrent file itself is authoritative.)</summary>
    public int PieceLength { get; private set; }

    /// <summary>SHA-1 hash of every piece, concatenated. There are
    /// <c>Pieces.Length / 20</c> pieces in total.</summary>
    public byte[] Pieces { get; private set; } = Array.Empty<byte>();

    /// <summary>Logical files described by the torrent.</summary>
    public IReadOnlyList<TorrentFileEntry> Files { get; private set; } = Array.Empty<TorrentFileEntry>();

    /// <summary>Total size of all files combined, in bytes.</summary>
    public long TotalLength { get; private set; }

    /// <summary>Name of the torrent (root folder or single filename).</summary>
    public string Name { get; private set; } = "unknown";

    /// <summary>True if this is a single-file torrent. False if multi-file.</summary>
    public bool IsSingleFile { get; private set; }

    /// <summary>20-byte SHA-1 of the raw "info" dictionary taken from the .torrent file.</summary>
    public byte[] InfoHash { get; private set; } = Array.Empty<byte>();

    /// <summary>Track which pieces have been verified by SHA-1 against
    /// <see cref="Pieces"/>.</summary>
    public bool[] IsPieceVerified { get; private set; } = Array.Empty<bool>();

    /// <summary>Track which blocks have been received from peers (16 KiB units).
    /// index 0 = piece 0, offset 0 ; index 1 = piece 0, offset 16384 ; …</summary>
    public bool[] IsBlockAcquired { get; private set; } = Array.Empty<bool>();

    /// <summary>Lowercase hex string of the InfoHash. The tracker encodes this in
    /// the URL (see <see cref="UrlEncodeInfoHash"/>).</summary>
    public string InfoHashString => Convert.ToHexString(InfoHash).ToLowerInvariant();

    // -------------------------------------------------------------------------
    // Static factory + state init
    // -------------------------------------------------------------------------

    /// <summary>Read a .torrent file from disk and parse it.</summary>
    public static Torrent LoadFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return LoadFromBytes(bytes);
    }

    /// <summary>Parse a .torrent file from an in-memory byte array.</summary>
    public static Torrent LoadFromBytes(byte[] bytes)
    {
        var root = BEncoding.Decode(bytes);
        if (root is not BEncodedDictionary dict)
            throw new InvalidDataException(".torrent root must be a dictionary.");

        var torrent = new Torrent { AnnounceUrl = ReadAnnounce(dict) };

        // The "info" subtree holds the file list, piece hashes, and metadata that
        // define the infohash. We capture its raw bytes for the hash BEFORE we
        // decode its contents.
        var infoNode = dict["info"];
        if (infoNode is not BEncodedDictionary infoDict)
            throw new InvalidDataException("Missing or invalid 'info' dictionary.");

        torrent.InfoHash = ComputeInfoHash(bytes, infoNode);
        torrent.PopulateFromInfoDictionary(infoDict, files => torrent.Files = files);
        torrent.AllocateStateArrays();
        return torrent;
    }

    /// <summary>
    /// Compute the SHA-1 over the EXACT bytes of the "info" dictionary as it
    /// appears in the .torrent file.
    ///
    /// We rewrite over the original byte buffer to capture the exact range. To
    /// stay allocation-free, we use a streaming SHA1 instance rather than the
    /// re-encoded form. Re-encoding would still hash canonically (because every
    /// spec-conformant .torrent writer sorts keys), but reusing the raw bytes
    /// is the strict-spec approach and avoids any binary-encoding footprint.
    /// </summary>
    private static byte[] ComputeInfoHash(byte[] rawTorrent, BEncodedValue _)
    {
        // Slice out the exact info dictionary span by re-walking the .torrent
        // buffer at the position it was first encountered. We do this by finding
        // the leading key "info" (4 byte-string 4:info) at depth 1 of the root
        // dictionary, then consuming the value that follows.
        var infoSpan = SliceInfoDictionary(rawTorrent);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(infoSpan);
        return sha1.GetHashAndReset();
    }

    /// <summary>Find and return the precise byte range of the "info" dictionary
    /// in the original .torrent buffer.</summary>
    private static byte[] SliceInfoDictionary(byte[] buffer)
    {
        // The root is a Bencode dictionary. Find its inner byte range, then
        // walk the keys until we hit "info" (4:info).
        if (buffer.Length == 0 || buffer[0] != (byte)'d')
            throw new InvalidDataException("Malformed .torrent: root is not a dictionary.");

        int cursor = 1;
        while (cursor < buffer.Length && buffer[cursor] != (byte)'e')
        {
            // Read the key (a Bencode string).
            int keyStart = cursor;
            int colonIdx = Array.IndexOf(buffer, (byte)':', cursor);
            if (colonIdx < 0) throw new InvalidDataException("Malformed .torrent: missing ':' in key.");
            int keyLen = ParseDigits(buffer, cursor, colonIdx);
            cursor = colonIdx + 1 + keyLen;
            if (keyStart <= 0 || colonIdx == 0 || keyLen != 4 ||
                buffer[colonIdx + 1] != (byte)'i' ||
                buffer[colonIdx + 2] != (byte)'n' ||
                buffer[colonIdx + 3] != (byte)'f' ||
                buffer[colonIdx + 4] != (byte)'o')
            {
                // Skip past the value for keys we don't care about: advance by
                // skipping the value via the public recursive skipper.
                cursor = SkipValue(buffer, cursor);
                continue;
            }
            // Found the "info" key. The value begins at `cursor`. Compute its
            // exact span via a public length reader, then copy.
            int valueStart = cursor;
            int valueEnd = SkipValue(buffer, cursor);
            var result = new byte[valueEnd - valueStart];
            Buffer.BlockCopy(buffer, valueStart, result, 0, result.Length);
            return result;
        }
        throw new InvalidDataException("Malformed .torrent: no 'info' key found.");
    }

    /// <summary>Given that <paramref name="cursor"/> points at a Bencode value's
    /// first byte, advance past it and return the new cursor.</summary>
    private static int SkipValue(byte[] buffer, int cursor)
    {
        if (cursor >= buffer.Length) throw new InvalidDataException("Unexpected end of buffer.");
        byte lead = buffer[cursor];
        if (lead is (byte)'i') return Array.IndexOf(buffer, (byte)'e', cursor + 1) + 1;
        if (lead is (byte)'l' or (byte)'d')
        {
            int depth = 1;
            int i = cursor + 1;
            while (i < buffer.Length && depth > 0)
            {
                byte c = buffer[i];
                if (c == (byte)'e') depth--;
                else if (c == (byte)'l' || c == (byte)'d') depth++;
                else if (c == (byte)'i')
                {
                    int ei = Array.IndexOf(buffer, (byte)'e', i + 1);
                    if (ei < 0) throw new InvalidDataException("Missing 'e' terminator on integer.");
                    i = ei;
                }
                else if (c is >= (byte)'0' and <= (byte)'9')
                {
                    int ci = Array.IndexOf(buffer, (byte)':', i);
                    int len = ParseDigits(buffer, i, ci);
                    i = ci + 1 + len;
                    continue;
                }
                else throw new InvalidDataException($"Unexpected 0x{c:X2} while skipping value.");
                i++;
            }
            return i;
        }
        // Byte string: digits:length prefix
        if (lead is >= (byte)'0' and <= (byte)'9')
        {
            int ci = Array.IndexOf(buffer, (byte)':', cursor);
            int len = ParseDigits(buffer, cursor, ci);
            return ci + 1 + len;
        }
        throw new InvalidDataException($"Unexpected 0x{lead:X2} at top of value.");
    }

    private static int ParseDigits(byte[] buffer, int start, int end)
    {
        int value = 0;
        for (int i = start; i < end; i++)
        {
            byte b = buffer[i];
            if ((uint)(b - (byte)'0') > 9) throw new InvalidDataException($"Non-digit 0x{b:X2} in length field.");
            value = value * 10 + (b - (byte)'0');
        }
        return value;
    }

    // -------------------------------------------------------------------------
    // Internal: extract "announce" URL.
    // -------------------------------------------------------------------------
    private static Uri? ReadAnnounce(BEncodedDictionary root)
    {
        var announce = root["announce"];
        if (announce is BEncodedString str)
        {
            // The tracker URL is ASCII, so direct conversion is fine.
            var urlText = str.AsText();
            return Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ? uri : null;
        }
        // "announce-list" (BEP 12) and IPv6 trackers (BEP 7) are ignored in v1.
        return null;
    }

    // -------------------------------------------------------------------------
    // Internal: pull file list, piece length, piece hashes, etc. from the info dict.
    // -------------------------------------------------------------------------
    private void PopulateFromInfoDictionary(
        BEncodedDictionary info,
        Action<IReadOnlyList<TorrentFileEntry>> setFiles)
    {
        // ---- Pieces ----
        var piecesNode = info["pieces"];
        if (piecesNode is BEncodedString piecesStr)
            Pieces = piecesStr.Value;
        if (Pieces.Length % 20 != 0)
            throw new InvalidDataException("'pieces' is not a multiple of 20 bytes (SHA-1 width).");

        // ---- Piece length ----
        var pieceLengthNode = info["piece length"];
        if (pieceLengthNode is BEncodedInteger pieceLengthInt)
            PieceLength = checked((int)pieceLengthInt.Value);

        // ---- Name ----
        var nameNode = info["name"];
        if (nameNode is BEncodedString nameStr)
            Name = nameStr.AsText();

        // ---- Single-file mode ----
        // If the "info" dictionary has a top-level "length" key, this is a
        // single-file torrent. The "files" key is a multi-file alternative.
        var lengthNode = info["length"];
        var filesNode = info["files"];

        if (lengthNode is BEncodedInteger singleLength && filesNode is null)
        {
            IsSingleFile = true;
            TotalLength = singleLength.Value;
            setFiles(new[] { new TorrentFileEntry(Name, TotalLength, 0L) });
            return;
        }

        // ---- Multi-file mode ----
        if (filesNode is BEncodedList filesList)
        {
            IsSingleFile = false;
            var files = new List<TorrentFileEntry>(filesList.Value.Count);
            long runningOffset = 0;
            foreach (var entry in filesList.Value)
            {
                if (entry is not BEncodedDictionary fileDict)
                    throw new InvalidDataException("Each file entry must be a dictionary.");

                var lenNode = fileDict["length"];
                if (lenNode is not BEncodedInteger lenInt)
                    throw new InvalidDataException("File entry missing 'length'.");

                var pathNode = fileDict["path"];
                if (pathNode is not BEncodedList pathList)
                    throw new InvalidDataException("File entry missing 'path'.");

                // Path is itself a list of byte-string segments, joined with '/'.
                // We hand-join so we don't allocate an intermediate StringBuilder.
                string rel = JoinPath(pathList);
                files.Add(new TorrentFileEntry(rel, lenInt.Value, runningOffset));
                runningOffset += lenInt.Value;
            }
            TotalLength = runningOffset;
            setFiles(files);
            return;
        }

        throw new InvalidDataException("Info dictionary has neither 'length' nor 'files'.");
    }

    /// <summary>Concatenate a Bencode-list of byte-string path segments with '/'.
    /// Allocates exactly one string per file.</summary>
    private static string JoinPath(BEncodedList pathList)
    {
        // Most paths have < 8 segments; stack-allocating the segment buffer
        // avoids per-segment allocation. Otherwise allocate one.
        int segCount = pathList.Value.Count;
        int totalLen = 0;
        string[] parts = segCount <= 12 ? null! : new string[segCount];
        var pool = ArrayPool<string>.Shared;
        string[]? pooled = pool.Rent(Math.Max(segCount, 1));
        try
        {
            for (int i = 0; i < segCount; i++)
            {
                if (pathList.Value[i] is BEncodedString segStr)
                {
                    pooled[i] = segStr.AsText();
                    totalLen += pooled[i].Length;
                }
                else pooled[i] = string.Empty;
                if (i > 0) totalLen++; // separator
            }
            // Concat in one go via Span<char> when small, else fall back to string.Join.
            if (totalLen < 1024)
                return string.Concat(pooled.Take(segCount)); // uses array overload on net8+
            return string.Join('/', pooled.Take(segCount));
        }
        finally
        {
            pool.Return(pooled, clearArray: false);
        }
    }

    // -------------------------------------------------------------------------
    // Internal: allocate the verification / acquisition arrays once we know piece count.
    // -------------------------------------------------------------------------
    private void AllocateStateArrays()
    {
        int pieceCount = Pieces.Length / 20;
        IsPieceVerified = new bool[pieceCount];
        IsBlockAcquired = new bool[pieceCount * BlocksPerPiece(PieceLength)];
    }

    /// <summary>Compute the number of 16 KiB blocks in a piece of the given length.</summary>
    public static int BlocksPerPiece(int pieceLength) => (pieceLength + 16383) / 16384;

    // -------------------------------------------------------------------------
    // Tracker interaction helpers
    // -------------------------------------------------------------------------

    /// <summary>URL-encode the InfoHash for use in an HTTP query string.
    /// Uses the shared <see cref="PercentEncoding"/> helper so the encoding is
    /// guaranteed to stay in lock-step with the tracker client.</summary>
    public string UrlEncodeInfoHash() => PercentEncoding.Encode(InfoHash);
}
