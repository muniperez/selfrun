#!/usr/bin/env python3
"""No cloud, mounts, Docker, credentials or installed T3 required."""
import copy
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"
ARTIFACTS.mkdir(parents=True, exist_ok=True)


def git(path, *args):
    env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null",
               GIT_AUTHOR_NAME="Fixture", GIT_AUTHOR_EMAIL="fixture@example.invalid",
               GIT_COMMITTER_NAME="Fixture", GIT_COMMITTER_EMAIL="fixture@example.invalid",
               GIT_AUTHOR_DATE="2025-01-01T00:00:00+00:00", GIT_COMMITTER_DATE="2025-01-01T00:00:00+00:00")
    return subprocess.run(["git", "-C", str(path), *args], env=env, check=True,
                          capture_output=True, text=True, timeout=30).stdout.strip()


class FakeLinux:
    def __init__(self):
        self.events = []
        self.live = False
        self.unknown = False
        self.frozen = False
        self.ro = False
        self.mounted = False
        self.uuid = "fixture-fs"
        self.owner = {"workspaceId": "workspace"}
        self.launches = 0
        self.ambiguous = False
        self.on_freeze = None

    def mount(self, claim):
        self.events.append("mount")
        rt.require(claim["filesystemUuid"] == self.uuid, "Filesystem UUID mismatch")
        rt.require(claim["owner"] == self.owner, "Unexpected volume owner claim")
        self.mounted = True

    def ephemeral(self):
        self.events.append("ephemeral")
        rt.require(not self.mounted, "Ephemeral cannot mount primary")

    def layout(self):
        self.events.append("layout")

    def restore_ownership(self):
        self.events.append("restore-ownership")

    def launch(self, task_id, argv):
        self.launches += 1
        self.events.append("launch")
        if self.ambiguous:
            raise rt.Blocked("Ambiguous launch")
        self.live = True
        return "fixture-" + task_id + ".service"

    def task_running(self, task):
        rt.require("unit" in task, "Ambiguous task launch requires reconciliation")
        return self.live

    def idle(self, tasks):
        self.events.append("idle")
        rt.require(not self.unknown, "Unregistered live process")
        for task in tasks.values():
            rt.require(not self.task_running(task), "Registered task still running")

    def verify_t3_snapshot(self, sequence):
        self.events.append("t3-proof")
        return dict(type="T3CommittedIdle", threadSequence=0, globalV2Sequence=0, applicationSequence=0)

    def freeze(self):
        self.events.append("freeze")
        self.frozen = True
        if self.on_freeze:
            self.on_freeze()

    def thaw(self):
        self.frozen = False

    def writable(self):
        self.ro = False

    def start_services(self):
        self.events.append("start-services")

    def stop_services(self):
        self.events.append("stop-services")

    def readonly(self):
        self.events.append("readonly")
        self.ro = True

    def verify_fence(self, unmounted=False):
        rt.require(self.frozen and self.ro and (not unmounted or not self.mounted),
                   "Freeze no longer valid")

    def unmount(self):
        self.events.append("unmount")
        self.mounted = False


