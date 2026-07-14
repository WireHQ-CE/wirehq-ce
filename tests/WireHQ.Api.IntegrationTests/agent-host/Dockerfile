# A throwaway host for the WireHQ agent live end-to-end test. It builds the REAL Go agent (multi-stage) and
# runs it against the real mTLS gateway, with FAKE wireguard-tools shims (no kernel module — ADR-002): the
# agent enrols, polls a signed job, applies it with `wg-quick` (a no-op shim — the agent writes the .conf
# itself), and reports back. `wg show <iface> dump` is synthesized from the deployed config so telemetry has
# real peer keys. The applied config lands in /etc/wireguard so the test can read it back. (docs/13-agent.md)
#
# The build context is assembled at test time: this Dockerfile + the `wg` shim (copied to the test output via
# the csproj) plus a copy of the repo's `agent/` source — so `COPY agent/` below resolves. CGO is disabled so
# the static binary built on golang:alpine runs as-is on the alpine runtime.
FROM golang:1.23-alpine AS build
WORKDIR /src
COPY agent/ ./
RUN CGO_ENABLED=0 go build -o /wirehq-agent .

FROM alpine:3.20
RUN mkdir -p /etc/wireguard \
    && printf '#!/bin/sh\nexit 0\n' > /usr/local/bin/wg-quick \
    && chmod 0755 /usr/local/bin/wg-quick
COPY wg /usr/local/bin/wg
COPY --from=build /wirehq-agent /usr/local/bin/wirehq-agent
RUN chmod 0755 /usr/local/bin/wg /usr/local/bin/wirehq-agent

# Stay alive; the test drives `wirehq-agent enroll` + `run --once` via exec.
ENTRYPOINT ["sleep", "infinity"]
