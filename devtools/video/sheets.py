"""Labeled 1 fps contact sheets of one take for picking moments: review/<take>/sheet_NN.jpg, 6x5 frames of 320x180, each
stamped with its time in the raw video (s.s). Takes live in $CUPROUNDS_WORK/video. From 10 s before the blob spawned (the map) to the end.
Usage: python sheets.py <take> [step seconds]"""
import os, sys, cv2, numpy as np
take = sys.argv[1]; step = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
base = os.environ.get("CUPROUNDS_VIDEO") or os.path.join(os.environ.get("CUPROUNDS_WORK") or os.path.join(os.path.expanduser("~"), "cuprounds-work"), "video")
raw = os.path.join(base, "raw", take); out = os.path.join(base, "review", take)
os.makedirs(out, exist_ok=True)
for f in os.listdir(out): os.remove(os.path.join(out, f))
spawn = 0
try: spawn = int(open(os.path.join(raw, "meta.txt")).read().split("=")[1])
except Exception: pass
cap = cv2.VideoCapture(os.path.join(raw, "run.mp4")); dur = cap.get(cv2.CAP_PROP_FRAME_COUNT) / cap.get(cv2.CAP_PROP_FPS)
t = max(0.0, spawn - 14.0); tiles = []; n = 0
def flush():
    global tiles, n
    if not tiles: return
    while len(tiles) < 30: tiles.append(np.zeros((180, 320, 3), np.uint8))
    rows = [np.hstack(tiles[i:i + 6]) for i in range(0, 30, 6)]
    n += 1; cv2.imwrite(os.path.join(out, f"sheet_{n:02d}.jpg"), np.vstack(rows), [cv2.IMWRITE_JPEG_QUALITY, 88]); tiles = []
while t < dur - 0.2:
    cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000); ok, f = cap.read()
    if not ok: break
    f = cv2.resize(f, (320, 180), interpolation=cv2.INTER_AREA)
    lab = f"{t:.1f}"
    cv2.rectangle(f, (0, 0), (8 + 11 * len(lab), 20), (0, 0, 0), -1)
    cv2.putText(f, lab, (4, 15), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)
    tiles.append(f)
    if len(tiles) == 30: flush()
    t += step
flush()
print(f"{take}: {n} sheets from {max(0.0, spawn - 14.0):.0f} s to {dur:.0f} s (spawn at {spawn} s) -> {out}")
