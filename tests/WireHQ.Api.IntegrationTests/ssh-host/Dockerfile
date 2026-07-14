# A minimal SSH host for WireHQ deploy + telemetry tests. wireguard-tools are FAKE shims (wg-quick/
# systemctl exit 0; sudo is a passthrough; `wg` is the richer shim in ./wg that also answers `wg show
# <iface> dump` by synthesizing telemetry from the deployed config) so the tests exercise the real SSH
# orchestration (connect, upload, command sequence, verify, status) without the WireGuard kernel module
# (ADR-002). The config the provider writes lands in /etc/wireguard so the test can read it back.
FROM alpine:3.20

RUN apk add --no-cache openssh shadow coreutils \
    && ssh-keygen -A \
    && adduser -D -s /bin/sh deploy \
    && echo "deploy:deploypw" | chpasswd \
    && mkdir -p /etc/wireguard && chmod 777 /etc/wireguard \
    && mkdir -p /run/sshd \
    && printf '#!/bin/sh\nexec "$@"\n' > /usr/bin/sudo \
    && printf '#!/bin/sh\nexit 0\n' > /usr/bin/wg-quick \
    && printf '#!/bin/sh\nexit 0\n' > /usr/bin/systemctl \
    && chmod +x /usr/bin/sudo /usr/bin/wg-quick /usr/bin/systemctl \
    && sed -i 's/^#*PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config \
    && sed -i 's/^#*PermitRootLogin.*/PermitRootLogin no/' /etc/ssh/sshd_config

# The `wg` shim is a real file (readable; also serves `wg show <iface> dump`). 0755 so the non-owner
# `deploy` user can READ it — a shebang script needs read perm to run (chmod +x alone leaves it 0711).
COPY wg /usr/bin/wg
RUN chmod 0755 /usr/bin/wg

EXPOSE 22
CMD ["/usr/sbin/sshd", "-D", "-e"]
