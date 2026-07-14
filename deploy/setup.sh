#!/usr/bin/env bash
#
# WireHQ Community Edition — install bootstrap.
# Generates deploy/.env (fresh secrets + sane defaults) and offers to start the stack. You create
# your owner account in the BROWSER: the first visit to a fresh instance shows the setup wizard.
# Idempotent: refuses to overwrite an existing .env (your secrets encrypt data at rest — losing
# them means losing access to encrypted settings).
#
#   ./deploy/setup.sh
#
# Overrides (environment): WEB_PORT, WEB_BIND, APP_BASE_URL, OPEN_REGISTRATION, SMTP_*, and — for
# unattended installs that skip the browser wizard — OWNER_EMAIL + OWNER_PASSWORD (+ OWNER_NAME,
# ORGANIZATION_NAME), which seed the owner at boot instead.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"
compose_file="$here/docker-compose.yml"

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  bold=$'\033[1m'; dim=$'\033[2m'; gold=$'\033[33m'; green=$'\033[32m'; reset=$'\033[0m'
else
  bold=""; dim=""; gold=""; green=""; reset=""
fi
say() { printf '%s\n' "$*"; }
ok()  { printf '%s✓%s %s\n' "$green" "$reset" "$*"; }

if [ -f "$env_file" ]; then
  say "✋ $env_file already exists — leaving it untouched."
  say "   (Delete it first if you really want fresh secrets — encrypted settings will be unreadable.)"
  exit 0
fi
command -v openssl >/dev/null 2>&1 || { echo "✖ openssl is required to generate secrets." >&2; exit 1; }

# Best-effort LAN address for a sensible default base URL (falls back to localhost).
detect_host() {
  local ip=""
  ip="$(hostname -I 2>/dev/null || true)"; ip="${ip%% *}"
  if [ -z "$ip" ] && command -v ipconfig >/dev/null 2>&1; then
    ip="$(ipconfig getifaddr en0 2>/dev/null || true)"
  fi
  printf '%s' "${ip:-localhost}"
}

# hex keeps connection strings safe (no ';', '=', or quote characters).
pg_password="$(openssl rand -hex 24)"
app_password="$(openssl rand -hex 24)"
jwt_key="$(openssl rand -hex 48)"                 # 96 chars — comfortably ≥ the 32-byte minimum
secret_key="$(openssl rand -base64 32)"           # exactly 32 bytes, base64 — the required format

web_port="${WEB_PORT:-8080}"
web_bind="${WEB_BIND:-0.0.0.0}"
app_base_url="${APP_BASE_URL:-http://$(detect_host):${web_port}}"

cat > "$env_file" <<EOF
# WireHQ Community Edition — deploy/.env (written by setup.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ);
# git-ignored — never commit it).

# ── Generated secrets (keep safe; SECRET_PROTECTION_KEY encrypts settings at rest) ──────────
POSTGRES_PASSWORD=${pg_password}
WIREHQ_APP_PASSWORD=${app_password}
JWT_SIGNING_KEY=${jwt_key}
SECRET_PROTECTION_KEY=${secret_key}

# ── Reachability ─────────────────────────────────────────────────────────────────────────────
# The URL your users browse to (used in email links + CORS). LAN example: http://192.168.1.10:8080
APP_BASE_URL='${app_base_url}'
WEB_BIND=${web_bind}
WEB_PORT=${web_port}

# ── First run ────────────────────────────────────────────────────────────────────────────────
# Leave the OWNER_* lines empty (recommended): your first visit to the site shows the in-browser
# setup wizard, where you create the owner account. For UNATTENDED installs, set OWNER_EMAIL +
# OWNER_PASSWORD instead — the owner is then seeded at boot and the wizard never appears.
OWNER_EMAIL='${OWNER_EMAIL:-}'
OWNER_PASSWORD='${OWNER_PASSWORD:-}'
OWNER_NAME='${OWNER_NAME:-Owner}'
ORGANIZATION_NAME='${ORGANIZATION_NAME:-WireHQ}'

# ── Registration posture ─────────────────────────────────────────────────────────────────────
# false = invite-only (recommended): the owner invites users (requires SMTP below).
# true  = anyone who can reach the page can sign up.
OPEN_REGISTRATION=${OPEN_REGISTRATION:-false}

# ── SMTP (needed for invites, password reset, verification email) ───────────────────────────
# The only SMTP interface in the CE: edit here, \`docker compose up -d\`, and it re-syncs on boot.
SMTP_ENABLED=${SMTP_ENABLED:-false}
SMTP_HOST='${SMTP_HOST:-}'
SMTP_PORT=${SMTP_PORT:-587}
SMTP_USE_SSL=${SMTP_USE_SSL:-false}
SMTP_USERNAME='${SMTP_USERNAME:-}'
SMTP_PASSWORD='${SMTP_PASSWORD:-}'
SMTP_FROM_EMAIL='${SMTP_FROM_EMAIL:-no-reply@wirehq.local}'
SMTP_FROM_NAME='${SMTP_FROM_NAME:-WireHQ}'
EOF

chmod 600 "$env_file"
ok "Wrote $env_file (permissions 600) with generated secrets."

started=0
if [ -t 0 ] && command -v docker >/dev/null 2>&1; then
  printf '%s %s[Y/n]%s ' "Start WireHQ now (docker compose up -d --build; first build takes a few minutes)?" "$dim" "$reset"
  IFS= read -r reply
  case "${reply:-y}" in
    [Yy]*)
      docker compose -f "$compose_file" up -d --build
      started=1
      if command -v curl >/dev/null 2>&1; then
        printf '%s' "Waiting for WireHQ to come up "
        for _ in $(seq 1 45); do
          if curl -fsS -o /dev/null "http://localhost:${web_port}/api/v1/auth/security-config" 2>/dev/null; then
            say ""; ok "WireHQ is up."; break
          fi
          printf '.'; sleep 2
        done
      fi
      ;;
  esac
fi

say ""
say "${bold}Next:${reset}"
if [ "$started" = 1 ]; then
  say "  Open ${bold}${gold}${app_base_url}${reset} — the setup wizard will greet you: create your"
  say "  owner account in the browser and you'll land signed-in on the dashboard."
else
  say "  1. docker compose -f deploy/docker-compose.yml up -d --build"
  say "  2. Open ${bold}${gold}${app_base_url}${reset} — the setup wizard will greet you: create your"
  say "     owner account in the browser and you'll land signed-in on the dashboard."
fi
say "  ${dim}Email (invites, password reset): edit the SMTP_* lines in deploy/.env and \`docker compose up -d\`.${reset}"
say "  ${dim}Optional gateway container (tunnels in Docker, no host WireGuard): see the README § Gateway.${reset}"
