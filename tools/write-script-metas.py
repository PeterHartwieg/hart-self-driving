#!/usr/bin/env python3
"""Writes a .meta for every .cs file under the given folders that lacks one.

The GUID comes from the file's path relative to the repo, so a new script gets the same GUID on every
machine and the metas can be committed. Run by tools/build-mod.sh before Unity starts.
"""
import hashlib
import pathlib
import sys

TEMPLATE = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""

repo = pathlib.Path(__file__).resolve().parent.parent
for folder in sys.argv[1:]:
    for script in sorted(pathlib.Path(folder).rglob("*.cs")):
        meta = script.with_name(script.name + ".meta")
        if meta.exists():
            continue
        rel = script.resolve().relative_to(repo).as_posix()
        guid = hashlib.md5(("drivenrides:" + rel).encode()).hexdigest()
        meta.write_text(TEMPLATE.format(guid=guid))
        print(f"meta written: {rel}")
