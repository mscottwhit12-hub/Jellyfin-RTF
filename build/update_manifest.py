"""Adds the release being built to manifest.json (newest first). Run by the release workflow."""
import json
import os
from datetime import datetime, timezone

REPO = os.environ.get("GITHUB_REPOSITORY", "mscottwhit12-hub/Jellyfin-RTF")
version = os.environ["VERSION"]
zip_name = os.environ["ZIP"]
tag = os.environ.get("GITHUB_REF_NAME", "v" + version.rsplit(".", 1)[0])

with open("manifest.json", encoding="utf-8") as f:
    manifest = json.load(f)

plugin = manifest[0]
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": os.environ.get("NOTES", ""),
    "targetAbi": "12.0.0.0",
    "sourceUrl": f"https://github.com/{REPO}/releases/download/{tag}/{zip_name}",
    "checksum": os.environ["MD5"],
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})

with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print(f"manifest.json now lists {len(plugin['versions'])} version(s); newest {version}")
