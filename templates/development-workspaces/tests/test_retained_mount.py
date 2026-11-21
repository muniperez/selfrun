"""Reboot startup tests: all block devices and mount commands are synthetic."""
import configparser
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt


class MountRecoveryTests(unittest.TestCase):
    def setUp(self):
        state_dir = tempfile.TemporaryDirectory()
        self.addCleanup(state_dir.cleanup)
        self.state_dir = Path(state_dir.name)
        self.claim = dict(device="/dev/disk/by-id/minicloud-fixture", providerDeviceId="minicloud-fixture",
                          filesystemUuid="recorded-uuid", initializeOwner=True)
        self.config = dict(vmId="fixture", generation=3, kind="primary", workspace="/workspace",
                           volume=self.claim)
        self.state = dict(identity=dict(vmId="fixture", generation=3, kind="primary"),
                          configDigest=rt.digest(self.config), phase="Ready")
        self.adapter = Mock(spec=rt.Linux)
        self.adapter.mounted_source.return_value = None
        self.device = self.adapter.volume_device.return_value
        for patcher in (patch.object(rt, "Linux", return_value=self.adapter),
                        patch.object(rt, "load_config", return_value=self.state)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def test_slow_device_is_mounted_once_without_reinitializing_owner(self):
        self.device.exists.side_effect = [False, True]
        with patch.object(rt.time, "sleep") as sleep:
            rt.mount_for_services(self.config, self.state_dir)
        sleep.assert_called_once()
        self.adapter.mount.assert_called_once_with(dict(self.claim, initializeOwner=False))

        self.adapter.mounted_source.return_value = "/dev/verified-fixture"
        rt.mount_for_services(self.config, self.state_dir)
        self.adapter.verify_workspace_mount.assert_called_once_with()
        self.assertEqual(1, self.adapter.mount.call_count)
        self.assertTrue(self.claim["initializeOwner"])

    def test_missing_device_times_out_without_mount_or_fallback(self):
        self.device.exists.return_value = False
        with patch.object(rt.time, "monotonic", side_effect=[100, 145]), \
                self.assertRaisesRegex(rt.Blocked, "did not appear within 45 seconds"):
            rt.mount_for_services(self.config, self.state_dir)
        self.adapter.mount.assert_not_called()
        self.assertEqual([], list(self.state_dir.iterdir()))

    def test_fenced_or_stale_generation_cannot_reattach(self):
        for change in (dict(phase="Quiesced"), dict(fenceOperation="stop-operation"),
                       dict(configDigest="different-generation")):
            with self.subTest(change=change), patch.dict(self.state, change), \
                    self.assertRaises(rt.Blocked):
                rt.mount_for_services(self.config, self.state_dir)
        self.device.exists.assert_not_called()
        self.adapter.mount.assert_not_called()

    def test_ephemeral_service_start_does_not_mount(self):
        rt.mount_for_services(dict(kind="ephemeral"), self.state_dir)
        self.assertEqual([], self.adapter.mock_calls)


class InstalledMountUnitTests(unittest.TestCase):
    def unit(self, name):
        config = configparser.ConfigParser(strict=False, interpolation=None)
        self.assertTrue(config.read(HERE / "systemd" / name))
        return config

    def test_storage_unit_blocks_t3_and_both_scratch_units(self):
        mount = "minicloud-workspace-mount.service"
        service = self.unit(mount)["Service"]
        self.assertEqual("oneshot", service["Type"])
        self.assertTrue(service["ExecStart"].endswith("/runtime.py mount-for-services"))
        self.assertGreater(int(service["TimeoutStartSec"]), rt.DEVICE_WAIT_SECONDS)
        for dependent in ("minicloud-t3.service", "minicloud-scratch-mounts.service",
                          "minicloud-scratch-watch.service"):
            with self.subTest(dependent=dependent):
                unit = self.unit(dependent)["Unit"]
                self.assertIn(mount, unit["Requires"].split())
                # The T3 and watch units each have a separate ordering on scratch setup.
                self.assertRegex((HERE / "systemd" / dependent).read_text(),
                                 rf"(?m)^After=.*\b{mount.replace('.', '[.]')}(?: |$)")

    def test_mounted_identity_failure_is_not_repaired_or_initialized(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            adapter = rt.Linux(dict(workspace=str(workspace)))
            mount = ["mount", "-t", "ext4", "-o", "nodev,nosuid", "/dev/fixture", str(workspace)]
            with patch.object(adapter, "probe_volume", return_value="/dev/fixture"), \
                    patch.object(adapter, "mounted_source", side_effect=[None, "/dev/fixture"]), \
                    patch.object(rt, "run") as command, \
                    self.assertRaisesRegex(rt.Blocked, "Missing volume ownership claim"):
                adapter.mount(dict(initializeOwner=False, providerVolumeId="vol",
                                   owner=dict(organizationId="org", workspaceId="ws", providerAccountId="account",
                                              location="region", providerVolumeId="vol")))
            self.assertEqual([mount, ["umount", str(workspace)]], [c.args[0] for c in command.call_args_list])
            self.assertEqual([], list(workspace.iterdir()))

    def test_bootstrap_rerun_reuses_existing_mount_and_leaves_owner_marker_alone(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            marker = workspace / ".minicloud-volume.json"
            marker.write_text("existing owner")
            retained = workspace / "retained-marker"
            retained.write_text("user data")
            adapter = rt.Linux(dict(workspace=str(workspace)))
            claim = dict(initializeOwner=True)
            with patch.object(adapter, "probe_volume", return_value="/dev/fixture"), \
                    patch.object(adapter, "mounted_source", return_value="/dev/fixture"), \
                    patch.object(adapter, "verify_owner") as owner, patch.object(rt, "run") as command:
                adapter.mount(claim)
                adapter.mount(claim)
            self.assertEqual([claim, claim], [c.args[0] for c in owner.call_args_list])
            command.assert_not_called()
            self.assertEqual("existing owner", marker.read_text())
            self.assertEqual("user data", retained.read_text())

    def test_nonempty_root_directory_is_never_used_for_a_retained_volume(self):
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory)
            (workspace / "stale").touch()
            adapter = rt.Linux(dict(workspace=str(workspace)))
            with patch.object(adapter, "probe_volume", return_value="/dev/fixture"), \
                    patch.object(rt.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)), \
                    patch.object(rt, "run") as command, \
                    self.assertRaisesRegex(rt.Blocked, "Refuse local workspace fallback"):
                adapter.mount({})
            command.assert_not_called()
