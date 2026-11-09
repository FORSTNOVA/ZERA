using Android.App;
using Android.Runtime;

namespace ZERA.Android;

[Application(Label = "ZERA", Icon="@mipmap/appicon", AllowBackup = false)]
public sealed class MobileApplication : Application
{
    internal static AppController Controller { get; private set; }
    public MobileApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    public override void OnCreate()
    {
        base.OnCreate();
        // SocketsHttpHandler retains the core's CookieContainer, redirect and
        // credential-scope behavior on both platforms.
        AppContext.SetSwitch("System.Net.Http.UseNativeHttpHandler", false);
        Controller = new AppController(this);
    }
}
