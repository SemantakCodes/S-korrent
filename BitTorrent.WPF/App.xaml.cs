using System.Windows;
using BitTorrent.WPF.Services;
using BitTorrent.WPF.ViewModels;

namespace BitTorrent.WPF;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var engine = new DownloadEngine();
        var dialogService = new FileDialogService();
        var mainViewModel = new MainViewModel(engine, dialogService);

        var mainWindow = new MainWindow
        {
            DataContext = mainViewModel
        };
        mainWindow.Show();
    }
}
