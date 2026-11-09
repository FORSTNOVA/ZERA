package com.zera.downloader;

import org.json.*;
import org.libtorrent4j.*;
import org.libtorrent4j.swig.torrent_flags_t;
import java.io.*;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.*;

/** Android native engine; no subprocess, RPC listener or cloud credentials. */
public final class TorrentBridge {
    private static final Map<String, Job> jobs = new LinkedHashMap<>();
    private static File root;
    private static final ScheduledExecutorService monitor = Executors.newSingleThreadScheduledExecutor();
    private static final ExecutorService merger = Executors.newSingleThreadExecutor();
    private static class Worker {
        SessionManager session;
        TorrentHandle handle;
        int first, end;
        File directory;
        boolean complete;
        long done,total,speed;
        int peers;
    }
    private static class Job {
        String id, name, source, state="Paused", error="";
        int count=1;
        long total, done, speed;
        File directory;
        TorrentInfo info;
        List<Worker> workers=new ArrayList<>();
        boolean merging;
        List<String> trackers=new ArrayList<>();
        Set<Integer> pausedWorkers=new HashSet<>();
    }
    public static synchronized String call(String request) {
        try {
            JSONObject input=new JSONObject(request);
            initialize(new File(input.getString("root")));
            String action=input.getString("action");
            JSONObject p=input.optJSONObject("payload");if(p==null)p=new JSONObject();
            if(action.equals("add") || action.equals("file")) {
                Job job=new Job();job.id=UUID.randomUUID().toString();job.directory=new File(root,job.id);job.directory.mkdirs();
                job.source=action.equals("file")?p.getString("path"):p.getString("magnet");
                job.count=p.optInt("split",1);if(job.count!=1&&job.count!=2&&job.count!=4&&job.count!=8)throw new IOException("拆分数只能为 2、4 或 8。");
                if(job.source.startsWith("magnet:")) {
                    if(!job.source.contains("xt=urn:btih:"))throw new IOException("请输入 BT v1 磁力链接。");
                    if(job.count>1)throw new IOException("拆分下载需要本地 BT v1 种子。");
                    job.name="正在获取磁力元数据";
                } else {
                    File original=new File(job.source);
                    if(original.length()>16*1024*1024)throw new IOException("种子文件超过 16 MiB。");
                    File copy=new File(job.directory,"source.torrent");Files.copy(original.toPath(),copy.toPath());job.source=copy.getPath();
                    job.info=new TorrentInfo(copy);validate(job.info);job.name=job.info.name();job.total=job.info.totalSize();
                    if(job.info.numPieces()<job.count)throw new IOException("种子块数少于拆分数，请选择较少子任务。");
                    if(job.total>0 && job.directory.getUsableSpace()/Math.max(1,job.total)<(job.count>1?job.count+2:1))
                        throw new IOException("磁盘空间不足；拆分任务需预留（子任务数 + 2）倍大小。");
                }
                JSONArray trackers=input.optJSONArray("trackers");
                if(trackers!=null&&(job.info==null||!job.info.isPrivate()))for(int i=0;i<trackers.length();i++)job.trackers.add(trackers.getString(i));
                jobs.put(job.id,job);save();start(job);save();return list().toString();
            }
            if(action.equals("list"))return list().toString();
            if(action.equals("active"))return new JSONObject().put("active",jobs.values().stream().anyMatch(j->j.state.equals("Downloading")||j.state.equals("Queued")||j.merging)).toString();
            if(action.equals("pauseAll")){for(Job j:jobs.values())pause(j);save();return "{}";}
            if(action.equals("resumeAll")){
                for(Job j:jobs.values())if(j.state.equals("Paused")||j.state.equals("Failed")){
                    if(j.count>1&&jobs.values().stream().anyMatch(k->k!=j&&k.count>1&&k.state.equals("Downloading")))continue;
                    j.pausedWorkers.clear();start(j);
                }
                save();return list().toString();
            }
            Job job=jobs.get(p.getString("id"));if(job==null)throw new IOException("BT 任务不存在。");
            if(action.equals("pause"))pause(job);
            else if(action.equals("resume")){job.pausedWorkers.clear();start(job);}
            else if(action.equals("worker.pause")||action.equals("worker.resume")){
                int index=p.getInt("index");
                if(job.count<=1||index<0||index>=job.count)throw new IOException("拆分子任务不存在。");
                if(job.merging||job.state.equals("Completed"))throw new IOException("任务已下载完成，不能控制子会话。");
                Worker w=job.workers.isEmpty()?null:job.workers.get(index);
                if(w!=null&&w.complete)throw new IOException("此子任务已完成。");
                if(action.equals("worker.pause")){
                    job.pausedWorkers.add(index);
                    if(w!=null){w.handle.pause();w.session.pause();w.speed=0;}
                    boolean allPaused=true;
                    for(int i=0;i<job.count;i++)if(!job.pausedWorkers.contains(i)&&(job.workers.isEmpty()||!job.workers.get(i).complete))allPaused=false;
                    if(allPaused){job.state="Paused";job.speed=0;}
                }else{
                    if(job.state.equals("Paused"))for(int i=0;i<job.count;i++)job.pausedWorkers.add(i);
                    job.pausedWorkers.remove(index);start(job);
                }
            }
            else if(action.equals("remove")){
                if(job.merging)throw new IOException("正在校验合并，请等待完成。");
                pause(job);for(Worker w:job.workers)w.session.stop();jobs.remove(job.id);
            }else throw new IOException("未知 BT 操作。");
            save();return list().toString();
        }catch(Throwable error){return "{\"error\":"+JSONObject.quote(error instanceof IOException?error.getMessage():"BT 引擎操作失败，请检查种子和设备存储。")+"}";}
    }
    private static void initialize(File location)throws Exception {
        if(root!=null)return;root=location;root.mkdirs();
        File state=new File(root,"jobs.json");
        if(state.exists()){
            JSONArray list=new JSONArray(new String(Files.readAllBytes(state.toPath()),java.nio.charset.StandardCharsets.UTF_8));
            for(int i=0;i<list.length();i++){
                JSONObject p=list.getJSONObject(i);Job job=new Job();job.id=p.getString("id");
                if(!job.id.matches("[a-fA-F0-9-]{36}"))throw new IOException("BT 任务索引无效。");
                job.directory=new File(root,job.id);job.name=p.getString("name");job.source=p.getString("source");job.count=p.optInt("count",1);
                if(!job.source.startsWith("magnet:"))job.source=new File(job.directory,"source.torrent").getPath();
                if(!job.source.startsWith("magnet:")&&new File(job.source).exists()){job.info=new TorrentInfo(new File(job.source));validate(job.info);}
                job.total=p.optLong("total");job.done=p.optLong("done");job.state=p.optString("state").equals("Completed")?"Completed":"Paused";
                JSONArray trackers=p.optJSONArray("trackers");if(trackers!=null)for(int k=0;k<trackers.length();k++)job.trackers.add(trackers.getString(k));
                if(job.count!=1&&job.count!=2&&job.count!=4&&job.count!=8)throw new IOException("拆分任务索引无效。");
                JSONArray paused=p.optJSONArray("pausedWorkers");if(paused!=null)for(int k=0;k<paused.length();k++)job.pausedWorkers.add(paused.getInt(k));
                jobs.put(job.id,job);
            }
        }
        monitor.scheduleWithFixedDelay(()->{synchronized(TorrentBridge.class){try{tick();}catch(Throwable ignored){}}},1,1,TimeUnit.SECONDS);
    }
    private static void validate(TorrentInfo info)throws Exception {
        if(!info.isValid()||!info.hasV1())throw new IOException("暂不支持仅 BT v2 种子。");
        if(info.numFiles()>100000||info.pieceLength()>64*1024*1024)throw new IOException("种子文件数或块大小超出限制。");
        FileStorage fs=info.files();File sandbox=new File(root,"validate");
        for(int i=0;i<fs.numFiles();i++){
            String path=fs.filePath(i);
            if(fs.fileAbsolutePath(i)||fs.fileFlags(i).and_(FileStorage.FLAG_SYMLINK).non_zero())throw new IOException("不支持含绝对路径或符号链接的种子。");
            safeFile(sandbox,path);
        }
    }
    private static File safeFile(File directory,String path)throws IOException {
        File file=new File(directory,path);String base=directory.getCanonicalPath()+File.separator;
        if(!file.getCanonicalPath().startsWith(base)||path.contains("\\")||path.contains("\u0000"))throw new IOException("种子路径越界。");
        return file;
    }
    private static void start(Job job)throws Exception {
        if(job.merging)throw new IOException("正在校验合并，请稍后。");
        if(job.state.equals("Completed"))return;
        if(job.count==1&&!job.state.equals("Downloading")&&jobs.values().stream().filter(j->j!=job&&j.count==1&&j.state.equals("Downloading")).count()>=2){job.state="Queued";return;}
        if(job.count>1&&jobs.values().stream().anyMatch(j->j!=job&&j.count>1&&j.state.equals("Downloading")))throw new IOException("请先暂停另一组拆分任务。");
        if(job.workers.isEmpty()){
            if(!job.source.startsWith("magnet:")){job.info=new TorrentInfo(new File(job.source));validate(job.info);}
            for(int i=0;i<job.count;i++){
                Worker w=new Worker();w.directory=new File(job.directory,job.count>1?"worker-"+i:"content");w.directory.mkdirs();
                SettingsPack settings=new SettingsPack().uploadRateLimit(job.count==1?128*1024:256*1024/job.count).connectionsLimit(60).activeDownloads(2).activeSeeds(0);
                if(job.count>1)settings.setInteger(org.libtorrent4j.swig.settings_pack.int_types.min_reconnect_time.swigValue(),10);
                w.session=new SessionManager();w.session.start(new SessionParams(settings));
                AddTorrentParams p;
                if(job.info!=null){
                    org.libtorrent4j.swig.error_code parseError=new org.libtorrent4j.swig.error_code();
                    p=new AddTorrentParams(org.libtorrent4j.swig.add_torrent_params.load_torrent_file(job.source,parseError));
                    if(parseError.value()!=0)throw new IOException("无法读取原始种子元数据。");
                }else p=AddTorrentParams.parseMagnetUri(job.source);
                p.setSavePath(w.directory.getPath());p.setMaxConnections(40);
                p.setFlags(p.getFlags().and_(TorrentFlags.AUTO_MANAGED.inv()).or_(TorrentFlags.PAUSED));
                if(job.info!=null){
                    p.setTorrentInfo(job.info);w.first=i*job.info.numPieces()/job.count;w.end=(i+1)*job.info.numPieces()/job.count;
                    if(job.count>1){Priority[] priorities=Priority.array(Priority.IGNORE,job.info.numPieces());Arrays.fill(priorities,w.first,w.end,Priority.DEFAULT);p.piecePriorities(priorities);}
                }
                ErrorCode ec=new ErrorCode(new org.libtorrent4j.swig.error_code());w.handle=new SessionHandle(w.session.swig()).addTorrent(p,ec);
                if(ec.isError())throw new IOException("无法创建 BT 任务。");
                if(job.info==null||!job.info.isPrivate())for(String tracker:job.trackers)w.handle.addTracker(new AnnounceEntry(tracker));
                job.workers.add(w);
            }
        }
        for(int i=0;i<job.workers.size();i++){
            Worker w=job.workers.get(i);if(w.complete)continue;
            w.handle.unsetFlags(TorrentFlags.AUTO_MANAGED);
            if(job.pausedWorkers.contains(i)){w.handle.pause();w.session.pause();}
            else {w.handle.resume();w.session.resume();}
        }
        job.state="Downloading";job.error="";
    }
    private static void pause(Job job){
        for(Worker w:job.workers)if(!w.complete){w.handle.unsetFlags(TorrentFlags.AUTO_MANAGED);w.handle.pause();w.session.pause();w.speed=0;}
        if(!job.state.equals("Completed")&&!job.merging)job.state="Paused";job.speed=0;
    }
    private static void tick()throws Exception {
        boolean changed=false;
        for(Job job:jobs.values()){
            if(job.state.equals("Queued")){start(job);changed=true;}
            if(!job.state.equals("Downloading")||job.workers.isEmpty())continue;
            job.speed=0;job.done=0;boolean complete=true;
            for(Worker w:job.workers){
                if(w.complete){for(int piece=w.first;piece<w.end;piece++)job.done+=job.info.pieceSize(piece);continue;}
                TorrentStatus status=w.handle.status(true);job.speed+=status.downloadRate();
                w.speed=status.downloadRate();w.peers=status.numPeers();w.done=0;
                if(status.errorCode().isError()){pause(job);job.state="Failed";job.error="BT 下载失败，错误代码 "+status.errorCode().getValue();complete=false;break;}
                if(!status.hasMetadata()){complete=false;continue;}
                if(job.info==null){job.info=w.handle.torrentFile();validate(job.info);job.total=job.info.totalSize();job.name=job.info.name();w.end=job.info.numPieces();}
                w.total=0;for(int piece=w.first;piece<w.end;piece++)w.total+=job.info.pieceSize(piece);
                if(job.count==1){job.done=status.totalWantedDone();w.done=job.done;if(!status.isFinished())complete=false;}
                else {
                    boolean rangeComplete=true;
                    for(int piece=w.first;piece<w.end;piece++)if(w.handle.havePiece(piece)){job.done+=job.info.pieceSize(piece);w.done+=job.info.pieceSize(piece);}else {complete=false;rangeComplete=false;}
                    // Release seed connections as soon as this worker has its range.
                    // Seeds commonly allow only one connection per IP address.
                    if(rangeComplete){w.handle.pause();w.session.stop();w.handle=null;w.complete=true;w.speed=0;w.peers=0;}
                }
            }
            if(complete){
                pause(job);
                if(job.count==1){job.state="Completed";job.done=job.total;for(Worker w:job.workers)w.session.stop();job.workers.clear();}
                else {job.state="Merging";job.merging=true;merger.execute(()->merge(job));}
            }
            changed=true;
        }
        if(changed)save();
    }
    private static void merge(Job job){
        try {
            for(Worker w:job.workers)if(w.handle!=null)w.handle.flushCache();Thread.sleep(1500);
            File temp=new File(job.directory,"result-pending"),result=new File(job.directory,"result");
            if(result.exists())throw new IOException("结果目录已存在，请先检查保留内容。");temp.mkdirs();
            TorrentInfo info=job.info;FileStorage fs=info.files();MessageDigest sha1=MessageDigest.getInstance("SHA-1");
            for(int piece=0;piece<info.numPieces();piece++){
                Worker worker=null;for(Worker w:job.workers)if(piece>=w.first&&piece<w.end){worker=w;break;}
                byte[] bytes=new byte[info.pieceSize(piece)];long offset=(long)piece*info.pieceLength();int copied=0;
                for(int i=0;i<fs.numFiles()&&copied<bytes.length;i++){
                    long start=Math.max(offset,fs.fileOffset(i)),end=Math.min(offset+bytes.length,fs.fileOffset(i)+fs.fileSize(i));
                    if(end<=start)continue;int length=(int)(end-start),at=(int)(start-offset);
                    if(!fs.padFileAt(i))try(RandomAccessFile input=new RandomAccessFile(safeFile(worker.directory,fs.filePath(i)),"r")){input.seek(start-fs.fileOffset(i));input.readFully(bytes,at,length);}
                    copied+=length;
                }
                if(copied!=bytes.length||!Sha1Hash.fromBytes(sha1.digest(bytes)).equals(info.hashForPiece(piece)))throw new IOException("分片校验失败，原数据已保留，请继续任务重新校验。");
                for(int i=0;i<fs.numFiles();i++){
                    if(fs.padFileAt(i))continue;long start=Math.max(offset,fs.fileOffset(i)),end=Math.min(offset+bytes.length,fs.fileOffset(i)+fs.fileSize(i));
                    if(end<=start)continue;File output=safeFile(temp,fs.filePath(i));output.getParentFile().mkdirs();
                    try(RandomAccessFile dest=new RandomAccessFile(output,"rw")){dest.setLength(fs.fileSize(i));dest.seek(start-fs.fileOffset(i));dest.write(bytes,(int)(start-offset),(int)(end-start));}
                }
            }
            // Include zero length files, then atomically publish within the same filesystem.
            for(int i=0;i<fs.numFiles();i++)if(!fs.padFileAt(i)&&fs.fileSize(i)==0){File file=safeFile(temp,fs.filePath(i));file.getParentFile().mkdirs();file.createNewFile();}
            Files.move(temp.toPath(),result.toPath());
            synchronized(TorrentBridge.class){job.state="Completed";job.done=job.total;job.merging=false;for(Worker w:job.workers)w.session.stop();job.workers.clear();save();}
        }catch(Throwable ex){synchronized(TorrentBridge.class){job.state="Failed";job.error=ex instanceof IOException?ex.getMessage():"合并失败，分片已保留。";job.merging=false;for(Worker w:job.workers)w.session.stop();job.workers.clear();try{save();}catch(Exception ignored){}}}
    }
    private static JSONArray list()throws Exception {
        JSONArray array=new JSONArray();
        for(Job job:jobs.values())array.put(new JSONObject().put("id",job.id).put("name",job.name).put("state",job.state)
            .put("done",job.done).put("total",job.total).put("speed",job.speed).put("error",job.error)
            .put("split",job.count).put("workers",workerViews(job)).put("path",new File(job.directory,job.count>1?"result":"content").getPath()));
        return array;
    }
    private static JSONArray workerViews(Job job)throws Exception {
        JSONArray list=new JSONArray();
        if(job.count<=1)return list;
        for(int i=0;i<job.count;i++){
            Worker w=job.workers.isEmpty()?null:job.workers.get(i);
            String state=job.state.equals("Completed")||(w!=null&&w.complete)?"Completed":job.pausedWorkers.contains(i)||job.state.equals("Paused")?"Paused":job.state;
            long total=job.total/job.count;
            if(job.info!=null){total=0;for(int piece=i*job.info.numPieces()/job.count;piece<(i+1)*job.info.numPieces()/job.count;piece++)total+=job.info.pieceSize(piece);}
            long done=state.equals("Completed")?total:w!=null?w.done:0;
            list.put(new JSONObject().put("index",i).put("state",state).put("total",total).put("done",done).put("speed",w!=null?w.speed:0).put("peers",w!=null?w.peers:0));
        }
        return list;
    }
    private static void save()throws Exception {
        JSONArray array=new JSONArray();
        for(Job job:jobs.values())array.put(new JSONObject().put("id",job.id).put("name",job.name).put("source",job.source)
            .put("count",job.count).put("total",job.total).put("done",job.done).put("state",job.state).put("pausedWorkers",new JSONArray(job.pausedWorkers)).put("trackers",new JSONArray(job.trackers)));
        File pending=new File(root,"jobs.tmp");try(FileOutputStream output=new FileOutputStream(pending)){output.write(array.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));output.getFD().sync();}
        Files.move(pending.toPath(),new File(root,"jobs.json").toPath(),StandardCopyOption.REPLACE_EXISTING);
    }
}
