#!/usr/bin/env bash
#
# install-hooks.sh — one-time per clone. Points git at the committed hooks in .githooks/ so
# every contributor gets the same commit-msg / pre-commit / pre-push gates (docs/GIT.md §0).
#
#   ./scripts/install-hooks.sh
#
# Idempotent. Undo with:  git config --unset core.hooksPath

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

git config core.hooksPath .githooks
chmod +x .githooks/* 2>/dev/null || true

echo "✓ core.hooksPath → .githooks"
echo "  Hooks active: commit-msg (Conventional Commits), pre-commit (secret scan), pre-push (build/typecheck/lint)."

if ! command -v gitleaks >/dev/null 2>&1; then
  echo "  ℹ gitleaks not installed — pre-commit uses a basic built-in fallback."
  echo "    For thorough scanning:  brew install gitleaks"
fi
