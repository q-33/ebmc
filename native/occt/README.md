# native/occt: Open CASCADE (OCCT) Stage 0 spike

This folder is a go/no-go experiment for adding exact-solid (B-rep) CAD to the app. It builds Open CASCADE
Technology from source as static libraries, wraps a few calls in a plain-C shim library
(`libebmc_occt.dylib`, one file with OCCT linked in), and calls that library from .NET through P/Invoke.
The app now uses it for its exact-solid objects and STEP files (`MatterControlLib/DesignTools/Solid/`, see
"Use in the app" below).

Contents:

| Path | What it is |
|---|---|
| `build.sh` | Reproduces everything: clone OCCT, configure/build/install it (skipped if already installed), build the shim and the native smoke test, run the smoke test |
| `shim/ebmc_occt.h`, `shim/ebmc_occt.cpp` | The C API and its implementation |
| `shim/smoke_test.c` | Native C smoke test of the shim |
| `../../MatterControlLib/DesignTools/Solid/EbmcOcct.cs` | P/Invoke bindings plus a thin managed wrapper. The app's file; the spike (`dotnet-test/`) compiles the same file in, so there is one copy |
| `dotnet-test/Program.cs`, `OcctSpike.csproj` | Standalone net10.0 console test that prints a PASS/FAIL table |
| `dotnet-test/Directory.Build.props/.targets` | Empty stubs so the repo-root `Directory.Build.targets` does not apply to the spike |
| `_build/` | Git-ignored. OCCT source, build tree, install, shim output (about 890 MB) |

## OCCT version

- Source: https://github.com/Open-Cascade-SAS/OCCT, the official repository.
- Tag `V8_0_1`, commit `b8f597c677811d1f9f4d8a97f5ae2825c0353a42`. The lightweight tag `V8.0.1` points at
  the same commit. GitHub release "V8_0_1" was published 2026-07-30 and is not marked as a pre-release.
  It was the latest stable release when this was built (2026-11-12).
- The version string at runtime is `ebmc_occt 0.1; OCCT 8.0.1`.

## Licence obligations (plain words; not legal advice)

OCCT is licensed under the GNU LGPL 2.1 plus the "Open CASCADE exception". Both texts are in the checkout:

- `_build/src/LICENSE_LGPL_21.txt` (GNU Lesser General Public License, version 2.1)
- `_build/src/OCCT_LGPL_EXCEPTION.txt` (Open CASCADE exception, version 1.0)

The OCCT install step does not copy these files, so a packaging step must copy them from the source tree.
OCCT's own CMake prints a warning when you configure a static build: LGPL 2.1 limits static linking with
proprietary software.

What this means for this layout (the app calls one separate `libebmc_occt.dylib` that contains OCCT plus the
shim). The app's own source is under the BSD 2-Clause licence, but the app as distributed is a combined work under
the GNU AGPL v3, because the MatterSlice slicer (AGPL v3) is compiled into it (see `NOTICE.md`). The shim source in
this folder is BSD 2-Clause (header in each file); OCCT keeps its LGPL 2.1 + exception terms, and the dylib as a
whole is distributed under the LGPL 2.1. Whether that mix is acceptable is for the club to confirm; this is not
legal advice:

1. **The dylib is a work based on OCCT.** The shim is statically linked into the same binary as OCCT, so the
   dylib as a whole has to be distributable under LGPL 2.1 terms. Keep the shim source public (it is in this
   repo) and under a licence that allows this (BSD-2 or LGPL 2.1 both work). Done: the shim files carry a
   BSD 2-Clause header.
2. **The app is a "work that uses the Library".** It only loads the dylib at run time through P/Invoke, and
   the dylib is a separate file that a user can rebuild with `build.sh` and swap in. That covers the LGPL's
   "user can relink/replace the library" requirement (section 6). Replacing a file inside a signed `.app`
   breaks the bundle signature, so the user also has to re-sign it, for example with
   `codesign --force --deep --sign -`. Say so in the notice.
3. **Ship the notices.** Ship both licence texts with the app, along with OCCT's copyright notices. The
   exception also requires a "prominent notice in supporting documentation" that the software uses Open
   CASCADE Technology. The About/licences screen and NOTICE.md are the natural places.
4. **Source availability.** Provide the exact OCCT source that was used (tag and commit above) plus the build
   script and shim source. Do this by shipping the source with the binary or by a written offer (LGPL 2.1
   section 6). Hosting a source archive of the exact tag next to each release is the safest option. A link
   to upstream GitHub alone depends on a third party keeping it online.
5. Any changes made to OCCT itself (none so far) must also be published under the LGPL.

## How to rebuild

Requirements: Xcode command-line tools (Apple clang), `brew install cmake ninja`, git, and the .NET 10 SDK
for the test.

