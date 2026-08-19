using BitTorrent.Core;

namespace BitTorrent.WPF.Models;

public sealed class TorrentInfo
{
    public string Name { get; }
    public long TotalLength { get; }
    public int PieceLength { get; }
    public int PieceCount { get; }
    public string InfoHashHex { get; }
    public string InfoHashUrlEncoded { get; }
    public string? AnnounceUrl { get; }
    public bool IsSingleFile { get; }
    public IReadOnlyList<TorrentFileInfo> Files { get; }
    public byte[] InfoHash { get; }
    public byte[] Pieces { get; }
    public Torrent Torrent { get; }

    public TorrentInfo(Torrent torrent)
    {
        Torrent = torrent;
        Name = torrent.Name;
        TotalLength = torrent.TotalLength;
        PieceLength = torrent.PieceLength;
        PieceCount = torrent.Pieces.Length / 20;
        InfoHashHex = torrent.InfoHashString;
        InfoHashUrlEncoded = torrent.UrlEncodeInfoHash();
        AnnounceUrl = torrent.AnnounceUrl?.ToString();
        IsSingleFile = torrent.IsSingleFile;
        InfoHash = torrent.InfoHash;
        Pieces = torrent.Pieces;
        Files = torrent.Files.Select(f => new TorrentFileInfo(f)).ToList();
    }
}

public sealed class TorrentFileInfo
{
    public string RelativePath { get; }
    public long Length { get; }
    public long Offset { get; }

    public TorrentFileInfo(TorrentFileEntry entry)
    {
        RelativePath = entry.RelativePath;
        Length = entry.Length;
        Offset = entry.Offset;
    }
}