class LayoutTests(unittest.TestCase):
    def test_layout_prepares_only_tooling_and_preserves_user_application_data(self):
        for existing_home in (False, True):
            with self.subTest(existing_home=existing_home), \
                    tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
                workspace = Path(directory)
                application = workspace / "user-data"
                application.mkdir(mode=0o700)
                marker = application / "state.bin"
                marker.write_bytes(b"arbitrary user application state\0")
                if existing_home:
                    (workspace / "home").mkdir(mode=0o700)
                adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
                previous = os.umask(0o077)
                try:
                    with patch.object(rt.os, "chown", wraps=os.chown) as chown, \
                            patch.object(rt, "run") as process:
                        adapter.layout()
                    self.assertEqual(workspace.stat().st_mode & 0o777, 0o755)
                    managed = {"repos", "t3", "transcripts", "artifacts", "config", "home"}
                    self.assertEqual(managed | {"user-data"}, {p.name for p in workspace.iterdir()})
                    for relative in managed:
                        path = workspace / relative
                        self.assertEqual(path.stat().st_mode & 0o777, 0o700)
                        self.assertTrue(os.access(path, os.X_OK))
                        chown.assert_any_call(path, os.getuid(), os.getgid())
                    codex = workspace / "home/.codex"
                    self.assertTrue(codex.is_dir())
                    self.assertEqual(codex.stat().st_mode & 0o777, 0o700)
                    chown.assert_any_call(codex, os.getuid(), os.getgid())
                    self.assertEqual(b"arbitrary user application state\0", marker.read_bytes())
                    process.assert_not_called()
                finally:
                    os.umask(previous)

    def test_managed_home_symlink_is_rejected_without_touching_user_data(self):
        for relative in ("home", "home/.codex"):
            with self.subTest(relative=relative), \
                    tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
                root = Path(directory)
                workspace = root / "workspace"
                workspace.mkdir()
                outside = root / "outside"
                outside.mkdir()
                link = workspace / relative
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(outside, target_is_directory=True)
                adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
                with patch.object(rt.os, "chown"), patch.object(rt, "run") as run:
                    with self.assertRaisesRegex(rt.Blocked, "Workspace layout symlink"):
                        adapter.layout()
                self.assertEqual([], list(outside.iterdir()))
                run.assert_not_called()

    def test_restore_assigns_generic_user_data_to_coding_identity_only(self):
        with tempfile.TemporaryDirectory(prefix="ownership-", dir=ARTIFACTS) as directory:
            workspace = Path(directory)
            data = workspace / "custom-application" / "state"
            data.mkdir(parents=True)
            marker = data / "durable.bin"
            marker.write_bytes(b"engine-independent data")
            nested = data / "lost+found"
            nested.mkdir()
            nested_owner = nested / ".minicloud-volume.json"
            nested_owner.write_text("user-owned nested file")
            owner = workspace / ".minicloud-volume.json"
            owner.write_text("{}")
            adapter = rt.Linux(dict(workspace=str(workspace), uid=2000, gid=2000))
            with patch.object(rt.os, "chown") as chown:
                adapter.restore_ownership()
            self.assertEqual({workspace / "custom-application", data, marker, nested, nested_owner},
                             {call.args[0] for call in chown.call_args_list})
            for call in chown.call_args_list:
                self.assertEqual(call.args[1:], (2000, 2000))
                self.assertFalse(call.kwargs["follow_symlinks"])

    def test_workspace_symlink_is_rejected_without_changing_target_permissions(self):
        with tempfile.TemporaryDirectory(prefix="layout-", dir=ARTIFACTS) as directory:
            root = Path(directory)
            outside = root / "outside"
            outside.mkdir(mode=0o700)
            workspace = root / "workspace"
            workspace.symlink_to(outside, target_is_directory=True)
            adapter = rt.Linux(dict(workspace=str(workspace), uid=os.getuid(), gid=os.getgid()))
            with self.assertRaisesRegex(rt.Blocked, "Workspace layout symlink"):
                adapter.layout()
            self.assertEqual(outside.stat().st_mode & 0o777, 0o700)
            self.assertEqual(list(outside.iterdir()), [])


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="case-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        (self.workspace / "repos").mkdir()
        self.remote = self.root / "remote.git"
        self.remote.mkdir()
        git(self.remote, "init", "--bare", "--initial-branch=main")
        self.repo = self.workspace / "repos/project"
        git(self.workspace, "clone", str(self.remote), str(self.repo))
        (self.repo / "file.txt").write_text("initial\n")
        (self.repo / ".gitignore").write_text("ignored/\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "base")
        self.base = git(self.repo, "rev-parse", "HEAD")
        git(self.repo, "push", "origin", "main")
        git(self.repo, "checkout", "-b", "work/vm-a")
        git(self.repo, "push", "origin", "work/vm-a")
        # A genuinely independent second clone models another VM / external remote edits.
        self.second = self.root / "other-clone"
        git(self.root, "clone", str(self.remote), str(self.second))
        self.registration = dict(path=str(self.repo), origin=str(self.remote), baseSha=self.base,
                                 branch="work/vm-a", remoteRef="refs/heads/work/vm-a",
                                 pushAuthorized=True, defaultBranches=["main"])
        self.config = dict(vmId="vm-a", generation=3, kind="ephemeral", uid=2000, gid=2000,
                           workspace=str(self.workspace), ignoredDataAcknowledged=True,
                           repositories=[self.registration])
        self.adapter = FakeLinux()
        self.g = rt.Git(allow_local=True, verification_dir=self.root / "verify")
        self.runtime = rt.Runtime(self.config, self.root / "state", self.adapter, self.g, clock=lambda: 1000)
        self.request("bootstrap")

    def request(self, action, operation="operation-a", sequence=0, **kwargs):
        return self.runtime.handle(dict(vmId="vm-a", generation=3, operationId=operation,
                                        action=action, gatewayAdmissionsFenced=True,
                                        unobservableToolsAbsent=True, t3SnapshotSequence=sequence, **kwargs))

    def assert_blocked(self, action, match=None, **kwargs):
        with self.assertRaises(rt.Blocked) as error:
            self.request(action, **kwargs)
        if match:
            self.assertIn(match, str(error.exception))
        return error.exception

    def test_clean_pushed_saved_and_revalidated_without_local_git_writes(self):
        before = (self.repo / ".git/FETCH_HEAD").read_bytes() if (self.repo / ".git/FETCH_HEAD").exists() else None
        receipt = self.request("git-save")
        self.assertEqual("GitSaved", receipt["type"])
        self.assertEqual(self.base, receipt["repositories"][0]["remoteSha"])
        self.assertFalse(receipt["ignoredDataPreserved"])
        self.assertEqual("ReceiptValid", self.request("receipt-validate", receiptId=receipt["receiptId"])["type"])
        self.assertEqual(receipt, self.request("git-save"))
        after = (self.repo / ".git/FETCH_HEAD").read_bytes() if (self.repo / ".git/FETCH_HEAD").exists() else None
        self.assertEqual(before, after)
        durable = self.root / "state/receipts" / (receipt["receiptId"] + ".json")
        self.assertEqual(receipt, json.loads(durable.read_text()))
        # Remote-only recovery in another real clone.
        git(self.second, "checkout", "work/vm-a")
        self.assertEqual(self.base, git(self.second, "rev-parse", "HEAD"))

    def test_dirty_tracked_data_blocks_and_keeps_vm_frozen(self):
        (self.repo / "file.txt").write_text("uncommitted")
        error = self.assert_blocked("git-save", "tracked modifications")
        self.assertEqual("AwaitingGitSave", error.state)
        self.assertTrue(self.adapter.frozen)
        self.assertNotIn("unmount", self.adapter.events)
        self.assert_blocked("task-start", "fenced", taskId="new", argv=["true"])

    def test_frozen_git_save_retry_uses_application_cursor_after_project_only_use(self):
        (self.repo / "file.txt").write_text("uncommitted")

        def proof(sequence):
            rt.require(sequence is None or sequence == 5, "Stale application snapshot")
            return dict(type="T3CommittedIdle", threadSequence=0, globalV2Sequence=5,
                        applicationSequence=5)

        with patch.object(self.adapter, "verify_t3_snapshot", side_effect=proof):
            self.assert_blocked("git-save", "tracked modifications", sequence=5)
            self.assertTrue(self.runtime.s["freezeCompleted"])
            self.assert_blocked("git-save", "tracked modifications", sequence=5)
        self.assertNotIn("unmount", self.adapter.events)

    def test_git_preflight_is_advisory_leaves_admissions_open_and_does_not_mutate_checkout(self):
        state = copy.deepcopy(self.runtime.s)
        fetch_head = self.repo / ".git/FETCH_HEAD"
        before = fetch_head.read_bytes() if fetch_head.exists() else None
        result = self.request("git-check")
        self.assertEqual("GitSaveCandidate", result["type"])
        self.assertFalse(result["deletionEligible"])
        self.assertEqual(state, self.runtime.s)
        self.assertFalse(self.adapter.frozen)
        self.assertEqual(before, fetch_head.read_bytes() if fetch_head.exists() else None)
        (self.repo / "file.txt").write_text("user still editing")
        error = self.assert_blocked("git-check", "tracked")
        self.assertEqual("AwaitingGitSave", error.state)
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.assertFalse(self.adapter.ro)
        self.request("activity", source="user-input")
        # A previous advisory check cannot stand in for the final frozen Git proof.
        self.assert_blocked("git-save", "tracked")

    def test_status_work_inventory_is_read_only_advisory_and_unknown_work_is_not_idle(self):
        before = copy.deepcopy(self.runtime.s)
        self.assertNotIn("workInventory", self.request("status"))
        result = self.request("status", includeWorkInventory=True)
        self.assertEqual("IdleCandidate", result["workInventory"]["state"])
        self.assertFalse(result["workInventory"]["authoritative"])
        self.adapter.unknown = True
        result = self.request("status", includeWorkInventory=True)
        self.assertEqual("BusyOrUnknown", result["workInventory"]["state"])
        self.assertEqual(before, self.runtime.s)
        self.assertFalse(self.adapter.frozen)

    def test_failed_postfreeze_t3_proof_can_resume_without_requiring_readonly_mount(self):
        with patch.object(self.adapter, "verify_t3_snapshot",
                          side_effect=rt.Blocked("T3 thread changed")):
            self.assert_blocked("git-save", "T3 thread")
        self.assertEqual("Frozen", self.runtime.s["phase"])
        self.assertFalse(self.adapter.ro)
        self.assertNotIn("stop-services", self.adapter.events)
        self.assertFalse(any(receipt["type"] in ("GitSaved", "DiscardAuthorized", "PrimaryQuiesced")
                             for receipt in self.runtime.s["receipts"].values()))
        with patch.object(self.adapter, "writable") as remount:
            self.request("resume", deletionNotCommitted=True)
            remount.assert_not_called()
        self.assertFalse(self.adapter.frozen)
        self.assertEqual("Ready", self.runtime.s["phase"])

    def test_refusal_reports_failing_freeze_stage_without_exception_or_command_output(self):
        failures = [
            ("verify_t3_snapshot", PermissionError(13, "private bearer", "/private/credential"),
             "t3-snapshot", "permission_denied", None),
            ("readonly", subprocess.CalledProcessError(
                32, ["mount", "/private/credential"], output="private bearer", stderr="private key"),
             "remount-readonly", "tool_exit", 32),
        ]
        for method, error, stage, failure_type, exit_code in failures:
            with self.subTest(method=method):
                with patch.object(self.adapter, method, side_effect=error):
                    response = self.runtime.respond(dict(
                        vmId="vm-a", generation=3, operationId="operation-a", action="git-save",
                        gatewayAdmissionsFenced=True, unobservableToolsAbsent=True, t3SnapshotSequence=0))
                self.assertFalse(response["ok"])
                self.assertEqual(stage, response["diagnostic"]["stage"])
                self.assertEqual(failure_type, response["diagnostic"]["failureType"])
                self.assertEqual(exit_code, response["diagnostic"].get("exitCode"))
                self.assertNotIn("private", json.dumps(response))
                self.assertEqual("Frozen", self.runtime.s["phase"])
                self.assertFalse(any(r["type"] == "GitSaved" for r in self.runtime.s["receipts"].values()))
                self.request("resume", deletionNotCommitted=True)

    def test_blocked_tool_failure_retains_only_allowlisted_tool_and_subcommand(self):
        with patch.object(rt.subprocess, "run", side_effect=subprocess.CalledProcessError(
                1, ["systemctl", "stop", "private.service"], stderr="private bearer")):
            with self.assertRaises(rt.Blocked) as caught:
                rt.run(["systemctl", "stop", "private.service"])
        response = rt.failure_response(caught.exception, "stop-services")
        self.assertEqual(dict(stage="stop-services", failureType="tool_exit",
                              tool="systemctl", toolAction="stop", exitCode=1), response["diagnostic"])
        self.assertNotIn("private", json.dumps(response))

    def test_untracked_data_blocks(self):
        (self.repo / "untracked.txt").write_text("unsaved")
        self.assert_blocked("git-save", "untracked")

    def test_ignored_data_policy_explicit_and_never_uploaded(self):
        (self.repo / "ignored").mkdir()
        (self.repo / "ignored/secret").write_text("synthetic-not-a-credential")
        receipt = self.request("git-save")
        self.assertFalse(receipt["ignoredDataPreserved"])
        self.assertFalse((self.second / "ignored").exists())
        self.assertNotIn("backup", self.adapter.events)
        self.assert_blocked("backup", "ephemeral")

    def test_unpushed_commit_blocks(self):
        (self.repo / "file.txt").write_text("new\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "not-pushed")
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_user_poisoned_old_verifier_cannot_forge_remote_reachability(self):
        (self.repo / "file.txt").write_text("unpushed\n")
        git(self.repo, "commit", "-am", "unpushed")
        head = git(self.repo, "rev-parse", "HEAD")
        old = self.root / "verify" / rt.digest(str(self.repo))
        old.mkdir(parents=True)
        git(old, "init", "--bare")
        git(old, "--git-dir=" + str(old), "fetch", str(self.repo), "HEAD")
        (old / "info/grafts").write_text(self.base + " " + head + "\n")
        # Demonstrate the poisoned object graph would have made the old reused verifier accept it.
        git(old, "--git-dir=" + str(old), "merge-base", "--is-ancestor", head, self.base)
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_edit_after_push_blocks(self):
        (self.repo / "file.txt").write_text("pushed\n")
        git(self.repo, "commit", "-am", "pushed")
        git(self.repo, "push", "origin", "work/vm-a")
        (self.repo / "file.txt").write_text("after-push\n")
        self.assert_blocked("git-save", "tracked")

    def test_edit_during_freeze_detected(self):
        self.adapter.on_freeze = lambda: (self.repo / "file.txt").write_text("raced")
        self.assert_blocked("git-save", "tracked")

    def test_edit_after_receipt_invalidates(self):
        receipt = self.request("git-save")
        (self.repo / "file.txt").write_text("after receipt")
        self.assert_blocked("receipt-validate", receiptId=receipt["receiptId"])

    def test_explicit_resume_revokes_old_receipt_before_enabling_writes(self):
        receipt = self.request("git-save")
        self.assert_blocked("resume", "not-delete-committed")
        self.request("resume", deletionNotCommitted=True)
        self.assertFalse(self.adapter.ro)
        self.assertFalse(self.adapter.frozen)
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.assert_blocked("receipt-validate", "Stale receipt", receiptId=receipt["receiptId"])
        (self.repo / "file.txt").write_text("user may now resolve work")
        self.assert_blocked("git-save", "tracked", operation="operation-b")

    def test_mutations_require_registered_task_and_fail_if_freeze_is_lost(self):
        self.assert_blocked("activity", "registered live", source="task-progress", taskId="missing")
        self.request("task-start", taskId="task-a", argv=["true"])
        self.request("activity", source="task-progress", taskId="task-a")
        self.assert_blocked("task-complete", "still running", taskId="task-a")
        self.adapter.live = False
        self.request("task-complete", taskId="task-a")
        original_freeze = self.adapter.freeze
        def lost_freeze():
            original_freeze()
            self.adapter.frozen = False
        with patch.object(self.adapter, "freeze", side_effect=lost_freeze):
            self.assert_blocked("git-save", "Freeze")

    def test_second_check_detects_mutation_during_remote_verification(self):
        original = self.g.inspect
        calls = 0
        def mutate(*args):
            nonlocal calls
            result = original(*args)
            calls += 1
            if calls == 1:
                (self.repo / "untracked-race").write_text("changed")
            return result
        with patch.object(self.g, "inspect", side_effect=mutate):
            self.assert_blocked("git-save", "untracked")

    def test_remote_ref_mismatch(self):
        self.registration["remoteRef"] = "refs/heads/sibling"
        self.assert_blocked("git-save", "authorized")

    def test_remote_unavailable(self):
        shutil.move(self.remote, self.root / "remote-gone")
        self.assertEqual("AwaitingGitSave", self.assert_blocked("git-save").state)

    def test_remote_rewritten_after_receipt_blocks(self):
        receipt = self.request("git-save")
        git(self.second, "checkout", "--orphan", "unrelated")
        git(self.second, "rm", "-rf", ".")
        (self.second / "unrelated").write_text("other")
        git(self.second, "add", ".")
        git(self.second, "commit", "-m", "unrelated")
        # Test fixture deliberately simulates an external force update; runtime never pushes.
        git(self.second, "push", "--force", "origin", "HEAD:refs/heads/work/vm-a")
        self.assert_blocked("receipt-validate", receiptId=receipt["receiptId"])

    def test_missing_authority_origin_branch_base(self):
        for key, value in (("pushAuthorized", False), ("origin", str(self.root / "other.git")),
                           ("branch", "main"), ("baseSha", "main")):
            with self.subTest(key=key):
                old = self.registration[key]
                self.registration[key] = value
                self.assert_blocked("git-save")
                self.registration[key] = old

    def test_lfs_and_submodules_fail_closed(self):
        cases = [
            (".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n"),
            (".gitmodules", "[submodule \"x\"]\n path=x\n url=https://example.invalid/x\n"),
            ("pointer.bin", "version https://git-lfs.github.com/spec/v1\noid sha256:0000\nsize 4\n"),
        ]
        for name, content in cases:
            with self.subTest(name=name):
                path = self.repo / name
                path.write_text(content)
                git(self.repo, "add", name)
                git(self.repo, "commit", "-m", "unsupported")
                git(self.repo, "push", "origin", "work/vm-a")
                self.assert_blocked("git-save")
                git(self.repo, "rm", name)
                git(self.repo, "commit", "-m", "remove unsupported")
                git(self.repo, "push", "origin", "work/vm-a")

    def test_unregistered_checkout_and_linked_worktree(self):
        nested = self.workspace / "repos/unknown"
        git(self.workspace, "clone", str(self.remote), str(nested))
        self.assert_blocked("git-save", "inventory")
        shutil.rmtree(nested)
        outside = self.root / "external-worktree"
        git(self.repo, "worktree", "add", "-b", "extra", str(outside), "HEAD")
        self.assert_blocked("git-save", "Unregistered Git worktree")

    def codex_probe(self, relative="home/.codex/.tmp/git-A0bC9z"):
        probe = self.workspace / relative
        probe.mkdir(parents=True)
        (probe / "objects").mkdir()
        (probe / "refs").mkdir()
        (probe / "HEAD").write_bytes(b"ref: refs/heads/main\n")
        (probe / "HEAD").chmod(0o600)
        return probe

    def test_exact_empty_codex_probes_allow_git_save_without_mutation(self):
        probes = [self.codex_probe("home/.codex/.tmp/" + name)
                  for name in ("git-A0bC9z", "git-ZvtwGU", "git-U4CEZ5")]
        def snapshot():
            return {str(path): (path.lstat().st_mode, path.lstat().st_mtime_ns,
                               path.read_bytes() if path.is_file() else None)
                    for probe in probes for path in (probe, *probe.rglob("*"))}
        before = snapshot()

        self.assertEqual("GitSaveCandidate", self.request("git-check")["type"])
        receipt = self.request("git-save")
        self.assertEqual("GitSaved", receipt["type"])
        self.assertEqual(self.base, receipt["repositories"][0]["remoteSha"])
        self.assertEqual("ReceiptValid", self.request("receipt-validate", receiptId=receipt["receiptId"])["type"])
        self.assertEqual(before, snapshot())

    def test_codex_probe_with_git_data_or_unknown_layout_remains_blocked(self):
        for case in ("commit", "loose-object", "ref", "packed-refs", "alternates", "worktrees",
                     "reflog", "config", "unknown-file", "empty-object-subdir", "detached-head", "other-unborn-head"):
            with self.subTest(case=case):
                probe = self.codex_probe()
                if case == "commit":
                    git(probe, "--bare", "fetch", str(self.repo), "HEAD:refs/heads/saved")
                elif case == "loose-object":
                    git(probe, "--bare", "hash-object", "-w", str(self.repo / "file.txt"))
                elif case == "ref":
                    (probe / "refs/saved").write_text(self.base + "\n")
                elif case == "packed-refs":
                    (probe / "packed-refs").write_text(self.base + " refs/heads/saved\n")
                elif case == "alternates":
                    (probe / "objects/info").mkdir()
                    (probe / "objects/info/alternates").write_text(str(self.repo / ".git/objects") + "\n")
                elif case == "worktrees":
                    (probe / "worktrees").mkdir()
                    (probe / "worktrees/gitdir").write_text(str(self.repo / ".git") + "\n")
                elif case == "reflog":
                    (probe / "logs").mkdir()
                    (probe / "logs/HEAD").write_text(self.base + " retained recovery data\n")
                elif case == "config":
                    (probe / "config").write_text('[remote "origin"]\n url=https://example.invalid/recovery.git\n')
                elif case == "unknown-file":
                    (probe / "recoverable-data").write_text("retain this data\n")
                elif case == "empty-object-subdir":
                    (probe / "objects/pack").mkdir()
                elif case == "detached-head":
                    (probe / "HEAD").write_text(self.base + "\n")
                else:
                    (probe / "HEAD").write_text("ref: refs/heads/recovery\n")
                before = {str(path.relative_to(probe)): path.read_bytes()
                          for path in probe.rglob("*") if path.is_file()}
                try:
                    with self.assertRaisesRegex(rt.Blocked, "Unregistered bare repository"):
                        self.g.inspect([self.registration], self.workspace)
                    self.assertEqual(before, {str(path.relative_to(probe)): path.read_bytes()
                                              for path in probe.rglob("*") if path.is_file()})
                finally:
                    shutil.rmtree(probe)

    def test_bare_inventory_outside_exact_codex_probe_scope_remains_blocked(self):
        for relative in ("repos/git-A0bC9z", "home/.codex/git-A0bC9z",
                         "home/.codex/.tmp/nested/git-A0bC9z", "home/.codex/.tmp/git-A0bC9z-extra",
                         "home/.codex/.tmp/git-ABC_de"):
            with self.subTest(relative=relative):
                probe = self.codex_probe(relative)
                try:
                    with self.assertRaisesRegex(rt.Blocked, "Unregistered bare repository"):
                        self.g.inspect([self.registration], self.workspace)
                finally:
                    shutil.rmtree(probe)
        nested = self.workspace / "home/.codex/user-checkout"
        git(self.workspace, "clone", str(self.remote), str(nested))
        with self.assertRaisesRegex(rt.Blocked, "inventory"):
            self.g.inspect([self.registration], self.workspace)

    def test_codex_probe_symlinks_and_linked_metadata_remain_blocked(self):
        for case in ("HEAD", "objects", "refs", "probe", ".tmp", ".codex", "home", "hardlinked-head"):
            with self.subTest(case=case):
                probe = self.codex_probe()
                outside = self.root / ("external-probe-" + case.replace(".", ""))
                if case in ("HEAD", "hardlinked-head"):
                    outside.write_bytes((probe / "HEAD").read_bytes())
                    (probe / "HEAD").unlink()
                    if case == "HEAD":
                        (probe / "HEAD").symlink_to(outside)
                    else:
                        os.link(outside, probe / "HEAD")
                else:
                    target = probe / case if case in ("objects", "refs") else (
                        probe if case == "probe" else self.workspace / (
                            "home/.codex/.tmp" if case == ".tmp" else "home/.codex" if case == ".codex" else "home"))
                    target.rename(outside)
                    target.symlink_to(outside, target_is_directory=True)
                try:
                    with self.assertRaises(rt.Blocked):
                        self.g.inspect([self.registration], self.workspace)
                    self.assertTrue(outside.exists())
                finally:
                    home = self.workspace / "home"
                    home.unlink() if home.is_symlink() else shutil.rmtree(home)

    def test_expired_live_task_is_not_killed(self):
        self.request("task-start", taskId="task-a", argv=["sleep", "100"], leaseSeconds=1)
        self.runtime.clock = lambda: 9999
        self.assert_blocked("git-save", "still running")
        self.assertTrue(self.adapter.live)
        self.assertNotIn("freeze", self.adapter.events)
        self.request("cancel-drain")
        self.assertEqual("Ready", self.runtime.s["phase"])
        self.adapter.live = False
        self.request("task-complete", taskId="task-a")
        self.assertEqual("GitSaved", self.request("git-save")["type"])

    def test_unknown_and_unreachable_work_fail_closed(self):
        self.adapter.unknown = True
        self.assert_blocked("git-save", "Unregistered")
        self.assertNotIn("stop-services", self.adapter.events)
        with self.assertRaisesRegex(rt.Blocked, "Fence gateway"):
            self.runtime.handle(dict(action="git-save", vmId="vm-a", generation=3,
                                     operationId="operation-a"))

    def test_ambiguous_launch_never_replayed_after_restart(self):
        self.adapter.ambiguous = True
        self.assert_blocked("task-start", "Ambiguous", taskId="task-a", argv=["false"])
        self.runtime = rt.Runtime(self.config, self.root / "state", self.adapter, self.g)
        self.assert_blocked("task-start", "never replay", taskId="task-a", argv=["false"])
        self.assertEqual(1, self.adapter.launches)
        self.assert_blocked("git-save", "Ambiguous")

    def test_stale_generation_receipt_and_operation(self):
        receipt = self.request("git-save")
        with self.assertRaisesRegex(rt.Blocked, "Stale VM"):
            self.runtime.handle(dict(vmId="vm-a", generation=2, operationId="operation-a", action="status"))
        self.assert_blocked("receipt-validate", "Stale receipt", receiptId="forged")
        self.assert_blocked("receipt-validate", "Stale receipt", operation="operation-b",
                            receiptId=receipt["receiptId"])
        self.assert_blocked("discard", "stale operation", operation="operation-b",
                            acknowledgeLostIgnoredAndRootData=True, authorizedDiscard=True)
        self.adapter.frozen = False
        self.assert_blocked("receipt-validate", "Freeze", receiptId=receipt["receiptId"])

    def test_explicit_discard_requires_both_acknowledgements(self):
        self.assert_blocked("discard", "acknowledgement")
        receipt = self.request("discard", authorizedDiscard=True,
                               acknowledgeLostIgnoredAndRootData=True)
        self.assertEqual("DiscardAuthorized", receipt["type"])
        self.assertEqual(("vm-a", 3, "operation-a"),
                         (receipt["vmId"], receipt["generation"], receipt["operationId"]))

    def test_keepalive_and_passive_sockets_do_not_reset_activity(self):
        for source in ("keepalive", "websocket", "preview", "health", "logs"):
            self.assert_blocked("activity", "not useful", source=source)
        self.assertIsNone(self.runtime.s["activityAt"])
        self.request("activity", source="user-input")
        self.assertEqual(1000, self.runtime.s["activityAt"])
        self.request("task-start", taskId="task-a", argv=["true"])
        self.runtime.clock = lambda: 2000
        self.request("task-renew", taskId="task-a")
        self.assertEqual(1000, self.runtime.s["activityAt"])

    def primary(self):
        config = dict(self.config, kind="primary",
                      volume=dict(device="/dev/disk/by-id/fake", providerDeviceId="fake",
                                  providerVolumeId="vol-primary", filesystemUuid="fixture-fs",
                                  owner={"workspaceId": "workspace"}))
        return rt.Runtime(config, self.root / "primary-state", self.adapter, self.g)

    def test_primary_mount_identity_wrong_uuid_owner_and_no_format_resume(self):
        self.runtime = self.primary()
        self.runtime.c["volume"]["filesystemUuid"] = "wrong"
        self.assert_blocked("bootstrap", "UUID")
        self.runtime.c["volume"]["filesystemUuid"] = "fixture-fs"
        self.runtime.c["volume"]["owner"] = {"workspaceId": "another"}
        self.assert_blocked("bootstrap", "owner")
        self.runtime.c["volume"]["owner"] = {"workspaceId": "workspace"}
        self.request("bootstrap")
        self.request("bootstrap")
        self.assertTrue(self.adapter.mounted)
        self.assertNotIn("format", self.adapter.events)
        receipt = self.request("quiesce")
        self.assertEqual("PrimaryQuiesced", receipt["type"])
        self.assertTrue(receipt["unmounted"])
        self.assertLess(self.adapter.events.index("stop-services"), self.adapter.events.index("unmount"))
        self.assertEqual(receipt, self.request("quiesce"))

    def test_ephemeral_rejects_all_storage_claims(self):
        for field in ("volume", "backup", "restore"):
            with self.subTest(field=field), self.assertRaisesRegex(rt.Blocked, "forbidden"):
                rt.Runtime(dict(self.config, **{field: {"anything": True}}),
                           self.root / ("bad-" + field), self.adapter, self.g)
        with self.assertRaisesRegex(rt.Blocked, "Acknowledge"):
            rt.Runtime(dict(self.config, ignoredDataAcknowledged=False), self.root / "unack",
                       self.adapter, self.g)
        self.assertNotIn("mount", self.adapter.events)
        self.assert_blocked("restore", "separate")
        self.assert_blocked("quiesce", "Git save")
        with self.assertRaisesRegex(rt.Blocked, "implicit credentials"):
            rt.Runtime(dict(self.config, providerToken="synthetic-not-a-token"),
                       self.root / "unknown-config", self.adapter, self.g)

    def test_primary_backup_fails_without_encryption_or_offhost_destination(self):
        self.runtime = self.primary()
        self.request("bootstrap")
        self.assert_blocked("quiesce", "recipient", backupRequired=True)
        self.assertNotIn("unmount", self.adapter.events)
        self.runtime.c["backup"] = dict(recipient="age1synthetic", keyRecoveryId="external-key",
                                       retentionDays=7, destinationIdentity="vm-a")
        self.assert_blocked("backup", "Independent off-host")
        self.assertNotIn("unmount", self.adapter.events)

    def test_restore_cannot_overwrite_source_volume_or_nonempty_target(self):
        self.runtime = self.primary()
        self.runtime.s["phase"] = "New"
        source = dict(type="PrimaryBackup", kind="primary", sourceVolumeId="vol-primary")
        source["receiptId"] = rt.digest(source)
        self.assert_blocked("restore", "separate", authorizedRestore=True, backupReceipt=source)

    def make_streaming_backup(self, acknowledge=True):
        self.runtime = self.primary()
        self.runtime.c["backup"] = dict(transport="management-ssh-stream",
                                       recipient="age1test-fixture-not-a-real-key",
                                       keyRecoveryId="operator-independent-key",
                                       destinationIdentity="operator-offhost-storage", retentionDays=7)
        self.request("bootstrap")
        def fake_encrypt(source, output, recipient):
            # Use the real archive writer, but do not claim cryptographic validation.
            with output.open("wb") as stream:
                rt.write_backup_archive(source, stream)
        with patch.object(rt, "encrypt_archive", side_effect=fake_encrypt):
            self.assert_blocked("quiesce", "off-host acknowledgement", backupRequired=True)
        prepared = self.request("backup")
        stream = io.BytesIO()
        self.runtime.export_backup(prepared["receiptId"], stream)
        self.assertEqual(prepared["ciphertextSha256"], rt.hashlib.sha256(stream.getvalue()).hexdigest())
        self.assertEqual("AwaitingOffHostAcknowledgement", prepared["freshness"])
        self.assertNotIn("unmount", self.adapter.events)
        self.assertFalse((self.root / "primary-state/backup.tar").exists())
        self.assert_blocked("backup-ack", "confirmation", preparedReceiptId=prepared["receiptId"],
                            ciphertextSha256=prepared["ciphertextSha256"])
        if not acknowledge:
            return prepared, stream.getvalue()
        receipt = self.request("backup-ack", preparedReceiptId=prepared["receiptId"],
                               ciphertextSha256=prepared["ciphertextSha256"],
                               durablyStoredOffHost=True, independentRecoveryKeyConfirmed=True,
                               destinationIdentity="operator-offhost-storage")
        self.assertEqual("VerifiedOffHost", receipt["freshness"])
        self.assertFalse((self.runtime.root / receipt["objectId"]).exists())
        return receipt, stream.getvalue()

    def test_resume_discards_prepared_backup_before_reopening_writes(self):
        prepared, _ = self.make_streaming_backup(acknowledge=False)
        ciphertext = self.runtime.root / prepared["objectId"]
        self.assertTrue(ciphertext.exists())
        self.request("resume", deletionNotCommitted=True)
        self.assertNotIn("preparedBackup", self.runtime.s)
        self.assertFalse(ciphertext.exists())
        self.assert_blocked("backup-ack", "frozen", preparedReceiptId=prepared["receiptId"],
                            ciphertextSha256=prepared["ciphertextSha256"],
                            durablyStoredOffHost=True, independentRecoveryKeyConfirmed=True,
                            destinationIdentity="operator-offhost-storage")

    def test_resume_cannot_reuse_verified_backup_after_new_writes(self):
        receipt, _ = self.make_streaming_backup()
        self.request("resume", deletionNotCommitted=True)
        (self.repo / "file.txt").write_text("new writes after the acknowledged backup\n")
        def fake_encrypt(source, output, recipient):
            with output.open("wb") as stream:
                rt.write_backup_archive(source, stream)
        with patch.object(rt, "encrypt_archive", side_effect=fake_encrypt):
            self.assert_blocked("quiesce", "off-host acknowledgement", backupRequired=True)
        prepared = self.request("backup")
        self.assertNotEqual(receipt["objectId"], prepared["objectId"])
        self.assertNotEqual(receipt["representativeHashes"], prepared["representativeHashes"])

    def test_primary_streaming_backup_ack_and_restore_separate_owned_volume(self):
        (self.workspace / "lost+found").mkdir()
        (self.workspace / ".minicloud-volume.json").write_text("source volume ownership")
        state = self.workspace / "user-data/custom-service/state.bin"
        state.parent.mkdir(parents=True)
        state.write_bytes(b"generic application data\0\xff")
        nested = self.workspace / "user-data/lost+found"
        nested.mkdir()
        (nested / ".minicloud-volume.json").write_text("user-owned nested data")
        receipt, archive = self.make_streaming_backup()
        with rt.tarfile.open(fileobj=io.BytesIO(archive)) as content:
            self.assertNotIn("data/lost+found", content.getnames())
            self.assertNotIn("data/.minicloud-volume.json", content.getnames())
        stopped = self.request("quiesce", backupRequired=True)
        self.assertEqual(receipt, stopped["backup"])
        target = self.root / "restore-workspace"
        target.mkdir()
        (target / "lost+found").mkdir()
        owner = target / ".minicloud-volume.json"
        owner.write_text("separate target ownership")
        config = copy.deepcopy(self.runtime.c)
        config["workspace"] = str(target)
        config["volume"]["providerVolumeId"] = "vol-separate-target"
        config["volume"]["filesystemUuid"] = "target-fs"
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-state", self.adapter, self.g)
        self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                     backupReceipt=receipt)
        restored = self.runtime.restore_import("operation-a", io.BytesIO(archive))
        self.assertEqual("PrimaryRestored", restored["type"])
        self.assertTrue(restored["representativeHashesVerified"])
        self.assertEqual("initial\n", (target / "repos/project/file.txt").read_text())
        self.assertEqual(b"generic application data\0\xff",
                         (target / "user-data/custom-service/state.bin").read_bytes())
        self.assertEqual("separate target ownership", owner.read_text())
        self.assertEqual([], list((target / "lost+found").iterdir()))
        self.assertEqual("user-owned nested data",
                         (target / "user-data/lost+found/.minicloud-volume.json").read_text())

    def test_backup_rejects_recovered_or_aliased_filesystem_metadata(self):
        lost = self.workspace / "lost+found"
        lost.mkdir()
        (lost / "recovered-file").write_bytes(b"must not silently omit recovered data")
        with self.assertRaisesRegex(rt.Blocked, "recovered data"):
            rt.write_backup_archive(self.workspace, io.BytesIO())
        (lost / "recovered-file").unlink()
        lost.rmdir()
        for directory in (False, True):
            with self.subTest(directory=directory):
                outside = self.root / ("outside-directory" if directory else "outside-file")
                outside.mkdir() if directory else outside.write_bytes(b"do not follow")
                lost.symlink_to(outside, target_is_directory=directory)
                with self.assertRaisesRegex(rt.Blocked, "metadata"):
                    rt.write_backup_archive(self.workspace, io.BytesIO())
                lost.unlink()

    def test_backup_restore_preserves_unfollowed_tool_and_virtualenv_symlinks(self):
        os.link(self.repo / "file.txt", self.repo / "hardlinked.txt")
        outside = self.root / "outside-image-files"
        outside.mkdir()
        executable = outside / "tool"
        executable.write_bytes(b"external bytes must not enter the archive")
        links = {}
        for name in ("codex-linux-sandbox", "apply_patch", "applypatch"):
            links["home/.codex/tmp/arg0-linux/codex-arg0fixture/" + name] = str(executable)
        links[".venv/bin/python"] = os.path.relpath(executable, self.workspace / ".venv/bin")
        links["external-directory"] = str(outside)
        links["project-link"] = "repos/project"
        for relative, destination in links.items():
            path = self.workspace / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to(destination)
        rt.validate_backup_tree(self.workspace)
        receipt, archive = self.make_streaming_backup()
        with rt.tarfile.open(fileobj=io.BytesIO(archive)) as content:
            self.assertTrue(all(content.getmember("data/" + path).issym() for path in links))
            self.assertNotIn("data/external-directory/tool", content.getnames())
        target = self.root / "restore-links"
        target.mkdir()
        config = copy.deepcopy(self.runtime.c)
        config["workspace"] = str(target)
        config["volume"].update(providerVolumeId="separate-links", filesystemUuid="target-fs")
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-links-state", self.adapter, self.g)
        self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                     backupReceipt=receipt)
        with patch.object(rt.os, "chown") as chown:
            self.runtime.restore_import("operation-a", io.BytesIO(archive))
        for relative, destination in links.items():
            self.assertEqual(destination, os.readlink(target / relative))
            chown.assert_any_call(target / relative, 2000, 2000, follow_symlinks=False)
        self.assertEqual(b"external bytes must not enter the archive", executable.read_bytes())
        self.assertEqual("initial\n", (target / "project-link/file.txt").read_text())
        self.assertEqual((target / "repos/project/file.txt").stat().st_ino,
                         (target / "repos/project/hardlinked.txt").stat().st_ino)

    def test_restore_never_writes_through_deferred_links_or_links_volume_metadata(self):
        receipt, archive = self.make_streaming_backup()
        config = copy.deepcopy(self.runtime.c)
        outside = self.root / "outside-restore"
        outside.mkdir()
        for case in ("link-first", "file-first", "nested-links", "hardlink-to-link",
                     "traversal", "hardlink-to-owner"):
            with self.subTest(case=case):
                injected = io.BytesIO()
                with rt.tarfile.open(fileobj=injected, mode="w") as output:
                    with rt.tarfile.open(fileobj=io.BytesIO(archive)) as original:
                        for member in original:
                            output.addfile(member, original.extractfile(member) if member.isfile() else None)
                    link = rt.tarfile.TarInfo("data/trap")
                    link.type = rt.tarfile.SYMTYPE
                    link.linkname = str(outside)
                    file = rt.tarfile.TarInfo("data/trap/escaped")
                    file.size = 6
                    if case == "file-first":
                        output.addfile(file, io.BytesIO(b"unsafe"))
                    if case == "traversal":
                        link.name = "data/../escaped-link"
                    if case != "hardlink-to-owner":
                        output.addfile(link)
                    if case == "link-first":
                        output.addfile(file, io.BytesIO(b"unsafe"))
                    if case == "nested-links":
                        child = rt.tarfile.TarInfo("data/trap/child")
                        child.type = rt.tarfile.SYMTYPE
                        child.linkname = str(outside / "escaped")
                        output.addfile(child)
                    if case in ("hardlink-to-link", "hardlink-to-owner"):
                        hardlink = rt.tarfile.TarInfo("data/alias")
                        hardlink.type = rt.tarfile.LNKTYPE
                        hardlink.linkname = ("data/trap" if case == "hardlink-to-link"
                                             else "data/repos/../.minicloud-volume.json")
                        output.addfile(hardlink)
                target = self.root / ("restore-" + case)
                target.mkdir()
                owner = target / ".minicloud-volume.json"
                owner.write_text("unchanged target owner")
                current = copy.deepcopy(config)
                current["workspace"] = str(target)
                current["volume"].update(providerVolumeId="separate-" + case, filesystemUuid="target-fs")
                self.adapter.uuid = "target-fs"
                self.runtime = rt.Runtime(current, self.root / ("state-" + case), self.adapter, self.g)
                self.request("restore", authorizedRestore=True, independentRecoveryKeyConfirmed=True,
                             backupReceipt=receipt)
                with self.assertRaises((rt.Blocked, rt.tarfile.TarError)):
                    self.runtime.restore_import("operation-a", io.BytesIO(injected.getvalue()))
                self.assertEqual("RestoreFailed", self.runtime.s["phase"])
                self.assertEqual([], list(outside.iterdir()))
                self.assertFalse((self.root / "escaped-link").exists())
                self.assertFalse((self.root / "escaped-link").is_symlink())
                self.assertEqual("unchanged target owner", owner.read_text())

    def test_restore_rejects_nonempty_target_missing_ack_hash_tamper_and_replay(self):
        receipt, archive = self.make_streaming_backup()
        config = copy.deepcopy(self.runtime.c)
        config["volume"]["providerVolumeId"] = "different-volume"
        config["volume"]["filesystemUuid"] = "target-fs"
        self.adapter.uuid = "target-fs"
        self.runtime = rt.Runtime(config, self.root / "restore-state", self.adapter, self.g)
        self.assert_blocked("restore", "off VM", authorizedRestore=True, backupReceipt=receipt)
        self.assert_blocked("restore", "not empty", authorizedRestore=True, backupReceipt=receipt,
                            independentRecoveryKeyConfirmed=True)
        target = self.root / "empty-target"
        target.mkdir()
        config["workspace"] = str(target)
        self.runtime = rt.Runtime(config, self.root / "empty-restore-state", self.adapter, self.g)
        damaged = dict(receipt, representativeHashes={"repos/project/file.txt": "bad"})
        damaged["receiptId"] = rt.digest({k: v for k, v in damaged.items() if k != "receiptId"})
        self.request("restore", authorizedRestore=True, backupReceipt=damaged,
                     independentRecoveryKeyConfirmed=True)
        with self.assertRaisesRegex(rt.Blocked, "hash mismatch"):
            self.runtime.restore_import("operation-a", io.BytesIO(archive))
        self.assertEqual("RestoreFailed", self.runtime.s["phase"])
        with self.assertRaisesRegex(rt.Blocked, "No authenticated"):
            self.runtime.restore_import("operation-a", io.BytesIO(archive))

    def test_backup_encryption_failure_never_unmounts_or_claims_freshness(self):
        self.runtime = self.primary()
        self.runtime.c["backup"] = dict(transport="management-ssh-stream", recipient="bad",
                                       keyRecoveryId="external-key", retentionDays=7,
                                       destinationIdentity="operator")
        self.request("bootstrap")
        with patch.object(rt, "encrypt_archive", side_effect=rt.Blocked("Age encryption failed")):
            self.assert_blocked("quiesce", "Age", backupRequired=True)
        self.assertEqual("Failed", self.runtime.s["backupFreshness"])
        self.assertNotIn("lastBackup", self.runtime.s)
        self.assertNotIn("unmount", self.adapter.events)


