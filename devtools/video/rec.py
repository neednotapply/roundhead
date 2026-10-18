"""Record one game window (default Cuphead.exe) to an MP4 through PrintWindow (PW_RENDERFULLCONTENT), which captures the
window's own content even when other windows cover it, never the desktop. Frames are paced to a fixed rate: when a grab is
late, the last frame is repeated so the video keeps real time. Stops when the window goes away, after --secs, or when the
--stop file appears. Usage: python -I rec.py out.mp4 [--proc Cuphead.exe] [--fps 30] [--secs 600] [--stop path]"""
import argparse, ctypes, ctypes.wintypes as wt, os, queue, subprocess, sys, threading, time
import numpy as np, cv2

try:
    import imageio_ffmpeg; FF = imageio_ffmpeg.get_ffmpeg_exe()
except Exception:
    FF = "ffmpeg"
u32 = ctypes.WinDLL("user32", use_last_error=True); g32 = ctypes.WinDLL("gdi32"); k32 = ctypes.WinDLL("kernel32")
u32.SetProcessDPIAware()
WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

def proc_name(pid):
    h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h: return ""
    buf = ctypes.create_unicode_buffer(260); n = wt.DWORD(260)
    ok = k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)); k32.CloseHandle(h)
    return os.path.basename(buf.value).lower() if ok else ""

def find_window(exe):
    best = []
    def cb(h, _):
        if not u32.IsWindowVisible(h): return True
        pid = wt.DWORD(); u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        if proc_name(pid.value) == exe.lower():
            r = wt.RECT(); u32.GetClientRect(h, ctypes.byref(r))
            if r.right * r.bottom > 0: best.append((r.right * r.bottom, h))
        return True
    u32.EnumWindows(WNDENUMPROC(cb), 0)
    return max(best)[1] if best else None

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

_g0 = {}
def grab(h, _g=_g0):
    r = wt.RECT()
    if not u32.GetClientRect(h, ctypes.byref(r)): return None
    w, hh = r.right, r.bottom
    if w <= 0 or hh <= 0: return None
    if _g.get("size") != (w, hh):
        if _g: g32.SelectObject(_g["mdc"], _g["old"]); g32.DeleteObject(_g["bmp"]); g32.DeleteDC(_g["mdc"])
        wdc = u32.GetDC(h); mdc = g32.CreateCompatibleDC(wdc); bmp = g32.CreateCompatibleBitmap(wdc, w, hh); u32.ReleaseDC(h, wdc)
        _g.update(size=(w, hh), mdc=mdc, bmp=bmp, old=g32.SelectObject(mdc, bmp), buf=np.empty((hh, w, 4), np.uint8),
                  bi=BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), w, -hh, 1, 32, 0, 0, 0, 0, 0, 0))
    ok = u32.PrintWindow(h, _g["mdc"], 3)
    g32.GetDIBits(_g["mdc"], _g["bmp"], 0, hh, _g["buf"].ctypes.data_as(ctypes.c_void_p), ctypes.byref(_g["bi"]), 0)
    return _g["buf"][:, :, :3] if ok else None

def stable_size(h, secs=4.0, limit=120.0):
    """Waits until the window's client size has not changed for secs (the game settles its resolution after start)."""
    t0 = time.time(); last = None; since = time.time()
    while time.time() - t0 < limit and u32.IsWindow(h):
        r = wt.RECT(); u32.GetClientRect(h, ctypes.byref(r)); cur = (r.right, r.bottom)
        if cur != last: last = cur; since = time.time()
        elif time.time() - since >= secs: return cur
        time.sleep(0.25)
    return last

