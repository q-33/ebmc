#!/bin/zsh
# Copyright (c) 2026, East Bay Makers Club and contributors
# SPDX-License-Identifier: BSD-2-Clause
#
# Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
# following conditions are met:
# 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
#    disclaimer.
# 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
#    disclaimer in the documentation and/or other materials provided with the distribution.
# THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
# INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
# DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
# SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
# SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
# WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
# OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
#
# This file is EBMC's own wrapper code. The library built from it (libebmc_occt) also contains Open CASCADE
# Technology, which is under the GNU LGPL 2.1 with the Open CASCADE exception; that library as a whole is
# distributed under those terms (see native/occt/README.md and StaticData/License/OpenCASCADE.txt).
#
# Builds Open CASCADE Technology (static, lean) and the EBMC C shim libebmc_occt.dylib on macOS arm64.
#
#   native/occt/build.sh            # everything: fetch OCCT, build+install it (skipped if already installed),
#                                   # build the shim dylib and the native smoke test, run the smoke test
#   native/occt/build.sh occt       # only fetch + build + install OCCT
#   native/occt/build.sh shim       # only the shim + smoke test (needs the OCCT install), then stage it
#   native/occt/build.sh stage      # only copy a stripped dylib to dist/<rid>/ for the app and test builds
#
# Everything large goes into native/occt/_build/ (git-ignored):
#   _build/src      OCCT source checkout (shallow clone of OCCT_TAG)
#   _build/occt     OCCT CMake/Ninja build tree
#   _build/install  OCCT install (headers + static libs)
#   _build/shim     libebmc_occt.dylib, smoke test binary
# and the stripped copy the app ships goes to native/occt/dist/<rid>/ (also git-ignored).
#
# Requirements: Xcode command line tools (clang), cmake, ninja (brew install cmake ninja), git.
# Environment overrides: JOBS (parallel jobs, default all cores), MACOSX_DEPLOYMENT_TARGET (default 12.0),
# ARCH (default arm64).
set -euo pipefail

HERE="${0:A:h}"
B="$HERE/_build"
SRC="$B/src"
OBJ="$B/occt"
INST="$B/install"
SHIMOUT="$B/shim"

OCCT_REPO="https://github.com/Open-Cascade-SAS/OCCT.git"
OCCT_TAG="V8_0_1"                                    # == V8.0.1, GitHub release "V8_0_1" (2026-07-30)
OCCT_COMMIT="b8f597c677811d1f9f4d8a97f5ae2825c0353a42"

JOBS="${JOBS:-$(sysctl -n hw.ncpu)}"
export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
ARCH="${ARCH:-arm64}"

# Toolkits we ask for explicitly; CMake adds their dependencies (see README: STEP drags in TKXCAF,
# which depends on OCAF (TKCAF/TKLCAF/TKCDF/TKVCAF) and on Visualization (TKService/TKV3d)).
OCCT_TOOLKITS="TKPrim TKBO TKBool TKFillet TKOffset TKMesh TKShHealing TKDESTEP"

phase="${1:-all}"

fetch_occt() {
	if [[ -d "$SRC/.git" ]]; then
		local have; have="$(git -C "$SRC" rev-parse HEAD)"
		if [[ "$have" != "$OCCT_COMMIT" ]]; then
			echo "error: $SRC is at $have, expected $OCCT_COMMIT ($OCCT_TAG). Remove it and re-run." >&2
			exit 1
		fi
		echo "== OCCT source present at $OCCT_TAG ($have)"
		return
	fi
	mkdir -p "$B"
	echo "== cloning OCCT $OCCT_TAG"
	git clone --depth 1 --branch "$OCCT_TAG" "$OCCT_REPO" "$SRC"
	local have; have="$(git -C "$SRC" rev-parse HEAD)"
	if [[ "$have" != "$OCCT_COMMIT" ]]; then
		echo "error: tag $OCCT_TAG resolved to $have, expected $OCCT_COMMIT" >&2
		exit 1
	fi
}

