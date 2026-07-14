#!/usr/bin/env bash
#
# restore-db.sh — restore the production database from a backup-db.sh dump. DESTRUCTIVE: it overwrites
# the current database with the dump's contents (the dump is --clean --if-exists). Use for the recovery
# drill and as a genuine last resort after a bad migration.
#
#   ./deploy/restore-db.sh backups/wirehq-20260625-120000-pre-0.6.0.sql.gz
#
# Rehearse this regularly against a throwaway DB so a real restore is muscle memory, not a panic.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# SaaS prod uses docker-compose.prod.yml; a self-hosted (Community Edition) install ships only
# docker-compose.yml — fall back so one script serves both. Override with WIREHQ_COMPOSE_FILE.
compose_file="${WIREHQ_COMPOSE_FILE:-$here/docker-compose.prod.yml}"
[ -f "$compose_file" ] || compose_file="$here/docker-compose.yml"
compose="docker compose -f $compose_file"
dump="${1:?usage: restore-db.sh <backup.sql.gz>}"

[[ -f "$dump" ]] || { echo "✖ Backup not found: $dump" >&2; exit 1; }

echo "⚠ About to RESTORE $dump over the current 'wirehq' database — this overwrites live data."
read -r -p "  Type 'restore' to proceed: " confirm
[[ "$confirm" == "restore" ]] || { echo "Aborted."; exit 1; }

echo "→ Stopping API/web so nothing writes mid-restore…"
$compose stop api web || true

echo "→ Restoring…"
gunzip -c "$dump" | $compose exec -T db psql -U wirehq -d wirehq

echo "→ Restarting API/web…"
$compose up -d api web

echo "✓ Restore complete. Verify the app + /api/health/ready."
