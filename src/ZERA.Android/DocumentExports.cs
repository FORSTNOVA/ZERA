using Android.Content;
using Android.Provider;
using Android.Webkit;
using Uri=Android.Net.Uri;

namespace ZERA.Android;

// SAF writes sequential completed copies; the download engine retains seekable staging files.
internal static class DocumentExports
{
    internal static string TreeName(ContentResolver resolver,Uri tree)
    {
        var doc=DocumentsContract.BuildDocumentUriUsingTree(tree,DocumentsContract.GetTreeDocumentId(tree));
        using var cursor=resolver.Query(doc,new[]{DocumentsContract.Document.ColumnDisplayName},null,null,null);
        return cursor?.MoveToFirst()==true?cursor.GetString(0):"所选公共目录";
    }
    internal static string Mime(string path)=>MimeTypeMap.Singleton.GetMimeTypeFromExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant())??"application/octet-stream";
    internal static (string uri,int files) CopyTree(ContentResolver resolver,string tree,string id,IReadOnlyList<(string path,string relative)> files)
    {
        var uri=Uri.Parse(tree);var parent=DocumentsContract.BuildDocumentUriUsingTree(uri,DocumentsContract.GetTreeDocumentId(uri));
        var folder=DocumentsContract.CreateDocument(resolver,parent,DocumentsContract.Document.MimeTypeDir,"ZERA-"+id[..Math.Min(8,id.Length)]+"-"+Guid.NewGuid().ToString("N")[..8])??throw new IOException("无法创建导出目录，请重新授权。");
        var directories=new Dictionary<string,Uri>{{"",folder}};int count=0;
        foreach(var file in files){
            string relative=file.relative.Replace('\\','/');var pieces=relative.Split('/');
            if(pieces.Any(p=>p is "" or "." or ".."))throw new IOException("结果文件路径无效。");
            string current="";Uri destination=folder;
            foreach(string part in pieces[..^1]){
                string key=current.Length==0?part:current+"/"+part;
                if(!directories.TryGetValue(key,out var child)){
                    child=DocumentsContract.CreateDocument(resolver,destination,DocumentsContract.Document.MimeTypeDir,part)??throw new IOException("创建子目录失败。");directories[key]=child;
                }
                destination=child;current=key;
            }
            var target=DocumentsContract.CreateDocument(resolver,destination,Mime(file.path),pieces[^1])??throw new IOException("创建导出文件失败。");
            CopyFile(resolver,file.path,target);count++;
        }
        return(folder.ToString(),count);
    }
    internal static void CopyFile(ContentResolver resolver,string path,Uri destination)
    {
        using var input=File.OpenRead(path);
        using var output=resolver.OpenOutputStream(destination,"wt")??throw new IOException("无法写入所选文件。");
        input.CopyTo(output,128*1024);output.Flush();
    }
}
