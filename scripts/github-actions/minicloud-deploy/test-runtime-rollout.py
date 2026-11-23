#!/usr/bin/env python3
"""Exercise health failure and interrupted recovery without a Docker daemon."""
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest


SOURCE = Path(__file__).with_name('remote-deploy.sh').read_text()
FOOTER = '\nif [ "${2:-}" = "--observability" ]; then'
STUBS = r'''
preflight() { :; }
acquire_lock() { :; }
install_runtime_maintenance() { :; }
prepare_paths() {
  [ "${RECOVER_ONLY}" = 0 ] || exit 12
  mkdir -p "$APP_DIR/shared"
  touch "$ENV_FILE"
}
verify_retained_images() { :; }
build_artifacts() { :; }
verify_image_asset_requirements() { :; }
wait_for_rollout_gate() { :; }
validate_next_configs() { :; }
migrate_postgres_if_needed() { :; }
ensure_postgres_data_owner() { :; }
start_infra() { :; }
sync_postgres_password() { :; }
deploy_observability_optional() { :; }
cleanup_deployment_artifacts() { :; }
compose_diagnostics() { :; }
caddy_diagnostics() { :; }
sudo() {
  case "$1" in
    python3 | caddy | systemctl | -u) return 0 ;;
  esac
  command "$@"
}
curl() {
  if [ "$VERSION" = broken ]; then
    echo "HTTP 503" >&2
    return 22
  fi
}
sleep() { if [ "$1" != 2 ]; then command sleep "$@"; fi; }
'''

DOCKER = r'''#!/usr/bin/env python3
import json, os, sys, time
from pathlib import Path

root = Path(os.environ['FIXTURE'])
state = root / 'containers.json'
running = json.loads(state.read_text())
args = sys.argv[1:]
if args[0] == 'ps':
    assert 'label=com.docker.compose.project=minicloud-fixture' in args
    for service in running:
        print(f'{service}-id {service}')
elif args[0] == 'inspect':
    print(running[args[-1].removesuffix('-id')])
elif args[:3] == ['stop', '--time', '5']:
    for container in args[3:]:
        del running[container.removesuffix('-id')]
    state.write_text(json.dumps(running))
elif args[0] == 'compose' and 'up' in args:
    config = next(Path(args[i + 1]) for i, arg in enumerate(args)
                  if arg == '-f' and args[i + 1].endswith('/app.compose.json'))
    services = json.loads(config.read_text())['services']
    names = args[args.index('--wait-timeout') + 2:]
    if '--pull' in args:
        assert '--no-deps' in args and args[args.index('--pull') + 1] == 'never'
        assert running == {'db': 'sha256:database'}, running
        assert services['web']['image'] == 'sha256:v2', services
        with (root / 'recoveries').open('a') as log:
            log.write('restore\n')
        if os.environ['RECOVERY'] == 'fail':
            print('previous image could not start', file=sys.stderr)
            sys.exit(1)
        if os.environ['RECOVERY'] == 'crash':
            (root / 'restoring').touch()
            time.sleep(60)
    else:
        route = Path(os.environ['ROUTE'])
        with (root / 'routes-at-start').open('a') as log:
            log.write((route.read_text() if route.exists() else '<none>\n') + '---\n')
    for name in names:
        revision = services[name]['image']
        running[name] = revision if revision.startswith('sha256:') else 'sha256:' + os.environ['VERSION']
    state.write_text(json.dumps(running))
else:
    raise AssertionError(args)
'''


