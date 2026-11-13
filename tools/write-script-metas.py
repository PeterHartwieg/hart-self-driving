#!/usr/bin/env python3
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
