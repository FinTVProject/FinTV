#!/usr/bin/env python3
"""Patch a JPRM plugin zip so Jellyfin only loads managed assemblies.

Jellyfin PluginManager scans every *.dll under the plugin folder when meta.json
has no assemblies whitelist. Native SQLite files such as
runtimes/win-arm/native/e_sqlite3.dll then fail to load and the plugin is disabled.
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD_YAML = ROOT / "plugin" / "build.yaml"
if not BUILD_YAML.is_file():
    BUILD_YAML = ROOT / "build.yaml"
IMAGE_RESOURCE = "Jellyfin.Plugin.FinTV.logo.png"


def load_assemblies() -> list[str]:
    try:
        import yaml
    except ImportError as exc:
        raise SystemExit("PyYAML is required (install jprm or pyyaml)") from exc

    build_cfg = yaml.safe_load(BUILD_YAML.read_text(encoding="utf-8"))
    assemblies = build_cfg.get("assemblies")
    if not assemblies:
        raise SystemExit("build.yaml must define assemblies")
    return list(assemblies)


def finalize(zip_path: Path) -> None:
    assemblies = load_assemblies()
    tmp_path = zip_path.with_suffix(zip_path.suffix + ".tmp")

    with zipfile.ZipFile(zip_path, "r") as zin, zipfile.ZipFile(tmp_path, "w") as zout:
        for item in zin.infolist():
            if item.filename == "logo.png":
                continue

            data = zin.read(item.filename)
            if item.filename == "meta.json":
                meta = json.loads(data)
                meta["assemblies"] = assemblies
                meta["imageResourceName"] = IMAGE_RESOURCE
                meta["imagePath"] = ""
                data = json.dumps(meta, indent=4, sort_keys=True).encode("utf-8")
            zout.writestr(item, data)

    shutil.move(tmp_path, zip_path)
    print(f"Whitelisted {len(assemblies)} managed assemblies in {zip_path}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip_path", type=Path, help="Path to fintv_<version>.zip")
    args = parser.parse_args()
    zip_path = args.zip_path.expanduser().resolve()
    if not zip_path.is_file():
        print(f"Zip not found: {zip_path}", file=sys.stderr)
        return 1

    finalize(zip_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
