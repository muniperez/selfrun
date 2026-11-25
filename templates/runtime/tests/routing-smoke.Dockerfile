FROM ubuntu:24.04
RUN apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq python3 caddy sudo curl >/dev/null && rm -rf /var/lib/apt/lists/*
COPY templates/runtime/apply-selfrun-routing.py /opt/selfrun/agent/apply-selfrun-routing
RUN chmod 755 /opt/selfrun/agent/apply-selfrun-routing && useradd --system selfrun-agent && mkdir -p /opt/selfrun/apps/demo /opt/selfrun/caddy/apps /opt/selfrun/locks /var/lib/selfrun-agent && chown selfrun-agent /var/lib/selfrun-agent
COPY templates/runtime/tests/routing-smoke.sh /smoke.sh
CMD ["bash", "/smoke.sh"]
