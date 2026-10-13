#!/usr/bin/env python3
"""Compares two turn test results files case by case (docs/nwh-drive-spike.md): typically an NWH drive
(driver=nwh) against a Gley one. Usage: compare-drivers.py <results A> <results B> [kinds, e.g. Turn,UTurn]"""
import collections
import json
import sys


def load(path, kinds):
    runs = {}
    for line in open(path, encoding="utf-8"):
        if not line.strip():
            continue
        r = json.loads(line)
        if kinds and r["kind"] not in kinds:
            continue
        runs[(r["case"], r["car"].replace("ba:vehicletype_", ""))] = r
    return runs


def describe(r):
    if r is None:
        return "-"
    j = r.get("judge") or {}
    reasons = " ".join(x.split(":")[0] for x in j.get("Reasons", []))
    return f"{r['verdict']}{' (' + reasons + ')' if reasons and r['verdict'] != 'pass' else ''}"


def main(a_path, b_path, kinds):
    a, b = load(a_path, kinds), load(b_path, kinds)
    cars = sorted({car for _, car in a} | {car for _, car in b})
    print(f"A: {a_path}\nB: {b_path}")
    for car in cars:
        cases = sorted({c for c, k in a if k == car} | {c for c, k in b if k == car})
        both = [c for c in cases if (c, car) in a and (c, car) in b]
        if not both:
            continue
        counts = collections.Counter()
        print(f"\n{car}: {len(both)} cases in both")
        for c in both:
            ra, rb = a[(c, car)], b[(c, car)]
            counts[(ra["verdict"], rb["verdict"])] += 1
            if ra["verdict"] != rb["verdict"] or ra["verdict"] != "pass":
                ja, jb = ra.get("judge") or {}, rb.get("judge") or {}
                print(f"  {c:<26} A {describe(ra):<34} B {describe(rb):<34} entry {ja.get('EntrySpeed', 0):4.1f}/{jb.get('EntrySpeed', 0):4.1f} m/s")
        print("  verdicts (A, B): " + ", ".join(f"{k[0]}/{k[1]} {v}" for k, v in counts.most_common()))
        for label, runs in (("A", a), ("B", b)):
            js = [runs[(c, car)].get("judge") or {} for c in both]
            passed = [j for j in js if j.get("Verdict") == "pass"]
            if passed:
                dev = sum(j.get("PlainDeviationMax", 0) for j in passed) / len(passed)
                lat = sum(abs(j.get("LastGateLateral", 0)) for j in passed) / len(passed)
                head = sum(abs(j.get("LastGateHeadingError", 0)) for j in passed) / len(passed)
                print(f"  {label} passes: mean max lane deviation {dev:.2f} m, last gate {lat:.2f} m off and {head:.1f} degrees")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], set(sys.argv[3].split(",")) if len(sys.argv) > 3 else None)
