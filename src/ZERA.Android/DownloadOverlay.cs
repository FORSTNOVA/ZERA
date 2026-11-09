using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Runtime;
using View = Android.Views.View;

namespace ZERA.Android;

internal sealed class DownloadOverlay : IDisposable
{
    private readonly Service _service;
    private readonly WindowManagerLayoutParams _layoutParams;
    private IWindowManager _windowManager;
    private Handler _mainHandler;
    private View _rootView;
    private TextView _titleView;
    private TextView _detailView;
    private ProgressBar _progressBar;
    private Button _actionButton;
    private bool _isPaused;
    private bool _isClosed;
    private bool _isAttached;

    internal static bool Allowed(Context context)
    {
        if (context == null) return false;
        return global::Android.Provider.Settings.CanDrawOverlays(context);
    }

    public DownloadOverlay(Service service, Action pause, Action resume, Action open)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _mainHandler = new Handler(Looper.MainLooper);

        _layoutParams = new WindowManagerLayoutParams(
            WindowManagerLayoutParams.WrapContent,
            WindowManagerLayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = 24,
            Y = 120
        };

        RunOnMain(() =>
        {
            if (_isClosed) return;
            if (!Allowed(_service)) return;

            try
            {
                _windowManager = _service.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
                if (_windowManager == null) return;

                _rootView = BuildView(pause, resume, open);
                _windowManager.AddView(_rootView, _layoutParams);
                _isAttached = true;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ZERA.Overlay", "Overlay init failed: " + ex.Message);
                SafeRemove();
            }
        });
    }

    private View BuildView(Action pause, Action resume, Action open)
    {
        var density = _service.Resources?.DisplayMetrics?.Density ?? 1.0f;
        int Dp(int dp) => (int)(dp * density + 0.5f);

        // Container card: dark theme, compact, rounded corners
        var container = new LinearLayout(_service)
        {
            Orientation = Orientation.Vertical
        };
        container.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        container.SetMinimumWidth(Dp(220));

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(Dp(12));
        bg.SetColor(Color.ParseColor("#131a2e")); // Dark surface
        bg.SetStroke(Dp(1), Color.ParseColor("#3b508f")); // Primary border
        container.Background = bg;

        // Top drag handle / title bar
        var topRow = new LinearLayout(_service)
        {
            Orientation = Orientation.Horizontal
        };
        topRow.SetGravity(GravityFlags.CenterVertical);

        _titleView = new TextView(_service)
        {
            Text = "ZERA 下载",
            TextSize = 13,
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End
        };
        _titleView.SetSingleLine(true);
        _titleView.SetTextColor(Color.ParseColor("#ebf0fa"));
        _titleView.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);

        var titleLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f);
        topRow.AddView(_titleView, titleLp);

        // Drag touch listener on container / topRow
        float initialTouchX = 0, initialTouchY = 0;
        int initialX = 0, initialY = 0;

        topRow.Touch += (s, e) =>
        {
            if (e.Event == null) return;
            switch (e.Event.Action)
            {
                case MotionEventActions.Down:
                    initialX = _layoutParams.X;
                    initialY = _layoutParams.Y;
                    initialTouchX = e.Event.RawX;
                    initialTouchY = e.Event.RawY;
                    e.Handled = true;
                    break;
                case MotionEventActions.Move:
                    var dx = (int)(e.Event.RawX - initialTouchX);
                    var dy = (int)(e.Event.RawY - initialTouchY);
                    var metrics=_service.Resources.DisplayMetrics;
                    _layoutParams.X = Math.Clamp(initialX + dx,0,Math.Max(0,metrics.WidthPixels-(_rootView?.Width??0)));
                    _layoutParams.Y = Math.Clamp(initialY + dy,0,Math.Max(0,metrics.HeightPixels-(_rootView?.Height??0)));
                    if (_isAttached && _windowManager != null && _rootView != null)
                    {
                        try { _windowManager.UpdateViewLayout(_rootView, _layoutParams); } catch { }
                    }
                    e.Handled = true;
                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    e.Handled = true;
                    break;
            }
        };

        container.AddView(topRow);

        // Progress bar
        _progressBar = new ProgressBar(_service, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Max = 100,
            Progress = 0,
            Indeterminate = false
        };
        var pbLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(6))
        {
            TopMargin = Dp(8),
            BottomMargin = Dp(6)
        };
        container.AddView(_progressBar, pbLp);

        // Detail text (speed, bytes, percent)
        _detailView = new TextView(_service)
        {
            Text = "准备就绪",
            TextSize = 11,
            Ellipsize = global::Android.Text.TextUtils.TruncateAt.End
        };
        _detailView.SetSingleLine(true);
        _detailView.SetTextColor(Color.ParseColor("#a4b0ce"));
        var detailLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = Dp(8)
        };
        container.AddView(_detailView, detailLp);

        // Buttons row: 暂停/继续, 打开, 关闭
        var btnRow = new LinearLayout(_service)
        {
            Orientation = Orientation.Horizontal
        };
        btnRow.SetGravity(GravityFlags.CenterVertical);

        Button MakeBtn(string text, Color textColor, Color bgColor)
        {
            var btn = new Button(_service)
            {
                Text = text,
                TextSize = 11
            };
            btn.SetTextColor(textColor);
            btn.SetPadding(Dp(8), Dp(2), Dp(8), Dp(2));
            btn.SetMinimumHeight(Dp(28));
            btn.SetMinimumWidth(Dp(48));

            var bBg = new GradientDrawable();
            bBg.SetShape(ShapeType.Rectangle);
            bBg.SetCornerRadius(Dp(6));
            bBg.SetColor(bgColor);
            btn.Background = bBg;

            var lp = new LinearLayout.LayoutParams(0, Dp(28), 1.0f)
            {
                RightMargin = Dp(4)
            };
            btn.LayoutParameters = lp;
            return btn;
        }

        // Pause/Resume button
        _actionButton = MakeBtn("暂停", Color.ParseColor("#ebf0fa"), Color.ParseColor("#222d4b"));
        _actionButton.Click += (s, e) =>
        {
            if (_isPaused)
            {
                _isPaused = false;
                _actionButton.Text = "暂停";
                try { resume?.Invoke(); } catch { }
            }
            else
            {
                _isPaused = true;
                _actionButton.Text = "继续";
                try { pause?.Invoke(); } catch { }
            }
        };
        btnRow.AddView(_actionButton);

        // Open button
        var openBtn = MakeBtn("打开", Color.ParseColor("#ebf0fa"), Color.ParseColor("#3b6fe8"));
        openBtn.Click += (s, e) =>
        {
            try { open?.Invoke(); } catch { }
        };
        btnRow.AddView(openBtn);

        // Close button (Close hides for current service instance; Update won't reopen after close)
        var closeBtn = MakeBtn("关闭", Color.ParseColor("#ff6666"), Color.ParseColor("#222d4b"));
        ((LinearLayout.LayoutParams)closeBtn.LayoutParameters).RightMargin = 0;
        closeBtn.Click += (s, e) =>
        {
            _isClosed = true;
            SafeRemove();
        };
        btnRow.AddView(closeBtn);

        container.AddView(btnRow);
        return container;
    }

    public void Update(string title, string detail, int percent)
    {
        if (_isClosed) return;

        RunOnMain(() =>
        {
            if (_isClosed) return;

            // If not yet attached (e.g. initial add failed or permission granted later), do not force reopen if closed
            if (!_isAttached)
            {
                if (!Allowed(_service)) return;
                try
                {
                    _windowManager ??= _service.GetSystemService(Context.WindowService).JavaCast<IWindowManager>();
                    if (_windowManager != null && _rootView != null)
                    {
                        _windowManager.AddView(_rootView, _layoutParams);
                        _isAttached = true;
                    }
                }
                catch
                {
                    return;
                }
            }

            try
            {
                if (_titleView != null && !string.IsNullOrEmpty(title))
                {
                    _titleView.Text = title;
                }
                if (_detailView != null && detail != null)
                {
                    _detailView.Text = detail;
                }
                if (_progressBar != null)
                {
                    _progressBar.Progress = Math.Clamp(percent, 0, 100);
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ZERA.Overlay", "Overlay update failed: " + ex.Message);
            }
        });
    }

    private void SafeRemove()
    {
        if (_isAttached && _windowManager != null && _rootView != null)
        {
            try
            {
                _windowManager.RemoveViewImmediate(_rootView);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ZERA.Overlay", "Overlay remove error: " + ex.Message);
            }
            finally
            {
                _isAttached = false;
            }
        }
    }

    public void Dispose()
    {
        _isClosed = true;
        RunOnMain(() =>
        {
            SafeRemove();
            _rootView = null;
            _windowManager = null;
            _mainHandler = null;
        });
    }

    private void RunOnMain(Action action)
    {
        if (action == null) return;
        var handler = _mainHandler;
        if (handler == null) return;

        if (Looper.MyLooper() == Looper.MainLooper)
        {
            action();
        }
        else
        {
            handler.Post(action);
        }
    }
}
