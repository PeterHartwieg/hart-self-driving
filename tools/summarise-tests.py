#!/usr/bin/env python3
"""Summarises a turn test results file (docs/turn-tests.md): counts per kind and car, the controls, the failures."""
import collections
import json
import sys


def main(path):
    runs = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    if not runs:
        print("No runs.")
        return
    print(f"{path}: {len(runs)} runs, version {runs[0].get('version')}, speed {runs[0].get('speed')}x")
    table = collections.defaultdict(collections.Counter)
    for r in runs:
        table[(r["kind"], r["car"])][r["verdict"]] += 1
    print("\nkind / car                                   pass  fail  other")
    for (kind, car), c in sorted(table.items()):
        other = sum(v for k, v in c.items() if k not in ("pass", "fail"))
        print(f"{kind:<12} {car:<30} {c['pass']:>5} {c['fail']:>5} {other:>6}")

    controls = [r for r in runs if r["kind"] == "Control"]
    if controls:
        print("\nControls:")
        for r in controls:
            fx = r.get("fixture", {})
            expected = "fail " + str(fx.get("ExpectedFailure")) if fx.get("IsMutation") else "pass"
            reasons = " ".join(r.get("judge", {}).get("Reasons", []))
            print(f"  {r['case']:<40} expected {expected:<26} got {r['verdict']} {reasons}")

    reasons = collections.Counter()
    for r in runs:
        if r["verdict"] == "fail" and r["kind"] != "Control":
            for reason in r.get("judge", {}).get("Reasons", []):
                reasons[reason.split(":")[0]] += 1
    if reasons:
        print("\nFailure reasons: " + ", ".join(f"{k} {v}" for k, v in reasons.most_common()))

    print("\nFailures and other outcomes:")
    for r in runs:
        if r["verdict"] == "pass" or r["kind"] == "Control":
            continue
        j = r.get("judge", {})
        detail = " ".join(j.get("Reasons", [])) or r.get("why", "") or r.get("end", "")
        extra = f" entry {j.get('EntrySpeed', 0):.1f} m/s, intrusion {j.get('MaxIntrusion', 0):.2f} m, near miss {j.get('NearMiss', 0)}" if j else ""
        pic = f" [{r['picture']}]" if r.get("picture") else ""
        print(f"  {r['run']} {r['case']} ({r['car']}): {r['verdict']}, {detail}{extra}{pic}")


if __name__ == "__main__":
    main(sys.argv[1])
