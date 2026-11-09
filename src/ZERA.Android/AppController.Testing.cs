#if ZERA_TESTING
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ZERA.Core.Api;
using ZERA.Core.Models;
using ZERA.Core.Support;

namespace ZERA.Android;

// Compiled only into synthetic-fixture APKs, never delivery builds.
internal sealed partial class AppController
{
    private CloudFixture _testCloud;
    private async Task<object> Testing(string action,JsonElement p)
    {
        if(action=="test.qrSample")return new{png=QrImage(S(p,"text","https://pan.quark.cn/s/fixture"))};
        if(action=="test.cloudCalls")return _testCloud?.Calls.ToArray()??Array.Empty<string>();
        await _commands.WaitAsync();
        try{
            const string id="local-cloud-fixture";
            var client=new QuarkClient("__puus=local-fixture-only");
            var session=(QuarkSession)typeof(QuarkClient).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(client);
            session.Http.Dispose();_testCloud=new();
            typeof(QuarkSession).GetField("_http",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,new HttpClient(_testCloud));
            if(_clients.Remove(id,out var old))old.Dispose();_clients[id]=client;
            lock(_sync){
                _state.Accounts.RemoveAll(a=>a.Id==id);_state.Accounts.Add(new(){Id=id,Platform=0,Name="Synthetic Quark",Credential="__puus=local-fixture-only"});
                _state.Platform=0;_state.ActiveAccount=id;_state.LastAccounts[0]=id;ClearFiles();
                if(B(p,"share")){_shareUrl="https://pan.quark.cn/s/fixture";_shareId="fixture";_shareToken="fixture-token";}
                _files=new(){new(){Fid="fixture-file",Name="synthetic.txt",Size=12,Token="fixture-token"}};Save();
            }
            return StateView();
        }finally{_commands.Release();}
    }
    private sealed class CloudFixture : HttpMessageHandler
    {
        internal readonly List<string> Calls=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            string path=request.RequestUri.AbsolutePath;
            string body=request.Content==null?"":await request.Content.ReadAsStringAsync(token);
            Calls.Add(path+" "+body);
            object data=path switch{
                "/1/clouddrive/file/info/path_list"=>new[]{new{file_path="/ZERA",fid="fixture-dir"}},
                "/1/clouddrive/file"=>new{fid="created-dir"},
                "/1/clouddrive/file/download"=>new[]{new{fid="saved-file",file_name="synthetic.txt",size=2097279L,download_url="http://127.0.0.1:18880/payload.bin"}},
                "/1/clouddrive/share/sharepage/save"=>new{task_id="fixture-task"},
                "/1/clouddrive/task"=>new{status=2,save_as=new{save_as_top_fids=new[]{"saved-file"}}},
                "/1/clouddrive/file/sort"=>new{list=Array.Empty<object>()},
                "/1/clouddrive/file/recycle/list"=>new{list=new[]{new{fid="fixture-file",record_id="selected-record"},new{fid="unrelated-file",record_id="unrelated-record"}}},
                "/1/clouddrive/member"=>new{total_capacity=102400L,use_capacity=1024L},
                _=>new{}
            };
            string json=JsonSerializer.Serialize(new{status=200,code=0,data,metadata=new{_total=0}});
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
        }
    }
}
#endif
