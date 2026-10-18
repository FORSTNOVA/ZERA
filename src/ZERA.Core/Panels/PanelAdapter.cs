using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZERA.Core.Api;
using ZERA.Core.Models;
using ZERA.Core.Support;

namespace ZERA.Core.Panels
{
    public sealed class PanelLogin
    {
        public bool Valid { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Key { get; set; }
    }

    // Provider protocols, account identity and optional operations stay behind this boundary.
    public class PanelAdapter
    {
        protected readonly IPanelClient Client;
        protected PanelAdapter(IPanelClient client) { Client = client; }
        public static PanelAdapter For(IPanelClient client)
        {
            if (client is QuarkClient q) return new QuarkAdapter(q);
            if (client is BaiduClient b) return new BaiduAdapter(b);
            return new PanelAdapter(client);
        }
        public virtual async Task<PanelLogin> LoginAsync(CancellationToken cancel = default)
        {
            var result = await Client.ValidateAsync(cancel).ConfigureAwait(false);
            return new PanelLogin { Valid = result.ok, Name = result.nick,
                Key = Client is WebDavClient dav ? dav.AccountKey : null };
        }
        public virtual string AccountKey => Client is WebDavClient dav ? dav.AccountKey : null;
        public virtual void StartSession(Action expired) { }
        protected static NotSupportedException Unsupported() => new NotSupportedException("此网盘不支持该功能。");
        public static bool IsCapacityMessage(string text) => QuarkClient.IsCapacityMessage(text);
        public virtual Task<string> GetStokenAsync(string id, string code = "") => throw Unsupported();
        public virtual Task<List<QuarkFileEntry>> ShareDetailAsync(string id, string token, string directory) => throw Unsupported();
        public virtual Task OpenShareAsync(string url, string code) => throw Unsupported();
        public virtual Task<List<DownloadLink>> GetDownloadAsync(IList<string> ids) => DriveLinks(ids);
        private async Task<List<DownloadLink>> DriveLinks(IList<string> ids)
        {
            var result = new List<DownloadLink>();
            foreach (string id in ids) result.Add(await Client.ResolveDriveFileAsync(id).ConfigureAwait(false));
            return result;
        }
        public virtual Task<List<DownloadLink>> GetShareDownloadAsync(IList<string> ids, IList<string> tokens, string id, string token) => throw Unsupported();
        public virtual Task<List<string>> SaveShareAndGetFidsAsync(IList<string> ids, IList<string> tokens, string destination, string id, string token) => throw Unsupported();
        public virtual Task<string> PathFidAsync(string path) => throw Unsupported();
        public virtual Task<string> MkdirAsync(string path) => throw Unsupported();
        public virtual Task DeleteAsync(IList<string> ids) => throw Unsupported();
        public virtual Task PurgeRecycleAsync(IList<string> ids) => throw Unsupported();
        public virtual Task<BaiduMembershipInfo> QueryMembershipAsync(CancellationToken cancel = default) => throw Unsupported();
        public virtual long ServerSpeedLimitBps => 0;
        public virtual string LastResolveReport => "";
        public string CookieStr => Client.CookieStr;
        public Task<(bool ok, string nick)> ValidateAsync(CancellationToken cancel = default) => Client.ValidateAsync(cancel);
        public Task<DownloadLink> ResolveDriveFileAsync(string id, CancellationToken cancel = default) => Client.ResolveDriveFileAsync(id, cancel);
        public Task<List<DownloadLink>> ResolveShareFidsAsync(List<string> ids) => GetShareDownloadAsync(ids, new List<string>(), null, null);
        public virtual Task EnsureShareContextAsync(string id, string code) => throw Unsupported();
        public virtual Task<DownloadLink> SaveShareFileThenLinkAsync(string id, string name, long size, string directory) => throw Unsupported();
        public static string CredentialKey(string prefix, string identity)
        {
            using (var sha = SHA256.Create()) return prefix + string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(identity)).Take(12).Select(b => b.ToString("x2")));
        }
        public static string CredentialFingerprint(PanelKind kind, string credential)
        {
            if (kind == PanelKind.WebDav)
            {
                var account = WebDavAccount.Parse(credential);
                return CredentialKey("dav_", account.Endpoint + "\n" + account.Username);
            }
            string name = kind == PanelKind.Baidu ? BaiduConstants.LoginCookieKey : QuarkConstants.LoginCookieKey;
            var cookies = QuarkSession.ParseCookies(credential ?? "");
            var match = cookies.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            return CredentialKey(kind + "_", match.Value ?? credential ?? "");
        }
    }
    internal sealed class QuarkAdapter : PanelAdapter
    {
        private readonly QuarkClient _q;
        public QuarkAdapter(QuarkClient client) : base(client) { _q = client; }
        public override string AccountKey => _q.AccountSig;
        public override async Task<PanelLogin> LoginAsync(CancellationToken cancel = default)
        {
            cancel.ThrowIfCancellationRequested();
            var result = await _q.ValidateAsync().ConfigureAwait(false);
            string nick = result.nick;
            if (result.ok && (nick == "已登录用户" || string.IsNullOrEmpty(_q.AvatarUrl)))
            {
                try
                {
                    var info = await Task.Run(() => QuarkPythonBridge.FetchAccountInfo(_q.CookieStr), cancel).ConfigureAwait(false);
                    if (info.HasValue && info.Value.Ok)
                    { if (!string.IsNullOrEmpty(info.Value.Nickname)) nick = info.Value.Nickname; _q.SetAvatarUrl(info.Value.Avatar); }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Optional avatar lookup must not invalidate an otherwise valid login. */ }
            }
            return new PanelLogin { Valid = result.ok, Name = nick, Avatar = _q.AvatarUrl, Key = string.IsNullOrEmpty(_q.AccountSig) || _q.AccountSig == result.nick ?
                CredentialFingerprint(PanelKind.Quark, _q.CookieStr) : _q.AccountSig };
        }
        public override void StartSession(Action expired) { _q.LoginExpired += expired; _q.StartKeepAlive(); }
        public override Task<string> GetStokenAsync(string id, string code = "") => _q.GetStokenAsync(id, code);
        public override Task<List<QuarkFileEntry>> ShareDetailAsync(string id, string token, string directory) => _q.ShareDetailAsync(id, token, directory);
        public override Task<List<DownloadLink>> GetDownloadAsync(IList<string> ids) => _q.GetDownloadAsync(ids);
        public override Task<List<DownloadLink>> GetShareDownloadAsync(IList<string> ids, IList<string> tokens, string id, string token) => _q.GetShareDownloadAsync(ids, tokens, id, token);
        public override Task<List<string>> SaveShareAndGetFidsAsync(IList<string> ids, IList<string> tokens, string destination, string id, string token) => _q.SaveShareAndGetFidsAsync(ids, tokens, destination, id, token);
        public override Task<string> PathFidAsync(string path) => _q.PathFidAsync(path);
        public override Task<string> MkdirAsync(string path) => _q.MkdirAsync(path);
        public override Task DeleteAsync(IList<string> ids) => _q.DeleteAsync(ids);
        public override Task PurgeRecycleAsync(IList<string> ids) => _q.PurgeRecycleAsync(ids);
    }
    internal sealed class BaiduAdapter : PanelAdapter
    {
        private readonly BaiduClient _b;
        public BaiduAdapter(BaiduClient client) : base(client) { _b = client; }
        public override string AccountKey
        {
            get
            {
                string bduss = _b.CookieStr.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("BDUSS=", StringComparison.OrdinalIgnoreCase));
                if (bduss == null) return null;
                using (var sha = SHA1.Create()) return "bd_" + string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(bduss.Substring(6))).Take(4).Select(b => b.ToString("x2")));
            }
        }
        public override async Task<PanelLogin> LoginAsync(CancellationToken cancel = default)
        { var result = await _b.ValidateAsync(cancel).ConfigureAwait(false); return new PanelLogin { Valid = result.ok, Name = result.nick, Key = AccountKey ?? result.nick }; }
        public override Task OpenShareAsync(string url, string code) => _b.ResolveShareAsync(url, code, 1);
        public override Task<List<QuarkFileEntry>> ShareDetailAsync(string id, string token, string directory) => _b.ListShareDirAsync(directory == "0" ? "" : directory, 40, default);
        public override Task<List<DownloadLink>> GetShareDownloadAsync(IList<string> ids, IList<string> tokens, string id, string token) => _b.ResolveShareFidsAsync(ids.ToList());
        public override Task<BaiduMembershipInfo> QueryMembershipAsync(CancellationToken cancel = default) => _b.QueryMembershipAsync(cancel);
        public override long ServerSpeedLimitBps => _b.ServerSpeedLimitBps;
        public override string LastResolveReport => _b.LastResolveReport;
        public override Task EnsureShareContextAsync(string id, string code) => _b.EnsureShareContextAsync(id, code);
        public override Task<DownloadLink> SaveShareFileThenLinkAsync(string id, string name, long size, string directory) => _b.SaveShareFileThenLinkAsync(id, name, size, directory);
    }
}
