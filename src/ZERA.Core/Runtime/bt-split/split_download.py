"""ZERA independent BT piece-range tasks and verified final assembly (GPL-3.0).

Each worker owns a separate libtorrent session, storage directory and disjoint
piece range. Only SHA-1 verified pieces are exported to its compact part file.
The compact files/bitmaps are revalidated on restart and again before assembly.
"""
import ctypes
import hashlib
import json
import math
import msvcrt
import os
from pathlib import Path, PureWindowsPath
import shutil
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import libtorrent as lt


def write_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=True)
        stream.flush()
        os.fsync(stream.fileno())
    for attempt in range(20):
        try:
            os.replace(temporary, path)
            break
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(0.025)


def read_json(path, default=None):
    try:
        with Path(path).open(encoding="utf-8-sig") as stream:
            return json.load(stream)
    except FileNotFoundError:
        if default is not None:
            return default
        raise


def inspect(path, requested):
    path = Path(path)
    if path.stat().st_size > 8 * 1024 * 1024:
        raise ValueError("种子文件不能超过 8 MiB。")
    torrent = lt.torrent_info(str(path))
    if not torrent.info_hashes().has_v1():
        raise ValueError("拆分模式需要包含 BT v1 信息的种子。")
    if not 2 <= requested <= 8 or torrent.num_pieces() < 2:
        raise ValueError("请选择 2 至 8 个子任务，种子至少需要包含两个数据块。")
    if torrent.piece_length() > 32 * 1024 * 1024:
        raise ValueError("此种子单块超过 32 MiB，暂不支持拆分模式。")
    files = torrent.files()
    seen = set()
    entries = []
    for index in range(files.num_files()):
        name = files.file_path(index)
        components = PureWindowsPath(name).parts
        if PureWindowsPath(name).is_absolute() or PureWindowsPath(name).drive or not components:
            raise ValueError("种子包含不安全的文件路径。")
        for component in components:
            stem = component.split(".", 1)[0].upper()
            if component in (".", "..") or component.endswith((" ", ".")) or \
                    any(c in component for c in '<>:"|?*') or any(ord(c) < 32 for c in component) or \
                    stem in ("CON", "PRN", "AUX", "NUL") or \
                    stem in {"COM" + str(i) for i in range(1, 10)} | {"LPT" + str(i) for i in range(1, 10)}:
                raise ValueError("种子包含不支持的 Windows 文件名。")
        if files.file_flags(index) & lt.file_storage.flag_symlink:
            raise ValueError("拆分模式不接受含符号链接的种子。")
        normalized = str(PureWindowsPath(name)).casefold()
        if normalized in seen:
            raise ValueError("种子包含重复的文件路径。")
        seen.add(normalized)
        entries.append({"Path": name, "Size": files.file_size(index), "Offset": files.file_offset(index),
                        "Pad": bool(files.file_flags(index) & lt.file_storage.flag_pad_file)})
    count = min(requested, torrent.num_pieces())
    ranges = []
    for index in range(count):
        first = index * torrent.num_pieces() // count
        end = (index + 1) * torrent.num_pieces() // count
        ranges.append({"Index": index, "StartPiece": first, "EndPiece": end,
                       "Total": sum(torrent.piece_size(p) for p in range(first, end))})
    result = {"Name": torrent.name(), "Total": torrent.total_size(), "Workers": count,
              "PieceCount": torrent.num_pieces(), "PieceLength": torrent.piece_length(),
              "TorrentSha256": hashlib.sha256(path.read_bytes()).hexdigest(), "Ranges": ranges, "Files": entries}
    return torrent, result


