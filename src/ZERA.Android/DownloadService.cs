using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace ZERA.Android;

[Service(Exported=false,ForegroundServiceType=ForegroundService.TypeDataSync)]
public sealed class DownloadService : Service
{
    private System.Threading.Timer _timer;
    private PowerManager.WakeLock _wakeLock;
    private NotificationManager _manager;
    private int _commandRunning;
    private DownloadOverlay _overlay;
    private Handler _mainHandler;
    private int _ticking;
    private volatile bool _destroyed;
    private long _overlayPauseUntil;
    public override void OnCreate()
    {
        base.OnCreate();
        _mainHandler=new Handler(Looper.MainLooper);
        _manager=(NotificationManager)GetSystemService(NotificationService);
        _manager.CreateNotificationChannel(new NotificationChannel("downloads","下载任务",NotificationImportance.Low));
        StartForeground(1,BuildNotification(false));
        var power=(PowerManager)GetSystemService(PowerService);
        _wakeLock=power.NewWakeLock(WakeLockFlags.Partial,"ZERA:download");
        _wakeLock.Acquire(10*60*1000L);
        _timer=new System.Threading.Timer(_=>{
            if(_destroyed||Interlocked.Exchange(ref _ticking,1)!=0)return;
            try{
                if(Volatile.Read(ref _commandRunning)>0)return;
                MobileApplication.Controller.CheckAutoExports();
                if(!MobileApplication.Controller.HasActiveWork){
                    if(_overlay!=null&&global::System.Environment.TickCount64<Volatile.Read(ref _overlayPauseUntil)){
                        _manager.Notify(1,BuildNotification(true));_overlay.Update("ZERA · 已暂停","可点击继续，稍后关闭悬浮窗",0);return;
                    }
                    StopForeground(StopForegroundFlags.Detach);_manager.Notify(1,BuildNotification(true));StopSelf();return;
                }
                _manager.Notify(1,BuildNotification(false));
                UpdateOverlay();
                if(!_wakeLock.IsHeld)_wakeLock.Acquire(10*60*1000L);
            }catch(Exception ex){global::Android.Util.Log.Warn("ZERA.Service",ex.GetType().Name);}
            finally{Interlocked.Exchange(ref _ticking,0);}
        },null,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(2));
    }
    private void UpdateOverlay()
    {
        _mainHandler.Post(()=>{
        if(_destroyed)return;
        var controller=MobileApplication.Controller;
        if(!controller.FloatingEnabled||!DownloadOverlay.Allowed(this)){_overlay?.Dispose();_overlay=null;return;}
        _overlay??=new DownloadOverlay(this,()=>{Interlocked.Exchange(ref _overlayPauseUntil,global::System.Environment.TickCount64+5000);controller.PauseAll();},
            ()=>{
                Interlocked.Exchange(ref _overlayPauseUntil,0);Interlocked.Increment(ref _commandRunning);
                _=Task.Run(async()=>{try{await controller.ResumeAll();}catch{}finally{Interlocked.Decrement(ref _commandRunning);}});
            },
            ()=>StartActivity(new Intent(this,typeof(MainActivity)).AddFlags(ActivityFlags.NewTask|ActivityFlags.SingleTop)));
        var summary=controller.ProgressSummary();_overlay.Update(summary.title,summary.detail,summary.percent);
        });
    }
    private Notification BuildNotification(bool idle)
    {
        var summary=MobileApplication.Controller.ProgressSummary();
        var open=PendingIntent.GetActivity(this,0,new Intent(this,typeof(MainActivity)),PendingIntentFlags.Immutable|PendingIntentFlags.UpdateCurrent);
        var builder=new Notification.Builder(this,"downloads").SetContentTitle(idle?"ZERA · 下载已停止":summary.title)
            .SetContentText(idle?"点击继续管理任务":summary.detail).SetOnlyAlertOnce(true)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload).SetContentIntent(open).SetOngoing(!idle);
        if(!idle)builder.SetProgress(100,summary.percent,false);
        var pause=PendingIntent.GetService(this,1,new Intent(this,typeof(DownloadService)).SetAction("zera.pause"),PendingIntentFlags.Immutable|PendingIntentFlags.UpdateCurrent);
        var resume=PendingIntent.GetForegroundService(this,2,new Intent(this,typeof(DownloadService)).SetAction("zera.resume"),PendingIntentFlags.Immutable|PendingIntentFlags.UpdateCurrent);
        builder.AddAction(new Notification.Action.Builder((global::Android.Graphics.Drawables.Icon)null,idle?"继续任务":"全部暂停",idle?resume:pause).Build());
        return builder.Build();
    }
    public override StartCommandResult OnStartCommand(Intent intent,StartCommandFlags flags,int startId)
    {
        if(intent?.Action=="zera.pause")MobileApplication.Controller.PauseAll();
        if(intent?.Action=="zera.resume"){
            Interlocked.Increment(ref _commandRunning);
            _=Task.Run(async()=>{try{await MobileApplication.Controller.ResumeAll();}catch{}finally{Interlocked.Decrement(ref _commandRunning);}});
        }
        return StartCommandResult.NotSticky;
    }
    public override IBinder OnBind(Intent intent)=>null;
    public override void OnTimeout(int startId,ForegroundService fgsType)
    { MobileApplication.Controller.PauseAll();StopSelf(); }
    public override void OnDestroy()
    {
        _destroyed=true;_mainHandler?.RemoveCallbacksAndMessages(null);
        _overlay?.Dispose();_overlay=null;
        _timer?.Dispose();if(_wakeLock?.IsHeld==true)_wakeLock.Release();_wakeLock?.Dispose();
        base.OnDestroy();
    }
}
