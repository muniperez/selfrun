import hashlib
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"


class T3StateTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="t3-state-fixture-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.db = Path(self.temp.name) / "statev2.sqlite"
        self.writer = sqlite3.connect(self.db)
        self.addCleanup(self.writer.close)
        self.writer.executescript("""
            CREATE TABLE orchestration_events (
                sequence INTEGER PRIMARY KEY, application_event_version INTEGER, aggregate_kind TEXT);
            CREATE TABLE orchestration_v2_projection_metadata (
                projection_name TEXT PRIMARY KEY, schema_version INTEGER, last_sequence INTEGER);
            INSERT INTO orchestration_v2_projection_metadata VALUES ('thread-projections',2,0);
        """)
        for suffix, status in (
                ("runs", "completed"), ("run_attempts", "completed"), ("nodes", "completed"),
                ("runtime_requests", "resolved"), ("provider_sessions", "stopped"),
                ("provider_threads", "closed"), ("provider_turns", "completed"),
                ("turn_items", "completed"), ("subagents", "completed")):
            table = "orchestration_v2_projection_" + suffix
            self.writer.execute("CREATE TABLE " + table + " (thread_id TEXT,status TEXT)")
            self.writer.execute("INSERT INTO " + table + " VALUES (?,?)", ("thread-fixture", status))
        self.writer.execute("CREATE TABLE orchestration_v2_effect_outbox (thread_id TEXT,status TEXT)")
        self.writer.execute("INSERT INTO orchestration_v2_effect_outbox VALUES ('thread-fixture','succeeded')")
        self.writer.commit()

    def test_http_application_cursor_includes_projects_but_thread_projection_does_not(self):
        self.writer.execute("INSERT INTO orchestration_events VALUES (9,1,'project')")
        self.writer.execute("INSERT INTO orchestration_events VALUES (10,2,'project')")
        self.writer.execute("INSERT INTO orchestration_events VALUES (11,1,'thread')")
        self.writer.execute("INSERT INTO orchestration_v2_projection_metadata VALUES ('unrelated',999,0)")
        self.writer.commit()
        before = hashlib.sha256(self.db.read_bytes()).hexdigest()
        result = rt.t3_idle_database(self.db, 10)
        self.assertEqual((0, 10, 10), (result["threadSequence"], result["globalV2Sequence"],
                                       result["applicationSequence"]))
        self.assertEqual(before, hashlib.sha256(self.db.read_bytes()).hexdigest())
        with self.assertRaisesRegex(rt.Blocked, "application state changed"):
            rt.t3_idle_database(self.db, 0)
        self.writer.execute("INSERT INTO orchestration_events VALUES (12,1,'project')")
        self.writer.commit()
        self.assertEqual(12, rt.t3_idle_database(self.db, 12)["applicationSequence"])
        with self.assertRaisesRegex(rt.Blocked, "application state changed"):
            rt.t3_idle_database(self.db, 10)

    def test_pinned_foundation_version_one_allows_projects_but_requires_empty_thread_state(self):
        tables = [name for name, in self.writer.execute(
            "SELECT name FROM sqlite_master WHERE type='table'") if name !=
            "orchestration_v2_projection_metadata"]
        for table in tables:
            self.writer.execute("DELETE FROM " + table)
        self.writer.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=1")
        self.writer.commit()
        result = rt.t3_idle_database(self.db, 0)
        self.assertTrue(result["emptyBootstrap"])
        self.assertEqual(1, result["schemaVersion"])
        self.writer.execute("INSERT INTO orchestration_events VALUES (1,2,'project')")
        self.writer.execute("INSERT INTO orchestration_events VALUES (2,1,'project')")
        self.writer.commit()
        result = rt.t3_idle_database(self.db, 2)
        self.assertTrue(result["emptyBootstrap"])
        self.assertEqual((0, 1, 2), (result["threadSequence"], result["globalV2Sequence"],
                                     result["applicationSequence"]))
        self.writer.execute("DELETE FROM orchestration_events")
        self.writer.commit()
        for sql in ("INSERT INTO orchestration_events VALUES (1,1,'thread')",
                    "INSERT INTO orchestration_events VALUES (1,2,'unknown')",
                    "INSERT INTO orchestration_events VALUES (1,2,NULL)",
                    "INSERT INTO orchestration_v2_projection_runs VALUES ('old','completed')",
                    "INSERT INTO orchestration_v2_effect_outbox VALUES ('old','succeeded')"):
            with self.subTest(sql=sql):
                self.writer.execute(sql)
                self.writer.commit()
                with self.assertRaisesRegex(rt.Blocked, "projection is unknown"):
                    rt.t3_idle_database(self.db, 0)
                for table in tables:
                    self.writer.execute("DELETE FROM " + table)
                self.writer.commit()
        self.writer.execute("CREATE TABLE orchestration_v2_projection_messages (message TEXT)")
        self.writer.execute("INSERT INTO orchestration_v2_projection_messages VALUES ('stale')")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "projection is unknown"):
            rt.t3_idle_database(self.db, 0)

    def test_stale_application_cursor_and_thread_projection_lag_independently_block(self):
        self.writer.execute("INSERT INTO orchestration_events VALUES (1,2,'thread')")
        self.writer.execute("INSERT INTO orchestration_events VALUES (2,2,'project')")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "application state changed"):
            rt.t3_idle_database(self.db, 0)
        with self.assertRaisesRegex(rt.Blocked, "V2 thread cursor"):
            rt.t3_idle_database(self.db, 2)
        self.writer.execute("UPDATE orchestration_v2_projection_metadata SET last_sequence=1")
        self.writer.commit()
        self.assertEqual(1, rt.t3_idle_database(self.db, 2)["threadSequence"])
        self.writer.execute("INSERT INTO orchestration_events VALUES (3,2,'thread')")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "V2 thread cursor"):
            rt.t3_idle_database(self.db, 3)

    def test_active_pending_and_unknown_statuses_in_every_verified_work_table_block(self):
        for table, status in (
                ("runs", "running"), ("runs", "queued"), ("runs", "waiting"),
                ("run_attempts", "pending"), ("nodes", "waiting"),
                ("runtime_requests", "pending"), ("provider_sessions", "running"),
                ("provider_threads", "active"), ("provider_turns", "pending"),
                ("turn_items", "waiting"), ("subagents", "pending"), ("runs", "future-idle")):
            with self.subTest(table=table, status=status):
                name = "orchestration_v2_projection_" + table
                old = self.writer.execute("SELECT status FROM " + name).fetchone()[0]
                self.writer.execute("UPDATE " + name + " SET status=?", (status,))
                self.writer.commit()
                with self.assertRaisesRegex(rt.Blocked, "active/pending work or an unknown status"):
                    rt.t3_idle_database(self.db, 0)
                self.writer.execute("UPDATE " + name + " SET status=?", (old,))
                self.writer.commit()
        self.writer.execute("UPDATE orchestration_v2_effect_outbox SET status='running'")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "effect_outbox"):
            rt.t3_idle_database(self.db, 0)

    def test_readonly_reader_observes_committed_wal_instead_of_immutable_stale_snapshot(self):
        self.writer.execute("PRAGMA journal_mode=WAL")
        self.writer.execute("PRAGMA wal_autocheckpoint=0")
        self.writer.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        self.writer.execute("UPDATE orchestration_v2_projection_runs SET status='running'")
        self.writer.commit()
        self.assertTrue(self.db.with_name(self.db.name + "-wal").exists())
        with self.assertRaisesRegex(rt.Blocked, "active/pending"):
            rt.t3_idle_database(self.db, 0)

    def test_unknown_schema_version_missing_table_and_invalid_cursor_fail_closed(self):
        for sequence in (-1, True, "0"):
            with self.subTest(sequence=sequence), self.assertRaises(rt.Blocked):
                rt.t3_idle_database(self.db, sequence)
        self.writer.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=3")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "projection is unknown"):
            rt.t3_idle_database(self.db, 0)
        self.writer.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=2")
        self.writer.execute("DROP TABLE orchestration_v2_projection_runtime_requests")
        self.writer.commit()
        with self.assertRaisesRegex(rt.Blocked, "schema"):
            rt.t3_idle_database(self.db, 0)


if __name__ == "__main__":
    unittest.main()