class Worker:
    def __init__(self, root, torrent, plan, count, private):
        self.plan = plan
        self.torrent = torrent
        self.root = root / ("worker-%02d" % plan["Index"])
        self.root.mkdir(exist_ok=True)
        self.part = self.root / "data.part"
        self.bitmap = self.root / "verified.bits"
        self.file = self.part.open("r+b" if self.part.exists() else "w+b")
        length = plan["EndPiece"] - plan["StartPiece"]
        stored = self.bitmap.read_bytes() if self.bitmap.exists() else b""
        self.done = set()
        for local in range(length):
            if local // 8 < len(stored) and stored[local // 8] & (1 << (local % 8)):
                piece = plan["StartPiece"] + local
                self.file.seek(local * torrent.piece_length())
                data = self.file.read(torrent.piece_size(piece))
                if len(data) == torrent.piece_size(piece) and hashlib.sha1(data).digest() == torrent.hash_for_piece(piece):
                    self.done.add(piece)
        self.session = None
        self.handle = None
        self.pending = set()
        self.count = count
        self.private = private
        self.paused = True
        self.dirty = False
        self.downloaded = sum(torrent.piece_size(piece) for piece in self.done)
        self.status = dict(plan, Completed=self.completed(), Status="paused", DownloadSpeed=0, UploadSpeed=0, Connections=0, Error="")
        self.commit()

    def completed(self):
        return self.downloaded

    def complete(self):
        return len(self.done) == self.plan["EndPiece"] - self.plan["StartPiece"]

    def commit(self):
        self.file.flush()
        os.fsync(self.file.fileno())
        bits = bytearray((self.plan["EndPiece"] - self.plan["StartPiece"] + 7) // 8)
        for piece in self.done:
            local = piece - self.plan["StartPiece"]
            bits[local // 8] |= 1 << (local % 8)
        temporary = self.bitmap.with_suffix(".tmp")
        with temporary.open("wb") as stream:
            stream.write(bits)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, self.bitmap)
        self.dirty = False

    def start(self):
        settings = {"listen_interfaces": "0.0.0.0:0", "enable_dht": not self.private, "enable_lsd": False,
                    "enable_upnp": False, "enable_natpmp": False, "connections_limit": max(4, 40 // self.count),
                    "upload_rate_limit": 262144 // self.count, "active_downloads": 1, "active_seeds": 0,
                    "alert_mask": int(lt.alert.category_t.error_notification | lt.alert.category_t.storage_notification)}
        self.session = lt.session(settings)
        parameters = lt.add_torrent_params()
        parameters.ti = self.torrent
        parameters.save_path = str(self.root / "storage")
        parameters.storage_mode = lt.storage_mode_t.storage_mode_sparse
        parameters.flags &= ~lt.torrent_flags.auto_managed
        parameters.flags &= ~lt.torrent_flags.paused
        parameters.piece_priorities = [4 if self.plan["StartPiece"] <= p < self.plan["EndPiece"] and p not in self.done else 0
                                       for p in range(self.torrent.num_pieces())]
        self.handle = self.session.add_torrent(parameters)
        if not self.private:
            for url in getattr(self, "extra_trackers", []):
                self.handle.add_tracker({"url": url, "tier": 1})
        self.paused = False

    def tick(self, paused):
        if self.handle:
            for alert in self.session.pop_alerts():
                if isinstance(alert, lt.read_piece_alert):
                    piece = int(alert.piece)
                    self.pending.discard(piece)
                    if alert.error.value():
                        raise IOError("读取已下载的数据块失败，请检查磁盘。")
                    if not self.plan["StartPiece"] <= piece < self.plan["EndPiece"]:
                        raise ValueError("子任务返回了范围外的数据块。")
                    data = bytes(alert.buffer)
                    if len(data) != self.torrent.piece_size(piece) or hashlib.sha1(data).digest() != self.torrent.hash_for_piece(piece):
                        raise IOError("子任务数据块校验失败，未写入合并分片。")
                    self.file.seek((piece - self.plan["StartPiece"]) * self.torrent.piece_length())
                    self.file.write(data)
                    if piece not in self.done:
                        self.downloaded += len(data)
                        self.done.add(piece)
                    self.dirty = True
            if self.dirty:
                self.commit()
        if self.complete():
            if self.handle and not self.paused:
                self.handle.pause()
                self.paused = True
            self.status.update(Status="complete", Completed=self.plan["Total"], DownloadSpeed=0, UploadSpeed=0, Connections=0)
            return
        if not paused and self.handle is None:
            self.start()
        if self.handle:
            if paused != self.paused:
                self.handle.pause() if paused else self.handle.resume()
                self.paused = paused
            state = self.handle.status(lt.torrent_handle.query_pieces)
            if state.errc.value():
                raise IOError("BT 子任务出错，请检查磁盘空间、目录权限和种源。")
            # Export at most two pieces at once per worker, bounding memory.
            if not paused:
                for piece in range(self.plan["StartPiece"], self.plan["EndPiece"]):
                    if len(self.pending) >= 2:
                        break
                    if piece not in self.done and piece not in self.pending and piece < len(state.pieces) and state.pieces[piece]:
                        self.handle.read_piece(piece)
                        self.pending.add(piece)
            self.status.update(DownloadSpeed=0 if paused else state.download_payload_rate,
                               UploadSpeed=0 if paused else state.upload_payload_rate, Connections=state.num_peers)
        self.status.update(Status="paused" if paused else "active", Completed=self.completed())

    def close(self):
        if self.handle:
            self.handle.pause()
            self.session.remove_torrent(self.handle)
        if self.dirty:
            self.commit()
        self.file.close()


def parent_watch(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x100000, False, pid)
    if not handle:
        raise RuntimeError("无法关联主程序进程。")
    return kernel, handle


def verify_result(output, torrent, plan, check_control):
    files = plan["Files"]
    for entry in files:
        if not entry["Pad"]:
            path = output.joinpath(*PureWindowsPath(entry["Path"]).parts)
            if not path.is_file() or path.stat().st_size != entry["Size"]:
                return False
    file_index = 0
    for piece in range(torrent.num_pieces()):
        while not check_control():
            time.sleep(0.2)
        position = piece * torrent.piece_length()
        remaining = torrent.piece_size(piece)
        checksum = hashlib.sha1()
        while remaining:
            while position >= files[file_index]["Offset"] + files[file_index]["Size"]:
                file_index += 1
            entry = files[file_index]
            take = min(remaining, entry["Offset"] + entry["Size"] - position)
            if entry["Pad"]:
                checksum.update(bytes(take))
            else:
                with output.joinpath(*PureWindowsPath(entry["Path"]).parts).open("rb") as stream:
                    stream.seek(position - entry["Offset"])
                    checksum.update(stream.read(take))
            position += take
            remaining -= take
        if checksum.digest() != torrent.hash_for_piece(piece):
            return False
    return True


def assemble(root, torrent, plan, workers, check_control, report):
    output = root / "result"
    if output.exists():
        if verify_result(output, torrent, plan, check_control):
            report("complete", plan["Total"])
            return
        raise FileExistsError("结果目录已经存在，请先检查已有文件；程序不会覆盖它。")
    temporary = root / "result.partial"
    marker = temporary / ".zera-merge"
    if temporary.exists():
        if not marker.exists() or marker.read_text() != plan["TorrentSha256"]:
            raise IOError("合并临时目录不是当前任务创建的，已停止以保护已有文件。")
        shutil.rmtree(temporary)
    temporary.mkdir()
    marker.write_text(plan["TorrentSha256"])
    # Avoid a seed path colliding with the owned merge marker.
    files = plan["Files"]
    for entry in files:
        if entry["Pad"]:
            continue
        target = temporary.joinpath(*PureWindowsPath(entry["Path"]).parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target == marker:
            raise ValueError("种子文件名与合并标记冲突。")
        if entry["Size"] == 0:
            target.touch(exist_ok=False)
    file_index, current = 0, None
    written, last_report = 0, 0.0
    try:
        for worker in workers:
            worker.file.seek(0)
            for piece in range(worker.plan["StartPiece"], worker.plan["EndPiece"]):
                while not check_control():
                    report("paused", written)
                    time.sleep(0.2)
                data = worker.file.read(torrent.piece_size(piece))
                if len(data) != torrent.piece_size(piece) or hashlib.sha1(data).digest() != torrent.hash_for_piece(piece):
                    raise IOError("合并前分片校验失败，保留断点；点击继续可检查并重下损坏数据块。")
                at = 0
                position = piece * torrent.piece_length()
                while at < len(data):
                    while file_index < len(files) and position >= files[file_index]["Offset"] + files[file_index]["Size"]:
                        if current:
                            current.flush()
                            os.fsync(current.fileno())
                            current.close()
                            current = None
                        file_index += 1
                    entry = files[file_index]
                    take = min(len(data) - at, entry["Offset"] + entry["Size"] - position)
                    if not entry["Pad"]:
                        if current is None:
                            current = temporary.joinpath(*PureWindowsPath(entry["Path"]).parts).open("xb")
                        current.write(memoryview(data)[at:at + take])
                    position += take
                    at += take
                written += len(data)
                if time.monotonic() - last_report >= 0.2:
                    report("merging", written)
                    last_report = time.monotonic()
        if current:
            current.flush()
            os.fsync(current.fileno())
            current.close()
            current = None
        # Publish only after every piece has passed the original torrent hash.
        while not check_control():
            report("paused", written)
            time.sleep(0.2)
        os.rename(temporary, output)
        (output / ".zera-merge").unlink()
        report("complete", written)
    finally:
        if current:
            current.close()


def run(root, parent_pid):
    root = Path(root).resolve()
    lock = (root / "worker.lock").open("a+b")
    lock.seek(0)
    lock.write(b"0")
    lock.flush()
    lock.seek(0)
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
    config = read_json(root / "job.json")
    torrent, plan = inspect(root / "source.torrent", int(config["Workers"]))
    if plan["TorrentSha256"] != config["TorrentSha256"]:
        raise ValueError("种子内容与原始拆分计划不一致，已停止。")
    workers = []
    kernel, parent = parent_watch(parent_pid)
    last_status = {}

    def report(state, merge_completed=0, error=""):
        nonlocal last_status
        last_status = {"Name": plan["Name"], "Status": state, "Total": plan["Total"],
                       "Completed": sum(w.status["Completed"] for w in workers), "MergeCompleted": merge_completed,
                       "Workers": [w.status for w in workers], "Error": error}
        write_json(root / "status.json", last_status)

    def control():
        value = read_json(root / "control.json")
        if value.get("Stop") or kernel.WaitForSingleObject(parent, 0) == 0:
            raise InterruptedError("主程序已停止任务。")
        flags = value.get("Paused", [])
        if len(flags) != len(plan["Ranges"]):
            raise ValueError("子任务暂停状态与拆分计划不一致。")
        return flags

    try:
        for entry in plan["Ranges"]:
            worker = Worker(root, torrent, entry, plan["Workers"], torrent.priv())
            worker.extra_trackers = read_json(root / "control.json").get("Trackers") or []
            workers.append(worker)
        while True:
            paused = control()
            for worker, pause in zip(workers, paused):
                worker.tick(bool(pause))
            if all(w.complete() for w in workers):
                report("merging")
                assemble(root, torrent, plan, workers, lambda: not all(control()), report)
                break
            report("paused" if all(w.complete() or p for w, p in zip(workers, paused)) else "active")
            time.sleep(0.2)
    except InterruptedError:
        report("paused")
    except Exception as error:
        # Do not log tracker URLs, passkeys or native exception bodies.
        message = str(error) if type(error) in (ValueError, IOError, FileExistsError) else "独立子任务异常（" + type(error).__name__ + "），保留已校验分片，请检查磁盘与种源后继续。"
        if "://" in message:
            message = "独立子任务异常，保留已校验分片，请检查磁盘与种源后继续。"
        report("error", error=message)
    finally:
        for worker in workers:
            worker.close()
        kernel.CloseHandle(parent)
        lock.close()


if __name__ == "__main__":
    try:
        if len(sys.argv) == 4 and sys.argv[1] == "--inspect":
            print(json.dumps(inspect(sys.argv[2], int(sys.argv[3]))[1], ensure_ascii=True))
        elif len(sys.argv) == 3:
            run(sys.argv[1], int(sys.argv[2]))
        else:
            raise ValueError("无效的分块下载参数。")
    except Exception as error:
        # Inspection failures have no job yet; report a bounded, credential-free message.
        message = str(error) if type(error) is ValueError else "种子不支持拆分，或引擎/存储目录不可用。请使用 BT v1 种子并检查目录权限。"
        print(json.dumps({"Error": message}, ensure_ascii=True))
        sys.exit(1)
