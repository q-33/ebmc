#!/bin/bash
#
# Builds "EBMC.app" for the Mac this script runs on
# (Apple silicon or Intel) and puts it in dist/ at the top of the repository.
#
#   Tools/macOS/build-app.sh          build the app
#   Tools/macOS/build-app.sh --run    build it, then open it
#
# Needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
#
# The app is self contained (it carries the .NET runtime and StaticData), so it
# can be copied to another Mac with the same kind of processor. It is signed
# "ad hoc", which is enough to run where it was built. A copy that travels by
# download or AirDrop is quarantined by macOS: the person receiving it has to
# right-click the app and choose Open the first time, or you need an Apple
# Developer ID to sign and notarize it.

set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
	echo "This script builds a macOS application and has to run on a Mac." >&2
	exit 1
fi

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO/MatterControl.CrossPlatform/MatterControl.CrossPlatform.csproj"
APP_NAME="EBMC"
BUNDLE_ID="org.eastbaymakersclub.mattercontrol"
DIST="$REPO/dist"
APP="$DIST/$APP_NAME.app"
PUBLISH="$DIST/publish"

if ! command -v dotnet >/dev/null 2>&1; then
	echo "The .NET SDK was not found. Install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0" >&2
	exit 1
fi

if [[ ! -f "$REPO/Submodules/agg-sharp/agg/Agg.csproj" ]]; then
	echo "The submodules are missing. Run this in the repository first:" >&2
	echo "    git submodule update --init --recursive" >&2
	exit 1
fi

case "$(uname -m)" in
	arm64) RID="osx-arm64" ;;
	x86_64) RID="osx-x64" ;;
	*) echo "Unsupported processor: $(uname -m)" >&2; exit 1 ;;
esac

echo "Building for $RID ..."
rm -rf "$PUBLISH" "$APP"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$PUBLISH" -nologo -v quiet

echo "Assembling $APP_NAME.app ..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/" "$APP/Contents/MacOS/"

# Data files belong in Resources; the application looks for StaticData there
mv "$APP/Contents/MacOS/StaticData" "$APP/Contents/Resources/StaticData"

# Libraries for other systems are dead weight inside this app
rm -f "$APP/Contents/MacOS/glfw3.dll" "$APP/Contents/MacOS/libglfw.3.dylib"
find "$APP/Contents/MacOS/native" -mindepth 1 -maxdepth 1 -type d ! -name "$RID" -exec rm -rf {} +

VERSION="$(git -C "$REPO" describe --tags --always 2>/dev/null || echo "0")"

# The application icon, scaled from the one MatterControl already uses. Optional.
ICON_KEY=""
ICON_SOURCE="$REPO/StaticData/Icons/application.png"
ICONSET="$DIST/MatterControl.iconset"
if [[ -f "$ICON_SOURCE" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
	rm -rf "$ICONSET"
	mkdir -p "$ICONSET"
	for size in 16 32 128 256 512; do
		sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
		double=$((size * 2))
		sips -z "$double" "$double" "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
	done
	if iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/MatterControl.icns"; then
		ICON_KEY="<key>CFBundleIconFile</key><string>MatterControl</string>"
	fi
	rm -rf "$ICONSET"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleName</key><string>$APP_NAME</string>
	<key>CFBundleDisplayName</key><string>$APP_NAME</string>
	<key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
	<key>CFBundleExecutable</key><string>MatterControl</string>
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
	<key>CFBundleShortVersionString</key><string>$VERSION</string>
	<key>CFBundleVersion</key><string>$VERSION</string>
	$ICON_KEY
	<key>NSPrincipalClass</key><string>NSApplication</string>
	<!-- Draw at the display's full resolution on Retina screens -->
	<key>NSHighResolutionCapable</key><true/>
	<key>LSApplicationCategoryType</key><string>public.app-category.graphics-design</string>
</dict>
</plist>
PLIST

# Apple silicon refuses to run code that is not signed. An ad hoc signature
# ("-") needs no Apple account.
if ! codesign --force --deep --sign - "$APP" 2>/dev/null; then
	echo "Warning: codesign failed. If macOS refuses to open the app, sign it by hand:" >&2
	echo "    codesign --force --deep --sign - \"$APP\"" >&2
fi

rm -rf "$PUBLISH"

echo
echo "Built: $APP"

if [[ "${1:-}" == "--run" ]]; then
	open "$APP"
fi
