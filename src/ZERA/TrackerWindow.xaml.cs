using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ZERA.Core.Download;

namespace ZERA
{
    public partial class TrackerWindow : Window
    {
        private readonly TrackerStore _store;
        private TrackerSettings _settings;
        private bool _updating;
        public TrackerWindow(TrackerStore store)
        {
            InitializeComponent();
            DialogChrome.StyleWindow(this, 860, 790);
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 30));
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 30));
            _store = store; _settings = store.Load();
            ManualBox.Text = _settings.ManualText; AutoBox.IsChecked = _settings.AutoUpdate;
            RefreshList();
            Closing += (s, e) => { if (_updating) e.Cancel = true; };
        }
        private TrackerSubscription Selected => SubscriptionsList.SelectedItem as TrackerSubscription;
        private void RefreshList()
        {
            SubscriptionsList.ItemsSource = null;
            SubscriptionsList.ItemsSource = _settings.Subscriptions;
            InfoText.Text = "手动列表与启用订阅缓存合计 " + _settings.Effective.Count + " 条（去重后）。";
        }
        private void OnSelect(object sender, SelectionChangedEventArgs e)
        {
            var sub = Selected;
            NameBox.Text = sub?.Name ?? ""; UrlBox.Text = sub?.Url ?? "";
            EnabledBox.IsChecked = sub?.Enabled ?? true;
            UpsertButton.Content = sub == null ? "添加订阅" : "更新所选";
        }
        private void OnNew(object sender, RoutedEventArgs e)
        {
            SubscriptionsList.SelectedItem = null;
            NameBox.Clear(); UrlBox.Clear(); EnabledBox.IsChecked = true; NameBox.Focus();
        }
        private void ShowError(Exception ex) => AppDialog.Show(this, ex.Message, "Tracker 管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void OnUpsert(object sender, RoutedEventArgs e)
        {
            try
            {
                string name = NameBox.Text.Trim(), url = TrackerStore.SubscriptionUrl(UrlBox.Text);
                if (name.Length == 0 || name.Length > 80) throw new ArgumentException("请填写不超过 80 字的订阅名称。");
                var selected = Selected;
                if (_settings.Subscriptions.Any(s => s != selected && s.Url == url)) throw new ArgumentException("此地址已订阅。");
                if (selected == null)
                {
                    if (_settings.Subscriptions.Count >= 20) throw new ArgumentException("最多支持 20 个订阅。");
                    selected = new TrackerSubscription(); _settings.Subscriptions.Add(selected);
                }
                if (selected.Url != url)
                { selected.Trackers.Clear(); selected.LastAttemptUtc = selected.LastSuccessUtc = default; selected.Error = ""; }
                selected.Name = name; selected.Url = url; selected.Enabled = EnabledBox.IsChecked == true;
                RefreshList(); SubscriptionsList.SelectedItem = selected;
            }
            catch (Exception ex) { ShowError(ex); }
        }
        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (Selected != null) { _settings.Subscriptions.Remove(Selected); RefreshList(); }
        }
        private void OnDefaults(object sender, RoutedEventArgs e)
        {
            foreach (var sub in new[] {
                new TrackerSubscription { Name = "ngosang 精选", Url = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt" },
                new TrackerSubscription { Name = "XIU2 精选", Url = "https://raw.githubusercontent.com/XIU2/TrackersListCollection/master/best.txt" } })
                if (_settings.Subscriptions.Count < 20 && !_settings.Subscriptions.Any(s => s.Url == sub.Url)) _settings.Subscriptions.Add(sub);
            RefreshList();
        }
        private void SaveDraft()
        {
            // The editor has an explicit commit button; do not silently discard an edited subscription.
            if (NameBox.Text.Length > 0 || UrlBox.Text.Length > 0)
            {
                var sub = Selected;
                if (sub == null || sub.Name != NameBox.Text.Trim() || sub.Url != TrackerStore.SubscriptionUrl(UrlBox.Text) || sub.Enabled != (EnabledBox.IsChecked == true))
                    throw new ArgumentException("订阅编辑尚未确认，请先点击“添加订阅”或“更新所选”。");
            }
            _settings.ManualText = ManualBox.Text; _settings.AutoUpdate = AutoBox.IsChecked == true;
            _store.Save(_settings);
        }
        private void OnSave(object sender, RoutedEventArgs e)
        { try { SaveDraft(); Close(); } catch (Exception ex) { ShowError(ex); } }
        private async void OnUpdate(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveDraft(); _updating = true;
                EditorPanel.IsEnabled = SubscriptionsList.IsEnabled = ManualBox.IsEnabled = AutoBox.IsEnabled = UpdateButton.IsEnabled = SaveButton.IsEnabled = false;
                InfoText.Text = "正在更新已启用订阅…";
                _settings = await _store.UpdateAsync(true); RefreshList();
                InfoText.Text += " 更新结束；失败原因见订阅状态列。";
            }
            catch (Exception ex) { ShowError(ex); }
            finally
            {
                _updating = false;
                EditorPanel.IsEnabled = SubscriptionsList.IsEnabled = ManualBox.IsEnabled = AutoBox.IsEnabled = UpdateButton.IsEnabled = SaveButton.IsEnabled = true;
            }
        }
    }
}
