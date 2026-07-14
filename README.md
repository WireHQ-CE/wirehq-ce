# WireHQ Community Edition

**Self-hosted, open-source WireGuard management.** A single control plane you run on your own server to
manage WireGuard — networks, gateways (instances), identity-bound peers, keys, configs + QR export, and
deployment to a gateway (in-container or over SSH to your own box). No SaaS account, no telemetry, no billing.

> This is the **Community Edition** — a stripped, open fork of the private WireHQ SaaS codebase. It contains
> **only** the WireGuard-management core. The hosted product (wirehq.net — Free / Pro / Enterprise) is a
> separate, private codebase; none of its SaaS surface (platform/super-admin tier, billing, telemetry, SIEM,
> data-residency, marketing site/CMS, enterprise identity) lives here — see
> [What's intentionally NOT here](#whats-intentionally-not-here-its-the-saas-product) below.

## What's included

- **WireGuard management** — networks, instances (gateways), identity-bound peers, key generation + rotation,
  client config + **QR** export, full **server-config** export, config-version history.
- **Deployment** — bind a gateway to **Local** (config-only export — always free). Remote push over
  **SSH** or the **outbound Agent** (backup → apply → verify → rollback, config-drift detection, the
  Gateway container) is the **Remote Deployment** module from the
  [WireHQ Marketplace](https://wirehq.net/marketplace) — a one-off purchase, no subscription.
- **Your own org(s)** — users, **Teams**, role-based access control, MFA, sessions — you are the admin.
- **Runtime Row-Level Security** (Postgres RLS) tenant isolation and **Argon2id** password hashing — the same
  hardened security core as the SaaS product (this is a fork of *current* `main`, not an early cut).
- A per-org **audit log**.

## Quickstart (self-host with Docker)

```bash
./deploy/setup.sh
```

That generates `deploy/.env` (fresh secrets, sane defaults) and offers to start the stack
(`docker compose -f deploy/docker-compose.yml up -d --build`). Then **open the URL it prints — the
setup wizard greets you in the browser**: create your owner account (name, email, password,
organization) and you land signed-in on the dashboard. The wizard only exists while the instance
has no users; it locks itself the moment the owner is created.

For **unattended installs**, skip the browser step by pre-seeding the owner:
`OWNER_EMAIL=you@example.com OWNER_PASSWORD='…' ./deploy/setup.sh` (also honored: `OWNER_NAME`,
`ORGANIZATION_NAME`, `APP_BASE_URL`, `WEB_BIND`, `WEB_PORT`, `OPEN_REGISTRATION`, `SMTP_*` — all
written into `deploy/.env`).

The stack is **invite-only by default** (`OPEN_REGISTRATION=false`): add users from
**Users → Invite** (requires SMTP), or set `OPEN_REGISTRATION=true` while you onboard your team.
For email, edit the `SMTP_*` lines in `deploy/.env` and `docker compose up -d` — the settings
re-sync on every boot. Migrations and seeding run in a one-shot `migrate` job before the API
starts; only the web port is published — put your own TLS proxy in front for anything beyond a
trusted LAN (and set `WEB_BIND=127.0.0.1`). Back up with `deploy/backup-db.sh`.

## Gateway (optional): run the tunnels in a container — no WireGuard install on the host

> Part of the **Remote Deployment** Marketplace module (one-off purchase; see the README § Deployment
> note above). The instructions below describe the capability once the module is active.

The **WireHQ Gateway** is the outbound agent + WireGuard in one container. It enrolls against your
own control plane above, pulls signed configs, and brings the interface up **inside the container**
(the host's kernel WireGuard when available, bundled userspace otherwise — only `NET_ADMIN` +
`/dev/net/tun` needed):

```bash
./deploy/gateway-cert.sh [public-host-or-ip]       # once — the agent-gateway TLS certificate
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.gateway.yml up -d --build
# mint a token in the app (WireGuard → Agents → Enroll agent), then:
WIREHQ_ENROLL_TOKEN=<token> docker compose -f deploy/docker-compose.yml \
    -f deploy/docker-compose.gateway.yml up -d gateway
```

Bind an instance to the agent (Instance → Deployment → Agent) and **Deploy** — `wg show` inside the
container shows the live interface. Prefer the tunnels in the **host's** network namespace? Give the
`gateway` service `network_mode: host` (see the notes in `deploy/docker-compose.gateway.yml`).
Remember: peers must reach the WireGuard UDP port (default 51820) — public IP or a router
port-forward; the container changes the management story, not the networking one. Remote boxes can
run the same image against `https://<your-host>:28443` (pass that host to `gateway-cert.sh` so the
certificate covers it).

## What's intentionally NOT here (it's the SaaS product)

Platform / super-admin / customer-management tier · Stripe billing & plans · the observability & audit
*platform* (SIEM export, data-residency, diagnostics console, RUM) · enterprise identity (SSO/SCIM/Access
Policies) · the public marketing site + CMS. You are the super-admin of your own instance, so there is no
platform tier above you.

## Status

This repo is a **generated, deeply-stripped build**: the SaaS surface (platform tier, billing, CMS, telemetry
platform) is removed, the database schema is a single fresh baseline (no SaaS tables), the first-run path is
`.env`-seeded (see the Quickstart), the test suite is re-based to the shipped feature set (WireGuard, auth,
row-level security, orchestration, agents, audit), and the optional Gateway container bundles userspace
WireGuard. What remains before a public release is tracked in [`STRIP.md`](STRIP.md) (§ Follow-ups).

## License

**GNU Affero General Public License v3.0 (AGPLv3)** — see [`LICENSE`](LICENSE). Protective copyleft suited to
open-core: if you run a modified version as a network service, you must offer its source to your users. The
hosted WireHQ SaaS (wirehq.net) is a separate, private codebase and is not derived from this repository.