```sh
native/occt/build.sh          # full build; OCCT is skipped if _build/install already has the libs
native/occt/build.sh shim     # only the shim + smoke test
cd native/occt/dotnet-test && dotnet run -c Release
```

`JOBS`, `ARCH` (default `arm64`) and `MACOSX_DEPLOYMENT_TARGET` (default `12.0`) can be overridden. To rebuild
OCCT, delete `_build/install` (and `_build/occt` for a clean configure).

### OCCT configure options used

The module/toolkit option names were checked against `CMakeLists.txt`, `adm/cmake/vardescr.cmake` and
`adm/cmake/occt_defs_flags.cmake` of this tag.

```
-G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS_RELEASE="-O2 -DNDEBUG" -DCMAKE_C_FLAGS_RELEASE="-O2 -DNDEBUG"
-DCMAKE_CXX_FLAGS="-fvisibility=hidden -fvisibility-inlines-hidden" -DCMAKE_C_FLAGS="-fvisibility=hidden"
-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 -DCMAKE_POSITION_INDEPENDENT_CODE=ON
-DINSTALL_DIR=<_build/install> -DBUILD_LIBRARY_TYPE=Static -DBUILD_CPP_STANDARD=C++17
-DBUILD_RELEASE_DISABLE_EXCEPTIONS=OFF
-DBUILD_MODULE_{FoundationClasses,ModelingData,ModelingAlgorithms,Visualization,ApplicationFramework,DataExchange,Draw}=OFF
-DBUILD_ADDITIONAL_TOOLKITS="TKPrim TKBO TKBool TKFillet TKOffset TKMesh TKShHealing TKDESTEP"
-DBUILD_DOC_Overview=OFF -DBUILD_GTEST=OFF -DBUILD_USE_PCH=OFF -DBUILD_USE_VCPKG=OFF
-DUSE_TK=OFF -DUSE_TCL=OFF -DUSE_FREETYPE=OFF -DUSE_FREEIMAGE=OFF -DUSE_FFMPEG=OFF -DUSE_OPENVR=OFF
-DUSE_RAPIDJSON=OFF -DUSE_DRACO=OFF -DUSE_TBB=OFF -DUSE_EIGEN=OFF -DUSE_VTK=OFF -DUSE_OPENGL=OFF
-DUSE_GLES2=OFF -DUSE_XLIB=OFF -DUSE_MMGR_TYPE=NATIVE
```

All modules are switched off. Only the listed toolkits are requested, and OCCT's CMake adds their
dependencies itself.

- **STEP pulls in OCAF and parts of Visualization.** `TKDESTEP` depends on `TKXCAF`. `TKXCAF` depends on the
  OCAF toolkits `TKCAF`, `TKLCAF`, `TKCDF` and `TKVCAF`, and on the Visualization toolkits `TKService` and
  `TKV3d` (plus `TKHLR`). So 26 toolkits get built. Most of the extra code is dead-stripped when the shim is
  linked. The final dylib links no frameworks: OpenGL, AppKit, IOKit and FreeType are not needed.
- `BUILD_RELEASE_DISABLE_EXCEPTIONS=OFF`. OCCT's default (`ON`) defines `No_Exception` in Release builds,
  which turns range and null checks into undefined behaviour. With `OFF`, bad input raises `Standard_Failure`,
  and the shim turns that into an error code instead of a crash.
- `-O2` was used instead of OCCT's default `-O3`, as the brief asked. Performance was not compared.

### Build facts (MacBook, Apple silicon, 14 cores, shared with another build)

| | |
|---|---|
| OCCT build + install, `ninja -j14` | 213 s wall (2222 s user CPU), 4609 build steps, 0 compiler warnings in the log |
| Installed static libs (`_build/install/lib`) | 26 `libTK*.a`, 142 MB (largest: TKDESTEP 24 MB, TKGeomAlgo 12.9 MB, TKBool 11.8 MB) |
| Installed headers | 36 MB |
| Source checkout / build tree | 346 MB / 334 MB |
| Shim compile + link + smoke test | about 2 s |
| `libebmc_occt.dylib` | 24.9 MB unstripped, 19.0 MB after `strip -x`, 8.7 MB gzipped. arm64, minos 12.0 |
| Exported symbols (`nm -gU`) | only the 19 `_ebmc_*` functions |
| Dependencies (`otool -L`) | `@rpath/libebmc_occt.dylib`, `/usr/lib/libc++.1.dylib`, `/usr/lib/libSystem.B.dylib` |

## C API summary (see `shim/ebmc_occt.h` for the full contract)

- Every function returns `int` status: `EBMC_OK` (0) or `EBMC_ERR_*`. The codes are NULL_ARG, INVALID_ARG,
  INDEX, NOT_DONE, OCCT (Standard_Failure caught), STD, UNKNOWN and IO. `ebmc_occt_last_error()` returns a
  thread-local message. All C++ exceptions are caught inside the library.
