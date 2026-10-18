using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ZERA.Core.Download;
using ZERA.Core.Support;

namespace ZERA
{
    public partial class TorrentWindow : Window
    {
        private readonly TorrentEngine _engine;
        private readonly SplitTorrentEngine _splitEngine;
        private readonly DispatcherTimer _timer;
        private readonly TrackerStore _trackers;
        private readonly DispatcherTimer _trackerTimer;
        private bool _trackerUpdating, _trackerDialog;
        private bool _busy, _refreshing, _shutdown;

        private sealed class Row
        {
            public TorrentStatus Task { get; set; }
            public string Name => Task.Name;
            public long Connections => Task.Connections;
            public string StateText => Task.Status == "active" ? (Task.Metadata ? "获取元数据" : "下载 / 校验") :
                Task.Status == "merging" ? "合并 / 校验" :
                Task.Status == "paused" ? "已暂停" : Task.Status == "waiting" ? "排队中" :
                Task.Status == "complete" ? "已完成" : Task.Status == "error" ? "失败" : "已移除";
            public string ProgressText => Task.Total > 0 ? Task.Percent.ToString("F1") + "%" :
                Task.Status == "paused" ? "待继续校验" : "等待种源";
            public string DownloadText => Util.FormatSize(Task.DownloadSpeed) + "/s";
            public string UploadText => Util.FormatSize(Task.UploadSpeed) + "/s";
        }

