"""Compile a Ludusavi manifest snapshot to the offline Windows catalog (requires PyYAML)."""

import gzip
import json
import sys
from pathlib import Path

import yaml


def rules(entries):
    for path, entry in (entries or {}).items():
        entries_for_path = entry if isinstance(entry, list) else [entry]
        conditions = [
            condition
            for variant in entries_for_path
            for condition in ((variant or {}).get("when") or [{}])
            if condition.get("os") in (None, "windows")
        ]
        if conditions:
            stores = sorted({c["store"] for c in conditions}) if all("store" in c for c in conditions) else []
            yield {"path": path, "stores": stores}


def store_ids(game, store):
    ids = [str(i) for i in (game.get("id", {}).get(f"{store}Extra") or [])]
    primary = game.get(store, {}).get("id")
    return sorted(set(ids + ([str(primary)] if primary is not None else [])))


def compile_catalog(manifest):
    aliases = {}
    for name, game in manifest.items():
        if "alias" in game:
            aliases.setdefault(game["alias"], []).append(name)

    for name, game in manifest.items():
        if "alias" in game:
            continue
        files = list(rules(game.get("files")))
        registry = list(rules(game.get("registry")))
        steam, gog = store_ids(game, "steam"), store_ids(game, "gog")
        if files or registry or steam or gog:
            yield {
                "name": name,
                "names": sorted(set([name, *aliases.get(name, []), *(game.get("installDir") or {})])),
                "steam": steam,
                "gog": gog,
                "files": files,
                "registry": registry,
                "launch": [rule for rule in rules(game.get("launch")) if rule["path"].lower().endswith(".exe")],
            }


if __name__ == "__main__":
    source, destination = map(Path, sys.argv[1:])
    with source.open(encoding="utf-8") as stream:
        manifest = yaml.load(stream, Loader=yaml.CSafeLoader)
    with destination.open("wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as output:
        data = json.dumps(list(compile_catalog(manifest)), ensure_ascii=False, separators=(",", ":"))
        output.write(data.encode("utf-8"))