class LinuxAdapterTests(unittest.TestCase):
    def test_wrong_uuid_rejected_without_mount_or_format(self):
        adapter = rt.Linux(dict(workspace="/workspace"))
        claim = dict(device="/dev/disk/by-id/scsi-volume", providerDeviceId="scsi-volume",
                     filesystemUuid="expected")
        fake_stat = type("Stat", (), {"st_mode": 0o060000})()
        calls = []
        def command(argv, **kwargs):
            calls.append(argv)
            return "ext4" if "TYPE" in argv else "wrong"
        with patch.object(Path, "stat", return_value=fake_stat), patch.object(rt, "run", side_effect=command):
            with self.assertRaisesRegex(rt.Blocked, "UUID"):
                adapter.mount(claim)
        self.assertTrue(all(argv[0] == "blkid" for argv in calls))

    def test_unexpected_filesystem_never_formatted(self):
        adapter = rt.Linux(dict(workspace="/workspace"))
        fake_stat = type("Stat", (), {"st_mode": 0o060000})()
        with patch.object(Path, "stat", return_value=fake_stat), patch.object(rt, "run", return_value="xfs") as command:
            with self.assertRaisesRegex(rt.Blocked, "never formats"):
                adapter.mount(dict(device="/dev/disk/by-id/a", providerDeviceId="a", filesystemUuid="fs"))
            self.assertEqual("blkid", command.call_args.args[0][0])

    def test_canonical_urls_disallow_helpers_tokens_and_local_sidechannels(self):
        for value in ("file:///workspace/primary", "ext::command", "git@example.com:org/repo",
                      "https://secret@example.com/repo", "https://host/../repo"):
            with self.subTest(value=value), self.assertRaises(rt.Blocked):
                rt.canonical_remote(value)
        self.assertEqual("https://example.invalid/org/repo.git",
                         rt.canonical_remote("https://example.invalid/org/repo.git"))


if __name__ == "__main__":
    unittest.main()
