#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FINAL_APP="$ROOT/artifacts/client/RMC-MLCCS.app"
BUILD_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/rmc-client-build.XXXXXX")"
APP="$BUILD_ROOT/RMC-MLCCS.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"
SOURCES_DIR="$ROOT/client/RMC-MLCCS/Sources"

trap 'rm -rf "$BUILD_ROOT"' EXIT

clean_app_xattrs() {
  xattr -cr "$APP" 2>/dev/null || true
  find "$APP" -exec xattr -d com.apple.FinderInfo {} \; 2>/dev/null || true
  find "$APP" -exec xattr -d 'com.apple.fileprovider.fpfs#P' {} \; 2>/dev/null || true
}

mkdir -p "$MACOS" "$RESOURCES"

cp "$ROOT/client/RMC-MLCCS/Info.plist" "$CONTENTS/Info.plist"
if [[ "${CRC_EMBED_PRIVATE_CONFIG:-0}" == "1" ]]; then
  "$ROOT/scripts/generate-private-config.sh" >/dev/null
  cp "$ROOT/config/client.private.json" "$RESOURCES/config.json"
  echo "Warning: private device credentials were embedded because CRC_EMBED_PRIVATE_CONFIG=1." >&2
fi

SWIFT_SOURCES=()
while IFS= read -r source_file; do
  SWIFT_SOURCES+=("$source_file")
done < <(find "$SOURCES_DIR" -name '*.swift' -print | sort)

swiftc -parse-as-library -swift-version 5 -O \
  -target arm64-apple-macosx26.0 \
  -o "$MACOS/RMC-MLCCS" \
  "${SWIFT_SOURCES[@]}" \
  -framework SwiftUI \
  -framework AppKit \
  -framework ApplicationServices \
  -framework CoreGraphics \
  -framework Security

if [[ -f "$ROOT/client/RMC-MLCCS/Resources/AppIcon.icns" ]]; then
  cp "$ROOT/client/RMC-MLCCS/Resources/AppIcon.icns" "$RESOURCES/AppIcon.icns"
fi

clean_app_xattrs
codesign --force --deep --sign - "$APP" >/dev/null
clean_app_xattrs
codesign --verify --deep --strict "$APP"

rm -rf "$FINAL_APP"
mkdir -p "$(dirname "$FINAL_APP")"
ditto --noextattr --noqtn "$APP" "$FINAL_APP"
APP="$FINAL_APP"
clean_app_xattrs
printf 'Client app: %s\n' "$FINAL_APP"
