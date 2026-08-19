using System.IO;
using Microsoft.Win32;
using System.Windows;

namespace BitTorrent.WPF.Services;

public interface IFileDialogService
{
    string? ShowOpenFileDialog(string title, string filter);
    string? ShowFolderBrowserDialog(string title, string initialPath);
    string? ShowSaveFileDialog(string title, string filter, string defaultFileName);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? ShowOpenFileDialog(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true
        };
        
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowSaveFileDialog(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true
        };
        
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowFolderBrowserDialog(string title, string initialPath)
    {
        
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = "Select this folder",
            Filter = "Folders|*.this.is.a.folder",
            CheckPathExists = true,
            InitialDirectory = initialPath,
            ValidateNames = false
        };

        if (dialog.ShowDialog() == true)
        {
            return Path.GetDirectoryName(dialog.FileName);
        }
        return null;
    }
}
