# libtorrent independent piece-range engine

- Python distribution: libtorrent 2.0.14 (`lt.version` = 2.0.14.0), CPython 3.13 Windows x64.
- Upstream: https://www.libtorrent.org/ and https://github.com/arvidn/libtorrent
- Wheel: https://files.pythonhosted.org/packages/ff/21/9b729da6c62eb59fabf869ed0c75972ddffdd12fd6be9708d043b8693333/libtorrent-2.0.14-cp313-cp313-win_amd64.whl
- Wheel SHA256: `aa024bc2f5512f19c98b0fb450e6f1a26aac495b3448f46fdd976997aab3aa61`.
- Wheel URL/hash were discovered using the Aliyun PyPI index after direct pypi.org TLS connections failed; the actual wheel was downloaded from the official files.pythonhosted.org host over HTTPS and checked against that hash.
- Unmodified extension SHA256: `53f5d4d64a6bc766f70bac8a00e8a70022b4a29bfd78d22eeaf5de3b71c48cd2`.
- Source: https://github.com/arvidn/libtorrent/releases/download/v2.0.14/libtorrent-rasterbar-2.0.14.tar.gz
- Source SHA256: `1b0b21b9755b5fbec23ca9ba2d2d10434ecb6711c39f37f5fc9d5aa25cf369c9`.
- License: BSD-3-Clause, full upstream LICENSE.libtorrent included. Boost license is included as LICENSE.Boost.

`split_download.py` is ZERA's own GPL-3.0 coordinator. It runs using the existing
bundled CPython with isolated interpreter flags. It creates independent native
sessions and does not change the upstream libtorrent extension.

The extension imports MSVCP140.dll. An unmodified Microsoft-signed C++ runtime
14.50.35719.0 is bundled next to the extension; its signature was verified as
Valid before copying from this machine's installed Microsoft runtime.
MSVCP140.dll SHA256: `def46aa6a8f72f27bafac0c43334419486a4d1dcdb6c479a8ef7034b3e1fa4cb`.
The existing bundled Python supplies VCRUNTIME140.dll and VCRUNTIME140_1.dll;
other imports are Windows 10/11 system libraries. Microsoft runtime terms:
https://visualstudio.microsoft.com/license-terms/

Native file storage can require extra disk space. Piece export is independently
hash checked; compact part files and verified bitmaps are never treated as trusted
without revalidation when resuming. No netdisk cookies or credentials are passed.

