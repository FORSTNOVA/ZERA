using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ZERA.Core.Api;

internal static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static BaiduMembershipInfo Parse(string json) => BaiduMembershipInfo.Parse(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json));
    private sealed class Handler : HttpMessageHandler
    {
        public string Body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/rest/2.0/membership/user" && request.RequestUri.Query.Contains("method=query"), "Read-only membership endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) });
        }
    }
    private static int Main()
    { try { Run().GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    private static async Task Run()
    {
        foreach (string cluster in new[] { "vip", "vipv2", "svip" })
        {
            var info = Parse("{\"error_code\":0,\"currenttime\":1700000000,\"current_product\":{\"detail_cluster\":\""+cluster+"\"},\"reminder\":{\""+cluster+"\":{\"leftseconds\":\"3600\"}}}");
            Check(info.IsMember == true && info.Kind.Contains(cluster == "svip" ? "SVIP" : "VIP"), "Membership type " + cluster);
            Check(info.ExpiresUtc == new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(1700003600), "Server clock and remaining seconds expiry");
            Check(info.TrafficNote.Contains("无法查询"), "Unknown traffic is never zero or unlimited");
        }
        Check(Parse("{\"error_code\":0,\"current_product\":{},\"reminder\":{}}").IsMember == false, "Ordinary account");
        Check(Parse("{\"error_code\":0,\"current_product\":{\"detail_cluster\":\"new_unknown\"}}").IsMember == null, "New products remain unknown");
        Check(Parse("{\"error_code\":0}").IsMember == null, "Missing identity remains unknown");
        Check(Parse("{\"error_code\":0,\"currenttime\":9223372036854775807,\"current_product\":{\"detail_cluster\":\"vip\"},\"reminder\":{\"vip\":{\"leftseconds\":3600}}}").ExpiresUtc == null, "Overflow expiry rejected");
        foreach (string invalid in new[] { "{}", "{\"error_code\":-6,\"error_msg\":\"SECRET-CREDENTIAL\"}" })
        {
            try { Parse(invalid); throw new Exception("Bad result accepted"); }
            catch (InvalidOperationException ex) { Check(!ex.Message.Contains("SECRET"), "No raw server text in error"); }
        }
        using (var client = new BaiduClient("BDUSS=test-only"))
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var session = typeof(BaiduClient).GetField("_session", flags).GetValue(client);
            var field = session.GetType().GetField("_http", flags);
            ((HttpClient)field.GetValue(session)).Dispose();
            var handler = new Handler { Body = "{\"error_code\":0,\"current_product\":{\"detail_cluster\":\"vipv2\"}}" };
            field.SetValue(session, new HttpClient(handler));
            var info = await client.QueryMembershipAsync();
            Check(info.IsMember == true && client.BuildDownloadContext().SuggestedThreads == BaiduConstants.VipThreads, "Member state updates after query");
            Check(client.BuildDownloadContext().SpeedNote.Contains("不代表极速流量已启用"), "Concurrency is not activation");
            handler.Body = "{\"error_code\":-6}";
            try { await client.QueryMembershipAsync(); throw new Exception("Failure accepted"); } catch (InvalidOperationException) { }
            Check(ReferenceEquals(info, client.Membership), "Failed query preserves last valid state");
            handler.Body = "{\"error_code\":0,\"current_product\":{},\"reminder\":{}}";
            await client.QueryMembershipAsync();
            Check(client.BuildDownloadContext().SuggestedThreads == BaiduConstants.FreeThreads, "Expired/ordinary account refresh");
        }
        Console.WriteLine("PASS: VIP/vipv2/SVIP and ordinary identities, UTC expiry, unknown fields, overflow and error handling");
        Console.WriteLine("PASS: read-only endpoint, manual refresh, failure preservation, concurrency note and no fabricated traffic balance");
    }
}
