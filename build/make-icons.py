"""Renders the VDM icon with headless Chrome and writes the Windows .ico and the extension PNGs.

    python build/make-icons.py

Needs only Python and Chrome (or Edge). Small icon sizes are stored as 32-bit BMP frames, which
every Windows component reads; 256 px is stored as PNG.
"""
import os
import struct
import subprocess
import sys
import tempfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The mark on a teal tile: an arrow dropping into a bar cut into parts, the last part still filling.
SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="100%" height="100%">
  <rect width="24" height="24" rx="5.4" fill="#1b6c80"/>
  <path d="M12 4.2v8.6M8.1 9.2 12 13.1l3.9-3.9" fill="none" stroke="#fff" stroke-width="2.3"
        stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="4.3" y="16" width="4.6" height="3.8" rx="1" fill="#fff"/>
  <rect x="9.7" y="16" width="4.6" height="3.8" rx="1" fill="#fff"/>
  <rect x="15.1" y="16" width="4.6" height="3.8" rx="1" fill="#fff" opacity=".45"/>
</svg>"""

# macOS draws app icons inside a margin (the tile is about 80% of the square, centred).
SVG_MAC = SVG.replace('viewBox="0 0 24 24"', 'viewBox="-3 -2.6 30 30"')
MAC_SIZES = {16: "16x16", 32: "16x16@2x 32x32", 64: "32x32@2x", 128: "128x128", 256: "128x128@2x 256x256",
             512: "256x256@2x 512x512", 1024: "512x512@2x"}

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
EXT_SIZES = [16, 32, 48, 128]


def find_chrome():
    for p in [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ]:
        if os.path.exists(p):
            return p
    sys.exit("Chrome or Edge is needed to render the icon.")


def render(chrome, size, out, svg=SVG):
    html = os.path.join(tempfile.gettempdir(), "vdm-icon.html")
    with open(html, "w", encoding="utf-8") as f:
        f.write('<html><body style="margin:0;background:transparent;overflow:hidden">'
                f'<div style="width:{size}px;height:{size}px">{svg}</div></body></html>')
    profile = tempfile.mkdtemp(prefix="vdm-chrome-")
    subprocess.run([chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                    f"--user-data-dir={profile}", "--default-background-color=00000000",
                    f"--window-size={size},{size}", "--force-device-scale-factor=1",
                    f"--screenshot={out}", "file:///" + html.replace("\\", "/")],
                   check=True, capture_output=True, timeout=60)


def decode_png(data):
    """Returns (width, height, RGBA bytes) for the 8-bit RGBA PNGs Chrome writes."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, w, h = 8, b"", 0, 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", body[:10])
            assert depth == 8 and ctype in (2, 6), (depth, ctype)
            bpp = 4 if ctype == 6 else 3
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = w * bpp
    out = bytearray()
    prev = bytearray(stride)
    i = 0
    for _ in range(h):
        ftype = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if ftype == 1:
                line[x] = (line[x] + a) & 255
            elif ftype == 2:
                line[x] = (line[x] + b) & 255
            elif ftype == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        prev = line
        if bpp == 3:
            for x in range(w):
                out += line[x * 3:x * 3 + 3] + b"\xff"
        else:
            out += line
    return w, h, bytes(out)


def bmp_frame(w, h, rgba):
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = rgba[(y * w + x) * 4:(y * w + x) * 4 + 4]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_stride = ((w + 31) // 32) * 4
    return header + b"".join(rows) + bytes(mask_stride * h)


def main():
    chrome = find_chrome()
    tmp = tempfile.mkdtemp(prefix="vdm-icons-")
    pngs = {}
    for size in sorted(set(ICO_SIZES + EXT_SIZES)):
        out = os.path.join(tmp, f"{size}.png")
        render(chrome, size, out)
        with open(out, "rb") as f:
            pngs[size] = f.read()

    frames = []
    for size in ICO_SIZES:
        if size >= 256:
            frames.append((size, pngs[size]))
        else:
            w, h, rgba = decode_png(pngs[size])
            assert (w, h) == (size, size), (w, h, size)
            frames.append((size, bmp_frame(w, h, rgba)))
    ico = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    blobs = b""
    for size, blob in frames:
        dim = 0 if size >= 256 else size
        ico += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset + len(blobs))
        blobs += blob
    ico_path = os.path.join(ROOT, "src", "VDM.App", "Assets", "vdm.ico")
    with open(ico_path, "wb") as f:
        f.write(ico + blobs)
    print("wrote", ico_path)

    icons = os.path.join(ROOT, "extension", "src", "icons")
    os.makedirs(icons, exist_ok=True)
    for size in EXT_SIZES:
        with open(os.path.join(icons, f"icon-{size}.png"), "wb") as f:
            f.write(pngs[size])
    with open(os.path.join(ROOT, "src", "VDM.App", "Assets", "vdm-256.png"), "wb") as f:
        f.write(pngs[256])
    print("wrote extension icons to", icons)

    # The iconset build/build-mac.sh turns into vdm.icns with iconutil.
    iconset = os.path.join(ROOT, "build", "mac", "vdm.iconset")
    os.makedirs(iconset, exist_ok=True)
    for size, names in MAC_SIZES.items():
        out = os.path.join(tmp, f"mac-{size}.png")
        render(chrome, size, out, SVG_MAC)
        for name in names.split():
            with open(out, "rb") as src, open(os.path.join(iconset, f"icon_{name}.png"), "wb") as dst:
                dst.write(src.read())
    print("wrote macOS iconset to", iconset)


if __name__ == "__main__":
    main()
