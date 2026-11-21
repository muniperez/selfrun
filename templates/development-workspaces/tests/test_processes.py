import copy
import hashlib
import os
from pathlib import Path
import sys
import shutil
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"
CGROUP = "0::/minicloudwork.slice/minicloud-t3.service"


class FixtureProcfs(rt.Procfs):
    def __init__(self, root):
        super().__init__(root, machine="x86_64")
        self.exe = dict(path="/usr/bin/bash", uid=0, mode=0o100755)
        self.descriptor = ("/dev/pts/3", 5, 7, os.makedev(136, 3))
        self.on_tty = None
        self.frozen_state = False
        self.exe_by_pid = {}

    def executable(self, pid):
        return dict(self.exe_by_pid.get(pid, self.exe))

    def tty(self, pid):
        if self.on_tty:
            self.on_tty()
        return self.descriptor

    def frozen(self, process):
        return self.frozen_state


class IdleTerminalTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="process-fixture-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.records = {}
        self.write(1, uid=0, ppid=0, tty=0)
        self.write(100, ppid=1, tty=0, argv=["t3", "serve"])
        self.write(110, ppid=100)
        self.reader = FixtureProcfs(self.root)
        self.config = dict(workspace="/workspace", uid=2000, observedTerminalInputs=True)
        self.adapter = rt.Linux(self.config, self.reader)

    def write(self, pid, **overrides):
        record = self.records.setdefault(pid, dict(uid=2000, ppid=100, pgrp=pid, session=pid,
                                                  tty=os.makedev(136, 3), foreground=pid,
                                                  start=pid * 100, state="S", threads=1,
                                                  cgroup=CGROUP, children="", argv=["/bin/bash", "-i"],
                                                  syscall="0 0x0 0x7ffc0000 0x400 0x0 0x0 0x0 0x7ffc0100 0x7ffff000",
                                                  wchan="wait_woken"))
        record.update(overrides)
        directory = self.root / str(pid)
        directory.mkdir(exist_ok=True)
        fields = [record["state"], record["ppid"], record["pgrp"], record["session"],
                  record["tty"], record["foreground"], *([0] * 13), record["start"]]
        (directory / "stat").write_text(f"{pid} (fixture shell) " + " ".join(map(str, fields)) + "\n")
        (directory / "status").write_text("Uid:\t" + "\t".join([str(record["uid"])] * 4) +
                                         "\nThreads:\t" + str(record["threads"]) + "\n")
        (directory / "cgroup").write_text(record["cgroup"] + "\n")
        (directory / "cmdline").write_bytes(b"\0".join(a.encode() for a in record["argv"]) + b"\0")
        (directory / "syscall").write_text(record["syscall"] + "\n")
        (directory / "wchan").write_text(record["wchan"] + "\n")
        task = directory / "task" / str(pid)
        task.mkdir(parents=True, exist_ok=True)
        (task / "children").write_text(record["children"])

    def inspect(self):
        with patch.object(rt, "run", return_value="100"):
            self.adapter.idle({})

    def test_idle_t3_descendant_blocked_in_foreground_pty_read_is_recognized_only_with_opt_in(self):
        self.inspect()
        self.config.pop("observedTerminalInputs")
        with self.assertRaisesRegex(rt.Blocked, "not qualified"):
            self.inspect()
        self.config["observedTerminalInputs"] = False
        with self.assertRaises(rt.Blocked):
            self.inspect()

    def test_interactive_login_and_automatic_tty_shell_forms(self):
        for argv in (["-bash"], ["/bin/bash"], ["/bin/bash", "--noprofile", "--norc", "-i"],
                     ["/bin/bash", "-il"]):
            with self.subTest(argv=argv):
                self.write(110, argv=argv)
                self.inspect()
        self.reader.exe["path"] = "/usr/bin/dash"
        self.write(110, argv=["/bin/sh", "-i"])
        self.inspect()

    def pselect(self, mask=1, **overrides):
        (self.root / "110/mem").write_bytes(bytes(64) + mask.to_bytes(8, "little"))
        self.write(110, syscall="270 0x1 0x40 0x0 0x0 0x0 0x0 0x100 0x200",
                   wchan="do_select", **overrides)

    def test_native_pselect_requires_actual_stdin_only_fd_set_and_infinite_wait(self):
        self.pselect()
        self.inspect()
        self.write(110, wchan="get_signal")
        with self.assertRaisesRegex(rt.Blocked, "sleeping stdin-only"):
            self.inspect()
        self.reader.frozen_state = True
        self.inspect()
        for mask in (0, 2, 3):
            with self.subTest(mask=mask):
                self.pselect(mask)
                with self.assertRaises(rt.Blocked):
                    self.inspect()
        self.pselect()
        for syscall in ("270 0x2 0x40 0x0 0x0 0x0 0x0 0x100 0x200",
                        "270 0x1 0x40 0x0 0x0 0x80 0x0 0x100 0x200",
                        "270 0x1 0x40 0x80 0x0 0x0 0x0 0x100 0x200"):
            with self.subTest(syscall=syscall):
                self.write(110, syscall=syscall)
                with self.assertRaisesRegex(rt.Blocked, "infinite stdin-only"):
                    self.inspect()

    def test_pselect_fd_set_change_or_unreadable_memory_fails_closed(self):
        self.pselect()
        self.reader.on_tty = lambda: (self.root / "110/mem").write_bytes(bytes(64) + (3).to_bytes(8, "little"))
        with self.assertRaisesRegex(rt.Blocked, "wait evidence changed"):
            self.inspect()
        self.reader.on_tty = None
        self.pselect()
        (self.root / "110/mem").unlink()
        with self.assertRaisesRegex(rt.Blocked, "Cannot prove idle"):
            self.inspect()

    def telemetry_monitor(self):
        shutil.rmtree(self.root / "110")
        self.config["observedTerminalInputs"] = False
        binary = self.root / "fixture-release/resource-monitor/linux-x64/t3-resource-monitor"
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"synthetic immutable telemetry binary")
        binary.chmod(0o755)
        self.write(120, ppid=100, tty=0, threads=2, argv=[str(binary)])
        (self.root / "120/environ").write_bytes(b"HOME=/workspace/home\0")
        self.reader.exe_by_pid[120] = dict(path=str(binary), uid=0, mode=0o100755)
        for pid in (100, 120):
            (self.root / str(pid) / "fd").mkdir()
            for fd in (0, 1):
                (self.root / str(pid) / "fd" / str(fd)).symlink_to(f"pipe:[{1000 + fd}]")
        return binary

    def test_only_exact_pinned_supervised_telemetry_helper_is_infrastructure_without_terminal_opt_in(self):
        binary = self.telemetry_monitor()
        monitor = dict(path=str(binary.relative_to(self.root)), sha256=hashlib.sha256(binary.read_bytes()).hexdigest())
        with patch.object(rt, "T3_ROOT", self.root), \
                patch.object(rt, "installed_t3", return_value=dict(resourceMonitor=monitor)), \
                patch.object(rt.Linux, "verify_unit_file"):
            self.inspect()
            self.reader.exe_by_pid[120]["path"] = "/usr/bin/bash"
            with self.assertRaisesRegex(rt.Blocked, "Unregistered live coding process"):
                self.inspect()
            self.reader.exe_by_pid[120]["path"] = str(binary)
            self.write(120, argv=[str(binary), "--unknown"])
            with self.assertRaisesRegex(rt.Blocked, "arguments or unregistered children"):
                self.inspect()
            self.write(120, argv=[str(binary)])
            for change in (dict(tty=123), dict(threads=3), dict(ppid=1),
                           dict(cgroup="0::/minicloudwork.slice/other.service")):
                original = copy.deepcopy(self.records[120])
                with self.subTest(change=change):
                    self.write(120, **change)
                    with self.assertRaisesRegex(rt.Blocked, "unknown process identity"):
                        self.inspect()
                self.write(120, **original)
            self.write(130, ppid=120, tty=0, argv=["unknown-child"])
            with self.assertRaisesRegex(rt.Blocked, "[Uu]nregistered"):
                self.inspect()

    def test_connect_relay_is_infrastructure_only_for_pinned_supervised_command(self):
        binary = self.telemetry_monitor()
        argv = [str(binary), "tunnel", "--no-autoupdate", "--loglevel", "info",
                "--output", "default", "--metrics", "127.0.0.1:0", "run"]
        self.write(120, argv=argv)
        (self.root / "120/environ").write_bytes(b"TUNNEL_TOKEN=synthetic-secret\0")
        with patch.object(rt, "T3_RELAY_CLIENT", str(binary)), \
                patch.object(rt, "installed_t3",
                             return_value=dict(relayClient=dict(sha256=hashlib.sha256(binary.read_bytes()).hexdigest()))), \
                patch.object(rt.Linux, "verify_unit_file"):
            self.inspect()
            self.write(120, argv=argv[:-1] + ["--url", "http://other.invalid"])
            with self.assertRaisesRegex(rt.Blocked, "unsupported arguments"):
                self.inspect()
            self.write(120, argv=argv)
            (self.root / "120/environ").write_bytes(b"TUNNEL_TOKEN=secret\0LD_PRELOAD=evil\0")
            with self.assertRaisesRegex(rt.Blocked, "unsafe environment"):
                self.inspect()
            (self.root / "120/environ").write_bytes(b"TUNNEL_TOKEN=secret\0")
            binary.write_bytes(b"modified")
            with self.assertRaisesRegex(rt.Blocked, "digest mismatch"):
                self.inspect()

    def test_telemetry_helper_mutated_binary_owner_or_non_ipc_stdio_blocks(self):
        binary = self.telemetry_monitor()
        original = binary.read_bytes()
        monitor = dict(path=str(binary.relative_to(self.root)), sha256=hashlib.sha256(original).hexdigest())
        with patch.object(rt, "T3_ROOT", self.root), \
                patch.object(rt, "installed_t3", return_value=dict(resourceMonitor=monitor)), \
                patch.object(rt.Linux, "verify_unit_file"):
            binary.write_bytes(b"changed")
            with self.assertRaisesRegex(rt.Blocked, "digest mismatch"):
                self.inspect()
            binary.write_bytes(original)
            self.reader.exe_by_pid[120]["uid"] = 2000
            with self.assertRaisesRegex(rt.Blocked, "root-owned immutable"):
                self.inspect()
            self.reader.exe_by_pid[120]["uid"] = 0
            (self.root / "120/environ").write_bytes(b"LD_PRELOAD=/workspace/inject.so\0")
            with self.assertRaisesRegex(rt.Blocked, "unsafe loader environment"):
                self.inspect()
            (self.root / "120/environ").write_bytes(b"HOME=/workspace/home\0")
            (self.root / "120/fd/1").unlink()
            (self.root / "120/fd/1").symlink_to("/workspace/arbitrary-file")
            with self.assertRaisesRegex(rt.Blocked, "supervised stdio pipes"):
                self.inspect()

    def test_c_commands_scripts_background_and_stopped_jobs_cannot_be_classified_idle(self):
        inventory = self.reader.inventory
        for child_first in (False, True):
            with self.subTest(child_first=child_first), \
                    patch.object(self.reader, "inventory", side_effect=lambda: dict(
                        sorted(inventory().items(), reverse=child_first))):
                for argv in (["/bin/bash", "-c", "read"], ["/bin/bash", "-ic", "read"],
                             ["/bin/bash", "script.sh"], ["/bin/bash", "--rcfile", "custom"]):
                    with self.subTest(argv=argv):
                        self.write(110, argv=argv)
                        with self.assertRaisesRegex(rt.Blocked, "invocation"):
                            self.inspect()
                self.write(110, argv=["/bin/bash", "-i"])
                self.inspect()
                for state, uid in (("S", 2000), ("T", 2000), ("Z", 2000), ("S", 1001)):
                    with self.subTest(state=state, uid=uid):
                        self.write(120, ppid=110, state=state, uid=uid)
                        # Either the parent-child guard or the child's state guard can reject first.
                        with self.assertRaises(rt.Blocked):
                            self.inspect()
                shutil.rmtree(self.root / "120")

    def test_kernel_children_file_blocks_jobs_missing_from_first_inventory(self):
        self.write(110, children="120 ")
        with self.assertRaisesRegex(rt.Blocked, "children"):
            self.inspect()

    def test_unknown_agents_daemons_and_non_t3_ancestry_block(self):
        cases = [
            dict(ppid=1),
            dict(tty=0),
            dict(foreground=999),
            dict(pgrp=999),
            dict(state="R"),
            dict(state="T"),
            dict(threads=2),
            dict(cgroup="0::/unregistered.slice/unknown.service"),
        ]
        original = copy.deepcopy(self.records[110])
        for change in cases:
            with self.subTest(change=change):
                self.write(110, **dict(original, **change))
                with self.assertRaises(rt.Blocked):
                    self.inspect()
        self.write(110, **original)
        self.write(120, ppid=1, tty=0, argv=["codex"])
        with self.assertRaises(rt.Blocked):
            self.inspect()

    def test_untrusted_or_unapproved_shell_executables_block(self):
        original = dict(self.reader.exe)
        for change in (dict(path="/workspace/home/bash"), dict(uid=2000),
                       dict(mode=0o100777), dict(path="/usr/bin/python3")):
            with self.subTest(change=change):
                self.reader.exe = dict(original, **change)
                with self.assertRaisesRegex(rt.Blocked, "approved root-owned"):
                    self.inspect()

    def test_polling_or_unreadable_or_freezer_obscured_waits_require_native_adapter(self):
        original = self.records[110]["syscall"]
        for syscall in ("running", "-1 0x1 0x2", original.replace("0 0x0", "270 0x1", 1),
                        original.replace("0 0x0", "0 0x3", 1)):
            with self.subTest(syscall=syscall):
                self.write(110, syscall=syscall)
                with self.assertRaisesRegex(rt.Blocked, "Native terminal adapter"):
                    self.inspect()
        self.write(110, syscall=original, wchan="do_freezer_trap")
        with self.assertRaisesRegex(rt.Blocked, "Native terminal adapter"):
            self.inspect()
        (self.root / "110/syscall").unlink()
        with self.assertRaisesRegex(rt.Blocked, "kernel evidence"):
            self.inspect()

    def test_frozen_inventory_must_reprove_read_instead_of_reusing_previous_idle_claim(self):
        self.inspect()
        self.write(110, syscall="-1 0x1 0x2")
        with self.assertRaisesRegex(rt.Blocked, "Native terminal adapter"):
            self.inspect()

    def test_process_identity_fd_or_foreground_race_blocks(self):
        original = copy.deepcopy(self.records[110])
        for change in (dict(start=99999), dict(foreground=999),
                       dict(syscall="running"), dict(children="120 ")):
            with self.subTest(change=change):
                self.write(110, **original)
                self.reader.on_tty = lambda change=change: self.write(110, **change)
                with self.assertRaises(rt.Blocked):
                    self.inspect()

    def test_foreign_pty_and_service_identity_block(self):
        self.reader.descriptor = ("/dev/pts/99", 5, 7, os.makedev(136, 99))
        with self.assertRaisesRegex(rt.Blocked, "PTY device"):
            self.inspect()
        self.reader.descriptor = ("/dev/pts/3", 5, 7, os.makedev(136, 3))
        self.write(100, cgroup="0::/some-other.service")
        with self.assertRaisesRegex(rt.Blocked, "T3 service identity"):
            self.inspect()

    def test_late_child_or_new_process_blocks_second_inventory(self):
        original = self.reader.inventory
        calls = 0
        def late_child():
            nonlocal calls
            calls += 1
            if calls == 2:
                self.write(120, uid=1001, ppid=110)
            return original()
        with patch.object(self.reader, "inventory", side_effect=late_child):
            with self.assertRaisesRegex(rt.Blocked, "acquired children"):
                self.inspect()

    def test_real_descriptor_reader_rejects_non_tty_and_mismatched_stdin_stderr(self):
        reader = rt.Procfs(self.root, machine="x86_64")
        regular = SimpleNamespace(st_mode=0o100644, st_dev=5, st_ino=7, st_rdev=0)
        tty = SimpleNamespace(st_mode=0o020600, st_dev=5, st_ino=7, st_rdev=os.makedev(136, 3))
        other = SimpleNamespace(st_mode=0o020600, st_dev=5, st_ino=8, st_rdev=os.makedev(136, 4))
        with patch.object(rt.os, "readlink", return_value="/dev/pts/3"):
            with patch.object(Path, "stat", return_value=regular):
                with self.assertRaisesRegex(rt.Blocked, "character device"):
                    reader.tty(110)
            with patch.object(Path, "stat", side_effect=[tty, other]):
                with self.assertRaisesRegex(rt.Blocked, "identity differs"):
                    reader.tty(110)


