"""Save the dev arena's latest frame pair (world and UI buffers) as PNGs: <prefix>-world.png, <prefix>-ui.png and a
composite over a plain background, plus a line of alpha statistics for the UI picture. Reads only (and marks the buffer
it copies as being read, as Cuphead does)."""
import mmap, os, struct, sys
from PIL import Image

MAP = os.environ.get('PROBE_MAP', "Local\\CupRoundsArenaDev")
MAXW, MAXH = 1280, 720
FB = MAXW * MAXH * 4
FRAMES = 131072
UIFRAMES = FRAMES + 3 * FB
SIZE = FRAMES + 6 * FB
R = 65536
C_READING = 44

def main():
    prefix = sys.argv[1] if len(sys.argv) > 1 else 'frame'
    m = mmap.mmap(-1, SIZE, tagname=MAP)
    def ri(o): return struct.unpack_from('<i', m, o)[0]
    def rf(o): return struct.unpack_from('<f', m, o)[0]
    serial, ready, w, h = ri(R + 16), ri(R + 20), ri(R + 36), ri(R + 40)
    print('serial %d ready %d size %dx%d cam %.1f,%.1f ortho %.1f' % (serial, ready, w, h, rf(R + 24), rf(R + 28), rf(R + 32)))
    if w <= 0 or h <= 0 or serial == 0:
        print('no frame yet'); return 1
    struct.pack_into('<i', m, C_READING, ready)
    world = bytes(m[FRAMES + ready * FB: FRAMES + ready * FB + w * h * 4])
    ui = bytes(m[UIFRAMES + ready * FB: UIFRAMES + ready * FB + w * h * 4])
    struct.pack_into('<i', m, C_READING, -1)
    def save(buf, name):
        im = Image.frombytes('RGBA', (w, h), buf).transpose(Image.FLIP_TOP_BOTTOM)
        im.save(name)
        return im
    wi = save(world, prefix + '-world.png')
    ui_im = save(ui, prefix + '-ui.png')
    bg = Image.new('RGBA', (w, h), (150, 170, 200, 255))
    bg.alpha_composite(wi)
    bg.alpha_composite(ui_im)
    bg.convert('RGB').save(prefix + '-composite.png')
    for name, im in (('world', wi), ('ui', ui_im)):
        hist = im.getchannel('A').histogram()
        covered = sum(hist[1:])
        mean = sum(i * hist[i] for i in range(1, 256)) / max(1, covered)
        print('%s: %d covered px, %d opaque, mean alpha of covered %.0f' % (name, covered, hist[255], mean))
    return 0

if __name__ == '__main__':
    sys.exit(main())