class RolloutTests(unittest.TestCase):
    def setUp(self):
        work = tempfile.TemporaryDirectory(prefix='minicloud-rollout-')
        self.addCleanup(work.cleanup)
        self.root = Path(work.name)
        host = self.root / 'host'
        self.app = host / 'apps' / 'fixture'
        self.route = host / 'caddy' / 'apps' / 'fixture.caddy'
        self.route.parent.mkdir(parents=True)
        self.journal = self.app / '.rollout.pending'
        self.containers = self.root / 'containers.json'
        self.containers.write_text('{"db":"sha256:database"}')
        self.marker = self.root / 'volume' / 'data' / 'retained.txt'
        self.marker.parent.mkdir(parents=True)
        self.marker.write_text('durable marker\n')
        self.script = self.root / 'deploy.sh'
        assert FOOTER in SOURCE
        self.script.write_text(SOURCE.replace(FOOTER, '\n' + STUBS + FOOTER).replace('/opt/minicloud', str(host)))
        docker = self.root / 'docker'
        docker.write_text(DOCKER)
        docker.chmod(0o755)
        sudo = self.root / 'sudo'
        sudo.write_text('#!/bin/sh\n[ "$*" = "-n cloud-init status --wait" ]\n')
        sudo.chmod(0o755)

    def process(self, version, recovery='', recover_only=False):
        payload = self.root / 'payload.json'
        services = [{'name': 'web', 'image': 'fixture:mutable', 'port': 8080, 'public': True,
                     'host_port': 18080 if version != 'broken' else 18081,
                     'host': 'fixture.example.test' if version != 'broken' else 'candidate.example.test',
                     'health_path': '/health', 'env': {'REVISION': version},
                     'storage_mounts': [{'volume_id': 'volume', 'mount_path': '/data'}]}]
        if version == 'broken':
            services.append({'name': 'worker', 'image': 'fixture:mutable', 'port': 8080})
        payload.write_text(json.dumps({'app_name': 'fixture', 'app_env': 'production', 'customer': 'test',
                                       'deployment_id': version, 'database': 'none', 'services': services}))
        env = {**os.environ, 'PATH': f'{self.root}:{os.environ["PATH"]}', 'FIXTURE': str(self.root),
               'ROUTE': str(self.route),
               'VERSION': version, 'RECOVERY': recovery, 'RECOVER_ONLY': str(int(recover_only))}
        return subprocess.Popen(['bash', str(self.script), str(payload)], env=env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)

    def deploy(self, version, **kwargs):
        with self.process(version, **kwargs) as process:
            try:
                output, _ = process.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                raise
            return process.returncode, output

    def previous(self):
        for version in ('v1', 'v2'):
            status, output = self.deploy(version)
            self.assertEqual(status, 0, output)
            self.assertIn(f'__MINICLOUD_DEPLOY_COMPLETE__={version}', output)
            self.assertNotIn('__MINICLOUD_DEPLOY_ROLLBACK__', output)
            self.assertFalse(self.journal.exists())
            self.assertEqual(json.loads(self.containers.read_text())['web'], 'sha256:' + version)

    def assert_restored(self):
        self.assertEqual(json.loads(self.containers.read_text()), {'db': 'sha256:database', 'web': 'sha256:v2'})
        config = json.loads((self.app / 'app.compose.json').read_text())['services']['web']
        self.assertEqual('sha256:v2', config['image'])
        self.assertEqual('v2', config['environment']['REVISION'])
        self.assertEqual('/data', config['volumes'][0]['target'])
        self.assertIn('fixture.example.test', self.route.read_text())
        self.assertIn('127.0.0.1:18080', self.route.read_text())
        self.assertEqual('durable marker\n', self.marker.read_text())
        self.assertFalse(self.journal.exists())

    def routes_at_start(self):
        return (self.root / 'routes-at-start').read_text().split('---\n')[:-1]

    def assert_maintenance_while_candidate_starts(self):
        route = self.routes_at_start()[-1]
        self.assertIn('fixture.example.test {', route)
        self.assertIn('respond "Deployment in progress. Try again shortly." 503', route)
        self.assertIn('header Retry-After 30', route)
        self.assertNotIn('reverse_proxy', route)

    def test_failed_candidate_restores_exact_previous_image_and_route(self):
        self.previous()
        status, output = self.deploy('broken')
        self.assert_maintenance_while_candidate_starts()
        self.assertEqual(status, 1, output)
        self.assertIn('HTTP 503', output)
        self.assertIn('__MINICLOUD_DEPLOY_FAILURE__=health_check_failed', output)
        self.assertIn('__MINICLOUD_DEPLOY_ROLLBACK__=restored', output)
        self.assertNotIn('__MINICLOUD_DEPLOY_COMPLETE__', output)
        self.assert_restored()

    def test_healthy_replacement_becomes_public_only_after_health(self):
        self.previous()
        status, output = self.deploy('v3')
        self.assertEqual(status, 0, output)
        self.assertIn('__MINICLOUD_DEPLOY_COMPLETE__=v3', output)
        self.assert_maintenance_while_candidate_starts()
        self.assertEqual(json.loads(self.containers.read_text())['web'], 'sha256:v3')
        self.assertEqual(self.route.read_text(), 'fixture.example.test {\n  reverse_proxy 127.0.0.1:18080\n}\n')
        self.assertEqual('durable marker\n', self.marker.read_text())
        self.assertFalse(self.journal.exists())

    def test_failed_first_deploy_stops_candidate_without_activating_routes(self):
        status, output = self.deploy('broken')
        self.assertEqual(self.routes_at_start(), ['<none>\n'])
        self.assertEqual(status, 1, output)
        self.assertIn('__MINICLOUD_DEPLOY_FAILURE__=health_check_failed', output)
        self.assertIn('__MINICLOUD_DEPLOY_ROLLBACK__=no_previous', output)
        self.assertEqual(json.loads(self.containers.read_text()), {'db': 'sha256:database'})
        self.assertFalse(self.route.exists())
        self.assertFalse((self.app / 'app.compose.json').exists())
        self.assertFalse(self.journal.exists())

    def test_rollback_failure_reports_once_and_keeps_recovery_journal(self):
        self.previous()
        status, output = self.deploy('broken', recovery='fail')
        self.assertEqual(status, 1, output)
        self.assertIn('__MINICLOUD_DEPLOY_FAILURE__=health_check_failed', output)
        self.assertIn('__MINICLOUD_DEPLOY_ROLLBACK__=failed', output)
        self.assertIn('previous image could not start', output)
        self.assertEqual('restore\n', (self.root / 'recoveries').read_text())
        self.assertTrue(self.journal.exists())
        self.assertEqual(json.loads(self.containers.read_text()), {'db': 'sha256:database'})

    def test_interrupted_rollback_replays_before_new_rollout(self):
        self.previous()
        with self.process('broken', recovery='crash') as process:
            try:
                deadline = time.monotonic() + 10
                while not (self.root / 'restoring').exists():
                    self.assertIsNone(process.poll(), 'deploy exited without reaching rollback')
                    self.assertLess(time.monotonic(), deadline, 'rollback did not start')
                    time.sleep(.02)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGKILL)
                process.communicate(timeout=5)
        self.assertTrue(self.journal.exists())
        self.route.write_text('fixture.example.test {\n  reverse_proxy 127.0.0.1:18081\n}\n')
        status, output = self.deploy('v3', recover_only=True)
        self.assertEqual(status, 12, output)
        self.assertIn('Recovered interrupted app rollout', output)
        self.assertNotIn('__MINICLOUD_DEPLOY_ROLLBACK__', output)
        self.assert_restored()


if __name__ == '__main__':
    unittest.main()
