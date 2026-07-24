#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACT="$ROOT/artifacts/server"

"$ROOT/scripts/generate-private-config.sh" >/dev/null

rm -rf "$ARTIFACT"
mkdir -p "$ARTIFACT"

cp "$ROOT/server/rmc-server.mjs" "$ARTIFACT/rmc-server.mjs"
cp "$ROOT/server/protocol-v2.mjs" "$ARTIFACT/protocol-v2.mjs"
cp "$ROOT/package.json" "$ARTIFACT/package.json"
cp "$ROOT/package-lock.json" "$ARTIFACT/package-lock.json" 2>/dev/null || true
cp "$ROOT/config/server.private.json" "$ARTIFACT/server.private.json"

if [[ -f "$ARTIFACT/package-lock.json" ]]; then
  npm ci --omit=dev --prefix "$ARTIFACT" >/dev/null
else
  npm install --omit=dev --prefix "$ARTIFACT" >/dev/null
fi

chmod +x "$ARTIFACT/rmc-server.mjs"
printf 'Server artifact: %s\n' "$ARTIFACT"
