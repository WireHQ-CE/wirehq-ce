#!/bin/sh
#
# WireHQ Gateway entrypoint: enroll once (when the state volume holds no identity), then run.
#
#   WIREHQ_SERVER         required — the agent-gateway URL, e.g. https://api:28443
#   WIREHQ_ENROLL_TOKEN   required for FIRST boot only — single-use token from the Agents tab
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
if [ ! -f "$state/agent.key" ] || [ ! -f "$state/agent.crt" ] || [ ! -f "$state/ca.crt" ]; then
  : "${WIREHQ_ENROLL_TOKEN:?not enrolled yet and WIREHQ_ENROLL_TOKEN is not set — mint one in the Agents tab}"
  # shellcheck disable=SC2086  # $insecure is deliberately word-split (empty or a single flag)
  wirehq-agent enroll \
    --server "$WIREHQ_SERVER" \
    --token "$WIREHQ_ENROLL_TOKEN" \
    --name "${WIREHQ_AGENT_NAME:-$(hostname)}" \
    --state "$state" $insecure
fi

# shellcheck disable=SC2086
exec wirehq-agent run --server "$WIREHQ_SERVER" --state "$state" \
  --interval "${WIREHQ_POLL_INTERVAL:-30s}" $insecure
