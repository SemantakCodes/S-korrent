using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF.ViewModels;

public sealed class PeerViewModel : ViewModelBase
{
    private string _status = "Connecting";
    private double _downloadSpeed;
    private double _uploadSpeed;
    private int _piecesAvailable;
    private bool _isConnected;
    private bool _isChoked;
    private bool _isInterested;

    public string Endpoint { get; }
    public string? PeerId { get; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public double DownloadSpeed
    {
        get => _downloadSpeed;
        set => SetProperty(ref _downloadSpeed, value);
    }

    public double UploadSpeed
    {
        get => _uploadSpeed;
        set => SetProperty(ref _uploadSpeed, value);
    }

    public int PiecesAvailable
    {
        get => _piecesAvailable;
        set => SetProperty(ref _piecesAvailable, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public bool IsChoked
    {
        get => _isChoked;
        set => SetProperty(ref _isChoked, value);
    }

    public bool IsInterested
    {
        get => _isInterested;
        set => SetProperty(ref _isInterested, value);
    }

    public PeerViewModel(string endpoint, string? peerId = null)
    {
        Endpoint = endpoint;
        PeerId = peerId;
    }
}