package com.zera.downloader;

import android.webkit.JavascriptInterface;
import android.webkit.WebView;
import java.util.concurrent.ArrayBlockingQueue;

/** Keep WebView's JavaScript thread independent of managed worker callbacks. */
public final class CommandQueue {
    private final ArrayBlockingQueue<String> queue=new ArrayBlockingQueue<>(32);
    private final WebView view;
    public CommandQueue(WebView view){this.view=view;}
    @JavascriptInterface public void dispatch(String request) {
        if(request!=null&&request.length()<=256*1024)queue.offer(request);
    }
    public String take(){return queue.poll();}
    public void deliver(String script){view.post(()->view.evaluateJavascript(script,null));}
}
