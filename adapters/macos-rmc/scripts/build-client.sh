#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FINAL_APP="$ROOT/artifacts/client/RMC-MLCCS.app"
FINAL_MANIFEST="$ROOT/artifacts/client/build-manifest.json"
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
codesign --verify --deep --strict --verbose=2 "$FINAL_APP"

REPO_ROOT="$(cd "$ROOT/../.." && pwd)"
GIT_COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || printf 'unknown')"
if [[ -n "$(git -C "$REPO_ROOT" status --porcelain -- \
  adapters/macos-rmc/client/RMC-MLCCS \
  adapters/macos-rmc/scripts/build-client.sh 2>/dev/null)" ]]; then
  GIT_DIRTY=true
else
  GIT_DIRTY=false
fi
SOURCE_SHA="$(
  {
    find "$ROOT/client/RMC-MLCCS" -type f -print
    printf '%s\n' "$ROOT/scripts/build-client.sh"
  } | LC_ALL=C sort | while IFS= read -r source_file; do
    shasum -a 256 "$source_file"
  done | shasum -a 256 | awk '{print $1}'
)"
BINARY_SHA="$(shasum -a 256 "$FINAL_APP/Contents/MacOS/RMC-MLCCS" | awk '{print $1}')"
BUILT_AT="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
printf '{\n  "format": "crc-macos-build-manifest-v1",\n  "gitCommit": "%s",\n  "gitDirty": %s,\n  "sourceAggregateSha256": "%s",\n  "binarySha256": "%s",\n  "target": "arm64-apple-macosx26.0",\n  "signing": "adhoc",\n  "builtAt": "%s"\n}\n' \
  "$GIT_COMMIT" "$GIT_DIRTY" "$SOURCE_SHA" "$BINARY_SHA" "$BUILT_AT" > "$FINAL_MANIFEST"

printf 'Client app: %s\n' "$FINAL_APP"
printf 'Build manifest: %s\n' "$FINAL_MANIFEST"
