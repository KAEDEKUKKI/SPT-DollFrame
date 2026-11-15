#!/usr/bin/env python3
"""Schema validator for analysis/model_matrix.json.

Validates the pinned matrix contract: exact top-level keys, unitScale 0.078,
exactly 41 packages (2026-11-13 SDF/寫實重製管線重建：19 包家族原皮＋休息室/衣裝變體
＋萊娜（含海倫×2）＝41; 臂鏈 k > 1.45 的伊格蕾塔/佩莉/索米/芙洛倫/芙鈴/維爾德維持淘汰),
the 13 per-package keys and the 12 factor groups.
Prints the error list and exits non-zero on any violation.
"""
import json
import sys
from pathlib import Path

MATRIX = Path(__file__).resolve().parent.parent / "analysis" / "model_matrix.json"

TOP_KEYS = {"unitScale", "packages"}
PKG_KEYS = ["pkg", "bones", "verts", "skinBones", "heightU", "heightM", "hipM",
            "groundOffsetM", "scaleFlag", "missing", "dups", "accessoryMats", "factors"]
FACTOR_KEYS = ["hips", "spineRoot", "torso", "chestNeck", "neckHead", "hipOffset",
               "thigh", "shank", "foot", "clavicle", "uarm", "farm"]
EXPECTED_PACKAGES = 41

errors = []


def main() -> int:
    try:
        data = json.loads(MATRIX.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"FAIL: cannot read {MATRIX}: {e}")
        return 1

    if not isinstance(data, dict):
        errors.append(f"top level is {type(data).__name__}, expected object")
        report()
        return 1
    if set(data.keys()) != TOP_KEYS:
        errors.append(f"top-level keys {sorted(data.keys())} != {sorted(TOP_KEYS)}")
    if data.get("unitScale") != 0.078:
        errors.append(f"unitScale={data.get('unitScale')} != 0.078")
    packages = data.get("packages")
    if not isinstance(packages, list):
        errors.append("packages is not a list")
        report()
        return 1
    if len(packages) != EXPECTED_PACKAGES:
        errors.append(f"{len(packages)} packages != {EXPECTED_PACKAGES}")
    if not all(isinstance(p, dict) for p in packages):
        errors.append("packages contains non-object entries")
        report()
        return 1

    seen = set()
    for idx, p in enumerate(packages):
        label = p.get("pkg", f"#{idx}")
        if label in seen:
            errors.append(f"duplicate pkg {label}")
        seen.add(label)
        keys = list(p.keys())
        if set(keys) != set(PKG_KEYS):
            errors.append(f"{label}: keys {keys} != {PKG_KEYS}")
        if not isinstance(p.get("pkg"), str) or not p["pkg"].endswith(".gfc"):
            errors.append(f"{label}: pkg is not a .gfc name")
        for k in ("bones", "verts", "skinBones", "dups", "accessoryMats"):
            if not is_int(p.get(k)):
                errors.append(f"{label}: {k}={p.get(k)!r} is not an int")
        for k in ("heightU", "heightM", "hipM", "groundOffsetM"):
            if not isinstance(p.get(k), (int, float)) or isinstance(p.get(k), bool):
                errors.append(f"{label}: {k}={p.get(k)!r} is not a number")
        if not isinstance(p.get("scaleFlag"), bool):
            errors.append(f"{label}: scaleFlag={p.get('scaleFlag')!r} is not a bool")
        missing = p.get("missing")
        if not isinstance(missing, list) or not all(isinstance(x, str) for x in missing):
            errors.append(f"{label}: missing is not a string list")
        if isinstance(missing, list) and missing:
            errors.append(f"{label}: missing is non-empty: {missing}")
        factors = p.get("factors")
        if not isinstance(factors, dict):
            errors.append(f"{label}: factors is not an object")
        else:
            if list(factors.keys()) != FACTOR_KEYS:
                errors.append(f"{label}: factors keys {list(factors.keys())} != {FACTOR_KEYS}")
            for k, v in factors.items():
                if not isinstance(v, (int, float)) or isinstance(v, bool):
                    errors.append(f"{label}: factor {k}={v!r} is not a number")
    report()
    return 0


def is_int(v) -> bool:
    return isinstance(v, int) and not isinstance(v, bool)


def report() -> None:
    if errors:
        for e in errors:
            print(f"FAIL: {e}")
        sys.exit(1)
    print("OK")


if __name__ == "__main__":
    sys.exit(main())