- Shapes are opaque `ebmc_shape*` that the caller owns and releases with `ebmc_shape_free`. Operations never
  modify their inputs.
- Sub-shape indices are 0-based positions in `TopExp::MapShapes(shape, TopAbs_FACE/EDGE/VERTEX,
  TopTools_IndexedMapOfShape)`. That order is deterministic, and the STEP round trip preserved the counts.
- Functions: `ebmc_occt_version`, `ebmc_make_box`, `ebmc_make_cylinder`, `ebmc_translate`, `ebmc_boolean`
  (fuse/cut/common), `ebmc_shape_counts`, `ebmc_edge_info` (endpoints, length, degenerate/closed flags),
  `ebmc_fillet_edges`, `ebmc_chamfer_edges`, `ebmc_volume`, `ebmc_area`, `ebmc_bbox`, `ebmc_is_valid`,
  `ebmc_tessellate` + `ebmc_mesh_free`, `ebmc_write_step`, `ebmc_read_step`.
- `ebmc_tessellate` meshes a copy (`BRepBuilderAPI_Copy`) so the input, and every shape sharing its faces,
  stays unmodified. It returns:
  - a vertex array (doubles), with vertices on B-rep edges and vertices welded topologically
  - triangle indices, wound counter-clockwise from outside
  - a face id for each triangle
  - one polyline for each non-degenerate edge, with that edge's id
- Fillet and chamfer also run `BRepCheck_Analyzer` on the result, because OCCT can report success and still
  return an invalid solid. An invalid result is returned as `EBMC_ERR_NOT_DONE`.
- STEP read/write are serialised by a mutex, because the STEP translator uses global parameter tables.
  OCCT's default message printers are removed on first use, so the library prints nothing to stdout.
- OCCT's signal handler (`OSD::SetSignal`) is never installed, so it does not interfere with .NET's own
  signal handling.

## Test results

Native smoke test (`build.sh`): PASSED (box, fillet, cut, mesh, STEP round trip, two error paths).

.NET test: `dotnet run -c Release` in `dotnet-test/`. .NET 10.0.12, macOS 27.0 arm64. The dylib was copied
next to the binary and found by normal P/Invoke probing, with no resolver. The run passed 13 of 13 tests,
repeated 3 times with the same results (timings vary by about 3%).

| Test | Result | Measured |
|---|---|---|
| load library + version | PASS | `ebmc_occt 0.1; OCCT 8.0.1` |
| box 20×20×20 | PASS | V = 7999.999999999998, 6 faces / 12 edges / 8 vertices |
| fillet one edge r=3 (edge picked by geometry via `ebmc_edge_info`) | PASS | V = 7961.37166941154, expected 8000 − (1−π/4)·9·20 = 7961.371669411541 (rel err 1e-16), 7 faces |
| chamfer one edge d=2 | PASS | V = 7959.999999999998 (expected 7960), 7 faces |
| cut Ø5 through-hole | PASS | V = 7568.6725877128165 (rel err 0), valid |
| tessellate 0.05 mm / 0.5° | PASS | 3608 vertices, 7216 triangles, 18/18 edge polylines, 98 ms (first call). All face ids valid, every face meshed, closed (0 open, 0 non-manifold edges), consistently oriented, 0 degenerate triangles. Mesh volume 7568.6734 (rel err 1.05e-7) |
| tessellate leaves input untouched | PASS | coarse 196 / fine 7216 / coarse again 196 triangles |
| STEP write + read | PASS | 22.6 KB file, volume rel diff 6e-16, F/E/V 8/18/12 both ways, valid |
| error: fillet r=25 on 20 mm box | PASS | code 4, "fillet failed (radius too large ...); 1 faulty contour(s)". No crash; library still usable |
| error: bad edge index / missing STEP / negative size | PASS | codes 3 / 8 / 2, each with a message |
| loop 200× box + fillet + tessellate (0.05 mm/0.5°) | PASS | 8.3 to 8.5 ms per cycle, 1452 triangles per cycle. RSS after 50/100/150/200 cycles: 83.6/85.6/85.6/85.8 MB (flat) |
| two threads, independent shapes (40 cycles each of cut + volume + mesh, STEP every 10th, error path every cycle) | PASS | 3.9 s, identical volumes and triangle counts on both threads, no errors |
| two threads, shapes derived from one shared box (informational) | PASS | no errors, correct volumes. This shares one `ebmc_shape` across threads, which the header does not promise to support |

