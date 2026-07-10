#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

"$ROOT/scripts/generate-private-config.sh"
"$ROOT/scripts/build-server.sh"
"$ROOT/scripts/build-client.sh"

printf 'Built RMC-MLCCS artifacts under %s/artifacts\n' "$ROOT"