def gdigrab(h, o):
    """ffmpeg's own GDI window grabber (BitBlt of the window's client area): far cheaper than PrintWindow when the game's
    swap chain is GDI-readable; checked by the caller for black frames."""
    size = stable_size(h)
    n = u32.GetWindowTextLengthW(h); b = ctypes.create_unicode_buffer(n + 1); u32.GetWindowTextW(h, b, n + 1)
    print(f"rec: gdigrab on '{b.value}' {size}", flush=True)
    W, H = map(int, o.size.split("x"))
    ff = subprocess.Popen([FF, "-y", "-loglevel", "error", "-f", "gdigrab", "-framerate", str(o.fps), "-draw_mouse", "0", "-i", "title=" + b.value,
                           "-vf", f"scale={W}:{H}:flags=area", "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p",
                           "-movflags", "+faststart", o.out], stdin=subprocess.PIPE)
    start = time.time()
    while time.time() - start < o.secs and not (o.stop and os.path.exists(o.stop)) and u32.IsWindow(h) and ff.poll() is None: time.sleep(0.2)
    if ff.poll() is None:
        try: ff.stdin.write(b"q"); ff.stdin.flush(); ff.stdin.close()
        except OSError: pass
        try: ff.wait(20)
        except subprocess.TimeoutExpired: ff.kill()
    print(f"rec: gdigrab {time.time()-start:.1f} s, ffmpeg exit {ff.returncode} -> {o.out}")
    return 0

def main():
    a = argparse.ArgumentParser(); a.add_argument("out"); a.add_argument("--proc", default="Cuphead.exe"); a.add_argument("--fps", type=int, default=30)
    a.add_argument("--secs", type=float, default=600); a.add_argument("--stop", default=""); a.add_argument("--wait", type=float, default=120)
    a.add_argument("--size", default="1280x720"); a.add_argument("--method", default="print"); a.add_argument("--threads", type=int, default=2); o = a.parse_args()
    W, H = map(int, o.size.split("x"))
    t0 = time.time(); h = None
    while h is None and time.time() - t0 < o.wait:
        h = find_window(o.proc); time.sleep(0.5) if h is None else None
    if h is None: print("rec: no window"); return 1
    if o.method == "gdi": return gdigrab(h, o)
    stable_size(h, 3.0, 60.0)
    ff = subprocess.Popen([FF, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{W}x{H}", "-r", str(o.fps), "-i", "-",
                           "-c:v", "libx264", "-preset", "ultrafast", "-crf", "14", "-pix_fmt", "yuv420p", "-movflags", "+faststart", o.out], stdin=subprocess.PIPE)
    q = queue.Queue(maxsize=12)
    def writer():
        while True:
            item = q.get()
            if item is None: return
            data, k = item
            for _ in range(k): ff.stdin.write(data)
    wt_ = threading.Thread(target=writer, daemon=True); wt_.start()
    step = 1.0 / o.fps; start = time.time(); n = 0; dup = 0; grabs = []
    latest = {"seq": 0, "frame": np.zeros((H, W, 3), np.uint8)}; lock = threading.Lock(); running = [True]
    def grabber():
        st = {}
        while running[0]:
            g0 = time.time(); f = grab(h, st); dt = time.time() - g0
            if f is None: time.sleep(0.01); continue
            if f.shape[1] != W or f.shape[0] != H: f = cv2.resize(f, (W, H), interpolation=cv2.INTER_AREA)
            f = np.ascontiguousarray(f)
            with lock:
                grabs.append(dt); latest["seq"] += 1; latest["frame"] = f
    gts = [threading.Thread(target=grabber, daemon=True) for _ in range(max(1, o.threads))]
    for i, t in enumerate(gts): t.start(); time.sleep(step / max(1, o.threads))
    start = time.time(); seen = 0
    while True:
        if time.time() - start > o.secs or (o.stop and os.path.exists(o.stop)) or not u32.IsWindow(h): break
        with lock: seq, f = latest["seq"], latest["frame"]
        due = int((time.time() - start) / step) + 1
        k = max(1, due - n)
        if seq == seen: dup += k
        else: dup += k - 1
        seen = seq
        q.put((f.tobytes(), k)); n += k
        sl = start + n * step - time.time()
        if sl > 0: time.sleep(sl)
    running[0] = False
    for t in gts: t.join(2)
    q.put(None); wt_.join(); ff.stdin.close(); ff.wait()
    g = sorted(grabs)
    print(f"rec: {n} frames ({n/o.fps:.1f} s), {dup} repeated, grab median {1000*g[len(g)//2]:.1f} ms, p90 {1000*g[int(len(g)*0.9)]:.1f} ms -> {o.out}")
    return 0

sys.exit(main())
