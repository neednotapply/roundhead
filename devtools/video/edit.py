"""Cut a trailer from the raw takes by an edit list (JSON): crossfaded segments, optional caption per segment (lower band),
a title overlay on the first segment and an end card. Output H.264 MP4, yuv420p, faststart, no audio.
Usage: python edit.py edl.json   (takes and output in $CUPROUNDS_WORK/video; see edl_trailer.json)"""
import json, os, subprocess, sys
import cv2, numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

try:
    import imageio_ffmpeg; FF = imageio_ffmpeg.get_ffmpeg_exe()
except Exception:
    FF = "ffmpeg"
BASE = os.environ.get("CUPROUNDS_VIDEO") or os.path.join(os.environ.get("CUPROUNDS_WORK") or os.path.join(os.path.expanduser("~"), "cuprounds-work"), "video")
FONT_B = r"C:\Windows\Fonts\seguibl.ttf"; FONT_M = r"C:\Windows\Fonts\segoeuib.ttf"
edl = json.load(open(sys.argv[1], encoding="utf-8"))
W, H = edl.get("size", [1280, 720]); FPS = edl.get("fps", 30); XF = int(round(edl.get("xfade", 0.25) * FPS))
out = os.path.join(BASE, edl["out"])

def font(path, size): return ImageFont.truetype(path, size)

def text_layer(lines, fill_band=True, big=False):
    """RGBA overlay (H, W, 4) with the given text lines centred in a lower band, or centred big title lines."""
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    if big:
        f1 = font(FONT_B, 92); f2 = font(FONT_M, 34)
        t1, t2 = lines[0], (lines[1] if len(lines) > 1 else "")
        b1 = d.textbbox((0, 0), t1, font=f1); b2 = d.textbbox((0, 0), t2, font=f2) if t2 else (0, 0, 0, 0)
        hh = (b1[3] - b1[1]) + (b2[3] - b2[1]) + 30; y = (H - hh) // 2 - 40
        band = Image.new("RGBA", (W, H), (0, 0, 0, 0)); bd = ImageDraw.Draw(band)
        bd.rectangle((0, y - 34, W, y + hh + 40), fill=(0, 0, 0, 150)); im = Image.alpha_composite(im, band); d = ImageDraw.Draw(im)
        x1 = (W - (b1[2] - b1[0])) // 2 - b1[0]
        d.text((x1 + 4, y + 4 - b1[1]), t1, font=f1, fill=(0, 0, 0, 255))
        d.text((x1, y - b1[1]), t1, font=f1, fill=(255, 214, 92, 255))
        if t2:
            x2 = (W - (b2[2] - b2[0])) // 2 - b2[0]; y2 = y + (b1[3] - b1[1]) + 30 - b2[1]
            d.text((x2, y2), t2, font=f2, fill=(255, 255, 255, 255))
        return np.array(im)
    f = font(FONT_B, 32); pad = 12; hs = []
    for ln in lines: b = d.textbbox((0, 0), ln, font=f); hs.append((b, ln))
    tot = sum(b[3] - b[1] for b, _ in hs) + 12 * (len(hs) - 1)
    y0 = H - tot - pad - 16
    if fill_band:
        band = Image.new("RGBA", (W, H), (0, 0, 0, 0)); bd = ImageDraw.Draw(band)
        bd.rectangle((0, y0 - pad, W, H), fill=(0, 0, 0, 165)); im = Image.alpha_composite(im, band); d = ImageDraw.Draw(im)
    y = y0
    for b, ln in hs:
        x = (W - (b[2] - b[0])) // 2 - b[0]
        d.text((x + 3, y + 3 - b[1]), ln, font=f, fill=(0, 0, 0, 255))
        d.text((x, y - b[1]), ln, font=f, fill=(255, 255, 255, 255)); y += (b[3] - b[1]) + 12
    return np.array(im)