class PreviewServiceTests(unittest.TestCase):
    write = IdleTerminalTests.write
    unit = "minicloud-preview-static.service"

    def setUp(self):
        IdleTerminalTests.setUp(self)
        self.config.update(infrastructureUnits=["minicloud-t3.service", self.unit],
                           stopUnits=["minicloud-t3.service", self.unit],
                           workspace=str(self.root / "workspace"))
        self.adapter.workspace = Path(self.config["workspace"])
        self.group = "0::/minicloudwork.slice/" + self.unit
        self.write(200, ppid=1, tty=0, argv=["static-server"], cgroup=self.group)
        self.main_pid = "200"
        self.properties = dict(LoadState="loaded", FragmentPath="/etc/systemd/system/" + self.unit,
                               DropInPaths="", KillMode="process", SendSIGKILL="no",
                               Slice="minicloudwork.slice")
        self.calls = []

    def systemctl(self, argv):
        self.calls.append(argv)
        if argv[:2] == ["systemctl", "show"]:
            if "Job" in argv:
                return "ActiveState=inactive\nSubState=dead\nResult=success\nJob=\nMainPID=0\nControlPID=0"
            if "MainPID" in argv:
                return "100" if argv[2] == "minicloud-t3.service" else self.main_pid
            if "ActiveState" in argv:
                return "inactive"
            return "\n".join(key + "=" + value for key, value in self.properties.items())
        return ""

    def inspect(self):
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "verify_unit_file"):
            self.adapter.idle({})

    def test_only_registered_preview_main_is_allowed_without_terminal_opt_in(self):
        self.inspect()
        self.config["observedTerminalInputs"] = False
        self.write(110, uid=1001, ppid=1)
        self.inspect()
        self.config["infrastructureUnits"] = ["minicloud-t3.service"]
        self.config["stopUnits"] = ["minicloud-t3.service"]
        with self.assertRaisesRegex(rt.Blocked, "not qualified"):
            self.inspect()

    def test_preview_children_block_even_foreign_uid_or_t3_ancestry(self):
        for uid in (2000, 1001):
            with self.subTest(uid=uid):
                self.write(210, ppid=200, uid=uid, cgroup=self.group)
                with self.assertRaisesRegex(rt.Blocked, "unregistered children"):
                    self.inspect()
        self.write(200, ppid=100)
        self.write(210, ppid=200, uid=2000, cgroup=CGROUP)
        with self.assertRaisesRegex(rt.Blocked, "unregistered children"):
            self.inspect()

    def test_wrong_main_uid_or_cgroup_blocks(self):
        for change in (dict(uid=1001), dict(cgroup=CGROUP),
                       dict(cgroup=self.group + "/nested")):
            with self.subTest(change=change):
                self.write(200, uid=2000, cgroup=self.group)
                self.write(200, **change)
                with self.assertRaisesRegex(rt.Blocked, "Preview service identity"):
                    self.inspect()

    def test_unqualified_unit_settings_or_registration_block_before_stop(self):
        for key, value in (("KillMode", "control-group"), ("SendSIGKILL", "yes"),
                           ("Slice", "system.slice"), ("LoadState", "not-found")):
            with self.subTest(key=key):
                original = self.properties[key]
                self.properties[key] = value
                with patch.object(rt, "run", side_effect=self.systemctl), \
                        patch.object(self.adapter, "verify_unit_file"):
                    with self.assertRaisesRegex(rt.Blocked, "KillMode=process"):
                        self.adapter.stop_services()
                self.assertFalse(any(call[:2] == ["systemctl", "stop"] for call in self.calls))
                self.properties[key] = original
                self.calls.clear()
        self.config["stopUnits"].append("unregistered.service")
        with self.assertRaisesRegex(rt.Blocked, "not explicitly registered"):
            self.inspect()

    def test_invalid_names_or_missing_stop_registration_block(self):
        for name in ("minicloud-preview-.service", "minicloud-preview-UPPER.service",
                     "minicloud-preview-" + "x" * 25 + ".service", "unrelated-system.service"):
            with self.subTest(name=name):
                self.config["infrastructureUnits"] = [name]
                with self.assertRaisesRegex(rt.Blocked, "allowlist"):
                    self.inspect()
        self.config["infrastructureUnits"] = ["minicloud-t3.service", self.unit]
        self.config["stopUnits"] = ["minicloud-t3.service"]
        with self.assertRaisesRegex(rt.Blocked, "graceful stop"):
            self.inspect()

    def test_unit_fragment_and_every_dropin_must_be_trusted(self):
        self.properties["DropInPaths"] = "/etc/systemd/system/" + self.unit + ".d/operator.conf"
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "verify_unit_file") as check:
            self.adapter.idle({})
        self.assertEqual([call.args[0] for call in check.call_args_list],
                         [self.properties["FragmentPath"], self.properties["DropInPaths"]])
        unit = self.root / "operator.service"
        unit.write_text("[Service]\nKillMode=process\n")
        for uid, mode in ((2000, 0o100644), (0, 0o100664)):
            with self.subTest(uid=uid, mode=mode), \
                    patch.object(Path, "stat", return_value=SimpleNamespace(st_uid=uid, st_mode=mode)):
                with self.assertRaisesRegex(rt.Blocked, "root/operator-owned"):
                    self.adapter.verify_unit_file(str(unit))

    def test_preview_stop_queues_main_only_then_thaws_and_always_refreezes(self):
        self.config["stopUnits"] = [self.unit, "minicloud-t3.service"]
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "verify_unit_file"), \
                patch.object(self.adapter, "t3_cgroup_empty", return_value=True), \
                patch.object(self.adapter, "thaw", side_effect=lambda: self.calls.append(["thaw"])), \
                patch.object(self.adapter, "freeze", side_effect=lambda: self.calls.append(["freeze"])):
            self.adapter.stop_services()
        sequence = [call for call in self.calls if call[:2] == ["systemctl", "stop"] or
                    call in (["thaw"], ["freeze"])]
        self.assertEqual(sequence[:4], [["systemctl", "stop", "--no-block", self.unit],
                                       ["thaw"], ["systemctl", "stop", self.unit], ["freeze"]])
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "verify_unit_file"), \
                patch.object(self.adapter, "thaw", side_effect=rt.Blocked("thaw failed")), \
                patch.object(self.adapter, "freeze") as freeze:
            with self.assertRaisesRegex(rt.Blocked, "thaw failed"):
                self.adapter.stop_services()
            freeze.assert_called_once()

    def test_poststop_or_late_foreign_uid_cgroup_children_still_block(self):
        self.main_pid = "0"
        self.write(200, uid=1001, ppid=1)
        with self.assertRaisesRegex(rt.Blocked, "leftover cgroup"):
            self.inspect()
        self.main_pid = "200"
        self.write(200, uid=2000)
        inventory = self.reader.inventory
        calls = 0
        def late_child():
            nonlocal calls
            calls += 1
            if calls == 2:
                self.write(210, uid=1001, ppid=200, cgroup=self.group)
            return inventory()
        with patch.object(self.reader, "inventory", side_effect=late_child):
            with self.assertRaisesRegex(rt.Blocked, "unregistered children"):
                self.inspect()

    def test_resume_restarts_only_managed_tooling_not_registered_user_applications(self):
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "verify_unit_file"):
            self.adapter.start_services()
        self.assertEqual(self.calls[-1], ["systemctl", "start", "minicloud-t3.service"])

    def test_default_shutdown_stops_only_tooling_and_leaves_user_file_data_untouched(self):
        self.config.pop("infrastructureUnits")
        self.config.pop("stopUnits")
        self.adapter.workspace.mkdir()
        state = self.adapter.workspace / "custom-application.pid"
        state.write_bytes(b"user-owned data, not a lifecycle control file")
        with patch.object(rt, "run", side_effect=self.systemctl), \
                patch.object(self.adapter, "t3_cgroup_empty", return_value=True), \
                patch.object(self.adapter, "thaw"), patch.object(self.adapter, "freeze"):
            self.adapter.stop_services()
        stopped = [call[-1] for call in self.calls if call[:2] == ["systemctl", "stop"]]
        self.assertEqual(["minicloud-t3.service", "minicloud-t3.service"], stopped)
        self.assertEqual(b"user-owned data, not a lifecycle control file", state.read_bytes())


if __name__ == "__main__":
    unittest.main()
