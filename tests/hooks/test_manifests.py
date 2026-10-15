#!/usr/bin/env python3
"""The two plugin manifests must agree with each other (second-round inspector #13, #36).

Run: python3 -m unittest discover -s tests/hooks
"""
import json
import os
import re
import unittest

MANIFESTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".claude-plugin")


def load(name):
    with open(os.path.join(MANIFESTS, name), encoding="utf-8") as f:
        return json.load(f)


class ManifestTest(unittest.TestCase):
    def test_versions_agree_and_are_semver(self):
        plugin = load("plugin.json")
        market = load("marketplace.json")
        entry = next(p for p in market["plugins"] if p["name"] == plugin["name"])
        self.assertRegex(plugin["version"], r"^\d+\.\d+\.\d+$")
        self.assertEqual(plugin["version"], entry["version"])

    def test_version_is_not_older_than_the_slkit_breaking_release(self):
        # SlKit 0.2.0 broke its API; the plugin that ships it must not still say 0.1.x.
        plugin = load("plugin.json")
        major, minor, _ = (int(x) for x in plugin["version"].split("."))
        self.assertGreaterEqual((major, minor), (0, 2))

    def test_repository_and_homepage_are_set_together_or_not_at_all(self):
        # Before the repository is public they are left out: a link to a repository that does not exist
        # is worse than no link. Once it is published, they are set in BOTH manifests, to the same URL.
        plugin = load("plugin.json")
        market_entry = load("marketplace.json")["plugins"][0]
        urls = {plugin.get("repository"), plugin.get("homepage"), market_entry.get("homepage")}
        urls.discard(None)
        if urls:
            self.assertEqual(len(urls), 1, f"manifests disagree on the repository URL: {urls}")
            self.assertTrue(plugin.get("repository") and plugin.get("homepage") and market_entry.get("homepage"),
                            "repository/homepage must be present in plugin.json AND marketplace.json")
            self.assertRegex(next(iter(urls)), r"^https://")


if __name__ == "__main__":
    unittest.main()
