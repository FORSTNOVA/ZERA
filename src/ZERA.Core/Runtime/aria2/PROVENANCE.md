# Bundled aria2 1.37.0 (Windows x64 build1)

Retrieved 2026-10-18 from the upstream release, without modifying the executable.

- Release: https://github.com/aria2/aria2/releases/tag/release-1.37.0
- Binary archive: https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0-win-64bit-build1.zip
- Binary archive SHA256: `67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288`
- Executable SHA256: `be2099c214f63a3cb4954b09a0becd6e2e34660b886d4c898d260febfe9d70c2`
- Source: https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0.tar.xz
- Source archive SHA256: `60a420ad7085eb616cb6e2bdf0a7206d68ff3d37fb5a956dc44242eb2f79b66b`

The upstream executable is GPL-2.0-or-later and statically linked. See COPYING,
AUTHORS, LICENSE.OpenSSL, README.mingw and the source archive for licensing,
configuration, and build instructions. No changes or local patches were applied.
The matching static dependency source archives, with their license files, are
provided in source/. Dependencies and versions are those declared by upstream's
README.mingw and verified with `aria2c --version`.

Dependency download locations:

- GMP 6.3.0: https://ftp.gnu.org/gnu/gmp/gmp-6.3.0.tar.xz
- Expat 2.5.0: https://github.com/libexpat/libexpat/releases/download/R_2_5_0/expat-2.5.0.tar.xz
- zlib 1.3: https://zlib.net/fossils/zlib-1.3.tar.gz
- c-ares 1.19.1: https://github.com/c-ares/c-ares/releases/download/cares-1_19_1/c-ares-1.19.1.tar.gz
- libssh2 1.11.0: https://www.libssh2.org/download/libssh2-1.11.0.tar.xz
- SQLite 3.43.1: https://www.sqlite.org/2023/sqlite-amalgamation-3430100.zip

The Windows executable requires no additional DLLs. Its upstream README notes
that TLS 1.3 is unavailable in this build; this may affect trackers requiring it.
ZERA communicates with the engine via authenticated loopback JSON-RPC only.
