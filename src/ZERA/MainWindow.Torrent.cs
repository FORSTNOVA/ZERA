using System.IO;
using System.Windows;

namespace ZERA
{
    public partial class MainWindow
    {
        private TorrentWindow _torrentWindow;

        private void OnOpenTorrent(object sender, RoutedEventArgs e)
        {
            if (_torrentWindow == null)
                _torrentWindow = new TorrentWindow(_settings.OutDir,
                    Path.Combine(Path.GetDirectoryName(SettingsStore.FilePath), "bt-state")) { Owner = this };
            _torrentWindow.Show();
            if (_torrentWindow.WindowState == WindowState.Minimized) _torrentWindow.WindowState = WindowState.Normal;
            _torrentWindow.Activate();
        }
    }
}
