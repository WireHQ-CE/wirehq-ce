# wirehq-agent

The **outbound-only mTLS WireHQ agent** — a tiny static Go binary that runs on a host you want WireHQ to
manage. It enrols once, then makes only **outbound** connections to WireHQ: pulling **signed** deployment
jobs over mTLS, **verifying the signature before applying**, applying WireGuard config with `wg-quick`, and
reporting status + telemetry back. No inbound ports, no stored credentials, no standing access.

Feature overview, security model, and operator flow: [`docs/13-agent.md`](../docs/13-agent.md) (ADR-028).

## Build

```bash
cd agent
go build -o wirehq-agent .                                   # host build
GOOS=linux GOARCH=amd64 go build -o wirehq-agent-amd64 .     # cross-compile
GOOS=linux GOARCH=arm64 go build -o wirehq-agent-arm64 .
```

No third-party dependencies (standard library only).

## Install & run

```bash
# 1. Install the binary
sudo install -m 0755 wirehq-agent /usr/local/bin/wirehq-agent

# 2. Enrol with a single-use token (from WireHQ → WireGuard → Agents → Enroll agent)
sudo wirehq-agent enroll \
  --server https://wirehq.example.com:28443 \
  --token  <TOKEN>

# 3. Run it (foreground) — or use the systemd unit below
sudo wirehq-agent run --server https://wirehq.example.com:28443
```

The agent's identity (client key + cert, the org CA cert, the agent id) is stored under
`--state` (default `/var/lib/wirehq-agent`). The private key never leaves the host.

Each poll the agent also reports its **observed status** (and, for `AgentManaged` interfaces, the
locally-held interface public key). It records the hash of the config it applied per interface, and if the
on-disk config later changes out from under it (host-side tampering) it reports **config drift** to WireHQ —
which surfaces it on the instance's deployment panel and the agent's fleet row.

### systemd

`deploy/wirehq-agent.service` is a hardened unit (least-privilege: `CAP_NET_ADMIN` only). The server URL is
the instance parameter:

```bash
sudo cp deploy/wirehq-agent.service /etc/systemd/system/
sudo systemctl enable --now 'wirehq-agent@https://wirehq.example.com:28443'
```

## Commands

| Command | Purpose |
|---|---|
| `enroll --server <url> --token <token> [--name <n>] [--state <dir>] [--insecure]` | Redeem a single-use token → a client certificate. |
| `run --server <url> [--state <dir>] [--interval 30s] [--once] [--insecure]` | Poll for jobs, apply, and report. Loops every `--interval`; `--once` runs a single cycle then exits (cron-style scheduling). |

`--insecure` skips **server** TLS verification — **development only** (the dev gateway uses a self-signed
server certificate). In production the agent verifies the gateway's certificate normally.

## Layout

```
agent/
  main.go                       CLI (enroll · run)
  internal/agent/
    config.go                   on-disk identity (key/cert/ca + agent id)
    enroll.go                   keypair + CSR → POST /agent/v1/enroll → store
    client.go                   mTLS client (jobs · result · telemetry · heartbeat)
    bundle.go                   verify the CA signature BEFORE applying (ECDSA-P384/SHA-384, P1363)
    applier.go                  write config → wg-quick up, snapshot+rollback; wg show dump → telemetry
    run.go                      the poll loop
  deploy/wirehq-agent.service   hardened systemd unit
```
