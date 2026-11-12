# DWG engine source and licenses

The Windows x64 DWG engine is the unmodified `dwg2dxf.exe` and `libredwg-0.dll` from the [LibreDWG 0.14 release](https://github.com/LibreDWG/libredwg/releases/tag/0.14). The executable runs as a separate process. LibreDWG is GPL-3.0-or-later. See `COPYING` and `source/libredwg-0.14.tar.xz` for its license and source. The source includes the build system and installation instructions. The adjacent `libredwg-0.14-windows-workflow.yml` records upstream's Windows build configuration at the 0.14 tag.

| Shipped DLLs | MSYS2 MinGW64 package | License and source in this folder |
| --- | --- | --- |
| `libiconv-2.dll` | libiconv 1.18-1 | LGPL-2.1-or-later library, `licenses/libiconv`, `source/mingw-w64-libiconv-1.18-1.src.tar.zst` |
| `libpcre2-8-0.dll`, `libpcre2-16-0.dll` | PCRE2 10.47-1 | BSD-3-Clause with upstream notices, `licenses/pcre2`, `source/mingw-w64-pcre2-10.47-1.src.tar.zst` |

The DLLs match the official MSYS2 binaries byte for byte. Each MSYS2 source package contains the upstream source archive and its `PKGBUILD` recipe, plus the patches used by MSYS2 where applicable. Extract it in an MSYS2 MinGW64 environment and use the included recipe to build the dependency. Use LibreDWG's instructions and the Windows workflow for LibreDWG itself. TOPDF uses the DLLs via dynamic linking. Users can replace them in the same directory with ABI-compatible builds.

The installed `engines/vendor/libredwg` directory contains these materials alongside the binaries. [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0.html) · [GNU LGPL v2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html) · [MSYS2 source packages](https://repo.msys2.org/mingw/sources/)