        public TorrentWindow(string directory, string stateDirectory)
        {
            InitializeComponent();
            DialogChrome.StyleWindow(this, 940, 620);
            DirectoryBox.Text = directory;
            _engine = new TorrentEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Runtime", "aria2", "aria2c.exe"), stateDirectory);
            _splitEngine = new SplitTorrentEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Runtime"), Path.Combine(stateDirectory, "split"));
            _trackers = new TrackerStore(stateDirectory);
            _engine.TrackerProvider = _splitEngine.TrackerProvider = _trackers.GetEffective;
            _trackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _trackerTimer.Tick += async (s, e) => await UpdateTrackersAsync();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (s, e) => await RefreshAsync();
            Loaded += async (s, e) => { await RefreshAsync(); _timer.Start(); _trackerTimer.Start(); await UpdateTrackersAsync(); };
            IsVisibleChanged += async (s, e) => {
                if (IsVisible && !_shutdown) { _timer.Start(); await RefreshAsync(); }
                else _timer.Stop();
            };
            Closing += (s, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
            UpdateButtons();
        }

        private Row Selected => TorrentList.SelectedItem as Row;

        private async Task UpdateTrackersAsync()
        {
            if (_shutdown || _trackerUpdating || _trackerDialog) return;
            _trackerUpdating = true; TrackersButton.IsEnabled = false;
            try { await _trackers.UpdateAsync(false); }
            catch (Exception ex) { TrackersButton.ToolTip = ex.Message; }
            finally { _trackerUpdating = false; TrackersButton.IsEnabled = true; }
        }
        private void OnTrackers(object sender, RoutedEventArgs e)
        {
            if (_trackerUpdating || _trackerDialog) return;
            _trackerDialog = true;
            try { new TrackerWindow(_trackers) { Owner = this }.ShowDialog(); }
            catch (Exception ex) { AppDialog.Show(this, ex.Message, "Tracker 管理", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { _trackerDialog = false; }
        }

        private async Task RefreshAsync()
        {
            if (_refreshing || _shutdown) return;
            _refreshing = true;
            try
            {
                string gid = Selected?.Task.Gid;
                var tasks = await _engine.GetTasksAsync();
                tasks.AddRange(_splitEngine.GetTasks());
                TorrentList.ItemsSource = tasks.Select(t => new Row { Task = t }).ToList();
                if (gid != null) TorrentList.SelectedItem = TorrentList.Items.Cast<Row>().FirstOrDefault(r => r.Task.Gid == gid);
                if (!_busy)
                    StatusText.Text = Selected?.Task.Error.Length > 0 ? Selected.Task.Error :
                        tasks.Count == 0 ? "暂无任务。打开 .torrent 文件或粘贴磁力链接开始下载。" :
                        "普通 BT 最多并行 2 个；拆分模式最多启动一组，组内各子任务独立下载。速度取决于种源、网络和磁盘。";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _refreshing = false; UpdateButtons(); }
        }

        private async Task RunAsync(Func<Task> action)
        {
            if (_busy || _shutdown) return;
            _busy = true;
            AddTorrentButton.IsEnabled = AddMagnetButton.IsEnabled = AddSplitButton.IsEnabled = false;
            UpdateButtons();
            try { await action(); await RefreshAsync(); }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                AppDialog.Show(this, ex.Message, "种子下载", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _busy = false; AddTorrentButton.IsEnabled = AddMagnetButton.IsEnabled = AddSplitButton.IsEnabled = true; UpdateButtons(); }
        }

        private string OutputDirectory()
        {
            string directory = DirectoryBox.Text.Trim();
            if (!Path.IsPathRooted(directory)) throw new ArgumentException("请选择完整的下载目录路径。");
            return Path.GetFullPath(directory);
        }

        private async void OnAddMagnet(object sender, RoutedEventArgs e)
        {
            string magnet = MagnetBox.Text;
            await RunAsync(async () => {
                await _engine.AddMagnetAsync(magnet, OutputDirectory());
                MagnetBox.Clear();
                StatusText.Text = "已添加磁力任务，正在寻找种源。";
            });
        }

        private async void OnAddTorrent(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "BT 种子 (*.torrent)|*.torrent", Multiselect = false, CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            await RunAsync(async () => {
                await _engine.AddTorrentAsync(dialog.FileName, OutputDirectory());
                StatusText.Text = "已添加种子任务。";
            });
        }

        private void OnChooseDirectory(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 BT 下载目录", SelectedPath = DirectoryBox.Text })
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) DirectoryBox.Text = dialog.SelectedPath;
        }

        private async void OnAddSplit(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "BT v1 种子 (*.torrent)|*.torrent", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            int count = int.Parse(((ComboBoxItem)SplitCountBox.SelectedItem).Tag.ToString());
            await RunAsync(async () => {
                await _splitEngine.AddAsync(dialog.FileName, OutputDirectory(), count);
                StatusText.Text = "已创建独立块范围子任务，全部完成后自动校验合并。";
            });
        }

        private async void OnPause(object sender, RoutedEventArgs e)
        {
            var task = Selected?.Task;
            if (task != null) await RunAsync(() => task.Split ? _splitEngine.PauseAsync(task.Gid) : _engine.PauseAsync(task.Gid));
        }
        private async void OnResume(object sender, RoutedEventArgs e)
        {
            var task = Selected?.Task;
            if (task != null) await RunAsync(() => task.Split ? _splitEngine.ResumeAsync(task.Gid) : _engine.ResumeAsync(task.Gid));
        }
        private async void OnRemove(object sender, RoutedEventArgs e)
        {
            var task = Selected?.Task;
            if (task == null) return;
            if (AppDialog.Show(this, "移除任务「" + task.Name + "」？已下载文件和断点文件会保留。",
                "移除 BT 任务", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await RunAsync(() => task.Split ? _splitEngine.RemoveAsync(task.Gid) :
                _engine.RemoveAsync(task.Gid, task.Status == "complete" || task.Status == "error" || task.Status == "removed"));
        }
        private void OnOpenDirectory(object sender, RoutedEventArgs e)
        {
            string directory = Selected?.Task.Directory ?? DirectoryBox.Text;
            if (Directory.Exists(directory)) ShellLaunch.OpenFolder(directory);
        }
        private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
        private void UpdateButtons()
        {
            if (PauseButton == null) return;
            string state = Selected?.Task.Status;
            PauseButton.IsEnabled = !_busy && (state == "active" || state == "waiting" || state == "merging");
            ResumeButton.IsEnabled = !_busy && (state == "paused" || state == "error" && Selected?.Task.Split == true && Selected.Task.Group);
            RemoveButton.IsEnabled = !_busy && state != null && (Selected?.Task.Split != true || Selected.Task.Group);
            if (Selected?.Task.Error.Length > 0) StatusText.Text = Selected.Task.Error;
        }

        public void ShutdownEngine()
        {
            _shutdown = true;
            _timer.Stop();
            _trackerTimer.Stop();
            try { _splitEngine.Dispose(); }
            finally { _engine.Dispose(); Close(); }
        }
    }
}
