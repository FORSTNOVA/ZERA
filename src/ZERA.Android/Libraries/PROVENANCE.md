# Android native BitTorrent dependencies

## Local image decoding

ZXing.Net **0.16.11**, `lib/netstandard2.0/zxing.dll`, unmodified from the official NuGet package:
https://api.nuget.org/v3-flatcontainer/zxing.net/0.16.11/zxing.net.0.16.11.nupkg .
SHA-256: `f3b823b6fd6492525a7547989056883def5d43be1e12c4f63fa54df73e3c5cfc`.
Upstream source: https://github.com/micjahn/ZXing.Net/tree/v0.16.11.0 .
Apache-2.0; images are decoded entirely on the device, without uploading them.

## BitTorrent

Pinned unmodified upstream libtorrent4j **2.1.0-39**, MIT licensed.
Upstream source and Android build scripts:
https://github.com/aldenml/libtorrent4j/tree/v2.1.0-39

Downloaded from Maven Central over HTTPS:

- https://repo.maven.apache.org/maven2/org/libtorrent4j/libtorrent4j/2.1.0-39/libtorrent4j-2.1.0-39.jar
- https://repo.maven.apache.org/maven2/org/libtorrent4j/libtorrent4j-android-arm64/2.1.0-39/libtorrent4j-android-arm64-2.1.0-39.jar
- https://repo.maven.apache.org/maven2/org/libtorrent4j/libtorrent4j-android-x86_64/2.1.0-39/libtorrent4j-android-x86_64-2.1.0-39.jar

Native libraries were extracted from the architecture-specific JARs without changes.

| File | SHA-256 |
| --- | --- |
| `libtorrent4j-2.1.0-39.jar` | `f3cc58c318c7bcb680c9e20fd69695dd149602f33ccd953f932790dc771a4e35` |
| `../jni/arm64-v8a/libtorrent4j.so` | `6428158f8e012f048f85eb790317e55cf8557ba4018d53e543e86dd9c25cef2f` |
| `../jni/x86_64/libtorrent4j.so` | `5c48c09033912792451ebe8d0c495b9655ce44c921bed8c78b2e08a7c1d6a399` |

The JNI library contains upstream libtorrent and its native build dependencies;
their source revisions and build options are specified by the pinned upstream
`swig` build scripts. libtorrent is BSD-3-Clause, Boost is BSL-1.0, and OpenSSL
is Apache-2.0. License texts are provided alongside the bundled library.

`TorrentBridge.java`, `AssetClient.java`, and `CommandQueue.java` are ZERA code
and distributed under the repository's GPL-3.0 license.
