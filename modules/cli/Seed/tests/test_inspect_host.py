# SPDX-License-Identifier: MPL-2.0
import importlib.util
import hashlib
import fcntl
import json
import os
import pathlib
import shutil
import subprocess
import sys
import unittest
import uuid
from unittest.mock import patch

artifacts = pathlib.Path(sys.argv.pop(1)).resolve()
spec = importlib.util.spec_from_file_location("seed_inspector", pathlib.Path(__file__).parents[1] / "inspect-host.py")
inspector = importlib.util.module_from_spec(spec)
helper = (pathlib.Path(__file__).parents[1] / "provider-layout.py").read_text()
exec(helper, inspector.__dict__)
spec.loader.exec_module(inspector)
seed = "a" * 32


class InspectorTests(unittest.TestCase):
    def setUp(self):
        self.root = artifacts / ("inspector-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        (self.root / "opt").mkdir(mode=0o700)
        self.commands = []
        self.containers = []
        self.volumes = b""

    def tearDown(self):
        shutil.rmtree(self.root)

    def write(self, name, value):
        p = self.root / name.lstrip("/")
        p.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        p.write_text(value if isinstance(value, str) else json.dumps(value))
        p.chmod(0o600)
        return p

    def bind(self):
        self.write("/opt/minicloud.seed-operation", seed + "\n")
        self.write("/var/lib/minicloud-seed/" + seed + "/operation", seed + "\n")

    def install(self, **updates):
        self.bind()
        state = dict(schemaVersion=1, product="minicloud", root="/opt/minicloud",
                     recoveryDir="/var/lib/minicloud-seed/" + seed + "/recovery",
                     installationId="11111111-1111-4111-8111-111111111111",
                     operationId="22222222-2222-4222-8222-222222222222",
                     releaseTag="server-v1.2.3", sourceRevision="c" * 40, phase="ready", credentialsComplete=True,
                     configuration=dict(consoleUrl="https://cloud.example.com", apiUrl="https://api.cloud.example.com",
                                        appDomain="apps.example.com"), publicAddresses=["203.0.113.1"],
                     setupTokenFile="DO-NOT-EXPORT", recoverySecret="DO-NOT-EXPORT")
        state.update(updates)
        self.write("/opt/minicloud/installation.json", state)
        return state

    def run_docker(self, args, **kwargs):
        self.commands.append(args[1:])
        if args[1:] == ["ps", "-aq"]:
            output = b"\n".join(("a" * 12 + format(i, "x")).encode() for i in range(len(self.containers)))
        elif args[1:] == ["volume", "ls", "-q"]:
            output = self.volumes
        elif args[1] == "inspect":
            output = json.dumps(self.containers).encode()
        else:
            raise AssertionError("Unexpected mutation: " + repr(args))
        return subprocess.CompletedProcess(args, 0, stdout=output)

    def observe(self, provider=None):
        def snapshot():
            return {str(p): (p.lstat().st_mode, p.lstat().st_mtime_ns, p.read_bytes() if p.is_file() else None)
                    for p in (self.root, *self.root.rglob("*"))}
        before = snapshot()
        with patch.object(inspector.shutil, "which", return_value="docker"):
            result = inspector.inspect_host(seed, self.root, os.getuid(), self.run_docker, provider=provider)
        self.assertEqual(before, snapshot(), "Check modified host files, including on failure.")
        return result

    def test_empty_host_never_creates_lock_or_metadata(self):
        self.assertEqual("empty", self.observe()["state"])
        self.assertFalse((self.root / "opt/minicloud.installation.lock").exists())

    def test_only_empty_protected_containerd_package_directories_are_accepted(self):
        containerd = self.root / "opt/containerd"
        for name in ("bin", "lib"):
            (containerd / name).mkdir(mode=0o711, parents=True, exist_ok=True)
        containerd.chmod(0o711)
        self.assertEqual("empty", self.observe()["state"])
        self.write("/opt/containerd/bin/unknown", "do not adopt")
        self.assertEqual("reconciliation_required", self.observe()["state"])
        (containerd / "bin/unknown").unlink()
        (containerd / "lib").rmdir()
        (containerd / "lib").symlink_to(containerd / "bin")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_missing_corrupt_future_and_unknown_installation_metadata_fail_closed(self):
        for content in (None, "{", {"schemaVersion": 2}, {"schemaVersion": True}):
            with self.subTest(content=content):
                p = self.root / "opt/minicloud"
                p.mkdir(mode=0o700, exist_ok=True)
                if content is not None:
                    self.write("/opt/minicloud/installation.json", content)
                self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_preparation_interruption_is_not_a_fresh_installation(self):
        self.bind()
        self.assertEqual("preparing", self.observe()["state"])
        self.write("/var/lib/minicloud-seed/" + seed + "/preparation.pending", "{")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_unknown_preparation_or_interrupted_asset_upload_is_not_an_empty_host(self):
        self.write("/var/lib/minicloud-seed/" + "b" * 32 + "/operation", "b" * 32)
        self.assertEqual("reconciliation_required", self.observe()["state"])
        shutil.rmtree(self.root / "var/lib/minicloud-seed" / ("b" * 32))
        self.bind()
        self.write("/var/lib/minicloud-seed/" + seed + "/assets/checksums.txt.pending", "partial")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_partial_installer_preserves_ids_and_never_uses_its_status_or_bootstrap(self):
        state = self.install(phase="initialized", credentialsComplete=False)
        result = self.observe()
        self.assertEqual("installed", result["state"])
        self.assertEqual(state["installationId"], result["installationId"])
        self.assertEqual(state["operationId"], result["operationId"])
        self.assertEqual("unavailable", result["health"])
        self.assertNotIn("DO-NOT-EXPORT", json.dumps(result))
        self.assertEqual([["ps", "-aq"]], self.commands)

    def test_pending_upgrade_restore_fence_or_failed_maintenance_blocks_seed(self):
        self.install()
        for field in ("pendingUpgrade", "pendingRestore", "restoreInProgress", "heldFence"):
            with self.subTest(field=field):
                self.write("/opt/minicloud/maintenance.json", {"state": "upgraded", field: {}})
                self.assertEqual("reconciliation_required", self.observe()["state"])
        self.write("/opt/minicloud/maintenance.json", {"state": "started"})
        self.assertEqual("reconciliation_required", self.observe()["state"])
        self.write("/opt/minicloud/maintenance.json", {"state": "upgraded", "lastFailure": {"fenceReleased": False}})
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_completed_signed_maintenance_is_not_mistaken_for_active_upgrade(self):
        self.install()
        self.write("/opt/minicloud/maintenance.json", {
            "operation": "upgrade", "at": "2026-11-12", "state": "upgraded", "lastSuccessfulUpgrade": {}})
        self.assertEqual("installed", self.observe()["state"])

    def test_interrupted_installer_metadata_and_busy_shared_installer_lock_fail_closed(self):
        self.install()
        pending = self.write("/opt/minicloud/installation.json.pending", "{")
        self.assertEqual("reconciliation_required", self.observe()["state"])
        pending.unlink()
        lock = self.write("/opt/minicloud.installation.lock", "")
        with lock.open("r+") as stream:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertEqual("reconciliation_required", self.observe()["state"])
        self.assertEqual("installed", self.observe()["state"])

    def test_duplicate_json_and_symlinked_binding_fail_without_following_or_writing(self):
        self.bind()
        self.write("/var/lib/minicloud-seed/" + seed + "/preparation.json",
                   '{"schemaVersion":1,"schemaVersion":2}')
        self.assertEqual("reconciliation_required", self.observe()["state"])
        binding = self.root / "opt/minicloud.seed-operation"
        binding.unlink()
        binding.symlink_to(self.root / "var/lib/minicloud-seed" / seed / "operation")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_occupied_host_with_missing_metadata_is_not_empty(self):
        (self.root / "opt/customer-data").mkdir(mode=0o700)
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_provider_layout_does_not_override_unknown_occupancy_or_conflicting_binding(self):
        self.write("/opt/digitalocean/unknown", "not a vendor agent")
        self.assertEqual("reconciliation_required", self.observe(provider="digitalocean")["state"])
        self.write("/opt/minicloud.seed-operation", "b" * 32)
        self.assertEqual("reconciliation_required", self.observe(provider="digitalocean")["state"])

    def test_stale_ready_metadata_never_means_live_ready_or_auto_unseal_after_reboot(self):
        self.install()
        for i, name in enumerate(("postgres", "openbao", "openbao-agent", "kratos", "api", "dashboard", "worker", "caddy")):
            self.containers.append({
                "Config": {"Labels": {"com.docker.compose.project": "minicloud",
                                      "com.docker.compose.project.working_dir": "/opt/minicloud/bundle",
                                      "com.docker.compose.service": name}},
                "State": {"Running": True, "Health": {"Status": "unhealthy" if name == "openbao" else "healthy"}}})
        result = self.observe()
        self.assertEqual("ready", result["progress"])
        self.assertEqual("unavailable", result["health"])
        self.assertEqual("unavailable", result["openBao"])
        self.assertEqual(["ps", "-aq"], self.commands[0])
        self.assertEqual("inspect", self.commands[1][0])


class ProviderLayoutTests(unittest.TestCase):
    """Synthetic dpkg database, real package-manager queries and real file modes."""

    def setUp(self):
        self.root = artifacts / ("provider-layout-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.db = self.root / "var/lib/dpkg"
        (self.db / "info").mkdir(parents=True)
        self.vendor = self.root / "opt/digitalocean"
        self.vendor.mkdir(parents=True)
        self.files = {
            "do-agent": ["bin/do-agent", "do-agent/scripts/update.sh"],
            "droplet-agent": ["bin/droplet-agent", "droplet-agent/scripts/update.sh"],
        }
        records = []
        for package, names in self.files.items():
            paths = {"opt/digitalocean"}
            hashes = []
            for name in names:
                p = self.vendor / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text("synthetic package fixture\n")
                p.chmod(0o755)
                paths.update(str(parent.relative_to(self.root)) for parent in p.parents
                             if parent != self.root and self.root in parent.parents)
                paths.add(str(p.relative_to(self.root)))
                hashes.append(hashlib.md5(p.read_bytes()).hexdigest() + "  " + str(p.relative_to(self.root)))
            (self.db / "info" / (package + ".list")).write_text("\n".join("/" + p for p in sorted(paths)) + "\n")
            (self.db / "info" / (package + ".md5sums")).write_text("\n".join(hashes) + "\n")
            records.append("Package: " + package + "\nStatus: install ok installed\nArchitecture: amd64\n"
                           "Version: 1.0\nMaintainer: Synthetic fixture <fixture@example.com>\n"
                           "Description: Synthetic seed package fixture\n")
        (self.db / "status").write_text("\n".join(records))
        for directory in (self.vendor, *(p for p in self.vendor.rglob("*") if p.is_dir())):
            directory.chmod(0o755)
        self.commands = []
        self.real_lstat = pathlib.Path.lstat
        self.monitoring_owner = (1000, 65534)

    def tearDown(self):
        shutil.rmtree(self.root)

    def metadata(self, p, *args, **kwargs):
        info = self.real_lstat(p, *args, **kwargs)
        if p == self.vendor or self.vendor in p.parents:
            values = list(info)
            monitoring = p.name == "do-agent" or self.vendor / "do-agent" in p.parents
            values[4], values[5] = self.monitoring_owner if monitoring else (0, 0)
            return os.stat_result(values)
        return info

    def package_command(self, args, **kwargs):
        self.commands.append(args)
        if args[0] == "dpkg-query":
            args = [args[0], "--admindir=" + str(self.db), *args[1:]]
        elif args[0] == "dpkg":
            args = [args[0], "--root=" + str(self.root), *args[1:]]
        else:
            raise AssertionError("Not an observational package command")
        return subprocess.run(args, **kwargs)

    def validate(self, provider="digitalocean"):
        with patch.object(pathlib.Path, "lstat", lambda p, *args, **kwargs: self.metadata(p, *args, **kwargs)):
            inspector.validate_provider_layout(provider, self.root, self.package_command)

    def test_stock_monitoring_nonroot_modes_and_root_console_are_verified(self):
        before = {p: (p.stat().st_mtime_ns, p.read_bytes()) for p in self.vendor.rglob("*") if p.is_file()}
        self.validate()
        self.assertEqual(2, sum(args[:2] == ["dpkg", "--verify"] for args in self.commands))
        self.monitoring_owner = (0, 0)
        self.validate()
        self.assertEqual(before, {p: (p.stat().st_mtime_ns, p.read_bytes())
                                  for p in self.vendor.rglob("*") if p.is_file()})

    def register_conffile(self, package="do-agent", name="/etc/init/do-agent.conf"):
        status = self.db / "status"
        status.write_text(status.read_text().replace(
            "Package: " + package + "\n",
            "Package: " + package + "\nConffiles:\n " + name + " "
            + hashlib.md5(b"synthetic legacy Upstart configuration\n").hexdigest() + "\n", 1))
        inventory = self.db / "info" / (package + ".list")
        inventory.write_text(inventory.read_text() + name + "\n")

    def test_missing_legacy_upstart_conffile_does_not_reject_verified_provider_payload(self):
        self.register_conffile()
        result = self.package_command(["dpkg", "--verify", "do-agent"], capture_output=True, check=False)
        self.assertEqual(0, result.returncode)
        self.assertFalse(result.stderr)
        self.assertEqual(["missing", "c", "/etc/init/do-agent.conf"], result.stdout.decode().split())
        self.validate()
        legacy = self.root / "etc/init/do-agent.conf"
        legacy.parent.mkdir(parents=True)
        legacy.write_text("changed legacy configuration\n")
        with self.assertRaises(AssertionError):
            self.validate()
        legacy.unlink()
        binary = self.vendor / "bin/do-agent"
        original = binary.read_text()
        binary.write_text("changed package binary\n")
        with self.assertRaises(AssertionError):
            self.validate()
        binary.write_text(original)
        update = self.vendor / "do-agent/scripts/update.sh"
        update.unlink()
        with self.assertRaises(AssertionError):
            self.validate()

    def test_other_missing_conffiles_and_changed_legacy_configuration_are_refused(self):
        self.register_conffile(name="/etc/init/unknown-agent.conf")
        with self.assertRaises(AssertionError):
            self.validate()
        conffile = self.root / "etc/init/unknown-agent.conf"
        conffile.parent.mkdir(parents=True)
        conffile.write_text("synthetic legacy Upstart configuration\n")
        self.validate()
        self.register_conffile(package="droplet-agent")
        with self.assertRaises(AssertionError):
            self.validate()

    def test_dpkg_errors_and_unrecognized_or_additional_diagnostics_are_refused(self):
        for output, error, code in (
                (b"missing c /etc/init/do-agent.conf\nunexpected\n", b"", 0),
                (b"??5?????? c /etc/init/do-agent.conf\n", b"", 0),
                (b"missing /etc/init/do-agent.conf\n", b"", 0),
                (b"", b"verification unavailable\n", 0),
                (b"", b"", 1)):
            with self.subTest(output=output, error=error, code=code):
                def run(args, **kwargs):
                    if args[:2] == ["dpkg", "--verify"]:
                        return subprocess.CompletedProcess(args, code, stdout=output, stderr=error)
                    return self.package_command(args, **kwargs)
                with patch.object(pathlib.Path, "lstat", lambda p, *args, **kwargs: self.metadata(p, *args, **kwargs)):
                    with self.assertRaises(AssertionError):
                        inspector.validate_provider_layout("digitalocean", self.root, run)

    def test_observation_accepts_stock_layout_but_not_workloads_bindings_or_unknown_opt(self):
        self.register_conffile()
        # Model trusted ancestors without changing permissions on this shared VM.
        def metadata(p, *args, **kwargs):
            info = self.metadata(p, *args, **kwargs)
            if p == self.root or p in self.root.parents:
                values = list(info)
                values[0] &= ~0o022
                values[4] = 0
                return os.stat_result(values)
            return info
        workloads = b""
        volumes = b""
        def run(args, **kwargs):
            if args[0] == "docker":
                return subprocess.CompletedProcess(args, 0,
                    stdout=workloads if args[1:] == ["ps", "-aq"] else volumes)
            return self.package_command(args, **kwargs)
        def observe(provider="digitalocean"):
            with patch.object(pathlib.Path, "lstat", metadata), patch.object(inspector.shutil, "which", return_value="docker"):
                return inspector.inspect_host(seed, self.root, os.getuid(), run, provider=provider)["state"]
        self.assertEqual("empty", observe())
        self.assertEqual("reconciliation_required", observe("vultr"))
        workloads = b"a" * 12
        self.assertEqual("reconciliation_required", observe())
        workloads = b""
        volumes = b"customer-volume"
        self.assertEqual("reconciliation_required", observe())
        volumes = b""
        unknown = self.root / "opt/customer-data"
        unknown.mkdir()
        self.assertEqual("reconciliation_required", observe())
        unknown.rmdir()
        binding = self.root / "opt/minicloud.seed-operation"
        binding.write_text("b" * 32)
        binding.chmod(0o600)
        self.assertEqual("reconciliation_required", observe())

    def test_other_providers_without_vendor_directory_do_not_query_packages(self):
        shutil.rmtree(self.vendor)
        self.validate("hetzner")
        self.assertFalse(self.commands)

    def test_provider_status_ownership_and_integrity_are_required(self):
        with self.assertRaises(AssertionError):
            self.validate("hetzner")
        status = self.db / "status"
        original = status.read_text()
        status.write_text(original.replace("install ok installed", "deinstall ok config-files", 1))
        with self.assertRaises(AssertionError):
            self.validate()
        status.write_text(original)
        inventory = self.db / "info/do-agent.list"
        original = inventory.read_text()
        inventory.write_text(original.replace("/opt/digitalocean/bin/do-agent\n", ""))
        with self.assertRaises(AssertionError):
            self.validate()
        inventory.write_text(original)
        binary = self.vendor / "bin/do-agent"
        binary.write_text("changed package binary\n")
        with self.assertRaises(AssertionError):
            self.validate()

    def test_missing_checksum_authority_or_failed_package_query_is_not_verified(self):
        checksums = self.db / "info/do-agent.md5sums"
        original = checksums.read_text()
        for content in ("", original.splitlines()[0] + "\n"):
            checksums.write_text(content)
            with self.assertRaises(AssertionError):
                self.validate()
        checksums.write_text(original)
        def unavailable(args, **kwargs):
            return subprocess.CompletedProcess(args, 1, stdout=b"", stderr=b"unavailable")
        with patch.object(pathlib.Path, "lstat", lambda p, *args, **kwargs: self.metadata(p, *args, **kwargs)), self.assertRaises(AssertionError):
            inspector.validate_provider_layout("digitalocean", self.root, unavailable)

    def test_unknown_files_missing_files_and_unsafe_modes_or_owners_are_refused(self):
        extra = self.vendor / "customer-data"
        extra.write_text("unknown")
        with self.assertRaises(AssertionError):
            self.validate()
        extra.unlink()
        binary = self.vendor / "bin/do-agent"
        binary.chmod(0o775)
        with self.assertRaises(AssertionError):
            self.validate()
        binary.chmod(0o755)
        self.monitoring_owner = (1000, 1000)
        with self.assertRaises(AssertionError):
            self.validate()
        self.monitoring_owner = (1000, 65534)
        self.vendor.chmod(0o775)
        with self.assertRaises(AssertionError):
            self.validate()
        self.vendor.chmod(0o755)
        update = self.vendor / "do-agent/scripts/update.sh"
        update.unlink()
        update.symlink_to(binary)
        with self.assertRaises(AssertionError):
            self.validate()
        update.unlink()
        update.write_text("synthetic package fixture\n")
        update.chmod(0o755)
        binary.unlink()
        with self.assertRaises(AssertionError):
            self.validate()

    def test_preparation_executes_the_same_validator_before_accepting_opt(self):
        self.register_conffile()
        source = (pathlib.Path(__file__).parents[1] / "prepare-host.sh").read_text()
        start = source.index("if test -e /opt/containerd")
        end = source.index("test ! -e /opt/minicloud.seed-operation.pending")
        prefix = source[start:end]
        # Supply only synthetic filesystem/metadata and dpkg authorities to the
        # embedded helper; execute the actual preparation occupancy gate in bash.
        setup = ("namespace={}\nexec(" + repr(helper) + ",namespace)\n"
                 "namespace['validate_provider_layout'](sys.argv[1],pathlib.Path(" + repr(str(self.root)) + "),run)\n")
        metadata_code = """
real_lstat=pathlib.Path.lstat
def fixture_lstat(p,*args,**kwargs):
 info=real_lstat(p,*args,**kwargs)
 values=list(info)
 values[4],values[5]=(1000,65534) if p.name=='do-agent' or 'do-agent' in p.parts else (0,0)
 return os.stat_result(values)
pathlib.Path.lstat=fixture_lstat
"""
        python = ("import os,pathlib,subprocess,sys\n" + metadata_code
                  + "def run(args,**kwargs):\n"
                  + " option='--admindir='+"
                  + repr(str(self.db)) + " if args[0]=='dpkg-query' else '--root='+"
                  + repr(str(self.root)) + "\n"
                  + " args=[args[0],option,*args[1:]]\n"
                  + " return subprocess.run(args,**kwargs)\n" + setup)
        quote = lambda value: "'" + value.replace("'", "'\\''") + "'"
        # Path rewriting applies to the shell, not the already quoted Python.
        script = "set -euo pipefail\n" + prefix.replace("/opt", str(self.root / "opt")).replace(
            "__SEED_PROVIDER_LAYOUT__", quote(python))
        for provider, expected in (("digitalocean", 0), ("vultr", 1)):
            result = subprocess.run(["bash", "-s", "--", seed, "linux/amd64", "", "local-source", provider],
                                    input=script.encode(), capture_output=True)
            self.assertEqual(expected == 0, result.returncode == 0, result.stderr.decode())
        binary = self.vendor / "bin/do-agent"
        original = binary.read_text()
        binary.write_text("changed package binary\n")
        result = subprocess.run(["bash", "-s", "--", seed, "linux/amd64", "", "local-source", "digitalocean"],
                                input=script.encode(), capture_output=True)
        self.assertNotEqual(0, result.returncode)
        binary.write_text(original)
        status = self.db / "status"
        original = status.read_text()
        status.write_text(original.replace("install ok installed", "deinstall ok config-files", 1))
        result = subprocess.run(["bash", "-s", "--", seed, "linux/amd64", "", "local-source", "digitalocean"],
                                input=script.encode(), capture_output=True)
        self.assertNotEqual(0, result.returncode)
        status.write_text(original)
        (self.root / "opt/customer-data").mkdir()
        result = subprocess.run(["bash", "-s", "--", seed, "linux/amd64", "", "local-source", "digitalocean"],
                                input=script.encode(), capture_output=True)
        self.assertNotEqual(0, result.returncode)

    def test_preparation_without_vendor_layout_does_not_require_python_before_apt(self):
        shutil.rmtree(self.vendor)
        commands = self.root / "commands"
        commands.mkdir()
        (commands / "find").symlink_to(shutil.which("find"))
        source = (pathlib.Path(__file__).parents[1] / "prepare-host.sh").read_text()
        prefix = source[source.index("if test -e /opt/containerd"):
                        source.index("test ! -e /opt/minicloud.seed-operation.pending")]
        quote = lambda value: "'" + value.replace("'", "'\\''") + "'"
        script = "set -euo pipefail\n" + prefix.replace("/opt", str(self.root / "opt")).replace(
            "__SEED_PROVIDER_LAYOUT__", quote(helper + "\nvalidate_provider_layout(sys.argv[1])\n"))
        for provider in ("hetzner", "vultr", "digitalocean"):
            result = subprocess.run(["/bin/bash", "-s", "--", seed, "linux/amd64", "", "local-source", provider],
                                    input=script.encode(), capture_output=True, env={"PATH": str(commands)})
            self.assertEqual(0, result.returncode, result.stderr.decode())
        self.vendor.symlink_to(self.root / "missing-vendor")
        result = subprocess.run(["/bin/bash", "-s", "--", seed, "linux/amd64", "", "local-source", "hetzner"],
                                input=script.encode(), capture_output=True, env={"PATH": str(commands)})
        self.assertNotEqual(0, result.returncode)
        self.vendor.unlink()


class VultrLayoutTests(unittest.TestCase):
    """Stock Vultr helper scripts are root-owned and not package-managed."""

    def setUp(self):
        self.root = artifacts / ("vultr-layout-" + uuid.uuid4().hex)
        self.vendor = self.root / "opt/vultr"
        self.vendor.mkdir(parents=True)
        for name, mode in (("find_candidate_nics.sh", 0o540), ("version.sh", 0o555)):
            p = self.vendor / name
            p.write_text("#!/bin/sh\n")
            p.chmod(mode)
        self.vendor.chmod(0o755)
        self.real_lstat = pathlib.Path.lstat

    def tearDown(self):
        for p in self.vendor.iterdir():
            p.chmod(0o600)
        shutil.rmtree(self.root)

    def metadata(self, p, *args, **kwargs):
        info = self.real_lstat(p, *args, **kwargs)
        if p == self.vendor or self.vendor in p.parents:
            values = list(info)
            values[4], values[5] = 0, 0
            return os.stat_result(values)
        return info

    def validate(self, provider="vultr"):
        with patch.object(pathlib.Path, "lstat", lambda p, *args, **kwargs: self.metadata(p, *args, **kwargs)):
            inspector.validate_provider_layout(provider, self.root, lambda *args, **kwargs: self.fail("no commands"))

    def test_stock_layout_is_admitted_only_for_vultr(self):
        before = {p: (p.stat().st_mtime_ns, p.read_bytes()) for p in self.vendor.iterdir()}
        self.validate()
        for provider in ("digitalocean", "hetzner", None):
            with self.assertRaises(AssertionError):
                self.validate(provider)
        self.assertEqual(before, {p: (p.stat().st_mtime_ns, p.read_bytes()) for p in self.vendor.iterdir()})

    def test_unknown_unsafe_or_linked_entries_are_rejected(self):
        extra = self.vendor / "agent"
        extra.write_text("unknown\n")
        with self.assertRaises(AssertionError):
            self.validate()
        extra.unlink()
        version = self.vendor / "version.sh"
        version.chmod(0o557)
        with self.assertRaises(AssertionError):
            self.validate()
        version.chmod(0o4555)
        with self.assertRaises(AssertionError):
            self.validate()
        version.chmod(0o555)
        version.unlink()
        version.symlink_to(self.vendor / "find_candidate_nics.sh")
        with self.assertRaises(AssertionError):
            self.validate()
        version.unlink()
        (self.vendor / "find_candidate_nics.sh").chmod(0o600)
        (self.vendor / "find_candidate_nics.sh").unlink()
        with self.assertRaises(AssertionError):
            self.validate()

    def test_preparation_accepts_stock_vultr_opt_only_for_vultr(self):
        source = (pathlib.Path(__file__).parents[1] / "prepare-host.sh").read_text()
        prefix = source[source.index("if test -e /opt/containerd"):
                        source.index("test ! -e /opt/minicloud.seed-operation.pending")]
        python = ("import os,pathlib,sys\nreal_lstat=pathlib.Path.lstat\n"
                  "def fixture_lstat(p,*args,**kwargs):\n"
                  " values=list(real_lstat(p,*args,**kwargs))\n"
                  " values[4],values[5]=0,0\n"
                  " return os.stat_result(values)\n"
                  "pathlib.Path.lstat=fixture_lstat\n"
                  "namespace={}\nexec(" + repr(helper) + ",namespace)\n"
                  "namespace['validate_provider_layout'](sys.argv[1],pathlib.Path(" + repr(str(self.root)) + "))\n")
        quote = lambda value: "'" + value.replace("'", "'\\''") + "'"
        script = "set -euo pipefail\n" + prefix.replace("/opt", str(self.root / "opt")).replace(
            "__SEED_PROVIDER_LAYOUT__", quote(python))
        run = lambda provider: subprocess.run(["bash", "-s", "--", seed, "linux/amd64", "", "local-source", provider],
                                              input=script.encode(), capture_output=True)
        result = run("vultr")
        self.assertEqual(0, result.returncode, result.stderr.decode())
        self.assertNotEqual(0, run("hetzner").returncode)
        (self.root / "opt/customer-data").mkdir()
        self.assertNotEqual(0, run("vultr").returncode)


if __name__ == "__main__":
    unittest.main()
