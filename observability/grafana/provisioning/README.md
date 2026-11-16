# Tenant Grafana file provisioning (T10/T20)

The tenant-facing Grafana instance intentionally provisions **no** static data
sources or dashboards here.

Per-organization Grafana organizations and per-app Loki data sources are
provisioned dynamically by the Minicloud API (T20) through the private
operator path, using desired-state records and idempotent retries. A static
data source loaded by the tenant instance would bypass the app-bound
credential model, so tenant Grafana must not load the operator-only datasource.

The operator-only provisioning file lives at
`observability/profiling/grafana-provisioning/datasources/operator.yaml`, not
under this tenant-mounted directory. The optional `grafana-operator` service
mounts that separate directory in its own Grafana instance. It is
loopback-only, uses its own operator credentials, and is not reachable
through tenant Grafana sessions or the tenant query gateway.

The tenant Grafana bind mount
(`./observability/grafana/provisioning:/etc/grafana/provisioning:ro`) keeps
the local and production provisioning paths aligned.