Signed-bundle check: the test app was published self-contained (`osx-arm64`) into a temporary
`Foo.app/Contents/MacOS` with an Info.plist. It was signed the way `Tools/macOS/build-app.sh` signs the real
app (`codesign --force --deep --sign -`, no hardened runtime). `codesign --verify --deep --strict` passed,
and the app passed 13/13 both when run directly and through `open`. With hardened runtime plus ad-hoc
signing, .NET's own `libhostfxr.dylib` already fails to load ("different Team IDs"), so this has nothing to
do with OCCT. Adding `com.apple.security.cs.disable-library-validation` (or a real Developer ID signature)
fixes it, and then all 13 tests pass.

`EbmcOcct.cs` also compiles unchanged for `netstandard2.0` and `net6.0` (checked in a throwaway project). It
uses only `[DllImport]`, `SafeHandle`, `Marshal.Copy`, `Marshal.PtrToStructure<T>` and explicit UTF-8
string marshalling.

## Known limits

- Thread safety was tested only as described above: two threads on different shapes. Do not use or free the
  same `ebmc_shape` from two threads at once.
- Only the API surface needed for the spike exists. There are no transforms other than translate, no
  sketches/extrudes, no other primitives, and no face picking by ray.
- Edge welding in the mesh assumes both faces' polygons of an edge have the same node count, which is
  BRepMesh's normal behaviour. If they ever differ, the mesh would show open edges rather than crash. The
  mesh test checks for exactly that.
- Only arm64 macOS was built and tested.
- Fillet failures come back as a generic NOT_DONE with a faulty-contour count. There is no per-edge
  diagnosis yet.
- The 19 MB stripped dylib carries some OCAF/Visualization code that STEP drags in. A custom STEP-only path
  might shrink it; that was not tried.

## Use in the app (shim 0.2)

- `build.sh shim` (and `all`) now also **stages** a stripped copy at `dist/<rid>/libebmc_occt.dylib`
  (`dist/` is git-ignored; `build.sh stage` does only that step). `MatterControl.CrossPlatform.csproj` ships it as
  `native/osx-arm64/libebmc_occt.dylib` only when that file exists, and `Tests/EBMC.Tests` copies it next to the
  test assembly the same way. Without it the app builds and runs with solid modelling hidden.
- Shim 0.2 adds `ebmc_edge_geom` (curve type, midpoint, tangent, circle radius: edges are picked by geometry),
  `ebmc_transform` (general 3x4 affine matrix; similitudes exactly, anything else through
  `BRepBuilderAPI_GTransform`) and `ebmc_compound`. The app requires 0.2 or newer (`SolidKernel.RequiredVersion`).
  The smoke test covers the new functions; the .NET spike test still passes 13/13 against 0.2.
- The bindings live in `MatterControlLib/DesignTools/Solid/EbmcOcct.cs` (internal, with the 0.2 calls); the spike
  `dotnet-test/` links that file instead of keeping its own copy (its `bin/` and `obj/` are git-ignored).

## Other platforms (not done, for planning)

- **x86_64 / universal macOS:** run `ARCH=x86_64 build.sh` into a second `_build`, or configure OCCT with
  `-DCMAKE_OSX_ARCHITECTURES="arm64;x86_64"` (single-config Ninja handles fat static libs). Then link the
  shim with both `-arch` flags, or `lipo -create` two dylibs. That roughly doubles the build time and the
  dylib size (about 38 MB stripped as a universal binary, estimated, not measured).
- **Windows:** use Visual Studio 2022 (MSVC v143) plus CMake and Ninja in a "x64 Native Tools" prompt, with
  the same CMake options. OCCT turns `/EHa` and `/fp:precise` on itself. Build the shim as `ebmc_occt.dll`
  with `EBMC_OCCT_BUILD` defined (the header already switches to `__declspec(dllexport)`), static OCCT libs,
  and `/MD` runtime (the MSVC redistributable is then a dependency), or `/MT` to avoid it. P/Invoke finds
  `ebmc_occt.dll` with the same `DllImport("ebmc_occt")`.
- **Linux:** use GCC 9+ or clang, plus cmake and ninja, with the same options. Link the shim with
  `-Wl,--version-script` (exporting `ebmc_*`), `-Wl,--gc-sections`, and either `-static-libstdc++` or a
  libstdc++ dependency. It produces `libebmc_occt.so`. Build on the oldest glibc you intend to support.

## Next steps

1. Decide go/no-go. Stage 0 found no blocker on macOS arm64.
2. Settle licence handling: notice text, shipping licence files, a source archive per release, and the shim
   licence header.
3. Grow the C API toward what the app needs (transforms, extrude/revolve of sketches, more primitives, shell,
   face/edge picking support, mesh normals or per-face smoothing), keeping the same ABI rules.
4. Wire the build into CI for macOS arm64 and x86_64, then Windows and Linux, and cache `_build/install`
   (OCCT changes rarely).
5. Add the dylib to `Tools/macOS/build-app.sh` packaging. It goes in `Contents/MacOS` next to the app (found
   by default probing) and is signed with the bundle.