build_occt() {
	if [[ -f "$INST/lib/libTKDESTEP.a" && -f "$INST/lib/libTKFillet.a" && -f "$INST/lib/libTKMesh.a" ]]; then
		echo "== OCCT already installed in $INST (delete it to rebuild)"
		# the install must come from the recorded commit (the licence notice names it)
		if [[ -d "$SRC/.git" ]]; then
			local have; have="$(git -C "$SRC" rev-parse HEAD)"
			if [[ "$have" != "$OCCT_COMMIT" ]]; then
				echo "error: $SRC is at $have, expected $OCCT_COMMIT ($OCCT_TAG); rebuild OCCT from the recorded commit" >&2
				exit 1
			fi
		fi
		return
	fi
	fetch_occt
	echo "== configuring OCCT"
	cmake -S "$SRC" -B "$OBJ" -G Ninja \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_CXX_FLAGS_RELEASE="-O2 -DNDEBUG" \
		-DCMAKE_C_FLAGS_RELEASE="-O2 -DNDEBUG" \
		-DCMAKE_CXX_FLAGS="-fvisibility=hidden -fvisibility-inlines-hidden" \
		-DCMAKE_C_FLAGS="-fvisibility=hidden" \
		-DCMAKE_OSX_ARCHITECTURES="$ARCH" \
		-DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" \
		-DCMAKE_POSITION_INDEPENDENT_CODE=ON \
		-DINSTALL_DIR="$INST" \
		-DBUILD_LIBRARY_TYPE=Static \
		-DBUILD_CPP_STANDARD=C++17 \
		-DBUILD_RELEASE_DISABLE_EXCEPTIONS=OFF \
		-DBUILD_MODULE_FoundationClasses=OFF \
		-DBUILD_MODULE_ModelingData=OFF \
		-DBUILD_MODULE_ModelingAlgorithms=OFF \
		-DBUILD_MODULE_Visualization=OFF \
		-DBUILD_MODULE_ApplicationFramework=OFF \
		-DBUILD_MODULE_DataExchange=OFF \
		-DBUILD_MODULE_Draw=OFF \
		-DBUILD_ADDITIONAL_TOOLKITS="$OCCT_TOOLKITS" \
		-DBUILD_DOC_Overview=OFF \
		-DBUILD_GTEST=OFF \
		-DBUILD_USE_PCH=OFF \
		-DBUILD_USE_VCPKG=OFF \
		-DUSE_TK=OFF -DUSE_TCL=OFF -DUSE_FREETYPE=OFF -DUSE_FREEIMAGE=OFF -DUSE_FFMPEG=OFF \
		-DUSE_OPENVR=OFF -DUSE_RAPIDJSON=OFF -DUSE_DRACO=OFF -DUSE_TBB=OFF -DUSE_EIGEN=OFF \
		-DUSE_VTK=OFF -DUSE_OPENGL=OFF -DUSE_GLES2=OFF -DUSE_XLIB=OFF \
		-DUSE_MMGR_TYPE=NATIVE
	echo "== building OCCT with $JOBS jobs"
	local t0=$SECONDS
	cmake --build "$OBJ" -- -j"$JOBS"
	cmake --install "$OBJ"
	echo "== OCCT build+install took $((SECONDS - t0)) s"
}

build_shim() {
	if [[ ! -f "$INST/lib/libTKDESTEP.a" ]]; then
		echo "error: OCCT is not installed in $INST; run '$0 occt' first" >&2
		exit 1
	fi
	mkdir -p "$SHIMOUT"
	local inc="$INST/include/opencascade"
	[[ -d "$inc" ]] || inc="$INST/include"
	local cxxflags=(-std=c++17 -O2 -DNDEBUG -arch "$ARCH" -mmacosx-version-min="$MACOSX_DEPLOYMENT_TARGET"
		-fPIC -fvisibility=hidden -fvisibility-inlines-hidden -Wall -I"$inc")

	echo "== compiling shim"
	clang++ "${cxxflags[@]}" -c "$HERE/shim/ebmc_occt.cpp" -o "$SHIMOUT/ebmc_occt.o"

	# Export only ebmc_* (belt and braces: the OCCT archives are also built with hidden visibility).
	echo "_ebmc_*" > "$SHIMOUT/exports.txt"
	local libs=("$INST"/lib/libTK*.a)
	echo "== linking libebmc_occt.dylib against ${#libs[@]} OCCT static libs"
	clang++ -dynamiclib -arch "$ARCH" -mmacosx-version-min="$MACOSX_DEPLOYMENT_TARGET" \
		-o "$SHIMOUT/libebmc_occt.dylib" "$SHIMOUT/ebmc_occt.o" "${libs[@]}" \
		-install_name @rpath/libebmc_occt.dylib \
		-Wl,-exported_symbols_list,"$SHIMOUT/exports.txt" \
		-Wl,-dead_strip \
		-Wl,-dead_strip_dylibs

	echo "== building smoke test"
	clang -arch "$ARCH" -mmacosx-version-min="$MACOSX_DEPLOYMENT_TARGET" -O1 -Wall \
		-I"$HERE/shim" "$HERE/shim/smoke_test.c" -L"$SHIMOUT" -lebmc_occt \
		-Wl,-rpath,@loader_path -o "$SHIMOUT/smoke_test"
	echo "== running smoke test"
	(cd "$SHIMOUT" && ./smoke_test)

	echo "== exported symbols:"
	nm -gU "$SHIMOUT/libebmc_occt.dylib"
	echo "== dependencies:"
	otool -L "$SHIMOUT/libebmc_occt.dylib"
	ls -l "$SHIMOUT/libebmc_occt.dylib"
}

# Copies a stripped dylib to dist/<rid>/, where the app build (MatterControl.CrossPlatform.csproj) and the test
# project (Tests/EBMC.Tests) pick it up when it exists. dist/ is git-ignored: the binary is never committed.
stage_shim() {
	if [[ ! -f "$SHIMOUT/libebmc_occt.dylib" ]]; then
		echo "error: $SHIMOUT/libebmc_occt.dylib not built; run '$0 shim' first" >&2
		exit 1
	fi
	local rid
	case "$ARCH" in
		arm64) rid="osx-arm64" ;;
		x86_64) rid="osx-x64" ;;
		*) echo "error: no runtime id for ARCH=$ARCH" >&2; exit 1 ;;
	esac
	local dest="$HERE/dist/$rid"
	mkdir -p "$dest"
	cp "$SHIMOUT/libebmc_occt.dylib" "$dest/libebmc_occt.dylib.tmp"
	strip -x "$dest/libebmc_occt.dylib.tmp"
	mv "$dest/libebmc_occt.dylib.tmp" "$dest/libebmc_occt.dylib"
	echo "== staged $(ls -l "$dest/libebmc_occt.dylib" | awk '{print $5}') bytes at $dest/libebmc_occt.dylib"
}

case "$phase" in
	all)   build_occt; build_shim; stage_shim ;;
	occt)  build_occt ;;
	shim)  build_shim; stage_shim ;;
	stage) stage_shim ;;
	*) echo "usage: $0 [all|occt|shim|stage]" >&2; exit 2 ;;
esac
