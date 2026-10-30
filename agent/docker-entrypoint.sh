#!/bin/sh
#
# WireHQ Gateway entrypoint: enroll once (when the state volume holds no identity), then run.
#
#   WIREHQ_SERVER         required — the agent-gateway URL, e.g. https://api:28443
#   WIREHQ_ENROLL_TOKEN   required for FIRST boot only — single-use token from the Agents tab
#   WIREHQ_REKEY_TOKEN    optional — a re-key token (Agents tab → Re-key) for an agent whose certificate expired:
#                         re-issues THIS agent's certificate in place, keeping its instances. Keep the state volume.
#   WIREHQ_AGENT_NAME     optional — display name (default: the container hostname)
#   WIREHQ_POLL_INTERVAL  optional — job poll interval (default 30s)
#   WIREHQ_STATE_DIR      optional — identity/state dir (default /var/lib/wirehq-agent; volume-mount it)
#   WIREHQ_INSECURE_TLS   optional — "true" skips server TLS verification (DEVELOPMENT ONLY; prefer
#                         mounting the gateway certificate and pointing SSL_CERT_FILE at it)
#
set -eu

: "${WIREHQ_SERVER:?set WIREHQ_SERVER — the WireHQ agent-gateway URL, e.g. https://api:28443}"
state="${WIREHQ_STATE_DIR:-/var/lib/wirehq-agent}"

insecure=""
if [ "${WIREHQ_INSECURE_TLS:-false}" = "true" ]; then
  echo "⚠ WIREHQ_INSECURE_TLS=true — server TLS verification is OFF (development only)." >&2
  insecure="--insecure"
fi

# Mirrors the agent's own IsEnrolled() check (client key + cert + org CA on disk).
# (Skipped when a re-key token is supplied: that restores an existing agent's identity below, even into an empty volume.)
if [ -z "${WIREHQ_REKEY_TOKEN:-}" ] && { [ ! -f "$state/agent.key" ] || [ ! -f "$state/agent.crt" ] || [ ! -f "$state/ca.crt" ]; }; then
  : "${WIREHQ_ENROLL_TOKEN:?not enrolled yet and WIREHQ_ENROLL_TOKEN is not set — mint one in the Agents tab}"
  # shellcheck disable=SC2086  # $insecure is deliberately word-split (empty or a single flag)
  wirehq-agent enroll \
    --server "$WIREHQ_SERVER" \
    --token "$WIREHQ_ENROLL_TOKEN" \
    --name "${WIREHQ_AGENT_NAME:-$(hostname)}" \
    --state "$state" $insecure
fi

# Re-key in place (expired certificate / suspected compromise). Single-use, so after the first successful boot the
# variable is spent: a refusal is reported and the agent carries on with the identity it already has rather than
# crash-looping the container. Remove the variable once the agent is back.
if [ -n "${WIREHQ_REKEY_TOKEN:-}" ]; then
  # shellcheck disable=SC2086
  if wirehq-agent enroll --server "$WIREHQ_SERVER" --token "$WIREHQ_REKEY_TOKEN" --state "$state" $insecure; then
    echo "re-keyed: the agent's certificate was re-issued; remove WIREHQ_REKEY_TOKEN." >&2
  else
    echo "⚠ WIREHQ_REKEY_TOKEN was not accepted (already used or expired?) — continuing with the current identity." >&2
  fi
fi

# shellcheck disable=SC2086
exec wirehq-agent run --server "$WIREHQ_SERVER" --state "$state" \
  --interval "${WIREHQ_POLL_INTERVAL:-30s}" $insecure
