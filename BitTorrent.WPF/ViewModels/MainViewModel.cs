using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using BitTorrent.Core;
using BitTorrent.WPF.Models;
using BitTorrent.WPF.Services;
using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DownloadEngine _engine;
    private readonly IFileDialogService _dialogService;
    private TorrentViewModel? _selectedTorrent;
    private string _downloadPath;

    public ObservableCollection<TorrentViewModel> Torrents { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public TorrentViewModel? SelectedTorrent
    {
        get => _selectedTorrent;
        set => SetProperty(ref _selectedTorrent, value);
    }

    public string DownloadPath
    {
        get => _downloadPath;
        set => SetProperty(ref _downloadPath, value);
    }

    public ICommand OpenTorrentFileCommand { get; }
    public ICommand ChooseDownloadPathCommand { get; }
    public ICommand PauseSelectedCommand { get; }
    public ICommand ResumeSelectedCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand CreateTestTorrentCommand { get; }
    public ICommand StartTestTrackerCommand { get; }
    public ICommand ClearLogsCommand { get; }

    public MainViewModel(DownloadEngine engine, IFileDialogService dialogService)
    {
        _engine = engine;
        _dialogService = dialogService;
        _downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BitTorrent");
        
        Directory.CreateDirectory(_downloadPath);

        // Wire up engine logging
        _engine.LogMessage += msg => 
        {
            Application.Current?.Dispatcher.Invoke(() => Logs.Add(msg));
            // Keep only last 500 logs
            while (Logs.Count > 500) Logs.RemoveAt(0);
        };

        OpenTorrentFileCommand = new RelayCommand(async _ => await OpenTorrentFileAsync());
        ChooseDownloadPathCommand = new RelayCommand(async _ => await ChooseDownloadPathAsync());
        PauseSelectedCommand = new RelayCommand(_ => PauseSelected(), _ => SelectedTorrent?.IsDownloading == true);
        ResumeSelectedCommand = new RelayCommand(async _ => await ResumeSelectedAsync(), _ => SelectedTorrent != null && SelectedTorrent.IsDownloading == false);
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedTorrent != null);
        CreateTestTorrentCommand = new RelayCommand(async _ => await CreateTestTorrentAsync());
        StartTestTrackerCommand = new RelayCommand(async _ => await StartTestTrackerAsync());
        ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
    }

    private void RemoveSelected()
    {
        if (SelectedTorrent != null)
            RemoveTorrent(SelectedTorrent);
    }

    public async Task AddTorrentAsync(string torrentPath)
    {
        try
        {
            var torrent = Torrent.LoadFromFile(torrentPath);
            var vm = new TorrentViewModel(_engine, torrent, _downloadPath);
            Torrents.Add(vm);
            SelectedTorrent = vm;
            
            await vm.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load torrent: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void RemoveTorrent(TorrentViewModel torrent)
    {
        torrent.Stop();
        Torrents.Remove(torrent);
        if (SelectedTorrent == torrent)
            SelectedTorrent = Torrents.FirstOrDefault();
    }

    public async Task ChooseDownloadPathAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog("Select Download Folder", _downloadPath);
        if (!string.IsNullOrEmpty(path))
        {
            DownloadPath = path;
            Directory.CreateDirectory(path);
        }
    }

    public async Task OpenTorrentFileAsync()
    {
        var path = _dialogService.ShowOpenFileDialog("Select Torrent File", "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*");
        if (!string.IsNullOrEmpty(path))
        {
            await AddTorrentAsync(path);
        }
    }

    public void PauseSelected()
    {
        SelectedTorrent?.Stop();
    }

    public async Task ResumeSelectedAsync()
    {
        if (SelectedTorrent != null && !SelectedTorrent.IsDownloading)
        {
            await SelectedTorrent.StartAsync();
        }
    }

    public async Task CreateTestTorrentAsync()
    {
        try
        {
            var path = _dialogService.ShowSaveFileDialog("Save Test Torrent", "Torrent files (*.torrent)|*.torrent", "test.torrent");
            if (string.IsNullOrEmpty(path)) return;

            // Create test data file
            var testDataPath = Path.Combine(Path.GetDirectoryName(path)!, "test_data.bin");
            var testData = new byte[1024 * 1024]; // 1 MB
            new Random(42).NextBytes(testData);
            await File.WriteAllBytesAsync(testDataPath, testData);

            // Use local tracker on a known port
            const ushort trackerPort = 6969;
            var trackerUrl = $"http://127.0.0.1:{trackerPort}/announce";
            var torrentBytes = CreateTestTorrentBytes(testDataPath, "test_data.bin", trackerUrl);
            await File.WriteAllBytesAsync(path, torrentBytes);

            MessageBox.Show($"Test torrent created at:\n{path}\n\nTest data file:\n{testDataPath}\n\nNote: Start the local tracker (🧪 Test Tracker button) before downloading.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            
            await AddTorrentAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create test torrent: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private LocalTestTracker? _testTracker;

    public async Task StartTestTrackerAsync()
    {
        if (_testTracker != null) return;
        
        const ushort port = 6969;
        _testTracker = new LocalTestTracker(port);
        _testTracker.Start();
        
        MessageBox.Show($"Test tracker started on http://127.0.0.1:{port}/announce\n\nYou can now download test torrents created with this app.", "Tracker Started", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static byte[] CreateTestTorrentBytes(string filePath, string fileName, string announceUrl, ushort listenPort = 6881)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        var pieceLength = 16384;
        var pieceCount = (fileBytes.Length + pieceLength - 1) / pieceLength;
        var pieces = new byte[pieceCount * 20];

        using var sha1 = SHA1.Create();
        for (int i = 0; i < pieceCount; i++)
        {
            int offset = i * pieceLength;
            int len = Math.Min(pieceLength, fileBytes.Length - offset);
            var hash = sha1.ComputeHash(fileBytes, offset, len);
            Buffer.BlockCopy(hash, 0, pieces, i * 20, 20);
        }

        var info = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
        {
            [new BEncodedString(Encoding.UTF8.GetBytes("length"))] = new BEncodedInteger(fileBytes.Length),
            [new BEncodedString(Encoding.UTF8.GetBytes("name"))] = new BEncodedString(Encoding.UTF8.GetBytes(fileName)),
            [new BEncodedString(Encoding.UTF8.GetBytes("piece length"))] = new BEncodedInteger(pieceLength),
            [new BEncodedString(Encoding.UTF8.GetBytes("pieces"))] = new BEncodedString(pieces),
        });

        var root = new BEncodedDictionary(new Dictionary<BEncodedString, BEncodedValue>
        {
            [new BEncodedString(Encoding.UTF8.GetBytes("announce"))] = new BEncodedString(Encoding.UTF8.GetBytes(announceUrl)),
            [new BEncodedString(Encoding.UTF8.GetBytes("info"))] = info,
        });

        return BEncoding.Encode(root);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}