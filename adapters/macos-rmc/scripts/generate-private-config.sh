#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_DIR="$ROOT/config"
TLS_DIR="$CONFIG_DIR/tls"
SECRET_FILE="$CONFIG_DIR/rmc.shared-secret"
SERVER_CONFIG="$CONFIG_DIR/server.private.json"
CLIENT_CONFIG="$CONFIG_DIR/client.private.json"
OPENSSL_BIN="${OPENSSL_BIN:-openssl}"

mkdir -p "$CONFIG_DIR" "$TLS_DIR"

if [[ ! -f "$SECRET_FILE" ]]; then
  "$OPENSSL_BIN" rand -base64 48 > "$SECRET_FILE"
  chmod 600 "$SECRET_FILE"
fi

if [[ ! -f "$TLS_DIR/server.key" || ! -f "$TLS_DIR/server.crt" ]]; then
  "$OPENSSL_BIN" req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
    -keyout "$TLS_DIR/server.key" \
    -out "$TLS_DIR/server.crt" \
    -subj "/CN=lixinchen.ca" \
    -addext "subjectAltName=DNS:lixinchen.ca,DNS:localhost,IP:127.0.0.1"
  chmod 600 "$TLS_DIR/server.key"
fi

SECRET="$(tr -d '\r\n' < "$SECRET_FILE")"
FINGERPRINT="$("$OPENSSL_BIN" x509 -in "$TLS_DIR/server.crt" -noout -fingerprint -sha256 | sed 's/^.*=//' | tr -d ':' | tr '[:upper:]' '[:lower:]')"

cat > "$SERVER_CONFIG" <<JSON
{
  "listenHost": "0.0.0.0",
  "port": 5002,
  "sharedSecret": "$SECRET",
  "commandTimeoutSeconds": 600,
  "tls": {
    "enabled": true,
    "certificatePath": "tls/server.crt",
    "keyPath": "tls/server.key"
  },
  "logsDirectory": "logs"
}
JSON
chmod 600 "$SERVER_CONFIG"

cat > "$CLIENT_CONFIG" <<JSON
{
  "serverUrl": "wss://lixinchen.ca:5002/link",
  "sharedSecret": "$SECRET",
  "clientId": "",
  "allowInvalidServerCertificate": false,
  "pinnedServerCertificateSha256": "$FINGERPRINT",
  "reconnectDelaySeconds": 5,
  "commandTimeoutSeconds": 600,
  "requireSudoBeforeConnect": true,
  "allowRootCommands": true
}
JSON
chmod 600 "$CLIENT_CONFIG"

printf 'Private RMC config ready. Server port: 5002, TLS fingerprint: %s\n' "$FINGERPRINT"
