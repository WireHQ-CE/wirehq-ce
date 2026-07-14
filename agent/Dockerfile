# The "WireHQ Gateway" container — the outbound-only mTLS agent + WireGuard in one image
# (docs/16 Option A / docs/13). Run it on the box that should carry the tunnels:
#
#   docker run -d --name wirehq-gateway \
#     --cap-add=NET_ADMIN --device /dev/net/tun \
#     -p 51820:51820/udp \
#     -v wirehq-gateway:/var/lib/wirehq-agent \
#     -e WIREHQ_SERVER=https://<wirehq-host>:28443 \
#     -e WIREHQ_ENROLL_TOKEN=<single-use token from the Agents tab> \
#     wirehq-gateway
#
# WireGuard backend: wg-quick uses the host's kernel module when it's loaded (fast path — the kernel
# is shared with the container) and falls back to the bundled userspace wireguard-go otherwise
# (portability path — needs only NET_ADMIN + /dev/net/tun). For peers to reach this gateway, its UDP
# port must be reachable (public IP / router port-forward) — a container does not change that.
#
# Build context: the agent/ directory.

# ---- build (static agent binary) ----
FROM golang:1.23-alpine AS build
WORKDIR /src
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o /out/wirehq-agent .

# ---- build userspace WireGuard (not packaged in alpine) ----
# Fetched through the Go module proxy (sumdb-verified). @latest rather than the last tag: the
# 0.0.20230223 tag's vendored x/net predates Go 1.23's linkname restrictions and no longer links.
FROM golang:1.23-alpine AS wggo
RUN CGO_ENABLED=0 GOBIN=/out GOFLAGS=-trimpath go install golang.zx2c4.com/wireguard@latest \
 && mv /out/wireguard /out/wireguard-go

# ---- runtime ----
FROM alpine:3.20
# wireguard-tools = wg + wg-quick (bash script); iproute2/iptables for the interface + PostUp NAT
# rules; openresolv for wg-quick's DNS= handling. wg-quick uses the host kernel module when loaded
# and falls back to the bundled userspace wireguard-go otherwise.
RUN apk add --no-cache wireguard-tools bash iproute2 iptables openresolv
COPY --from=wggo /out/wireguard-go /usr/bin/wireguard-go

COPY --from=build /out/wirehq-agent /usr/local/bin/wirehq-agent
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh

# The enrolled identity (client key/cert, org CA, applied-state) — persist it or the gateway must
# re-enroll with a fresh token after every recreate.
VOLUME /var/lib/wirehq-agent

# Root is REQUIRED: wg-quick creates network interfaces, routes, and iptables rules — the very
# capability this container exists to exercise (scoped by --cap-add=NET_ADMIN, nothing broader;
# the image is single-purpose and runs no network-facing listener of its own).
# nosemgrep: dockerfile.security.missing-user-entrypoint.missing-user-entrypoint
ENTRYPOINT ["docker-entrypoint.sh"]
