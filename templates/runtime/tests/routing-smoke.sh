set -euo pipefail
cat > /opt/selfrun/agent/config.json <<'JSON'
{"appId":"app_demo","composeProject":"selfrun-demo"}
JSON
cat > /opt/selfrun/apps/demo/app.compose.json <<'JSON'
{"services":{"api":{"ports":["127.0.0.1:8080:8080"]},"worker":{}}}
JSON
printf '%s\n' 'selfrun-agent ALL=(root) NOPASSWD: /opt/selfrun/agent/apply-selfrun-routing ""' > /etc/sudoers.d/selfrun-agent-routing
chmod 440 /etc/sudoers.d/selfrun-agent-routing
visudo -cf /etc/sudoers.d/selfrun-agent-routing
cat > /usr/local/bin/systemctl <<'SH'
#!/bin/sh
case "$1" in
 reload) exec caddy reload --config /etc/caddy/Caddyfile ;;
 is-active) exec curl -fsS http://127.0.0.1:2019/config/ ;;
 *) exit 1 ;;
esac
SH
chmod 755 /usr/local/bin/systemctl
cat > /etc/caddy/Caddyfile <<'CADDY'
{
 local_certs
}
import /opt/selfrun/caddy/apps/*.caddy
CADDY
python3 -m http.server 8080 --bind 127.0.0.1 >/tmp/backend.log 2>&1 &
caddy start --config /etc/caddy/Caddyfile >/tmp/caddy.log 2>&1
cat > /var/lib/selfrun-agent/routing-request.json <<'JSON'
{"appId":"app_demo","composeProject":"selfrun-demo","routes":[{"hostname":"demo.app.muni.dev","pathPrefix":"/","serviceName":"api","servicePort":8080}]}
JSON
chown selfrun-agent /var/lib/selfrun-agent/routing-request.json
sudo -u selfrun-agent sudo -n /opt/selfrun/agent/apply-selfrun-routing
curl --retry 5 --retry-all-errors --retry-delay 1 -fsS --cacert /root/.local/share/caddy/pki/authorities/local/root.crt --resolve demo.app.muni.dev:443:127.0.0.1 https://demo.app.muni.dev/ >/dev/null
sudo -u selfrun-agent sudo -n /opt/selfrun/agent/apply-selfrun-routing
if sudo -u selfrun-agent sudo -n /opt/selfrun/agent/apply-selfrun-routing /etc/passwd 2>/dev/null; then exit 1; fi
python3 - <<'PY'
import json
p='/var/lib/selfrun-agent/routing-request.json'
r=json.load(open(p)); r['routes'][0]['serviceName']='worker'; open(p,'w').write(json.dumps(r))
PY
if sudo -u selfrun-agent sudo -n /opt/selfrun/agent/apply-selfrun-routing; then exit 1; fi
curl -fsS --cacert /root/.local/share/caddy/pki/authorities/local/root.crt --resolve demo.app.muni.dev:443:127.0.0.1 https://demo.app.muni.dev/ >/dev/null
printf 'PASS: restricted user, real Caddy validate/reload, unchanged routes, argument rejection, private target preserves traffic.\n'
