"""Preserve the emblem, using BMP icon frames below 256px for Windows shells."""
import io
import pathlib
import struct
from PIL import Image

root = pathlib.Path(__file__).resolve().parent.parent
path = root / 'src/TarkovOverlay/app.ico'
source = Image.open(path)
frames = []
for size in (16, 20, 24, 32, 40, 48, 64, 128, 256):
    frame = source.ico.getimage((size, size)).convert('RGBA')
    if size == 256:
        stream = io.BytesIO()
        frame.save(stream, format='PNG')
        payload = stream.getvalue()
    else:
        pixels = frame.tobytes('raw', 'BGRA', 0, -1)
        stride = ((size + 31) // 32) * 4
        mask = bytearray(stride * size)
        for y in range(size):
            for x in range(size):
                if frame.getpixel((x, size - 1 - y))[3] == 0:
                    mask[y * stride + x // 8] |= 1 << (7 - x % 8)
        payload = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0,
                              len(pixels) + len(mask), 0, 0, 0, 0) + pixels + mask
    frames.append((size, payload))
offset = 6 + len(frames) * 16
entries = []
for size, payload in frames:
    entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
    offset += len(payload)
source.close()
path.write_bytes(struct.pack('<HHH', 0, 1, len(frames)) + b''.join(entries) + b''.join(p for _, p in frames))
(root / 'docs/assets/app.ico').write_bytes(path.read_bytes())
print('Icon normalized: 9 resolutions, original artwork retained.')
