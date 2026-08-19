using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using BitTorrent.Core;
using BitTorrent.WPF.Models;
using BitTorrent.WPF.Services;
using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF.ViewModels;

public sealed class TorrentViewModel : ViewModelBase
{
    private readonly DownloadEngine _engine;
    private string _status = "Stopped";
    private double _overallProgress;
    private double _downloadSpeed;
    private double _uploadSpeed;
    private long _downloaded;
    private long _uploaded;
    private int _connectedPeers;
    private bool _isDownloading;
    private CancellationTokenSource? _cts;

    public TorrentInfo TorrentInfo { get; }
    public ObservableCollection<PieceViewModel> Pieces { get; } = new();
    public ObservableCollection<PeerViewModel> Peers { get; } = new();

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public double OverallProgress
    {
        get => _overallProgress;
        set => SetProperty(ref _overallProgress, value);
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

    public long Downloaded
    {
        get => _downloaded;
        set => SetProperty(ref _downloaded, value);
    }

    public long Uploaded
    {
        get => _uploaded;
        set => SetProperty(ref _uploaded, value);
    }

    public int ConnectedPeers
    {
        get => _connectedPeers;
        set => SetProperty(ref _connectedPeers, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RemoveCommand { get; }

    public TorrentViewModel(DownloadEngine engine, Torrent torrent, string destinationPath)
    {
        _engine = engine;
        TorrentInfo = new TorrentInfo(torrent);

        
        for (int i = 0; i < TorrentInfo.PieceCount; i++)
        {
            Pieces.Add(new PieceViewModel(this, i, TorrentInfo.PieceLength, 16384));
        }

        PauseCommand = new RelayCommand(_ => Stop(), _ => IsDownloading);
        ResumeCommand = new RelayCommand(async _ => await StartAsync(), _ => !IsDownloading);
        StopCommand = new RelayCommand(_ => Stop(), _ => IsDownloading);
        RemoveCommand = new RelayCommand(_ => {  });
    }

    public async Task StartAsync()
    {
        if (IsDownloading) return;
        
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Status = "Starting...";
        
        try
        {
            await _engine.StartDownloadAsync(this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        Status = "Stopping...";
    }

    public void UpdateProgress(int pieceIndex, int blocksReceived, bool isVerified = false)
    {
        if (pieceIndex >= 0 && pieceIndex < Pieces.Count)
        {
            var piece = Pieces[pieceIndex];
            piece.BlocksReceived = blocksReceived;
            piece.IsVerified = isVerified;
            
            
            int totalBlocks = Pieces.Sum(p => p.BlocksTotal);
            int receivedBlocks = Pieces.Sum(p => p.BlocksReceived);
            OverallProgress = totalBlocks > 0 ? (double)receivedBlocks / totalBlocks * 100 : 0;
        }
    }

    public void UpdatePeer(PeerViewModel peer)
    {
        var existing = Peers.FirstOrDefault(p => p.Endpoint == peer.Endpoint);
        if (existing == null)
        {
            Peers.Add(peer);
        }
        else
        {
            existing.Status = peer.Status;
            existing.DownloadSpeed = peer.DownloadSpeed;
            existing.UploadSpeed = peer.UploadSpeed;
            existing.PiecesAvailable = peer.PiecesAvailable;
            existing.IsConnected = peer.IsConnected;
            existing.IsChoked = peer.IsChoked;
            existing.IsInterested = peer.IsInterested;
        }
        ConnectedPeers = Peers.Count(p => p.IsConnected);
    }

    public void RemovePeer(string endpoint)
    {
        var peer = Peers.FirstOrDefault(p => p.Endpoint == endpoint);
        if (peer != null)
        {
            Peers.Remove(peer);
            ConnectedPeers = Peers.Count(p => p.IsConnected);
        }
    }
}
