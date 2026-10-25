"""Tests of manifest.py. Run: python3 -I scripts/test_manifest.py"""
import hashlib
import importlib.util
import io
import json
import os
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("manifest", os.path.join(HERE, "manifest.py"))
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)

GUID = "1daed904-1b05-483c-8544-16cb6dcced54"
URL = "https://github.com/Overlabing/jellyfin-plugin-sillon/releases/download"


def plugin_zip(version, guid=GUID, changelog=None):
    meta = {"guid": guid, "name": "Sillon", "version": version, "targetAbi": "12.2.0.0",
            "owner": "Overlabing", "category": "Music", "overview": "Overview " + version,
            "description": "Description " + version, "changelog": changelog or "Changes in " + version}
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as archive:
        archive.writestr("Jellyfin.Plugin.Sillon.dll", b"dll " + version.encode())
        archive.writestr("meta.json", json.dumps(meta))
    return buf.getvalue()


def release(version, published, zip_version=None, **extra):
    name = "Sillon_%s.zip" % (zip_version or version)
    return dict({"tag_name": "v" + version, "published_at": published, "draft": False, "prerelease": False,
                 "assets": [{"id": 1, "name": name, "browser_download_url": "%s/v%s/%s" % (URL, version, name)},
                            {"id": 2, "name": "notes.txt", "browser_download_url": "x"}]}, **extra)


class Store:
    def __init__(self, *versions, **zips):
        self.zips = {"Sillon_%s.zip" % v: plugin_zip(v) for v in versions}
        self.zips.update(zips)

    def __call__(self, asset):
        return self.zips[asset["name"]]


class BuildTests(unittest.TestCase):
    def test_fields_are_jellyfins_package_and_version_info(self):
        result = manifest.build([release("0.7.0.0", "2026-10-21T09:12:19Z")], Store("0.7.0.0"))

        self.assertEqual(1, len(result))
        package = result[0]
        self.assertEqual({"guid", "name", "description", "overview", "owner", "category", "imageUrl", "versions"},
                         set(package))
        self.assertEqual(GUID, package["guid"])
        self.assertEqual("https://overlabing.github.io/sillon/assets/icon.png", package["imageUrl"])
        self.assertEqual({
            "version": "0.7.0.0",
            "changelog": "Changes in 0.7.0.0",
            "targetAbi": "12.2.0.0",
            "sourceUrl": URL + "/v0.7.0.0/Sillon_0.7.0.0.zip",
            "checksum": hashlib.md5(plugin_zip("0.7.0.0")).hexdigest(),
            "timestamp": "2026-10-21T09:12:19Z",
        }, package["versions"][0])

    def test_every_release_is_kept_newest_first_and_the_newest_describes_the_package(self):
        releases = [release("0.9.0.0", "2026-12-01T00:00:00Z"), release("0.10.0.0", "2027-01-01T00:00:00Z"),
                    release("0.7.0.0", "2026-10-21T09:12:19Z")]

        package = manifest.build(releases, Store("0.7.0.0", "0.9.0.0", "0.10.0.0"))[0]

        self.assertEqual(["0.10.0.0", "0.9.0.0", "0.7.0.0"], [v["version"] for v in package["versions"]])
        self.assertEqual("Description 0.10.0.0", package["description"])

    def test_drafts_and_prereleases_are_left_out(self):
        releases = [release("0.7.0.0", "2026-10-21T09:12:19Z"), release("0.8.0.0", "x", draft=True),
                    release("0.8.0.1", "x", prerelease=True)]

        package = manifest.build(releases, Store("0.7.0.0"))[0]

        self.assertEqual(["0.7.0.0"], [v["version"] for v in package["versions"]])

    def test_a_tag_that_does_not_match_its_zip_is_refused(self):
        with self.assertRaisesRegex(ValueError, "v0.8.0.0"):
            manifest.build([release("0.8.0.0", "x", zip_version="0.7.0.0")], Store("0.7.0.0"))

    def test_a_release_without_its_zip_is_refused(self):
        bare = release("0.7.0.0", "x")
        bare["assets"] = bare["assets"][1:]
        with self.assertRaisesRegex(ValueError, "0 Sillon_"):
            manifest.build([bare], Store())

    def test_releases_of_another_plugin_guid_are_refused(self):
        store = Store("0.7.0.0", **{"Sillon_0.8.0.0.zip": plugin_zip("0.8.0.0", guid="00000000-0000-0000-0000-000000000000")})
        with self.assertRaisesRegex(ValueError, "guid"):
            manifest.build([release("0.7.0.0", "x"), release("0.8.0.0", "y")], store)

    def test_no_release_is_refused(self):
        with self.assertRaisesRegex(ValueError, "no published release"):
            manifest.build([release("0.8.0.0", "x", draft=True)], Store())


if __name__ == "__main__":
    unittest.main()
