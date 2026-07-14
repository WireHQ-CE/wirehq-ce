#!/usr/bin/env bash
#
# backup-db.sh — take a compressed, timestamped pg_dump of the production database.
#
# Run it BEFORE every migration/deploy (deploy.sh does this automatically) AND on a schedule (cron) for
# point-in-time safety. A backup you have never restored is a hope, not a backup — rehearse restore-db.sh.
#
#   ./deploy/backup-db.sh                      # write to ./backups/
#   BACKUP_DIR=/mnt/offbox ./deploy/backup-db.sh   # write elsewhere (prefer OFF this host)
#
# Retention: keeps the newest $BACKUP_KEEP (default 30) dumps in $BACKUP_DIR.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# SaaS prod uses docker-compose.prod.yml; a self-hosted (Community Edition) install ships only
# docker-compose.yml — fall back so one script serves both. Override with WIREHQ_COMPOSE_FILE.
compose_file="${WIREHQ_COMPOSE_FILE:-$here/docker-compose.prod.yml}"
[ -f "$compose_file" ] || compose_file="$here/docker-compose.yml"
compose="docker compose -f $compose_file"
BACKUP_DIR="${BACKUP_DIR:-$here/../backups}"
BACKUP_KEEP="${BACKUP_KEEP:-30}"
label="${1:-manual}"

mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%d-%H%M%S)"
out="$BACKUP_DIR/wirehq-$stamp-$label.sql.gz"

echo "→ Backing up wirehq → $out"
# Stream pg_dump out of the db container; never exposes a DB port. -T = no TTY (pipe-safe).
$compose exec -T db pg_dump -U wirehq --clean --if-exists wirehq | gzip > "$out"

size="$(du -h "$out" | cut -f1)"
echo "✓ Backup complete ($size)"

# Prune old backups, keeping the newest $BACKUP_KEEP. (POSIX/bash-3.2 safe — no mapfile, which macOS lacks.)
old_backups="$(ls -1t "$BACKUP_DIR"/wirehq-*.sql.gz 2>/dev/null | tail -n +$((BACKUP_KEEP + 1)) || true)"
if [ -n "$old_backups" ]; then
  echo "$old_backups" | while IFS= read -r old; do rm -f "$old"; done
  echo "  pruned $(printf '%s\n' "$old_backups" | wc -l | tr -d ' ') old backup(s)"
fi

echo "$out"
