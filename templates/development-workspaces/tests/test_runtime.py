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
        return dict(type="T3CommittedIdle", threadSequence=0, globalV2Sequence=0)

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

    def request(self, action, operation="operation-a", **kwargs):
        return self.runtime.handle(dict(vmId="vm-a", generation=3, operationId=operation,
                                        action=action, gatewayAdmissionsFenced=True,
                                        unobservableToolsAbsent=True, t3SnapshotSequence=0, **kwargs))

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
        for field in ("volume", "backup", "restore", "emulatorExportCommands"):
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

    def make_streaming_backup(self):
        self.runtime = self.primary()
        self.runtime.c["backup"] = dict(transport="management-ssh-stream",
                                       recipient="age1test-fixture-not-a-real-key",
                                       keyRecoveryId="operator-independent-key",
                                       destinationIdentity="operator-offhost-storage", retentionDays=7)
        self.request("bootstrap")
        def fake_encrypt(source, output, recipient):
            # Only transport/archive orchestration is faked, never claim cryptographic validation.
            with rt.tarfile.open(output, "w") as archive:
                archive.add(source, arcname="data")
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
        receipt = self.request("backup-ack", preparedReceiptId=prepared["receiptId"],
                               ciphertextSha256=prepared["ciphertextSha256"],
                               durablyStoredOffHost=True, independentRecoveryKeyConfirmed=True,
                               destinationIdentity="operator-offhost-storage")
        self.assertEqual("VerifiedOffHost", receipt["freshness"])
        self.assertFalse((self.runtime.root / receipt["objectId"]).exists())
        return receipt, stream.getvalue()

    def test_primary_streaming_backup_ack_and_restore_separate_owned_volume(self):
        receipt, archive = self.make_streaming_backup()
        stopped = self.request("quiesce", backupRequired=True)
        self.assertEqual(receipt, stopped["backup"])
        target = self.root / "restore-workspace"
        target.mkdir()
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
