#!/usr/bin/env bash
#
# WireHQ Community Edition — generate the agent-gateway TLS certificate.
#
# The agent gateway is a separate mTLS listener on the API. The API runs in Production here, which
# requires a real server certificate (Development auto-generates an ephemeral one, but agents would
# lose trust on every restart). This script mints a long-lived self-signed cert into deploy/certs/:
#   gateway.key / gateway.crt  — PEM (the .crt is what agents trust via SSL_CERT_FILE)
#   gateway.pfx                — PKCS#12 for the API (AgentGateway__ServerCertificatePath)
#
#   ./deploy/gateway-cert.sh [extra-host-or-ip]
#
# The SANs cover the in-compose name (`api`) + localhost. Pass your server's public DNS name or IP
# as an argument if REMOTE agents (on other machines) will connect — they dial that address, and
# TLS verification needs it in the certificate.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/certs"
mkdir -p "$certs"

if [ -f "$certs/gateway.pfx" ]; then
  echo "✋ $certs/gateway.pfx already exists — leaving it untouched."
  echo "   (Delete deploy/certs/ first for a fresh certificate; enrolled agents keep working — they"
  echo "   trust whatever deploy/certs/gateway.crt you mount, so re-distribute it if you rotate.)"
  exit 0
fi

command -v openssl >/dev/null 2>&1 || { echo "✖ openssl is required."; exit 1; }

san="DNS:api,DNS:localhost,IP:127.0.0.1"
if [ "${1:-}" != "" ]; then
  if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    san="$san,IP:$1"
  else
    san="$san,DNS:$1"
  fi
fi

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -sha256 -days 3650 -nodes \
  -keyout "$certs/gateway.key" -out "$certs/gateway.crt" \
  -subj "/CN=wirehq-agent-gateway" -addext "subjectAltName=$san" 2>/dev/null

# Empty-password PKCS#12 for the API side (the key never leaves this directory).
openssl pkcs12 -export -out "$certs/gateway.pfx" \
  -inkey "$certs/gateway.key" -in "$certs/gateway.crt" -passout pass:

chmod 600 "$certs/gateway.key" "$certs/gateway.pfx"
echo "✓ Wrote deploy/certs/gateway.{key,crt,pfx} (SANs: $san)."
echo
echo "Next:"
echo "  1. Mint an enrollment token in the app (WireGuard → Agents → Enroll agent)"
echo "  2. WIREHQ_ENROLL_TOKEN=<token> docker compose \\"
echo "       -f deploy/docker-compose.yml -f deploy/docker-compose.gateway.yml up -d --build"
