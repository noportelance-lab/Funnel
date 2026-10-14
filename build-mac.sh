#!/bin/bash
# =====================================================================
#  Funnel - macOS build
#  Produces a standalone Funnel.app in "./ios version"
#  Requires: the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0,
#            or: curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0)
#  Usage:    ./build-mac.sh            Apple silicon (arm64)
#            ./build-mac.sh x64        Intel Macs
# =====================================================================
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
arch="${1:-arm64}"
case "$arch" in arm64|x64) ;; *) echo "Architecture must be arm64 or x64." >&2; exit 1 ;; esac
rid="osx-$arch"
out="$here/ios version"
app="$out/Funnel.app"
work="$here/build/mac-$arch"

# PATH first, then the installers' standard locations.
dotnet="$(command -v dotnet || true)"
for c in "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet; do
  [ -z "$dotnet" ] && [ -x "$c" ] && dotnet="$c"
done
if [ -z "$dotnet" ] || ! "$dotnet" --list-sdks | grep -q '^[0-9]'; then
  echo "No .NET SDK found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 then re-run." >&2
  exit 1
fi
export DOTNET_ROOT="$(dirname "$dotnet")" DOTNET_CLI_TELEMETRY_OPTOUT=1
echo "Using .NET SDK: $dotnet"

echo "Building Funnel for $rid (self-contained single file)..."
rm -rf "$work"
"$dotnet" publish "$here/src/Funnel.csproj" -c Release -r "$rid" -p:FunnelMac=true -o "$work/publish"

echo "Packaging Funnel.app..."
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$work/publish/." "$app/Contents/MacOS/"
rm -f "$app/Contents/MacOS/"*.pdb "$app/Contents/MacOS/"*.xml

# App icon from the logo.
iconset="$work/Funnel.iconset"
mkdir -p "$iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$here/src/Assets/logo.png" --out "$iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$here/src/Assets/logo.png" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Funnel.icns"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$here/src/Funnel.csproj" | head -1)"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Funnel</string>
  <key>CFBundleDisplayName</key><string>Funnel</string>
  <key>CFBundleIdentifier</key><string>com.noeportelance.funnel</string>
  <key>CFBundleExecutable</key><string>Funnel</string>
  <key>CFBundleIconFile</key><string>Funnel</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHumanReadableCopyright</key><string>© Noé Portelance</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: required to run on Apple silicon. (Not notarised: on another Mac, the first
# launch is right-click → Open.)
codesign --force --deep --sign - "$app" >/dev/null
xattr -cr "$app" 2>/dev/null || true

mb=$(du -sm "$app" | cut -f1)
echo ""
echo "BUILD OK -> $app (${mb} MB)"
