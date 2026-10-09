# Native FFmpeg codec evidence audit

This audit identifies the packaged FFmpeg video plugin and its official published
source artifacts. It does not establish complete native redistribution compliance,
legal certainty, or a clean-Windows execution result. Overall gate remains OPEN.
The existing dev3 ZIP and earlier success/failure evidence remain unchanged.

## Binary-to-source chain

The packaged `opencv_videoio_ffmpeg500_64.dll` has SHA256
`7AABFF029D1FA47CF56EDE4DBBE05DC62DA3208F07CD16471D4C966A33F9AA31`
and MD5 `a821a1135251859655090c795af05789`. An independently downloaded
[official OpenCV binary](https://raw.githubusercontent.com/opencv/opencv_3rdparty/06dc20cad65dc7fcf784f70c95d46750520889a7/ffmpeg/opencv_videoio_ffmpeg_64.dll)
has the identical SHA256. The packaged filename differs; the bytes match.
The [pinned OpenCV downloader](https://github.com/opencv/opencv/blob/40738fb16ceddb5fb3fea747585f7ce6abb0605b/3rdparty/ffmpeg/ffmpeg.cmake)
records that binary commit and expected MD5. MD5 alone is not used as authenticity
proof; the independently checked SHA256 binds these bytes to the official artifact.

NuGet OpenCvSharp5.runtime.win 5.0.0.20261003 declares repository commit
`d8fc8914e09b43fe6e2ec71c1e376aef5188acf1`. Its OpenCV submodule is
`40738fb16ceddb5fb3fea747585f7ce6abb0605b`, matching the packaged native library's
runtime GetBuildInformation version-control field `40738fb`.
OpenCvSharpExtern.dll SHA256 is
`0EC75DB779FD13D528AF949EB135225CF155B534DA40601E179161740A2357BB`.
The native library reports OpenCV 5.0.0, FFmpeg prebuilt libraries avcodec
61.19.100, avformat 61.7.100, avutil 59.39.100 and swscale 8.3.100.

The official [source commit](https://github.com/opencv/opencv_3rdparty/commit/664c0098dcb47b361f20c1d6a518653c23f5f2b5)
has the exact binary commit as its parent. Its commit message explicitly names
that binary commit. Its published source archives contain FFmpeg n7.1, libvpx
v1.16.0, AOM v3.14.1, the OpenCV video plugin sources and build scripts.
The plugin's own embedded build information reports OpenCV 5.0.0-pre, build
2026-06-03, MinGW g++ (reported version 13.0.0), Linux host and Windows x86_64
output. This differs from the main MSVC OpenCvSharpExtern build, as expected for
a separately supplied plugin. The plugin OpenCV source submodule is
`a0a660fcb1e58a295e6caa6aee64ed4d369b0181` in the official binary/source tree.

## Codec identities and configuration

| Component | Binary evidence | Official source/build record | Scope |
|---|---|---|---|
| FFmpeg | Library ABI versions above; full embedded configure line | Source archive `ffmpeg-src-n7.1.tar.xz` and pinned recipe | Statically linked into the separately loaded plugin DLL |
| libvpx | VP8/VP9 encoder/decoder strings explicitly report v1.16.0; embedded configure line | `libvpx-src-v1.16.0.tar.xz` | Static VP8/VP9 codec library |
| AOM | AV1 encoder/decoder strings explicitly report 3.14.1; embedded CMake configuration | `aom-src-v3.14.1.tar.xz` | Static AV1 codec library |
| OpenH264 | Wrapper identifying dynamically loaded OpenH264; FFmpeg configure enables libopenh264 | Recipe selects v2.5.0 public API headers and compiles a dynamic-load wrapper | OpenH264 implementation DLL is NOT included in the inspected package; no external codec version claim |

FFmpeg's actual embedded configure line enables static linkage, w32threads,
libopenh264, libvpx and libaom; disables filters, bsfs, programs, debug, CUDA,
CUVID and NVENC; targets mingw32 x86_64. It contains neither `--enable-gpl` nor
`--enable-nonfree`. The source recipe agrees with this line, and the binary
contains a libswscale LGPL 2.1-or-later notice. These are separate factual checks.
Absence of flags or the main OpenCV `Non-free algorithms: NO` field alone does
not establish the whole binary's license/source obligations.

The official [OpenCV FFmpeg readme](https://github.com/opencv/opencv_3rdparty/blob/06dc20cad65dc7fcf784f70c95d46750520889a7/readme.txt)
identifies the Windows plugin as LGPL and documents dynamic loading of OpenH264.
The verified source archives include FFmpeg's LGPL 2.1 text and license inventory,
libvpx's BSD-style copyright/license plus PATENTS, and AOM's BSD-style license
plus Alliance for Open Media Patent License 1.0. Exact copies are provided in

ative-codec-notices/`. These notices do not assert patent clearance or replace
component-specific obligations. [FFmpeg's own guidance](https://ffmpeg.org/legal.html)
requires attention to exactly corresponding sources and build configuration;
it does not make a top-level NuGet license declaration sufficient.

## Precise remaining evidence gaps

1. The upstream `openh264-api-headers-v2.5.0.tar.xz` is only 32 bytes and contains
   zero tar entries. The upstream archive script uses `codec/api/svc`, while the
   official v2.5.0 repository has `codec/api/wels`. The exact upstream source
   package is therefore incomplete for the API headers used by the wrapper.
   This pipeline preserves that archive as evidence and separately supplies all
   four public API headers and LICENSE from official v2.5.0 commit
   `8c7008aeb6335e7d36ab0d9a023a63f82a8eaac0`. This supplement is labeled;
   it does not prove the original build used unmodified copies of those headers.
2. The plugin recipe uses static libgcc/libstdc++ and MinGW support; the binary
   contains a winpthreads source path. Docker uses Ubuntu 24.04 apt packages without
   exact compiler/runtime package pins. No exact toolchain package manifest,
   associated source/notice bundle, or provenance for every static runtime object
   is provided by the inspected source record. The plugin OpenCV archive contains
   only LICENSE and four FFmpeg headers, while the recipe also builds static
   core/imgproc/videoio. The full OpenCV commit is identified above, but its
   complete source tree is not bundled here. Compiler version text is not that
   missing manifest. No native rebuild or relink equivalence test was run.
3. The complete main OpenCvSharpExtern dependency tree (including image codecs,
   IPP and other native modules) is outside this bounded FFmpeg-plugin audit.
   Its full component source/notice audit remains OPEN. Main native build information
   is preserved rather than conflated with the separately built FFmpeg plugin.
4. Legal compliance approval and code signing were not requested or established.
   Clean-Windows execution remains UNRUN. No system features, installations,
   reboots or VM purchases were performed.

## Reusable package integration


`native-codec-evidence.json` pins the known native hashes, upstream commits,
source archive hashes/lengths and supplementary header hashes/lengths.
`Add-NativeCodecEvidence.ps1` refuses an unreviewed native binary, fetches only
pinned official source files (or verifies a supplied cache), copies notices and
writes a factual receipt. Future Publish-Portable runs include that source bundle
before generating package checksums. Failure stops publication; a package upgrade
cannot inherit this audit automatically. Existing dev3 delivery artifacts are
unchanged and do not contain this newly added source bundle.
`Get-NativeCodecAudit.ps1` reproduces the installed-host build-info/string probe
without production source changes. Its native load is audit evidence, not a clean
Windows proof. The evidence gate states CODEC_BINARY_AND_PUBLISHED_SOURCE_IDENTIFIED;
the complete native source/compliance gate stays OPEN.



