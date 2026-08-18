using System.Collections.ObjectModel;
using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF.ViewModels;

public sealed class PieceViewModel : ViewModelBase
{
    private readonly TorrentViewModel _parent;
    private int _blocksTotal;
    private int _blocksReceived;
    private bool _isVerified;
    private bool _isDownloading;

    public int Index { get; }
    public int BlocksTotal => _blocksTotal;
    public int BlocksReceived
    {
        get => _blocksReceived;
        set => SetProperty(ref _blocksReceived, value);
    }
    public double Progress => _blocksTotal > 0 ? (double)_blocksReceived / _blocksTotal : 0;
    public bool IsVerified
    {
        get => _isVerified;
        set => SetProperty(ref _isVerified, value);
    }
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }
    public bool IsComplete => _blocksReceived >= _blocksTotal;

    public PieceViewModel(TorrentViewModel parent, int index, int pieceLength, int blockSize)
    {
        _parent = parent;
        Index = index;
        _blocksTotal = (pieceLength + blockSize - 1) / blockSize;
        
        // Last piece may have fewer blocks
        if (index == parent.TorrentInfo.PieceCount - 1)
        {
            long lastPieceSize = parent.TorrentInfo.TotalLength - (long)index * pieceLength;
            _blocksTotal = (int)((lastPieceSize + blockSize - 1) / blockSize);
        }
    }
}