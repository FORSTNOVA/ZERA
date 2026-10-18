"""Offline verification of compact parts, corruption recovery and transactional assembly."""
import hashlib
import importlib.util
import pathlib
import tempfile
import sys
from contextlib import contextmanager

helper = pathlib.Path(sys.argv[1]).resolve()
sys.argv = [sys.argv[0]]
sys.path.insert(0, str(helper.parent))
spec = importlib.util.spec_from_file_location("split_download", helper)
split = importlib.util.module_from_spec(spec)
spec.loader.exec_module(split)
lt = split.lt


class PartsTests:
    def assertEqual(self, actual, expected):
        assert actual == expected, "values differ"

    def assertFalse(self, value):
        assert not value

    def assertNotIn(self, value, collection):
        assert value not in collection

    @contextmanager
    def assertRaises(self, expected):
        try:
            yield
        except expected:
            return
        raise AssertionError("Expected " + expected.__name__)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="zera-split-parts-")
        self.root = pathlib.Path(self.temporary.name)
        self.payload = bytes((i * 31 + i // 117) % 251 for i in range(40001))
        self.length = 16384
        info = {b"name": "中文任务".encode(), b"piece length": self.length,
                b"pieces": b"".join(hashlib.sha1(self.payload[i:i + self.length]).digest()
                                    for i in range(0, len(self.payload), self.length)),
                b"private": 1,
                b"files": [{b"length": 17001, b"path": [b"a.bin"]},
                           {b"length": 0, b"path": [b"empty.bin"]},
                           {b"length": 23000, b"path": ["子目录".encode(), b"b.bin"]}]}
        path = self.root / "source.torrent"
        path.write_bytes(lt.bencode({b"info": info}))
        self.torrent, self.plan = split.inspect(path, 2)
        self.workers = [split.Worker(self.root, self.torrent, r, 2, True) for r in self.plan["Ranges"]]
        for worker in self.workers:
            for piece in range(worker.plan["StartPiece"], worker.plan["EndPiece"]):
                data = self.payload[piece * self.length:(piece + 1) * self.length]
                worker.file.seek((piece - worker.plan["StartPiece"]) * self.length)
                worker.file.write(data)
                worker.done.add(piece)
                worker.downloaded += len(data)
            worker.commit()

    def tearDown(self):
        for worker in self.workers:
            if not worker.file.closed:
                worker.close()
        self.temporary.cleanup()

    def merge(self, report=lambda *args: None):
        split.assemble(self.root, self.torrent, self.plan, self.workers, lambda: True, report)

    def result(self):
        root = self.root / "result" / "中文任务"
        self.assertEqual((root / "empty.bin").stat().st_size, 0)
        return (root / "a.bin").read_bytes() + (root / "子目录" / "b.bin").read_bytes()

    def test_cross_file_and_short_piece(self):
        self.merge()
        self.assertEqual(self.result(), self.payload)
        # A restart after publishing the final directory verifies and adopts it.
        self.merge()
        self.assertEqual(self.result(), self.payload)

    def test_corruption_never_publishes_and_restart_rejects_bad_bitmap_bit(self):
        worker = self.workers[0]
        worker.file.seek(0)
        worker.file.write(b"wrong")
        worker.file.flush()
        with self.assertRaises(OSError):
            self.merge()
        self.assertFalse((self.root / "result").exists())
        worker.close()
        restored = split.Worker(self.root, self.torrent, worker.plan, 2, True)
        self.workers[0] = restored
        self.assertNotIn(restored.plan["StartPiece"], restored.done)
        self.assertFalse(restored.complete())

    def test_interrupted_merge_restarts_without_duplicate_bytes(self):
        def stop(state, completed):
            if state == "merging":
                raise InterruptedError("test interruption")
        with self.assertRaises(InterruptedError):
            self.merge(stop)
        self.assertFalse((self.root / "result").exists())
        self.merge()
        self.assertEqual(self.result(), self.payload)

    def test_existing_different_result_is_never_overwritten(self):
        self.merge()
        path = self.root / "result" / "中文任务" / "a.bin"
        path.write_bytes(b"user file")
        with self.assertRaises(FileExistsError):
            self.merge()
        self.assertEqual(path.read_bytes(), b"user file")

    def test_partition_covers_each_piece_once(self):
        _, plan = split.inspect(self.root / "source.torrent", 8)
        pieces = [piece for worker in plan["Ranges"] for piece in range(worker["StartPiece"], worker["EndPiece"])]
        self.assertEqual(pieces, list(range(self.torrent.num_pieces())))
        self.assertEqual(sum(worker["Total"] for worker in plan["Ranges"]), len(self.payload))


if __name__ == "__main__":
    for name in sorted(name for name in dir(PartsTests) if name.startswith("test_")):
        test = PartsTests()
        test.setUp()
        try:
            getattr(test, name)()
            print("PASS: " + name)
        finally:
            test.tearDown()
