#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_DIR="$ROOT/config"
DEVICE_AUTH_FILE="$CONFIG_DIR/rmc.device-auth-key"
OPERATOR_AUTH_FILE="$CONFIG_DIR/rmc.operator-auth-key"
E2EE_FILE="$CONFIG_DIR/rmc.e2ee-key"
SERVER_CONFIG="$CONFIG_DIR/server.private.json"
CLIENT_CONFIG="$CONFIG_DIR/client.private.json"
OPENSSL_BIN="${OPENSSL_BIN:-openssl}"
BROKER_URL="${CRC_BROKER_URL:-wss://lixinchen.ca/crc/v2/ws}"
DEVICE_ID="${CRC_DEVICE_ID:-$(scutil --get LocalHostName 2>/dev/null || hostname)-$(id -un)}"
OPERATOR_ID="${CRC_OPERATOR_ID:-operator-$(scutil --get LocalHostName 2>/dev/null || hostname)}"
DEVICE_KEY_ID="${CRC_DEVICE_KEY_ID:-${DEVICE_ID}-2026-01}"
OPERATOR_KEY_ID="${CRC_OPERATOR_KEY_ID:-${OPERATOR_ID}-2026-01}"

mkdir -p "$CONFIG_DIR"
for key_file in "$DEVICE_AUTH_FILE" "$OPERATOR_AUTH_FILE" "$E2EE_FILE"; do
  if [[ ! -f "$key_file" ]]; then
    "$OPENSSL_BIN" rand -base64 32 | tr '+/' '-_' | tr -d '=\r\n' > "$key_file"
    chmod 600 "$key_file"
  fi
done

DEVICE_AUTH_KEY="$(tr -d '\r\n' < "$DEVICE_AUTH_FILE")"
OPERATOR_AUTH_KEY="$(tr -d '\r\n' < "$OPERATOR_AUTH_FILE")"
E2EE_KEY="$(tr -d '\r\n' < "$E2EE_FILE")"

if [[ ! -f "$SERVER_CONFIG" ]] || ! grep -q '"brokerUrl"' "$SERVER_CONFIG"; then
  cat > "$SERVER_CONFIG" <<JSON
{
  "brokerUrl": "$BROKER_URL",
  "operatorId": "$OPERATOR_ID",
  "keyId": "$OPERATOR_KEY_ID",
  "brokerAuthKey": "$OPERATOR_AUTH_KEY",
  "devices": {
    "$DEVICE_ID": {
      "e2eeKey": "$E2EE_KEY"
    }
  },
  "commandTimeoutSeconds": 600,
  "reconnect": { "initialSeconds": 1, "maxSeconds": 60 },
  "localApi": { "host": "127.0.0.1", "port": 5002, "tokenFile": "operator.token" },
  "logsDirectory": "logs"
}
JSON
  chmod 600 "$SERVER_CONFIG"
fi

if [[ ! -f "$CLIENT_CONFIG" ]] || ! grep -q '"brokerUrl"' "$CLIENT_CONFIG"; then
  cat > "$CLIENT_CONFIG" <<JSON
{
  "brokerUrl": "$BROKER_URL",
  "deviceId": "$DEVICE_ID",
  "keyId": "$DEVICE_KEY_ID",
  "brokerAuthKey": "$DEVICE_AUTH_KEY",
  "e2eeKey": "$E2EE_KEY",
  "reconnectDelaySeconds": 1,
  "commandTimeoutSeconds": 600,
  "requireSudoBeforeConnect": true,
  "allowRootCommands": true
}
JSON
  chmod 600 "$CLIENT_CONFIG"
fi

printf 'Private CRC v2 RMC config ready for device %s and operator %s.\n' "$DEVICE_ID" "$OPERATOR_ID"
printf 'Register the generated auth key files with the broker; never commit them.\n'
