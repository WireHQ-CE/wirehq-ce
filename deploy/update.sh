#!/usr/bin/env bash
#
# update.sh — update this WireHQ install, backing up the database first.
#
#   ./deploy/update.sh
#
# What it does, in order:
#   1. Takes a compressed backup of your database (deploy/backup-db.sh → ./backups/).
#   2. Rebuilds and restarts from the source currently in your clone.
#   3. Tells you which version you ended up on.
#
# Run `git pull` FIRST to fetch the new release, then run this.
#
# Why a script rather than a note in the README: on a self-hosted install nothing else takes a backup for
# you. The SaaS deploy path does it automatically, but that script is not part of this distribution — so
# without this one, an update applies database changes with no safety net.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
compose="docker compose -f $here/docker-compose.yml"

cd "$root"

echo "═══ Updating WireHQ ═══"

# 1) Backup. Non-negotiable: an update can now transform existing data in place, so "it only ever added
#    things" is no longer a safety net you can rely on.
if [ -x "$here/backup-db.sh" ]; then
  echo "▸ 1/3 Backing up the database…"
  if ! "$here/backup-db.sh"; then
    echo ""
    echo "✖ The backup failed, so nothing has been updated."
    echo "  Fix the backup first — an update without one is not recoverable if it goes wrong."
    echo "  (If the database is not running: $compose up -d db)"
    exit 1
  fi
else
  echo "✖ deploy/backup-db.sh is missing or not executable — refusing to update without a backup."
  exit 1
fi

# 2) Rebuild + restart. The migrate job runs here, and the API waits for it to succeed.
echo "▸ 2/3 Rebuilding and restarting…"
if ! $compose up -d --build; then
  echo ""
  echo "✖ The update did not complete."
  echo ""
  echo "  Your previous containers may still be running and serving normally — Compose starts in"
  echo "  dependency order and stops before replacing the app if the database update fails. So the"
  echo "  site working is NOT proof the update landed. Check the version in step 3 below."
  echo ""
  echo "  See what happened:"
  echo "    $compose logs migrate"
  echo ""
  echo "  Then re-run ./deploy/update.sh — database changes that already applied are skipped, so"
  echo "  re-running is safe and picks up where it stopped."
  echo ""
  echo "  ⚠ Do NOT run '$compose down -v'. The -v flag DELETES your database."
  echo "    Your backup from step 1 is in ./backups/ and restores with ./deploy/restore-db.sh."
  exit 1
fi

# 3) Report what is actually running — the whole point is that "the app loads" is not evidence.
echo "▸ 3/3 Done. Now running:"
$compose ps --format '  {{.Service}}\t{{.Image}}\t{{.Status}}' 2>/dev/null \
  || $compose ps
echo ""
echo "✓ Update complete. Backup from before the update: ./backups/"
