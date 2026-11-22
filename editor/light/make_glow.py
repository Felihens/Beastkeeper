# Turns the totem light editor's saved spots (db docs glow/<animal>, dumped as JSON files) into res/glow.txt.
# Usage: python make_glow.py <folder with the dumped <animal>.json files> <path of res/glow.txt>
import sys, os, json

src, out = sys.argv[1], sys.argv[2]
POSES = ["idle", "walk", "run"]
lines = ["# Where the totem light sits on each animal, per pose: <prefab> <pose> x y z ball-size",
         "# (its own frame, metres at scale 1). Made with the totem light editor; poses: idle (drake: hovering),",
         "# walk (drake: on the ground), run (drake: flying forward)."]
for f in sorted(os.listdir(src)):
    if not f.endswith(".json"):
        continue
    name = f[:-5]
    d = json.load(open(os.path.join(src, f), encoding="utf-8"))
    if "data" in d and isinstance(d["data"], dict):
        d = d["data"]
    spots = {}
    for p in POSES:
        s = (d.get("poses") or {}).get(p)
        if s and "y" in s:
            spots[p] = s
    if "idle" not in spots and "y" in d:
        spots["idle"] = d   # the first version: one spot, read as standing
    for p in POSES:
        s = spots.get(p)
        if s:
            lines.append("%s %s %.4f %.4f %.4f %.3f" % (name, p, s["x"], s["y"], s["z"], s.get("size", 0.45)))
open(out, "w", encoding="utf-8", newline="\n").write("\n".join(lines) + "\n")
print("\n".join(lines[3:]))
