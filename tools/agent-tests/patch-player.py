"""Give the private player its own native Unity preferences, data and cache identity."""
import hashlib
import json
import pathlib
import sys
import UnityPy

PRODUCT = "Big Ambitions Agent Tests"

def patch(src, dst, product=PRODUCT):
    if product not in (PRODUCT, "Big Ambitions Agent Tests Mac"): raise ValueError("Unknown agent product")
    if src.resolve() == dst.resolve():
        raise ValueError("Never patch the original player in place")
    env = UnityPy.load(str(src))
    obj, = [o for o in env.objects if o.type.name == "PlayerSettings"]
    original = obj.read_typetree()
    if original["companyName"] != "Hovgaard Games" or original["productName"] != "Big Ambitions":
        raise ValueError("Unrecognized or already patched player identity")
    new = dict(original)
    new.update(productName=product, runInBackground=True, forceSingleInstance=False,
               submitAnalytics=False, defaultScreenWidth=960, defaultScreenHeight=540,
               defaultIsNativeResolution=False, fullscreenMode=3, resizableWindow=True)
    obj.save_typetree(new)
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes(env.file.save())
    check = UnityPy.load(str(dst))
    checked, = [o.read_typetree() for o in check.objects if o.type.name == "PlayerSettings"]
    assert checked == new
    # Preserve every other serialized object byte-for-byte.
    before = {o.path_id: o.get_raw_data() for o in UnityPy.load(str(src)).objects if o.type.name != "PlayerSettings"}
    after = {o.path_id: o.get_raw_data() for o in check.objects if o.type.name != "PlayerSettings"}
    assert before == after
    receipt = {"product": product, "source_sha256": hashlib.sha256(src.read_bytes()).hexdigest(),
               "patched_sha256": hashlib.sha256(dst.read_bytes()).hexdigest(),
               "changed_fields": [k for k in new if original[k] != new[k]]}
    dst.with_name("player-manifest.json").write_text(json.dumps(receipt, indent=2))
    print(json.dumps(receipt, indent=2))

if __name__ == "__main__":
    patch(pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3] if len(sys.argv) > 3 else PRODUCT)
