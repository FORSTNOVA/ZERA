using Android.Graphics;
using System.Text.Json;
using ZERA.Core.Api;
using ZERA.Core.Panels;
using ZERA.Core.Support;

namespace ZERA.Android;

internal sealed partial class AppController
{
    private readonly SemaphoreSlim _loginGate=new(1,1);
    private NativeLoginSession _nativeLogin;
    private BaiduQrLoginService _baiduLogin;
    private LoginQrService _quarkLogin;
    private CancellationTokenSource _loginCancel;
    private int _loginPlatform;
    private string _qrSession, _loginName;
    private object _loginView = new {state="Cancelled",hint=""};
    private Task _polling;
    private async Task<object> Login(string action,JsonElement p)
    {
        if(action=="login.cancel")_loginCancel?.Cancel();
        if(action=="login.poll" && _nativeLogin==null)return _loginView;
        await _loginGate.WaitAsync();
        try {
            if(action=="login.cancel") {CancelLogin();return _loginView;}
            if(action=="login.start") {
                CancelLogin();_loginCancel=new();_loginPlatform=I(p,"platform",_state.Platform);_loginName=S(p,"name");
                var kind=(PanelKind)_loginPlatform; string mode=S(p,"mode","qr");
                if(NativeCloudAccount.Supports(kind)) {
                    var options=p.TryGetProperty("options",out var o)&&o.ValueKind==JsonValueKind.Object ? o.Deserialize<NativeCloudAccount>(Json):new();
                    _nativeLogin=new NativeLoginSession(kind,options);
                    return await NativeResult(mode=="password" ? await _nativeLogin.PasswordAsync(S(p,"username"),S(p,"password"),_loginCancel.Token):await _nativeLogin.StartQrAsync(_loginCancel.Token));
                }
                if(mode!="qr")throw new NotSupportedException("此网盘请使用扫码或 Cookie 登录。");
                if(kind==PanelKind.Baidu) {
                    _baiduLogin=new();var qr=await _baiduLogin.GetQrCodeAsync();if(!qr.ok)throw new IOException(qr.error);
                    _qrSession=qr.sign;_loginView=new{state="Waiting",hint="请用百度 App 扫码确认",image=Convert.ToBase64String(qr.png)};
                } else if(kind==PanelKind.Quark) {
                    _quarkLogin=new();var qr=await _quarkLogin.GetQrCodeAsync();if(!qr.ok)throw new IOException(qr.error);
                    _qrSession=qr.token;_loginView=new{state="Waiting",hint="请用夸克 App 扫码确认",image=QrImage(qr.qrUrl)};
                } else throw new NotSupportedException("此平台不支持扫码登录。");
                var token=_loginCancel.Token;var baidu=_baiduLogin;var quark=_quarkLogin;string session=_qrSession;int platform=_loginPlatform;string name=_loginName;
                _polling=Task.Run(async()=> {
                    try {
                        string cookie;
                        if(baidu!=null) {
                            var poll=await baidu.PollForConfirmAsync(session,cancel:token);token.ThrowIfCancellationRequested();
                            if(poll.Stage!=BaiduQrStage.Confirmed)throw new IOException(poll.Error??"二维码已过期，请重新获取。");
                            var result=await baidu.ExchangeAsync(poll.TempBduss);if(!result.ok)throw new IOException(result.error);cookie=result.cookie;
                        }else{
                            var poll=await quark.PollForTicketWithStatusAsync(session,cancel:token);token.ThrowIfCancellationRequested();
                            if(poll.Stage!=QrLoginStage.GotTicket)throw new IOException("二维码已过期，请重新获取。");
                            var result=await quark.ExchangeTicketAsync(poll.Ticket);if(!result.ok)throw new IOException(result.error);cookie=result.cookie;
                        }
                        token.ThrowIfCancellationRequested();await _commands.WaitAsync(token);
                        try{token.ThrowIfCancellationRequested();await SaveAccount(platform,name,cookie,token);_loginView=new{state="Success",hint="登录成功"};}
                        finally{_commands.Release();}
                    }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)_loginView=new{state="Expired",hint=Redact(ex.Message)};}
                    Changed?.Invoke();
                });
                return _loginView;
            }
            if(_nativeLogin==null)throw new InvalidOperationException("请先开始登录。");
            return await NativeResult(action switch {
                "login.poll"=>await _nativeLogin.PollQrAsync(_loginCancel.Token),
                "login.challenge"=>await _nativeLogin.SubmitChallengeAsync(S(p,"code"),_loginCancel.Token),
                "login.sms"=>await _nativeLogin.SubmitChallengeAsync("send-sms",_loginCancel.Token),
                _=>throw new NotSupportedException("未知登录操作。")
            });
        } finally{_loginGate.Release();}
    }
    private async Task<object> NativeResult(NativeLoginResult result)
    {
        if(result.State==NativeLoginState.Success) {
            var cancel=_loginCancel.Token;
            await _commands.WaitAsync(cancel);try{cancel.ThrowIfCancellationRequested();await SaveAccount(_loginPlatform,_loginName,result.Account.Serialize(),cancel);}finally{_commands.Release();}
        }
        _loginView=new{state=result.State.ToString(),hint=result.Hint??"",image=result.Image!=null?Convert.ToBase64String(result.Image):!string.IsNullOrEmpty(result.QrText)?QrImage(result.QrText):null,verificationUrl=result.VerificationUrl};
        Changed?.Invoke();return _loginView;
    }
    private void CancelLogin()
    {
        _loginCancel?.Cancel();_nativeLogin?.Dispose();_baiduLogin?.Dispose();_quarkLogin?.Dispose();
        _loginCancel?.Dispose();_loginCancel=null;_nativeLogin=null;_baiduLogin=null;_quarkLogin=null;
        _loginView=new{state="Cancelled",hint="登录已取消"};
    }
    private static string QrImage(string text)
    {
        var matrix=QrEncoder.Encode(text);int n=matrix.GetLength(0),scale=6,size=(n+8)*scale;
        using var bitmap=Bitmap.CreateBitmap(size,size,Bitmap.Config.Argb8888);
        bitmap.EraseColor(Color.White);
        using var canvas=new Canvas(bitmap);using var paint=new Paint{Color=Color.Black};
        for(int y=0;y<n;y++)for(int x=0;x<n;x++)if(matrix[y,x])canvas.DrawRect((x+4)*scale,(y+4)*scale,(x+5)*scale,(y+5)*scale,paint);
        using var stream=new MemoryStream();bitmap.Compress(Bitmap.CompressFormat.Png,100,stream);return Convert.ToBase64String(stream.ToArray());
    }
}
