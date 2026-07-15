<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="web/src/assets/brand/logo-dark.svg">
  <img alt="WireHQ" src="web/src/assets/brand/logo-light.svg" width="300">
</picture>

<h3>Enterprise-grade WireGuard management — self-hosted, open source, free forever.</h3>

<p>
The full WireGuard control plane you run on your own server: users, devices, gateways,<br>
networks and keys — the same hardened core that powers our cloud. No account. No telemetry. No licence checks.
</p>

[![License: AGPLv3](https://img.shields.io/badge/License-AGPLv3-D98E00.svg?style=flat-square)](LICENSE)
&nbsp;![Self-hosted](https://img.shields.io/badge/Deploy-One%20command-1A1206.svg?style=flat-square)
&nbsp;![Built for WireGuard](https://img.shields.io/badge/Built%20for-WireGuard-88171A.svg?style=flat-square)
&nbsp;![No telemetry](https://img.shields.io/badge/Telemetry-None-2E7D32.svg?style=flat-square)

**[Website](https://wirehq.net) · [Marketplace](https://wirehq.net/marketplace) · [Quickstart](#-quickstart) · [Features](#-whats-included--free-forever) · [License](#-license)**

</div>

---

## What is WireHQ?

WireGuard is fast, modern and secure — but managing it by hand doesn't scale. Once you have more than a
handful of peers, you're editing config files, copying keys around, and hoping nothing drifts.

**WireHQ is the control plane that makes WireGuard manageable.** Networks, gateways, and identity-bound
peers in one place — with key generation and rotation, one-click client configs (with QR codes), a
tamper-evident audit trail, and role-based access for your team. It runs as a single Docker deployment on
your own hardware.

## What is the Community Edition?

**The Community Edition is WireHQ, free and open source, running entirely on your server.** It's not a demo
or a cut-down trial — it's the real WireGuard-management core of the platform, released under the AGPLv3.

- 🆓 **Free forever** — no account, no credit card, no seat limits on your own installs.
- 🔒 **Yours completely** — your keys, your data, your network. No telemetry, no phone-home, no licence checks.
- 🛡️ **The same hardened core as our cloud** — generated from the same source, so security fixes land here every release.
- 🧩 **Extend it when you're ready** — add optional [Marketplace modules](#-grow-with-the-marketplace) as a one-off purchase, no subscription.

> Prefer someone else to run it? [**WireHQ Cloud**](https://wirehq.net) is the fully managed, hosted platform —
> [see the comparison below](#community-edition-vs-wirehq-cloud).

## 🚀 Quickstart

Three commands from clone to a running control plane:

```bash
git clone https://github.com/WireHQ-CE/wirehq-ce
cd wirehq-ce
./deploy/setup.sh
```

`setup.sh` generates fresh secrets, writes them to `deploy/.env`, and starts the stack with Docker Compose.
When it finishes, **open the URL it prints** (default `http://your-server:8080`) — a browser setup wizard
walks you through creating your admin account, and you land signed-in on the dashboard.

That's it. You're running.

<details>
<summary><b>Requirements &amp; configuration options</b></summary>

<br>

**Requirements:** Docker + Docker Compose, and `openssl` (to generate secrets). That's all — the images build
from source on first run, so there's nothing else to install.

**Common options** (set as environment variables before `setup.sh`, or edit `deploy/.env` afterwards — settings
re-sync on every `docker compose up`):

| Variable | What it does |
|---|---|
| `WEB_PORT` / `WEB_BIND` | Change the published port / bind address (default `8080` on all interfaces). |
| `APP_BASE_URL` | The URL your users reach the app on (used for links in emails). |
| `SMTP_*` | Outgoing email — required for user invites and notifications. |
| `OPEN_REGISTRATION` | `true` to let anyone sign up; **invite-only by default**. |
| `OWNER_EMAIL` / `OWNER_PASSWORD` | Skip the browser wizard for **unattended installs** (also `OWNER_NAME`, `ORGANIZATION_NAME`). |

**Going to production?** Only the web port is published — put your own TLS proxy in front for anything beyond a
trusted LAN, and set `WEB_BIND=127.0.0.1`. Back up your database any time with `deploy/backup-db.sh`.

</details>

## ✨ What's included — free forever

Everything here ships in the Community Edition at no cost:

#### 🌐 WireGuard, properly managed
- Networks, gateways and **identity-bound peers** in one control plane
- **Key generation and rotation** — keys are created and stored securely, never pasted around
- One-click **client configs with QR export**, full **server-config export**, and config-version history
- Deploy configs by **local export** (always free), or push them to a gateway over **SSH** or the
  **WireHQ Gateway container** *(remote push is part of the [Remote Deployment](https://wirehq.net/marketplace)
  Marketplace module)*

#### 👥 Your team, your rules
- Real user accounts, **role-based access control**, and multi-factor authentication (TOTP)
- **Email invites**, invite-only by default — you decide who gets in
- You're the admin of your own instance: there's no vendor tier sitting above you

#### 🛡️ Serious security, by default
- **Postgres row-level security** — tenant isolation enforced *below* the application, not just in code
- **Argon2id** password hashing and encrypted secrets at rest
- A **tamper-evident, hash-chained audit log** — every change is recorded and verifiable
- **Security updates every release**, with an in-app notification when a new version is available

#### 🔓 Open and independent
- **AGPLv3** — read it, audit it, build it yourself. Openness *is* the security model.
- **Standard WireGuard underneath**, always. Your keys and configs are yours to export and take anywhere.

## 🧩 Grow with the Marketplace

The free core does the job on its own. When you need more, activate optional modules from the
[**WireHQ Marketplace**](https://wirehq.net/marketplace) — each is a **one-off purchase, no subscription**,
and unlocks instantly on your existing install.

| Module | What it adds |
|---|---|
| **Teams** | Group members into teams with team-scoped access. |
| **Fleet Dashboard** | A live, fleet-wide overview of every gateway, agent and peer. |
| **Drift Auto-reconverge** | Automatically re-applies the correct config whenever drift is detected. |
| **Bulk Enrolment** | Create peers in batches from templates — onboard a whole team at once. |
| **Custom Roles** | Author your own roles with precise, escalation-guarded permission sets. |
| **Audit Export** | Export the audit log as CSV, JSON, or **SIEM-ready OCSF / CEF**. |
| **Customisation & Rebranding** | Your logo, accent colour, product name and favicon throughout the app. |
| **API Extensions** | Scoped API keys and signed outbound webhooks for automation. |
| **Chat Alerts** | Route notifications to a **Microsoft Teams** or **Slack** channel. |

*More on the way* — SSO / SAML, LDAP directory sync, access policies, advanced notifications, scheduled
reporting, and backup & disaster-recovery are in progress. See the [Marketplace](https://wirehq.net/marketplace)
for the current lineup and pricing.

## Community Edition vs WireHQ Cloud

Same core, two ways to run it — pick what fits.

|  | **Community Edition** | **WireHQ Cloud** |
|---|:---:|:---:|
| **Runs on** | Your own hardware | Managed by us |
| **Price** | Free forever | Free, Pro & Enterprise plans |
| **WireGuard management core** | ✅ | ✅ |
| **Hardened security (RLS, MFA, audit log)** | ✅ | ✅ |
| **Marketplace modules** | ✅ Buy once | ✅ Included by plan |
| **Setup & upgrades** | You run it (one command) | Fully managed |
| **Enterprise identity (SSO/SCIM/LDAP)** | Coming to the Marketplace | ✅ Built in |
| **Your data location** | Entirely your network | Our cloud |

👉 **Want the managed experience?** [See plans & pricing →](https://wirehq.net/pricing)

## 🔐 Security

Because the Community Edition is generated from the same codebase that runs WireHQ Cloud, **the security
fixes we ship to paying customers land here too, every release** — and your install tells you in-app when a
newer version is available.

Found a security issue? Please disclose it responsibly — contact us via [wirehq.net](https://wirehq.net)
rather than opening a public issue, so we can fix it before it's widely known.

## 📄 License

WireHQ Community Edition is released under the **[GNU Affero General Public License v3.0](LICENSE)** (AGPLv3) —
a protective copyleft licence suited to open core: if you run a modified version as a network service, you
must offer its source to your users. The hosted WireHQ Cloud (wirehq.net) is a separate, private product and
is not derived from this repository.

---

<div align="center">

**[Deploy the Community Edition](#-quickstart) · [Explore the Marketplace](https://wirehq.net/marketplace) · [WireHQ Cloud](https://wirehq.net)**

<sub>WireGuard is a registered trademark of Jason A. Donenfeld. WireHQ is an independent management platform and is not affiliated with or endorsed by the WireGuard project.</sub>

</div>
