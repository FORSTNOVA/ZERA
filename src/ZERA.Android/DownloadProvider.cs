using Android.Content;
using Android.Database;
using Android.OS;
using Android.Webkit;
using Uri=Android.Net.Uri;

namespace ZERA.Android;

[ContentProvider(new[]{"com.zera.downloader.files"},Exported=false,GrantUriPermissions=true)]
public sealed class DownloadProvider : ContentProvider
{
    public override bool OnCreate()=>true;
    private static SavedTask TaskFor(Uri uri)
    {
        if(uri.Authority=="com.zera.downloader.files"&&uri.PathSegments.Count==3&&uri.PathSegments[0]=="bt") {
            string file=MobileApplication.Controller.TorrentFile(uri.PathSegments[1],uri.PathSegments[2]);
            return new SavedTask{Path=file,Name=Path.GetFileName(file)};
        }
        if(uri.Authority!="com.zera.downloader.files"||uri.PathSegments.Count!=1)throw new FileNotFoundException();
        return MobileApplication.Controller.FindTask(uri.LastPathSegment)??throw new FileNotFoundException();
    }
    public override ParcelFileDescriptor OpenFile(Uri uri,string mode)
    {if(mode!="r")throw new Java.Lang.SecurityException("只读文件");return ParcelFileDescriptor.Open(new Java.IO.File(TaskFor(uri).Path),ParcelFileMode.ReadOnly);}
    public override string GetType(Uri uri)=>MimeTypeMap.Singleton.GetMimeTypeFromExtension(Path.GetExtension(TaskFor(uri).Path).TrimStart('.'))??"application/octet-stream";
    public override ICursor Query(Uri uri,string[] projection,string selection,string[] selectionArgs,string sortOrder)
    {
        var task=TaskFor(uri);string[] columns=projection??new[]{"_display_name","_size"};var cursor=new MatrixCursor(columns);
        cursor.AddRow(columns.Select(c=>(Java.Lang.Object)(c=="_display_name"?new Java.Lang.String(task.Name):c=="_size"?Java.Lang.Long.ValueOf(File.Exists(task.Path)?new FileInfo(task.Path).Length:0):null)).ToArray());return cursor;
    }
    public override Uri Insert(Uri uri,ContentValues values)=>throw new Java.Lang.UnsupportedOperationException();
    public override int Update(Uri uri,ContentValues values,string selection,string[] selectionArgs)=>throw new Java.Lang.UnsupportedOperationException();
    public override int Delete(Uri uri,string selection,string[] selectionArgs)=>throw new Java.Lang.UnsupportedOperationException();
}