def over(frame, layer, a):
    if layer is None or a <= 0: return frame
    al = layer[:, :, 3:4].astype(np.float32) / 255.0 * a
    rgb = layer[:, :, 2::-1].astype(np.float32)  # RGBA -> BGR
    return (frame.astype(np.float32) * (1 - al) + rgb * al).astype(np.uint8)

def segment_frames(seg):
    cap = cv2.VideoCapture(os.path.join(BASE, "raw", seg["take"], "run.mp4"))
    cap.set(cv2.CAP_PROP_POS_MSEC, seg["start"] * 1000)
    n = int(round((seg["end"] - seg["start"]) * FPS)); frames = []
    speed = seg.get("speed", 1.0)
    src_n = int(round(n * speed)); src = []
    for _ in range(src_n):
        ok, f = cap.read()
        if not ok: break
        if f.shape[1] != W or f.shape[0] != H: f = cv2.resize(f, (W, H), interpolation=cv2.INTER_AREA)
        src.append(f)
    if not src: raise SystemExit(f"no frames for {seg}")
    for i in range(n): frames.append(src[min(len(src) - 1, int(i * speed))])
    cap_lines = seg.get("caption"); layer = text_layer(cap_lines if isinstance(cap_lines, list) else [cap_lines]) if cap_lines else None
    title = text_layer(seg["title"], big=True) if seg.get("title") else None
    fin = int(0.3 * FPS)
    for i in range(len(frames)):
        a = min(1.0, (i + 1) / fin, (len(frames) - i) / fin)
        f = frames[i]
        if layer is not None: f = over(f, layer, a)
        if title is not None:
            ta = min(1.0, (i + 1) / fin, max(0.0, (seg.get("title_secs", 3.0) * FPS - i) / fin))
            f = over(f, title, ta)
        frames[i] = f
    return frames

def end_card(last, card):
    n = int(card.get("secs", 3.5) * FPS)
    bg = Image.fromarray(cv2.cvtColor(last, cv2.COLOR_BGR2RGB)).filter(ImageFilter.GaussianBlur(14))
    bg = np.array(bg)[:, :, ::-1].copy(); bg = (bg.astype(np.float32) * 0.55).astype(np.uint8)
    layer = text_layer(card["lines"], big=True); frames = []
    for i in range(n):
        a = min(1.0, (i + 1) / int(0.5 * FPS)); frames.append(over(bg, layer, a))
    return frames

ff = subprocess.Popen([FF, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-",
                       "-c:v", "libx264", "-preset", edl.get("preset", "slow"), "-crf", str(edl.get("crf", 20)), "-pix_fmt", "yuv420p", "-profile:v", "high",
                       "-movflags", "+faststart", out], stdin=subprocess.PIPE)
tail = []; total = 0; last = None
segs = edl["segments"]
for si, seg in enumerate(segs):
    fr = segment_frames(seg)
    if tail and XF > 0 and not seg.get("cut"):
        k = min(XF, len(tail), len(fr))
        for i in range(k):
            a = (i + 1) / (k + 1); fr[i] = cv2.addWeighted(tail[len(tail) - k + i], 1 - a, fr[i], a, 0)
        tail = tail[:len(tail) - k]
    for f in tail: ff.stdin.write(f.tobytes()); total += 1
    keep = XF if si < len(segs) - 1 else 0
    body, tail = (fr[:-keep], fr[-keep:]) if keep and len(fr) > keep else (fr, [])
    for f in body: ff.stdin.write(f.tobytes()); total += 1
    last = fr[-1]
for f in tail: ff.stdin.write(f.tobytes()); total += 1
if edl.get("end_card"):
    for f in end_card(last, edl["end_card"]): ff.stdin.write(f.tobytes()); total += 1
ff.stdin.close(); ff.wait()
print(f"{out}: {total} frames, {total / FPS:.1f} s, {os.path.getsize(out) / 1e6:.1f} MB")
