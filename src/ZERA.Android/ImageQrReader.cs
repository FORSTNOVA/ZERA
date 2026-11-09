using Android.Content;
using Android.Graphics;
using ZXing;
using ZXing.Common;
using Uri=Android.Net.Uri;

namespace ZERA.Android;

internal static class ImageQrReader
{
    internal static string Read(Context context,Uri uri)
    {
        using var input=context.ContentResolver.OpenInputStream(uri)??throw new IOException("无法读取图片。");
        using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=input.Read(buffer,0,buffer.Length))>0){if(bytes.Length+n>16*1024*1024)throw new IOException("图片超过 16 MiB，请裁剪后重试。");bytes.Write(buffer,0,n);}
        return Read(bytes.ToArray());
    }
    internal static string Read(byte[] bytes)
    {
        using var options=new BitmapFactory.Options{InJustDecodeBounds=true};
        BitmapFactory.DecodeByteArray(bytes,0,bytes.Length,options)?.Dispose();
        if(options.OutWidth<=0||options.OutHeight<=0)throw new IOException("不是可识别的图片。");
        int sample=1;while((long)(options.OutWidth/sample)*(options.OutHeight/sample)>4*1024*1024||Math.Max(options.OutWidth,options.OutHeight)/sample>3000)sample*=2;
        options.InJustDecodeBounds=false;options.InSampleSize=sample;options.InPreferredConfig=Bitmap.Config.Argb8888;
        using var bitmap=BitmapFactory.DecodeByteArray(bytes,0,bytes.Length,options)??throw new IOException("图片解码失败。");
        int w=bitmap.Width,h=bitmap.Height;var pixels=new int[w*h];bitmap.GetPixels(pixels,0,w,0,0,w,h);
        var rgb=new byte[pixels.Length*3];
        for(int i=0;i<pixels.Length;i++){
            int color=pixels[i],alpha=(int)((uint)color>>24);
            for(int c=0;c<3;c++){int channel=(color>>(16-c*8))&255;rgb[i*3+c]=(byte)((channel*alpha+255*(255-alpha))/255);}
        }
        var reader=new BarcodeReaderGeneric{AutoRotate=true,Options=new DecodingOptions{TryHarder=true,TryInverted=true,PossibleFormats=new[]{BarcodeFormat.QR_CODE,BarcodeFormat.CODE_128}}};
        var result=reader.Decode(new RGBLuminanceSource(rgb,w,h,RGBLuminanceSource.BitmapFormat.RGB24));
        if(string.IsNullOrWhiteSpace(result?.Text))throw new IOException("未找到二维码，请使用清晰完整的二维码图片。");
        if(result.Text.Length>16384)throw new IOException("二维码内容过长。");
        return result.Text;
    }
}
