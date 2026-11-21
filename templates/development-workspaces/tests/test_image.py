import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import sys
import unittest
import zipfile
from unittest.mock import Mock, patch


HERE = Path(__file__).resolve().parents[1]
ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / "image" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load("generic_image_builder", "build.py")
native = load("generic_native_qualification", "native.py")


class ImageTests(unittest.TestCase):
    def recovery_probe(self, inventories, status_changes=None):
        probe = native.Native.__new__(native.Native)
        probe.client = Mock()
        probe.directory = Path("/synthetic-native-only")
        probe.t3_project_id = "synthetic-project"
        probe.environment_id = "synthetic-environment"
        probe.snapshot = 7
        probe.run_id = "synthetic-run"
        probe.config = {"volume": {"filesystemUuid": "synthetic-filesystem"}}
        probe.expires = native.dt.datetime.now(native.dt.timezone.utc) + native.dt.timedelta(minutes=1)
        service = dict(ActiveState="active", SubState="running", Job="", MainPID=123,
                       ControlPID=0, NRestarts=0)
        probe.t3_unit = Mock(return_value=service)
        status = dict(vmId=probe.run_id, generation=1, phase="Ready")
        statuses = status_changes or [status] * len(inventories)
        probe.control = Mock(side_effect=lambda *args, **kwargs:
            statuses[min(probe.control.call_count - 1, len(statuses) - 1)] | {
                "workInventory": inventories[min(probe.control.call_count - 1, len(inventories) - 1)]})
        probe.client.call.return_value = dict(
            environmentId=probe.environment_id, realTicketWebSocketOpened=True, snapshotSequence=7)
        clock = [0]
        def advance(seconds):
            self.assertGreater(seconds, 0)
            self.assertLessEqual(seconds, 0.2)
            clock[0] += 10
        patches = (
            patch.object(probe, "t3_ready", return_value=service),
            patch.object(probe, "wait"),
            patch.object(native, "T3Client", return_value=probe.client),
            patch.object(native, "command", return_value="synthetic-filesystem"),
            patch.object(native.http.client, "HTTPConnection"),
            patch.object(native.time, "monotonic", side_effect=lambda: clock[0]),
            patch.object(native.time, "sleep", side_effect=advance),
            patch.object(probe, "check_t3_pid"),
            patch.object(probe, "t3_diagnostic_evidence",
                         return_value={"cgroupPopulated": True, "directProcessCount": 2}),
        )
        mocks = [item.start() for item in patches]
        for item in patches:
            self.addCleanup(item.stop)
        response = mocks[4].return_value.getresponse.return_value
        response.status = 200
        response.read.return_value = json.dumps({"environmentId": probe.environment_id}).encode()
        return probe, mocks, clock

    def test_native_inventory_reason_mapping_never_leaks_unknown_values(self):
        for value in (None, {}, ["sentinel"], "sentinel"):
            self.assertEqual({"reasonCode": "other_idle_blocker"},
                             native.work_inventory_evidence({"workInventory": {"reason": value}}))
        self.assertEqual(
            {"reasonCode": "unregistered_coding_process"},
            native.work_inventory_evidence({"workInventory": {
                "reason": "Unregistered live coding process; terminal input observation is not qualified"}}))

    def test_common_t3_recovery_polls_transient_busy_until_idle(self):
        inventories = [
            dict(state="BusyOrUnknown", reason="Coding process inventory changed during inspection"),
            dict(state="BusyOrUnknown", reason="Unregistered live coding process; terminal input observation is not qualified"),
            dict(state="IdleCandidate"),
        ]
        probe, mocks, _ = self.recovery_probe(inventories)
        self.assertIsNone(probe.t3_recovered(100))
        self.assertEqual(3, probe.control.call_count)
        self.assertEqual(3, mocks[3].call_count)
        self.assertEqual(3, mocks[4].call_count)
        probe.client.call.assert_called_once()
        self.assertEqual(2, mocks[6].call_count)

    def test_common_t3_recovery_times_out_busy_or_malformed_inventory(self):
        for inventory in (dict(state="BusyOrUnknown", reason="Coding process inventory changed during inspection"),
                          dict(state="BusyOrUnknown", reason="secret-sentinel"),
                          {"state": "BusyOrUnknown"}, None, "secret-sentinel",
                          {"state": ["IdleCandidate"], "reason": object()}):
            with self.subTest(inventory=inventory):
                probe, mocks, clock = self.recovery_probe([inventory])
                with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK") as caught:
                    probe.t3_recovered(100)
                self.assertEqual(30, clock[0])
                self.assertEqual(3, probe.control.call_count)
                self.assertEqual(3, mocks[6].call_count)
                self.assertNotIn("secret-sentinel", json.dumps(caught.exception.evidence))
                if isinstance(inventory, dict) and inventory.get("reason") == \
                        "Coding process inventory changed during inspection":
                    self.assertEqual("inventory_changed", caught.exception.evidence["reasonCode"])

    def test_common_t3_recovery_fails_immediately_on_identity_drift(self):
        for change in (dict(vmId="changed"), dict(generation=2), dict(phase="StorageUnavailable")):
            with self.subTest(change=change):
                status = dict(vmId="synthetic-run", generation=1, phase="Ready")
                probe, mocks, _ = self.recovery_probe(
                    [{"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}, {"state": "IdleCandidate"}],
                    [status, status | change, status])
                with self.assertRaisesRegex(native.ProbeError, "VM_GENERATION_CHANGED_DURING_T3_RECOVERY"):
                    probe.t3_recovered(100)
                self.assertEqual(2, probe.control.call_count)
                self.assertEqual(1, mocks[6].call_count)

    def test_common_t3_recovery_rechecks_t3_and_mount_identity(self):
        probe, mocks, _ = self.recovery_probe([
            {"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}])
        mocks[4].return_value.getresponse.return_value.read.side_effect = [
            json.dumps({"environmentId": probe.environment_id}).encode(),
            json.dumps({"environmentId": "changed"}).encode(),
        ]
        with self.assertRaisesRegex(native.ProbeError, "T3_ENVIRONMENT_IDENTITY_CHANGED"):
            probe.t3_recovered(100)
        self.assertEqual(2, probe.control.call_count)
        self.assertEqual(1, mocks[6].call_count)
        probe, mocks, _ = self.recovery_probe([
            {"state": "BusyOrUnknown"}, {"state": "IdleCandidate"}])
        mocks[3].side_effect = ["synthetic-filesystem", "changed"]
        with self.assertRaisesRegex(native.ProbeError, "RETAINED_VOLUME_CHANGED_DURING_T3_RECOVERY"):
            probe.t3_recovered(100)
        self.assertEqual(2, probe.control.call_count)
        self.assertEqual(1, mocks[6].call_count)

    def test_common_t3_recovery_does_not_accept_idle_after_deadline(self):
        probe, mocks, clock = self.recovery_probe([{"state": "IdleCandidate"}])
        def late_status(*args, **kwargs):
            clock[0] = 31
            return dict(vmId=probe.run_id, generation=1, phase="Ready",
                        workInventory={"state": "IdleCandidate"})
        probe.control.side_effect = late_status
        with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK"):
            probe.t3_recovered(100)
        mocks[6].assert_not_called()

    def test_isolated_case_waits_for_startup_children_before_injecting_crash(self):
        busy = dict(state="BusyOrUnknown",
                    reason="Unregistered live coding process; terminal input observation is not qualified")
        for settles in (True, False):
            with self.subTest(settles=settles):
                inventories = [busy, busy, {"state": "IdleCandidate"}] if settles else [busy]
                probe, mocks, clock = self.recovery_probe(inventories)
                probe.config["vmId"] = probe.run_id
                probe.owns_runtime = True
                adapter = Mock()
                with patch.object(probe, "fresh_authorization"), \
                        patch.object(probe, "check_t3_freezer"), \
                        patch.object(probe, "qualify_t3_restart_policy"), \
                        patch.object(probe, "t3_stays_stopped"), \
                        patch.object(probe, "signal_t3_main") as crash:
                    if settles:
                        before = probe.t3_case_start(adapter)
                        probe.signal_t3_main(native.signal.SIGKILL)
                        self.assertEqual(before["idlePrecondition"]["idleObservations"], 3)
                        self.assertTrue(before["idlePrecondition"]["idleObserved"])
                        self.assertEqual(clock[0], 20)
                        crash.assert_called_once_with(native.signal.SIGKILL)
                    else:
                        with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK") as caught:
                            probe.t3_case_start(adapter)
                            probe.signal_t3_main(native.signal.SIGKILL)
                        self.assertEqual(clock[0], 30)
                        self.assertEqual(caught.exception.evidence["reasonCode"], "unregistered_coding_process")
                        crash.assert_not_called()
                self.assertEqual(probe.control.call_count, 3)
                probe.client.call.assert_not_called()
                adapter.stop_services.assert_called_once()
                adapter.start_services.assert_called_once()
                adapter.thaw.assert_called_once()
                adapter.freeze.assert_not_called()
                # Only the completed boundary resets; idle polling never stops/restarts or signals.
                mocks[3].assert_any_call(["systemctl", "reset-failed", native.T3_UNIT])
                self.assertEqual(sum(call.args[0][0] == "systemctl"
                                     for call in mocks[3].call_args_list), 1)

    def test_positive_idle_precondition_rejects_service_drift_and_late_idle(self):
        for change in ({"MainPID": 456}, {"NRestarts": 1}, {"ControlPID": 456},
                       {"Job": "42"}, {"SubState": "auto-restart"}):
            with self.subTest(change=change):
                probe, _, _ = self.recovery_probe([{"state": "IdleCandidate"}])
                before = probe.t3_unit.return_value
                probe.t3_unit.return_value = before | change
                with self.assertRaisesRegex(native.ProbeError, "T3_IDLE_SERVICE_IDENTITY_CHANGED"):
                    probe.t3_idle(before)
        for late in (29, 30, 31):
            with self.subTest(late=late):
                probe, _, clock = self.recovery_probe([{"state": "IdleCandidate"}])
                before = probe.t3_unit.return_value
                def slow_inventory(*args, **kwargs):
                    clock[0] = late
                    return dict(vmId=probe.run_id, generation=1, phase="Ready",
                                workInventory={"state": "IdleCandidate"})
                probe.control.side_effect = slow_inventory
                if late < 30:
                    self.assertTrue(probe.t3_idle(before)["idleObserved"])
                else:
                    with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK"):
                        probe.t3_idle(before)

    def test_t3_stop_failure_reports_counts_without_hiding_survivors(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="native-stop-evidence-", dir=ARTIFACTS) as directory:
            cgroup = Path(directory)
            (cgroup / "cgroup.events").write_text("populated 1\nfrozen 0\n")
            (cgroup / "cgroup.procs").write_text("123\n456\n789\n")
            probe = native.Native.__new__(native.Native)
            probe.rt = Mock()
            probe.rt.Linux.t3_cgroup_empty.return_value = False
            stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                           Job="", MainPID=0, ControlPID=0)
            with patch.object(native, "T3_CGROUP", cgroup), \
                    patch.object(probe, "t3_unit", return_value=stopped), \
                    patch.object(native, "command") as command, \
                    self.assertRaisesRegex(native.ProbeError, "T3_DID_NOT_REMAIN_STOPPED") as caught:
                probe.t3_stays_stopped(after_crash=True)
            evidence = caught.exception.evidence
            self.assertEqual(evidence["ActiveState"], "inactive")
            self.assertEqual(evidence["SubState"], "dead")
            self.assertTrue(evidence["cgroupPopulated"])
            self.assertEqual(evidence["directProcessCount"], 3)
            self.assertEqual(evidence["cgroupReadState"], "observed")
            self.assertFalse(evidence["mainPresent"] or evidence["controlPresent"] or evidence["jobPresent"])
            command.assert_not_called()
            self.assertNotIn("123", json.dumps(evidence))
            (cgroup / "cgroup.events").write_text("secret-args-sentinel\n")
            with patch.object(native, "T3_CGROUP", cgroup):
                evidence = native.Native.t3_diagnostic_evidence(stopped | {
                    "ActiveState": "secret-args-sentinel", "Job": "secret-args-sentinel"})
                self.assertEqual(evidence["cgroupReadState"], "malformed")
                self.assertNotIn("secret-args-sentinel", json.dumps(evidence))
                (cgroup / "cgroup.events").write_text("populated 1\n")
                (cgroup / "cgroup.procs").write_text("1\n" * 4097)
                self.assertEqual(native.Native.t3_diagnostic_evidence(stopped)["cgroupReadState"],
                                 "malformed-or-count-limit")
                (cgroup / "cgroup.procs").write_text("1" * 65537)
                self.assertEqual(native.Native.t3_diagnostic_evidence(stopped)["cgroupReadState"], "read-limit")
                (cgroup / "cgroup.procs").unlink()
                self.assertEqual(native.Native.t3_diagnostic_evidence(stopped)["cgroupReadState"],
                                 "absent-or-removed")

    def test_t3_restart_timer_is_distinct_from_a_start_job(self):
        probe = native.Native.__new__(native.Native)
        delay = dict(ActiveState="activating", SubState="auto-restart", Result="signal",
                     Job="", MainPID=0, ControlPID=0)
        with patch.object(probe, "wait_t3", return_value=delay):
            self.assertEqual(probe.t3_restart_delay("signal"), delay)
        for change in (dict(Job="42"), dict(ControlPID=123), dict(Result="oom-kill")):
            with self.subTest(change=change), patch.object(probe, "wait_t3", return_value=delay | change):
                with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_DELAY_NOT_OBSERVED"):
                    probe.t3_restart_delay("signal")

    def test_native_case_resets_accumulated_budget_only_after_qualified_drained_stop(self):
        probe = native.Native.__new__(native.Native)
        probe.run_id = "synthetic-run"
        probe.config = {"vmId": probe.run_id}
        probe.owns_runtime = True
        events = []
        adapter = Mock()
        adapter.stop_services.side_effect = lambda: events.append("stop")
        adapter.thaw.side_effect = lambda: events.append("thaw")
        adapter.start_services.side_effect = lambda: events.append("start")
        before = dict(MainPID=123, NRestarts=0)
        with patch.object(probe, "fresh_authorization"), \
                patch.object(probe, "check_t3_freezer"), \
                patch.object(probe, "check_t3_pid"), \
                patch.object(probe, "t3_ready", return_value=before), \
                patch.object(probe, "t3_idle",
                             side_effect=lambda state: events.append("idle") or {"idleObserved": True}), \
                patch.object(probe, "t3_stays_stopped",
                             side_effect=lambda **kw: events.append("drained")), \
                patch.object(native, "command") as command:
            def run(argv):
                if argv[1] == "show":
                    events.append("qualify")
                    return {"Restart": "on-failure", "RestartUSec": "5s", "OOMPolicy": "stop",
                            "StartLimitBurst": "5", "StartLimitIntervalUSec": "5min",
                            "KillMode": "process", "SendSIGKILL": "no"}[argv[4]]
                self.assertEqual(argv, ["systemctl", "reset-failed", native.T3_UNIT])
                events.append("reset")
            command.side_effect = run
            # Each independent fixture gets slot one even after earlier cases consumed starts.
            for _ in range(3):
                self.assertEqual(probe.t3_case_start(adapter),
                                 before | {"idlePrecondition": {"idleObserved": True}})
            self.assertEqual(events,
                             (["qualify"] * 7 + ["stop", "thaw", "drained", "reset", "start", "idle"]) * 3)

    def test_native_case_refuses_reset_with_unowned_frozen_undrained_or_changed_policy(self):
        for failure in ("ownership", "frozen", "policy", "drain", "counter"):
            with self.subTest(failure=failure):
                probe = native.Native.__new__(native.Native)
                probe.run_id = "synthetic-run"
                probe.config = {"vmId": probe.run_id}
                probe.owns_runtime = failure != "ownership"
                adapter = Mock()
                with patch.object(probe, "fresh_authorization"), \
                        patch.object(probe, "check_t3_freezer") as freezer, \
                        patch.object(probe, "qualify_t3_restart_policy") as policy, \
                        patch.object(probe, "t3_stays_stopped") as drained, \
                        patch.object(probe, "t3_ready", return_value={"NRestarts": 1}), \
                        patch.object(native, "command") as command:
                    if failure == "frozen":
                        freezer.side_effect = native.ProbeError("FROZEN")
                    elif failure == "policy":
                        policy.side_effect = native.ProbeError("UNQUALIFIED_T3_RESTART_POLICY")
                    elif failure == "drain":
                        drained.side_effect = native.ProbeError("T3_DID_NOT_REMAIN_STOPPED")
                    with self.assertRaises(native.ProbeError):
                        probe.t3_case_start(adapter)
                    if failure == "counter":
                        command.assert_called_once_with(["systemctl", "reset-failed", native.T3_UNIT])
                    else:
                        command.assert_not_called()
                        adapter.start_services.assert_not_called()

    def test_native_retry_budget_observes_five_real_starts_without_mid_assertion_reset(self):
        for early in (False, True):
            with self.subTest(early=early):
                probe = native.Native.__new__(native.Native)
                adapter = Mock()
                events = []
                start = dict(ActiveState="active", SubState="running", Job="", MainPID=100, NRestarts=0)
                starts = [start | dict(MainPID=100 + n, NRestarts=n) for n in range(5)]
                failed = dict(ActiveState="failed", SubState="failed", Job="", MainPID=0,
                              ControlPID=0, NRestarts=5, Result="signal")
                with patch.object(probe, "t3_case_start", return_value=start) as setup, \
                        patch.object(probe, "t3_ready", side_effect=starts[1:]) as ready, \
                        patch.object(probe, "t3_idle") as idle, \
                        patch.object(probe, "signal_t3_main", side_effect=range(100, 105)) as kill, \
                        patch.object(probe, "t3_restart_delay",
                                     side_effect=[{"NRestarts": n} for n in range(5)]), \
                        patch.object(probe, "wait_t3",
                                     side_effect=[failed] if early else starts[1:] + [failed]), \
                        patch.object(probe, "check_t3_pid"), \
                        patch.object(probe, "t3_stays_stopped",
                                     side_effect=lambda **kw: events.append(("exhausted", kw)) or failed), \
                        patch.object(probe, "t3_recovered"), \
                        patch.object(native, "command",
                                     side_effect=lambda argv: events.append(("command", argv))), \
                        patch.object(native.time, "monotonic", side_effect=[0, 30]):
                    if early:
                        with self.assertRaisesRegex(native.ProbeError, "T3_RETRY_BUDGET_EXHAUSTED_EARLY"):
                            probe.t3_retry_budget(adapter)
                        self.assertEqual(events, [])
                        adapter.start_services.assert_not_called()
                    else:
                        result = probe.t3_retry_budget(adapter)
                        self.assertEqual(result["retryBudgetSuccessfulStarts"], starts)
                        self.assertEqual(kill.call_count, 5)
                        self.assertEqual([call.args[0] for call in idle.call_args_list], starts[1:])
                        self.assertEqual(ready.call_count, 4)
                        self.assertEqual(events, [
                            ("exhausted", {"failed": True}),
                            ("command", ["systemctl", "reset-failed", native.T3_UNIT])])
                        adapter.start_services.assert_called_once()
                    setup.assert_called_once_with(adapter)

    def test_native_recovery_isolates_each_crash_and_frozen_stop_case(self):
        probe = native.Native.__new__(native.Native)
        probe.config = {}
        probe.rt = Mock()
        events = []
        before = {"MainPID": 123, "NRestarts": 0, "idlePrecondition": {"idleObserved": True}}
        limit = Mock()
        limit.read_text.return_value = "max"
        with patch.object(probe, "t3_case_start",
                          side_effect=lambda adapter: events.append("setup") or before) as setup, \
                patch.object(probe, "signal_t3_main",
                             side_effect=lambda sig: events.append("signal") or 123), \
                patch.object(probe, "t3_restart_delay"), \
                patch.object(probe, "check_t3_freezer"), \
                patch.object(probe, "t3_recovered"), \
                patch.object(probe, "t3_unit", return_value={"NRestarts": 1}), \
                patch.object(probe, "t3_stays_stopped"), \
                patch.object(probe, "t3_frozen_stop",
                             side_effect=lambda adapter, stage: events.append(stage) or {}), \
                patch.object(probe, "wait_t3", return_value={"ExecMainCode": 1, "ExecMainStatus": 130}), \
                patch.object(probe, "t3_retry_budget",
                             side_effect=lambda adapter: events.append("separate-budget") or {}), \
                patch.object(native, "T3_CGROUP") as cgroup, \
                patch.object(native, "command"):
            cgroup.__truediv__.return_value = limit
            probe.t3_recovery()
        self.assertEqual(setup.call_count, 7)
        self.assertEqual(events, ["setup", "signal", "setup", "setup", "signal",
                                  "setup", "before-freeze", "setup", "auto-restart",
                                  "setup", "start-pre", "setup", "signal", "separate-budget"])

    def test_retry_budget_does_not_crash_busy_automatic_restart_or_reset_it(self):
        probe, _, _ = self.recovery_probe([{"state": "BusyOrUnknown"}])
        before = probe.t3_unit.return_value
        start = before | {"MainPID": 100}
        adapter = Mock()
        with patch.object(probe, "t3_case_start", return_value=start), \
                patch.object(probe, "signal_t3_main", return_value=100) as crash, \
                patch.object(probe, "t3_restart_delay", return_value={"NRestarts": 0}), \
                patch.object(probe, "wait_t3", return_value=before | {"NRestarts": 1}), \
                patch.object(probe, "t3_recovered") as recovered, \
                self.assertRaisesRegex(native.ProbeError, "T3_RESTART_LEFT_UNKNOWN_WORK"):
            probe.t3_retry_budget(adapter)
        crash.assert_called_once_with(native.signal.SIGKILL)
        adapter.start_services.assert_not_called()
        recovered.assert_not_called()

    def frozen_signal_probe(self, empty=True, kill_error=True):
        probe = native.Native.__new__(native.Native)
        probe.run_id = "synthetic-native-only"
        probe.owns_runtime = True
        probe.config = {"vmId": probe.run_id}
        probe.expires = native.dt.datetime.now(native.dt.timezone.utc) + native.dt.timedelta(minutes=1)
        probe.rt = Mock()
        probe.rt.Linux.t3_cgroup_empty.return_value = empty
        before = dict(ActiveState="active", SubState="running", Result="success", Job="",
                      MainPID=123, ControlPID=0, NRestarts=2,
                      ExecMainPID=123, ExecMainCode=0, ExecMainStatus=0)
        probe.t3_unit = Mock(return_value=before)
        clock = [0]
        def advance(seconds):
            clock[0] += seconds
        def run(argv, **kwargs):
            if argv[1] == "show":
                return subprocess.CompletedProcess(argv, 0, "/minicloudwork.slice/" + native.T3_UNIT, "")
            return subprocess.CompletedProcess(
                argv, 1 if kill_error else 0, "",
                "Failed to send signal SIGKILL to auxiliary processes: Invalid argument" if kill_error else "")
        patches = (
            patch.object(probe, "fresh_authorization"),
            patch.object(probe, "check_t3_freezer"),
            patch.object(native.Path, "read_text", return_value="0::/minicloudwork.slice/" + native.T3_UNIT),
            patch.object(native.subprocess, "run", side_effect=run),
            patch.object(native.time, "monotonic", side_effect=lambda: clock[0]),
            patch.object(native.time, "sleep", side_effect=advance),
        )
        mocks = [item.start() for item in patches]
        for item in patches:
            self.addCleanup(item.stop)
        delay = before | dict(ActiveState="activating", SubState="auto-restart", Result="signal",
                              MainPID=0, ExecMainCode=native.os.CLD_KILLED,
                              ExecMainStatus=native.signal.SIGKILL)
        return probe, before, delay, mocks

    def test_frozen_t3_sigkill_requires_actual_delivery_even_after_auxiliary_einval(self):
        for kill_error in (False, True):
            with self.subTest(kill_error=kill_error):
                probe, before, delay, mocks = self.frozen_signal_probe(kill_error=kill_error)
                probe.t3_unit.side_effect = [before, before, delay]
                result = probe.signal_frozen_t3_cgroup(before)
                self.assertEqual(result["restartDelay"], delay)
                self.assertTrue(result["mainKilledBySIGKILL"] and result["serviceCgroupDrained"])
                if kill_error:
                    self.assertEqual(result["signalCommandFailure"]["exitCode"], 1)
                    self.assertEqual(result["signalCommandFailure"]["errorCategories"], ["invalid", "failed"])
                else:
                    self.assertIsNone(result["signalCommandFailure"])
                self.assertEqual(mocks[3].call_args.args[0],
                                 ["systemctl", "kill", "--kill-whom=all", "--signal=SIGKILL", native.T3_UNIT])
                self.assertEqual(mocks[1].call_args_list[0].args, (True,))
                self.assertEqual(mocks[1].call_args_list[-1].args, (True,))

    def test_frozen_t3_signal_error_or_success_cannot_hide_missing_delivery(self):
        for kill_error, change, empty in (
                (True, {"ActiveState": "active", "SubState": "running", "MainPID": 123}, True),
                (True, {}, False),
                (False, {}, False),
                (True, {"ExecMainPID": 122}, True),
                (True, {"ExecMainCode": native.os.CLD_EXITED, "ExecMainStatus": 0}, True),
                (True, {"ExecMainStatus": native.signal.SIGTERM}, True),
                (True, {"NRestarts": 3}, True),
                (True, {"ControlPID": 456}, True),
                (True, {"Job": "42"}, True)):
            with self.subTest(kill_error=kill_error, change=change, empty=empty):
                probe, before, delay, _ = self.frozen_signal_probe(empty=empty, kill_error=kill_error)
                probe.t3_unit.side_effect = [before] + [delay | change] * 20
                with self.assertRaisesRegex(native.ProbeError, "T3_CGROUP_SIGKILL_NOT_OBSERVED") as caught:
                    probe.signal_frozen_t3_cgroup(before)
                failure = caught.exception.evidence["signalCommandFailure"]
                if kill_error:
                    self.assertEqual(failure["exitCode"], 1)
                else:
                    self.assertIsNone(failure)

    def test_frozen_t3_signal_refuses_unowned_or_changed_target(self):
        for change in ("runtime", "unit-cgroup", "pid-cgroup", "main-pid", "restart"):
            with self.subTest(change=change):
                probe, before, _, mocks = self.frozen_signal_probe()
                expected = "T3_SIGNAL_TARGET_CHANGED"
                if change == "runtime":
                    probe.owns_runtime = False
                    expected = "T3_SIGNAL_REQUIRES_SYNTHETIC_RUNTIME"
                elif change == "unit-cgroup":
                    mocks[3].side_effect = None
                    mocks[3].return_value = subprocess.CompletedProcess([], 0, "/minicloudwork.slice", "")
                    expected = "T3_UNIT_NOT_IN_OWNED_CGROUP"
                elif change == "pid-cgroup":
                    mocks[2].return_value = "0::/minicloudwork.slice/" + native.T3_UNIT + "/unqualified"
                    expected = "T3_PID_NOT_IN_OWNED_CGROUP"
                else:
                    probe.t3_unit.side_effect = [before | (
                        {"MainPID": 124} if change == "main-pid" else {"NRestarts": 3})]
                with self.assertRaisesRegex(native.ProbeError, expected):
                    probe.signal_frozen_t3_cgroup(before)
                self.assertFalse(any(call.args[0][1] == "kill" for call in mocks[3].call_args_list))

    def test_frozen_t3_signal_unavailable_is_not_treated_as_auxiliary_failure(self):
        probe, before, _, mocks = self.frozen_signal_probe()
        mocks[3].side_effect = [
            subprocess.CompletedProcess([], 0, "/minicloudwork.slice/" + native.T3_UNIT, ""),
            subprocess.TimeoutExpired("systemctl", 3),
        ]
        with self.assertRaisesRegex(native.ProbeError, "COMMAND_UNAVAILABLE_OR_TIMEOUT"):
            probe.signal_frozen_t3_cgroup(before)
        self.assertEqual(probe.t3_unit.call_count, 1)

    def test_frozen_t3_signal_does_not_accept_a_late_state_observation(self):
        probe, before, delay, mocks = self.frozen_signal_probe()
        probe.t3_unit.side_effect = [before, delay]
        mocks[4].side_effect = [0, 0, 0, 0, 4]
        with self.assertRaisesRegex(native.ProbeError, "T3_CGROUP_SIGKILL_NOT_OBSERVED"):
            probe.signal_frozen_t3_cgroup(before)

    def test_frozen_t3_prestart_still_requires_frozen_job_and_one_restart(self):
        for change in ({}, {"MainPID": 789}, {"Job": ""}, {"NRestarts": 4}):
            with self.subTest(change=change):
                probe, before, delay, _ = self.frozen_signal_probe()
                prestart = delay | dict(SubState="start-pre", ControlPID=456, Job="42", NRestarts=3) | change
                adapter = Mock()
                with patch.object(probe, "t3_ready", return_value=before), \
                        patch.object(probe, "signal_frozen_t3_cgroup",
                                     return_value={"restartDelay": delay}) as delivery, \
                        patch.object(probe, "wait_t3", return_value=prestart), \
                        patch.object(probe, "t3_stays_stopped", return_value={"stopped": True}) as stopped, \
                        patch.object(probe, "check_t3_pid") as owned_pid:
                    if change:
                        with self.assertRaisesRegex(native.ProbeError, "T3_FROZEN_PRESTART_JOB_NOT_OBSERVED"):
                            probe.t3_frozen_stop(adapter, "start-pre")
                        adapter.stop_services.assert_not_called()
                        stopped.assert_not_called()
                    else:
                        result = probe.t3_frozen_stop(adapter, "start-pre")
                        self.assertEqual(result["beforeRuntimeStop"], prestart)
                        owned_pid.assert_called_once_with(456)
                        adapter.stop_services.assert_called_once()
                        stopped.assert_called_once_with(frozen=True, after_crash=True)
                    delivery.assert_called_once_with(before)
                    adapter.freeze.assert_called_once()
                    adapter.thaw.assert_called_once()

    def test_t3_cancelled_restart_allows_retained_signal_result(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        probe.rt.Linux.t3_cgroup_empty.return_value = True
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        cleared = stopped | dict(Result="success")
        with patch.object(probe, "t3_unit", side_effect=[stopped, cleared]), \
                patch.object(probe, "check_t3_freezer") as freezer, \
                patch.object(native.time, "monotonic", side_effect=[0, 0, 6]), \
                patch.object(native.time, "sleep"):
            self.assertEqual(probe.t3_stays_stopped(frozen=True, after_crash=True), cleared)
        self.assertEqual([call.args for call in freezer.call_args_list], [(True,), (True,)])

    def test_t3_retained_signal_does_not_hide_unexpected_stop_state(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        for state, empty, options in (
                (stopped, True, {}),
                (stopped | dict(Job="42"), True, {"after_crash": True}),
                (stopped, False, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed"), True, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed", Result="start-limit-hit"),
                 True, {"failed": True})):
            with self.subTest(state=state, empty=empty, options=options):
                probe.rt.Linux.t3_cgroup_empty.return_value = empty
                with patch.object(probe, "t3_unit", return_value=state), \
                        self.assertRaisesRegex(native.ProbeError, "T3_DID_NOT_REMAIN_STOPPED"):
                    probe.t3_stays_stopped(**options)

    def test_copilot_install_verifies_pin_and_uses_credential_free_initialize(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(builder, "download") as download, \
                patch.object(builder, "run") as run, \
                patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "is_file", return_value=True), \
                patch.object(builder.Path, "write_text") as write, patch.object(builder.Path, "chmod"), \
                patch.object(builder, "probe_copilot", return_value={"version": "1.0.114"}) as probe:
            builder.install_copilot(lock, Path("/synthetic-build-cache"))
        download.assert_called_once_with(lock["copilot"], Path("/synthetic-build-cache/copilot.tar.gz"))
        run.assert_called_once_with(["tar", "--no-same-owner", "-xf",
                                     "/synthetic-build-cache/copilot.tar.gz", "-C", "/opt/minicloud-copilot"])
        self.assertIn('exec /opt/minicloud-copilot/copilot "$@"', write.call_args.args[0])
        self.assertIn("COPILOT_AUTO_UPDATE=false", write.call_args.args[0])
        probe.assert_called_once_with(expected_version=lock["copilot"]["version"],
                                      environment={"PATH": "/usr/local/bin:/usr/bin:/bin",
                                                   "HOME": "/synthetic-build-cache"})

    def test_native_source_control_versions_use_coding_uid_and_service_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(native, "command", side_effect=["gh version " + lock["gh"]["version"],
                                                         "glab version " + lock["glab"]["version"]]) as run:
            result = native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        self.assertTrue(result["githubVersionMatched"] and result["gitlabVersionMatched"])
        for call in run.call_args_list:
            self.assertEqual(call.args[0][:7],
                             ["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups", "env", "-i",
                              "HOME=/workspace/home"])
            self.assertIn("PATH=/usr/local/bin:/usr/bin:/bin", call.args[0])

    def test_native_source_control_rejects_missing_binaries_wrong_version_or_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        for outputs in ([native.ProbeError("COMMAND_UNAVAILABLE_OR_TIMEOUT")],
                        ["gh version " + lock["gh"]["version"], native.ProbeError("COMMAND_FAILED")],
                        ["gh version 0.0.0"]):
            with self.subTest(outputs=len(outputs)), patch.object(native, "command", side_effect=outputs):
                with self.assertRaises(native.ProbeError):
                    native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        with patch.object(native, "command") as run:
            with self.assertRaisesRegex(native.ProbeError, "UNEXPECTED_T3_SERVICE_PATH"):
                native.source_control_versions("/usr/bin:/bin", lock)
            run.assert_not_called()

    @unittest.skipUnless(shutil.which("node"), "Optional Node validates the native T3 controller")
    def test_pinned_t3_source_control_discovery_fails_closed_and_redacts_auth_output(self):
        module = (HERE / "image/source-control-probe.mjs").as_uri()
        script = """
import assert from "node:assert/strict";
import { sourceControlEvidence } from MODULE;
const versions = { gh: "2.83.1", glab: "1.74.0" };
const discovery = { sourceControlProviders: [
  { kind: "github", executable: "gh", status: "available", version: { _tag: "Some", value: "gh version 2.83.1" },
    auth: { status: "unauthenticated", detail: "sensitive-diagnostic", account: "private-account" } },
  { kind: "gitlab", executable: "glab", status: "available", version: { _tag: "Some", value: "glab 1.74.0 (build)" },
    auth: { status: "unauthenticated" } }
] };
const evidence = sourceControlEvidence(discovery, versions);
assert.equal(evidence.pinnedT3SourceControlDiscoveryChecked, true);
assert.equal(JSON.stringify(evidence).includes("sensitive"), false);
assert.equal(JSON.stringify(evidence).includes("private-account"), false);
for (const index of [0, 1]) {
  for (const change of [{ status: "missing" }, { auth: { status: "unknown" } },
    { auth: { status: "authenticated" } }, { version: { _tag: "None" } },
    { version: { _tag: "Some", value: "2.83.10" } }]) {
    const invalid = structuredClone(discovery);
    Object.assign(invalid.sourceControlProviders[index], change);
    assert.throws(() => sourceControlEvidence(invalid, versions), /SOURCE_CONTROL_DISCOVERY_FAILED/);
  }
}
assert.throws(() => sourceControlEvidence({ sourceControlProviders: [] }, versions));
assert.throws(() => sourceControlEvidence({ sourceControlProviders:
  [...discovery.sourceControlProviders, discovery.sourceControlProviders[0]] }, versions));
""".replace("MODULE", json.dumps(module))
        subprocess.run(["node", "--input-type=module", "-e", script], check=True, capture_output=True, text=True)

    def test_native_command_retains_safe_exception_identity_without_raw_stderr(self):
        with self.assertRaises(native.ProbeError) as caught:
            native.command([sys.executable, "-c",
                            "raise FileNotFoundError(2, 'synthetic-sensitive-error-value')"])
        evidence = caught.exception.evidence
        self.assertEqual(evidence["exceptionClass"], "FileNotFoundError")
        self.assertEqual(evidence["errno"], 2)
        self.assertNotIn("synthetic-sensitive-error-value", json.dumps(evidence))

    def test_install_does_not_request_a_workspace_database(self):
        class PackagesObserved(Exception):
            pass

        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            if argv[:2] == ["apt-get", "-y"]:
                raise PackagesObserved()
            return ""

        with patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "iterdir", return_value=[]), \
                patch.object(builder.Path, "write_text"), patch.object(builder, "run", side_effect=run):
            with self.assertRaises(PackagesObserved):
                builder.install("a" * 40, "synthetic-disposable-builder")
        install = calls[-1]
        self.assertIn("age", install)
        self.assertIn("git", install)
        self.assertFalse(any(value.startswith(("postgresql", "libpq")) for value in install))


class NativeRestoreTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="native-restore-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def archive(self, files, link=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w") as archive:
            for name, value in files.items():
                member = tarfile.TarInfo(name)
                if link:
                    member.type = tarfile.SYMTYPE
                    member.linkname = "../outside"
                    archive.addfile(member)
                else:
                    member.size = len(value)
                    archive.addfile(member, io.BytesIO(value))
        output.seek(0)
        return output

    def test_restores_only_selected_nested_user_files_with_verified_hashes(self):
        files = {"config/settings.json": b'{"user":true}', "transcripts/session.txt": b"user data"}
        expected = {name: hashlib.sha256(value).hexdigest() for name, value in files.items()}
        result = native.restore_selected_files(self.archive(files), expected, self.root)
        self.assertTrue(result["allSha256Matched"])
        for name, value in files.items():
            self.assertEqual((self.root / name).read_bytes(), value)

    def test_rejects_archive_links_instead_of_following_them(self):
        expected = {"config/settings.json": hashlib.sha256(b"user data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "UNSAFE_RESTORE_MEMBER"):
            native.restore_selected_files(self.archive({"config/settings.json": b""}, link=True),
                                          expected, self.root)
        self.assertFalse((self.root / "config").exists())

    def test_rejects_wrong_hash_and_missing_selected_file(self):
        expected = {"config/settings.json": hashlib.sha256(b"expected data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "RESTORED_USER_FILE_HASH_MISMATCH"):
            native.restore_selected_files(self.archive({"config/settings.json": b"changed"}), expected, self.root)
        with self.assertRaisesRegex(native.ProbeError, "USER_BACKUP_MEMBER_MISSING"):
            native.restore_selected_files(self.archive({}), expected, self.root)


class SourceControlInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="source-control-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())

    def download(self, item, target, *, kind="file"):
        with tarfile.open(target, "w:gz") as archive:
            member = tarfile.TarInfo(item["executable"] if kind != "missing" else "unexpected")
            if kind == "link":
                member.type = tarfile.SYMTYPE
                member.linkname = "/outside"
                archive.addfile(member)
            else:
                member.size = 6
                archive.addfile(member, io.BytesIO(b"binary"))
            # Unselected members must never be extracted.
            outside = tarfile.TarInfo("../../outside")
            outside.size = 6
            archive.addfile(outside, io.BytesIO(b"unsafe"))

    def install(self, download=None, versions=None):
        with patch.object(builder, "Path", return_value=self.bin), \
                patch.object(builder, "download", side_effect=download or self.download), \
                patch.object(builder.os, "chown"), \
                patch.object(builder, "run", side_effect=versions or [
                    "gh version " + self.lock["gh"]["version"],
                    "glab " + self.lock["glab"]["version"]]):
            builder.install_source_control_clis(self.lock, self.root)

    def test_installs_both_pinned_binaries_without_extracting_other_members(self):
        self.install()
        self.assertEqual((self.bin / "gh").read_bytes(), b"binary")
        self.assertEqual((self.bin / "glab").read_bytes(), b"binary")
        self.assertEqual(set(path.name for path in self.bin.iterdir()), {"gh", "glab"})
        self.assertFalse((self.root.parent / "outside").exists())

    def test_rejects_missing_or_linked_executable(self):
        for kind in ("missing", "link"):
            with self.subTest(kind=kind), self.assertRaisesRegex(builder.Blocked, "archive layout"):
                self.install(download=lambda item, target: self.download(item, target, kind=kind))
        self.assertEqual(list(self.bin.iterdir()), [])

    def test_rejects_wrong_installed_version(self):
        with self.assertRaisesRegex(builder.Blocked, "Wrong source-control CLI version"):
            self.install(versions=["gh version 2.83.10"])

    def test_checksum_mismatch_blocks_before_any_executable_is_installed(self):
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"not-the-release")):
            with self.assertRaisesRegex(builder.Blocked, "checksum mismatch"):
                builder.download(self.lock["gh"], self.root / "download")
        self.assertEqual(list(self.bin.iterdir()), [])


class AgentInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="agent-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        chown = patch.object(builder.os, "chown")
        chown.start()
        self.addCleanup(chown.stop)

    def test_agent_pins_are_consistent_across_lock_npm_lock_and_api_agent_list(self):
        package = json.loads((HERE / "image/tools/package.json").read_text())
        npm_lock = json.loads((HERE / "image/tools/package-lock.json").read_text())
        claude = npm_lock["packages"]["node_modules/@anthropic-ai/claude-code-linux-x64"]
        self.assertEqual(package["dependencies"]["@anthropic-ai/claude-code-linux-x64"], self.lock["claude"]["version"])
        self.assertEqual((claude["version"], claude["integrity"]),
                         (self.lock["claude"]["version"], self.lock["claude"]["integrity"]))
        self.assertEqual({agent["id"]: agent["version"] for agent in self.lock["agents"]},
                         {name: self.lock[name]["version"]
                          for name in ("codex", "copilot", "claude", "antigravity", "muse")})
        for name in ("antigravity", "muse"):
            self.assertRegex(self.lock[name]["sha256"], "^[0-9a-f]{64}$")
            self.assertTrue(self.lock[name]["url"].startswith("https://"))

    def test_claude_wrapper_disables_self_update_and_requires_the_pinned_version(self):
        binary = self.root / self.lock["claude"]["executable"]
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"binary")
        wrapper = self.root / "claude"
        with patch.object(builder, "run", return_value="2.1.328 (Claude Code)") as run:
            builder.install_claude(self.lock, self.root, wrapper)
        self.assertEqual(run.call_args.args[0], [str(wrapper), "--version"])
        self.assertIn("DISABLE_AUTOUPDATER=1", wrapper.read_text())
        self.assertIn("exec " + str(binary) + ' "$@"', wrapper.read_text())
        with patch.object(builder, "run", return_value="2.1.3289 (Claude Code)"), \
                self.assertRaisesRegex(builder.Blocked, "Wrong Claude Code version"):
            builder.install_claude(self.lock, self.root, wrapper)
        binary.unlink()
        with self.assertRaisesRegex(builder.Blocked, "package layout"):
            builder.install_claude(self.lock, self.root, wrapper)

    def antigravity(self, members, size_matches=True):
        lock = json.loads(json.dumps(self.lock))
        lock["antigravity"]["members"] = {"agy_acp_server.par": 3, "localharness_external": 4}

        def download(item, target):
            with zipfile.ZipFile(target, "w") as archive:
                for name, data in members.items():
                    archive.writestr(name, data)
            item["size"] = target.stat().st_size if size_matches else 1
        destination = self.root / "antigravity"
        with patch.object(builder, "download", side_effect=download):
            builder.install_antigravity(lock, self.root, destination)
        return destination

    def test_antigravity_copies_only_pinned_members(self):
        destination = self.antigravity({"agy_acp_server.par": b"par", "localharness_external": b"ext!",
                                        "../outside": b"unsafe"})
        self.assertEqual(sorted(path.name for path in destination.iterdir()),
                         ["agy_acp_server.par", "localharness_external"])
        self.assertEqual((destination / "agy_acp_server.par").read_bytes(), b"par")
        self.assertFalse((self.root.parent / "outside").exists())
        self.assertFalse((self.root / "antigravity.zip").exists())

    def test_antigravity_rejects_missing_resized_members_or_archive_size(self):
        for members, size_matches, message in (
                ({"agy_acp_server.par": b"par"}, True, "archive layout"),
                ({"agy_acp_server.par": b"parx", "localharness_external": b"ext!"}, True, "archive layout"),
                ({"agy_acp_server.par": b"par", "localharness_external": b"ext!"}, False, "size mismatch")):
            with self.subTest(message=message, members=sorted(members)):
                shutil.rmtree(self.root / "antigravity", ignore_errors=True)
                with self.assertRaisesRegex(builder.Blocked, message):
                    self.antigravity(members, size_matches)

    def test_muse_is_checksum_and_size_verified_and_wrapped_without_self_update(self):
        lock = json.loads(json.dumps(self.lock))
        lock["muse"].update(sha256=hashlib.sha256(b"muse").hexdigest(), size=4)
        destination, wrapper = self.root / "muse", self.root / "muse-wrapper"
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")):
            builder.install_muse(lock, self.root, destination, wrapper)
        self.assertEqual((destination / "muse").read_bytes(), b"muse")
        self.assertIn("MUSE_NO_AUTO_UPDATE=1", wrapper.read_text())
        lock["muse"]["size"] = 5
        shutil.rmtree(destination)
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")), \
                self.assertRaisesRegex(builder.Blocked, "size mismatch"):
            builder.install_muse(lock, self.root, destination, wrapper)
