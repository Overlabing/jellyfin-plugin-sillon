#!/usr/bin/env python3
"""Converts sillon-gateway's SQLite database into the plugin's requests.json and devices.json.

Standard library only; run it as `python3 -I scripts/migrate-from-gateway.py --db gateway.db --out DIR`.
The database is opened read-only.
"""
import argparse
import json
import os
import re
import sqlite3
import sys
import uuid
from collections import Counter
from datetime import datetime, timedelta, timezone

STATUSES = ("requested", "downloading", "available", "failed", "cancelled")
_STAMP = re.compile(r"^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}:\d{2})(?:\.(\d+))?\s*(Z|[+-]\d{2}:?\d{2})?$")


def stamp(value):
    """Gateway time (RFC 3339, any offset, up to 9 fraction digits) to UTC RFC 3339 with microseconds; None stays None."""
    if value is None or str(value).strip() == "":
        return None
    m = _STAMP.match(str(value).strip())
    if not m:
        raise ValueError("unreadable date: %r" % (value,))
    day, clock, frac, zone = m.groups()
    moment = datetime.strptime(day + " " + clock, "%Y-%m-%d %H:%M:%S")
    moment = moment.replace(microsecond=int((frac or "0").ljust(6, "0")[:6]))
    if zone and zone != "Z":
        sign = 1 if zone[0] == "+" else -1
        digits = zone[1:].replace(":", "")
        moment -= sign * timedelta(hours=int(digits[:2]), minutes=int(digits[2:]))
    return moment.replace(tzinfo=timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def user_id(value):
    """Jellyfin's 32 hex characters without dashes; anything that is not a GUID is kept as is."""
    try:
        return uuid.UUID(value).hex
    except (ValueError, AttributeError, TypeError):
        return value


def columns(db, table):
    return {row[1] for row in db.execute("SELECT * FROM pragma_table_info(?)", (table,))}


def read_requests(db):
    have = columns(db, "requests")
    if not have:
        raise SystemExit("error: no requests table in the database")

    def col(name):
        return name if name in have else "NULL"

    rows = db.execute(
        "SELECT id,user_id,user_name,foreign_album_id,foreign_artist_id,lidarr_album_id,artist,title,year,cover_url,"
        "status,jellyfin_item_id,error,tracks_imported,tracks_total,%s,%s,created_at,updated_at FROM requests ORDER BY id"
        % (col("notified_at"), col("library_added_at"))
    ).fetchall()
    out = []
    for (rid, uid, uname, album, artist_id, lidarr, artist, title, year, cover, status, item, error,
         imported, total, notified, library_added, created, updated) in rows:
        if status not in STATUSES:
            raise SystemExit("error: request %s has an unknown status %r" % (rid, status))
        out.append({
            "id": rid,
            "userId": user_id(uid),
            "userName": uname or "",
            "foreignAlbumId": album,
            "foreignArtistId": artist_id or "",
            "lidarrAlbumId": lidarr,
            "artist": artist or "",
            "title": title or "",
            "year": year or 0,
            "coverUrl": cover or "",
            "status": status,
            "jellyfinItemId": item or "",
            "error": error or "",
            "tracksImported": imported or 0,
            "tracksTotal": total or 0,
            "notifiedAt": stamp(notified),
            "libraryAddedAt": stamp(library_added),
            "createdAt": stamp(created),
            "updatedAt": stamp(updated),
        })
    return out


def read_devices(db):
    have = columns(db, "devices")
    if not have:
        raise SystemExit("error: no devices table in the database")
    env = "env" if "env" in have else ("environment" if "environment" in have else "''")
    rows = db.execute(
        "SELECT token,user_id,platform,%s,created_at,updated_at FROM devices ORDER BY created_at, token" % env
    ).fetchall()
    return [{
        "token": token,
        "userId": user_id(uid),
        "platform": platform or "",
        "env": environment or "",
        "createdAt": stamp(created),
        "updatedAt": stamp(updated),
    } for token, uid, platform, environment, created, updated in rows]


def has_content(path, key):
    """True when the file exists and holds something other than an empty document."""
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        return False
    try:
        with open(path, encoding="utf-8") as f:
            return bool(json.load(f).get(key))
    except (ValueError, AttributeError, OSError):
        return True


def write(path, key, items):
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as f:
        json.dump({key: items}, f, indent=2, ensure_ascii=False)
        f.write("\n")
    os.replace(temp, path)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Convert sillon-gateway's SQLite database to the plugin's JSON files.")
    parser.add_argument("--db", required=True, help="the gateway's SQLite database (a copy)")
    parser.add_argument("--out", required=True, help="the directory receiving requests.json and devices.json")
    parser.add_argument("--dry-run", action="store_true", help="read and summarise, write nothing")
    parser.add_argument("--force", action="store_true", help="overwrite existing non-empty files")
    args = parser.parse_args(argv)

    if not os.path.isfile(args.db):
        raise SystemExit("error: %s is not a file" % args.db)
    db = sqlite3.connect("file:%s?mode=ro" % os.path.abspath(args.db).replace("?", "%3f").replace("#", "%23"), uri=True)
    try:
        requests, devices = read_requests(db), read_devices(db)
    except sqlite3.DatabaseError as e:
        raise SystemExit("error: cannot read %s: %s" % (args.db, e))
    finally:
        db.close()

    targets = (("requests.json", "requests", requests), ("devices.json", "devices", devices))
    if not args.dry_run and not args.force:
        for name, key, _ in targets:
            if has_content(os.path.join(args.out, name), key):
                raise SystemExit("error: %s already has content; use --force to overwrite" % os.path.join(args.out, name))

    if not args.dry_run:
        os.makedirs(args.out, exist_ok=True)
        for name, key, items in targets:
            write(os.path.join(args.out, name), key, items)

    prefix = "dry run, nothing written: " if args.dry_run else ""
    print("%s%d requests, %d devices" % (prefix, len(requests), len(devices)))
    for status, n in sorted(Counter(r["status"] for r in requests).items()):
        print("  requests %-12s %d" % (status, n))
    names = {r["userId"]: r["userName"] for r in requests if r["userName"]}
    for uid, n in sorted(Counter(d["userId"] for d in devices).items()):
        print("  devices  %s%s: %d" % (uid, " (%s)" % names[uid] if uid in names else "", n))
    return 0


if __name__ == "__main__":
    sys.exit(main())
