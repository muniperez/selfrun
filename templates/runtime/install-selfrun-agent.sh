#!/usr/bin/env bash
set -euo pipefail

install -d -o root -g root -m 0755 /opt/selfrun/agent
install -d -m 0750 /var/lib/selfrun-agent

if ! id selfrun-agent >/dev/null 2>&1; then
  useradd --system --home /var/lib/selfrun-agent --shell /usr/sbin/nologin selfrun-agent
fi

for group in docker systemd-journal adm; do
  if getent group "${group}" >/dev/null 2>&1; then
    usermod -aG "${group}" selfrun-agent || true
  fi
done
chown -R selfrun-agent:selfrun-agent /var/lib/selfrun-agent
chown root:selfrun-agent /opt/selfrun/agent/config.json
chown selfrun-agent:selfrun-agent /opt/selfrun/agent/runtime-token
chmod 0640 /opt/selfrun/agent/config.json
chmod 0600 /opt/selfrun/agent/runtime-token
chown root:root /opt/selfrun/agent/selfrun-agent /opt/selfrun/agent/apply-selfrun-routing
chmod 0755 /opt/selfrun/agent/selfrun-agent /opt/selfrun/agent/apply-selfrun-routing

install -m 0644 /opt/selfrun/agent/selfrun-agent.service /etc/systemd/system/selfrun-agent.service
command -v python3 >/dev/null
install -d -o root -g root -m 0755 /etc/sudoers.d
printf '%s\n' 'selfrun-agent ALL=(root) NOPASSWD: /opt/selfrun/agent/apply-selfrun-routing ""' > /etc/sudoers.d/selfrun-agent-routing
chmod 0440 /etc/sudoers.d/selfrun-agent-routing
visudo -cf /etc/sudoers.d/selfrun-agent-routing
systemctl daemon-reload
systemctl enable selfrun-agent.service
systemctl restart selfrun-agent.service
