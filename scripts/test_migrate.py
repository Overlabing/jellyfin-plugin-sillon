"""Tests of migrate-from-gateway.py. Run: python3 -I scripts/test_migrate.py

`--make-fixture PATH` writes the fixture database and exits (used by the xUnit migration test).
"""
import contextlib
import importlib.util
import io
import json
import os
import sqlite3
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("migrate", os.path.join(HERE, "migrate-from-gateway.py"))
migrate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migrate)

# the gateway's schema (store.go) plus the library_added_at column
SCHEMA = """
CREATE TABLE devices (token TEXT PRIMARY KEY, user_id TEXT NOT NULL, platform TEXT NOT NULL DEFAULT '',
  env TEXT NOT NULL DEFAULT 'production', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE requests (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, user_name TEXT NOT NULL DEFAULT '',
  foreign_album_id TEXT NOT NULL, foreign_artist_id TEXT NOT NULL DEFAULT '', lidarr_album_id INTEGER,
  artist TEXT NOT NULL DEFAULT '', title TEXT NOT NULL DEFAULT '', year INTEGER NOT NULL DEFAULT 0,
  cover_url TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL CHECK (status IN ('requested','downloading','available','failed','cancelled')),
  jellyfin_item_id TEXT, error TEXT, tracks_imported INTEGER NOT NULL DEFAULT 0, tracks_total INTEGER NOT NULL DEFAULT 0,
  notified_at TEXT, library_added_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
"""
ALICE = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9"
BOB = "f0e1d2c3b4a5968778695a4b3c2d1e0f"


def make_fixture(path):
    db = sqlite3.connect(path)
    db.executescript(SCHEMA)
    db.executemany("INSERT INTO requests VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [
        (1, ALICE, "alice", "mb-album-1", "mb-artist-1", 11, "Artist One", "Album One", 2001, "http://c/1",
         "available", "jf-item-1", None, 9, 10, "2026-10-01T10:00:00.123456789Z", "2026-10-01T10:05:00+02:00",
         "2026-09-30T08:00:00Z", "2026-10-01T10:05:00.5Z"),
        (2, BOB, "bob", "mb-album-2", "mb-artist-2", None, "Artist Two", "Album Two", 0, "",
         "requested", None, None, 0, 0, None, None, "2026-10-02T12:00:00Z", "2026-10-02T12:00:00Z"),
        (3, ALICE, "alice", "mb-album-3", "mb-artist-3", 13, "Artist Three", "Album Three", 1999, "",
         "cancelled", None, "no longer wanted", 0, 0, None, None, "2026-10-03T12:00:00Z", "2026-10-03T13:00:00Z"),
    ])
    db.executemany("INSERT INTO devices VALUES (?,?,?,?,?,?)", [
        ("token-alice-1", ALICE, "ios", "development", "2026-09-01T09:00:00Z", "2026-09-02T09:00:00Z"),
        ("token-bob-1", BOB, "ios", "production", "2026-09-05T09:00:00Z", "2026-09-05T09:00:00Z"),
    ])
    db.commit()
    db.close()


def run(*args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        migrate.main(list(args))
    return out.getvalue()


class MigrateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.db = os.path.join(self.tmp.name, "gateway.db")
        self.out = os.path.join(self.tmp.name, "out")
        make_fixture(self.db)

    def load(self, name):
        with open(os.path.join(self.out, name), encoding="utf-8") as f:
            return json.load(f)

    def test_converts_requests(self):
        run("--db", self.db, "--out", self.out)
        by_id = {r["id"]: r for r in self.load("requests.json")["requests"]}
        self.assertEqual([1, 2, 3], sorted(by_id))
        one = by_id[1]
        self.assertEqual("0a1b2c3d4e5f6071" "8293a4b5c6d7e8f9", one["userId"])
        self.assertEqual("available", one["status"])
        self.assertEqual("2026-10-01T10:00:00.123456Z", one["notifiedAt"])
        self.assertEqual("2026-10-01T08:05:00.000000Z", one["libraryAddedAt"])
        self.assertEqual("2026-10-01T10:05:00.500000Z", one["updatedAt"])
        self.assertEqual(11, one["lidarrAlbumId"])
        self.assertEqual((9, 10), (one["tracksImported"], one["tracksTotal"]))
        self.assertEqual(BOB, by_id[2]["userId"])
        self.assertIsNone(by_id[2]["lidarrAlbumId"])
        self.assertIsNone(by_id[2]["libraryAddedAt"])
        self.assertEqual("", by_id[2]["jellyfinItemId"])
        self.assertEqual("cancelled", by_id[3]["status"])
        self.assertEqual("no longer wanted", by_id[3]["error"])

    def test_converts_devices(self):
        run("--db", self.db, "--out", self.out)
        devices = {d["token"]: d for d in self.load("devices.json")["devices"]}
        self.assertEqual("development", devices["token-alice-1"]["env"])
        self.assertEqual("0a1b2c3d4e5f60718293a4b5c6d7e8f9", devices["token-alice-1"]["userId"])
        self.assertEqual("2026-09-01T09:00:00.000000Z", devices["token-alice-1"]["createdAt"])

    def test_summary_has_counts_and_no_token(self):
        summary = run("--db", self.db, "--out", self.out)
        self.assertIn("3 requests, 2 devices", summary)
        self.assertIn("cancelled", summary)
        self.assertNotIn("token-", summary)

    def test_dry_run_writes_nothing(self):
        run("--db", self.db, "--out", self.out, "--dry-run")
        self.assertFalse(os.path.exists(self.out))

    def test_refuses_to_overwrite_without_force(self):
        run("--db", self.db, "--out", self.out)
        with self.assertRaises(SystemExit):
            run("--db", self.db, "--out", self.out)
        run("--db", self.db, "--out", self.out, "--force")

    def test_overwrites_an_empty_document(self):
        os.makedirs(self.out)
        with open(os.path.join(self.out, "requests.json"), "w") as f:
            json.dump({"requests": []}, f)
        run("--db", self.db, "--out", self.out)
        self.assertEqual(3, len(self.load("requests.json")["requests"]))

    def test_database_without_optional_columns(self):
        old = os.path.join(self.tmp.name, "old.db")
        db = sqlite3.connect(old)
        db.executescript(SCHEMA.replace("notified_at TEXT, library_added_at TEXT,", "").replace(
            "env TEXT NOT NULL DEFAULT 'production',", ""))
        db.execute("INSERT INTO requests(user_id,foreign_album_id,status,created_at,updated_at) VALUES(?,?,?,?,?)",
                   (ALICE, "a", "requested", "2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z"))
        db.execute("INSERT INTO devices(token,user_id,created_at,updated_at) VALUES(?,?,?,?)",
                   ("t", ALICE, "2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z"))
        db.commit()
        db.close()
        run("--db", old, "--out", self.out)
        self.assertIsNone(self.load("requests.json")["requests"][0]["notifiedAt"])
        self.assertEqual("", self.load("devices.json")["devices"][0]["env"])


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--make-fixture":
        make_fixture(sys.argv[2])
    else:
        unittest.main()
