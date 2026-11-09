package com.zera.downloader;

import android.content.Context;
import android.webkit.*;
import java.io.*;
import java.util.*;

/** The privileged offline origin only serves these three bundled files. */
public final class AssetClient extends WebViewClient {
    private final Context context;
    public AssetClient(Context context) { this.context=context; }
    public static void applyTheme(android.app.Activity activity,WebView view,boolean dark){
        activity.runOnUiThread(()->{
            int color=android.graphics.Color.parseColor(dark?"#0b101e":"#f0f4fa");
            view.setBackgroundColor(color);activity.getWindow().getDecorView().setBackgroundColor(color);
            if(android.os.Build.VERSION.SDK_INT>=30){
                android.view.WindowInsetsController controller=activity.getWindow().getInsetsController();
                int mask=android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
                if(controller!=null)controller.setSystemBarsAppearance(dark?0:mask,mask);
            }else activity.getWindow().getDecorView().setSystemUiVisibility(dark?0:android.view.View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|android.view.View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        });
    }
    public static void applyInsets(android.view.View view) {
        view.setOnApplyWindowInsetsListener((v,insets)->{
            if(android.os.Build.VERSION.SDK_INT>=30){
                android.graphics.Insets bars=insets.getInsets(android.view.WindowInsets.Type.systemBars());
                android.graphics.Insets ime=insets.getInsets(android.view.WindowInsets.Type.ime());
                v.setPadding(bars.left,bars.top,bars.right,Math.max(bars.bottom,ime.bottom));
            }else v.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        });
        view.requestApplyInsets();
    }
    @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request) {
        String path=request.getUrl().getPath();
        try {
            if(!"https".equals(request.getUrl().getScheme())||!"app.zera.local".equals(request.getUrl().getHost())||
                !("/index.html".equals(path)||"/app.css".equals(path)||"/app.js".equals(path)))
                return new WebResourceResponse("text/plain","UTF-8",403,"Blocked",Collections.emptyMap(),new ByteArrayInputStream(new byte[0]));
            Map<String,String> headers=new HashMap<>();
            headers.put("Content-Security-Policy","default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'");
            return new WebResourceResponse(path.endsWith(".js")?"application/javascript":path.endsWith(".css")?"text/css":"text/html","UTF-8",200,"OK",headers,context.getAssets().open(path.substring(1)));
        }catch(IOException ex){return new WebResourceResponse("text/plain","UTF-8",new ByteArrayInputStream("界面资源缺失，请重新安装完整 APK。".getBytes(java.nio.charset.StandardCharsets.UTF_8)));}
    }
    @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request) {
        if("app.zera.local".equals(request.getUrl().getHost())&&"https".equals(request.getUrl().getScheme()))return false;
        if(request.isForMainFrame()&&"https".equals(request.getUrl().getScheme())) {
            try{context.startActivity(new android.content.Intent(android.content.Intent.ACTION_VIEW,request.getUrl()));}catch(Exception ignored){}
        }
        return true;
    }
}
