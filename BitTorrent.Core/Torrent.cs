


using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using BitTorrent.Core;

namespace BitTorrent.Core;


public sealed record TorrentFileEntry(
    string RelativePath,
    long Length,
    long Offset         
);

public sealed class Torrent
{
    
    
    

    
    
    
    public Uri? AnnounceUrl { get; private set; }

    
    
    
    public int PieceLength { get; private set; }

    
    
    public byte[] Pieces { get; private set; } = Array.Empty<byte>();

    
    public IReadOnlyList<TorrentFileEntry> Files { get; private set; } = Array.Empty<TorrentFileEntry>();

    
    public long TotalLength { get; private set; }

    
    public string Name { get; private set; } = "unknown";

    
    public bool IsSingleFile { get; private set; }

    
    public byte[] InfoHash { get; private set; } = Array.Empty<byte>();

    
    
    public bool[] IsPieceVerified { get; private set; } = Array.Empty<bool>();

    
    
    public bool[] IsBlockAcquired { get; private set; } = Array.Empty<bool>();

    
    
    public string InfoHashString => Convert.ToHexString(InfoHash).ToLowerInvariant();

    
    
    

    
    public static Torrent LoadFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return LoadFromBytes(bytes);
    }

    
    public static Torrent LoadFromBytes(byte[] bytes)
    {
        var root = BEncoding.Decode(bytes);
        if (root is not BEncodedDictionary dict)
            throw new InvalidDataException(".torrent root must be a dictionary.");

        var torrent = new Torrent { AnnounceUrl = ReadAnnounce(dict) };

        
        
        
        var infoNode = dict["info"];
        if (infoNode is not BEncodedDictionary infoDict)
            throw new InvalidDataException("Missing or invalid 'info' dictionary.");

        torrent.InfoHash = ComputeInfoHash(bytes, infoNode);
        torrent.PopulateFromInfoDictionary(infoDict, files => torrent.Files = files);
        torrent.AllocateStateArrays();
        return torrent;
    }

    
    
    
    
    
    
    
    
    
    
    private static byte[] ComputeInfoHash(byte[] rawTorrent, BEncodedValue _)
    {
        
        
        
        
        var infoSpan = SliceInfoDictionary(rawTorrent);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(infoSpan);
        return sha1.GetHashAndReset();
    }

    
    
    private static byte[] SliceInfoDictionary(byte[] buffer)
    {
        
        
        if (buffer.Length == 0 || buffer[0] != (byte)'d')
            throw new InvalidDataException("Malformed .torrent: root is not a dictionary.");

        int cursor = 1;
        while (cursor < buffer.Length && buffer[cursor] != (byte)'e')
        {
            
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
                
                
                cursor = SkipValue(buffer, cursor);
                continue;
            }
            
            
            int valueStart = cursor;
            int valueEnd = SkipValue(buffer, cursor);
            var result = new byte[valueEnd - valueStart];
            Buffer.BlockCopy(buffer, valueStart, result, 0, result.Length);
            return result;
        }
        throw new InvalidDataException("Malformed .torrent: no 'info' key found.");
    }

    
    
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

    
    
    
    private static Uri? ReadAnnounce(BEncodedDictionary root)
    {
        var announce = root["announce"];
        if (announce is BEncodedString str)
        {
            
            var urlText = str.AsText();
            return Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ? uri : null;
        }
        
        return null;
    }

    
    
    
    private void PopulateFromInfoDictionary(
        BEncodedDictionary info,
        Action<IReadOnlyList<TorrentFileEntry>> setFiles)
    {
        
        var piecesNode = info["pieces"];
        if (piecesNode is BEncodedString piecesStr)
            Pieces = piecesStr.Value;
        if (Pieces.Length % 20 != 0)
            throw new InvalidDataException("'pieces' is not a multiple of 20 bytes (SHA-1 width).");

        
        var pieceLengthNode = info["piece length"];
        if (pieceLengthNode is BEncodedInteger pieceLengthInt)
            PieceLength = checked((int)pieceLengthInt.Value);

        
        var nameNode = info["name"];
        if (nameNode is BEncodedString nameStr)
            Name = nameStr.AsText();

        
        
        
        var lengthNode = info["length"];
        var filesNode = info["files"];

        if (lengthNode is BEncodedInteger singleLength && filesNode is null)
        {
            IsSingleFile = true;
            TotalLength = singleLength.Value;
            setFiles(new[] { new TorrentFileEntry(Name, TotalLength, 0L) });
            return;
        }

        
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

    
    
    private static string JoinPath(BEncodedList pathList)
    {
        
        
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
                if (i > 0) totalLen++; 
            }
            
            if (totalLen < 1024)
                return string.Concat(pooled.Take(segCount)); 
            return string.Join('/', pooled.Take(segCount));
        }
        finally
        {
            pool.Return(pooled, clearArray: false);
        }
    }

    
    
    
    private void AllocateStateArrays()
    {
        int pieceCount = Pieces.Length / 20;
        IsPieceVerified = new bool[pieceCount];
        IsBlockAcquired = new bool[pieceCount * BlocksPerPiece(PieceLength)];
    }

    
    public static int BlocksPerPiece(int pieceLength) => (pieceLength + 16383) / 16384;

    
    
    

    
    
    
    public string UrlEncodeInfoHash() => PercentEncoding.Encode(InfoHash);
}

