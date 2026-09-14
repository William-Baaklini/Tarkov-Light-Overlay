"""Validate the portable ZIP's runtime, every map tile, data, and checksum."""
import hashlib
import json
import pathlib
import struct
import zipfile
import zlib
import sys


def bundle_entries(exe):
    # .NET's bundle manifest layout: dotnet/runtime, Microsoft.NET.HostModel/Bundle.
    signature = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
    marker = exe.find(signature)
    assert marker >= 8, 'No .NET single-file bundle marker'
    position = struct.unpack_from('<Q', exe, marker - 8)[0]
    major, minor, count = struct.unpack_from('<III', exe, position)
    assert major == 6 and minor == 0, 'Unexpected bundle format'
    position += 12

    def read_string():
        nonlocal position
        length = shift = 0
        while True:
            byte = exe[position]
            position += 1
            length |= (byte & 127) << shift
            if byte < 128:
                break
            shift += 7
        value = exe[position:position + length].decode('utf8')
        position += length
        return value

    read_string()  # Extraction/cache identifier.
    position += 40  # Deps/config locations and extraction flags.
    entries = {}
    for _ in range(count):
        offset, size, compressed, kind = struct.unpack_from('<qqqB', exe, position)
        position += 25
        name = read_string()
        assert offset >= 0 and offset + (compressed or size) <= len(exe), name
        entries[name] = (offset, size, compressed)
    return entries

root = pathlib.Path(__file__).resolve().parent.parent
release_dir = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else root / 'release'
archive = release_dir / 'TLO-win-x64.zip'
prefix = 'TLO-win-x64/'
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None, 'ZIP CRC failure'
    names = set(package.namelist())
    for name in ('TLO.exe', 'data/icons.idx',
                 'tiles/maps.json', 'README.md', 'THIRD-PARTY-NOTICES.md'):
        assert prefix + name in names, f'Missing runtime asset: {name}'
    for name in ('dotnet-LICENSE.txt', 'dotnet-THIRD-PARTY-NOTICES.txt',
                 'windowsdesktop-LICENSE.txt', 'webview2-LICENSE.txt', 'webview2-NOTICE.txt'):
        assert prefix + 'licenses/' + name in names, f'Missing dependency license: {name}'
    exe = package.read(prefix + 'TLO.exe')
    pe = struct.unpack_from('<I', exe, 0x3c)[0]
    assert exe[:2] == b'MZ' and exe[pe:pe+4] == b'PE\0\0'
    assert struct.unpack_from('<H', exe, pe + 4)[0] == 0x8664, 'Expected Windows x64 executable'
    root_files = {n.removeprefix(prefix) for n in names if '/' not in n.removeprefix(prefix)}
    assert root_files == {'TLO.exe', 'README.md', 'THIRD-PARTY-NOTICES.md'}, f'Unexpected root files: {root_files}'
    entries = bundle_entries(exe)
    # CoreCLR itself is linked into the Windows single-file host.
    for name in ('TLO.dll', 'TLO.runtimeconfig.json', 'System.Private.CoreLib.dll', 'System.Windows.Forms.dll',
                 'WebView2Loader.dll', 'Microsoft.Web.WebView2.WinForms.dll'):
        assert name in entries, f'Missing bundled dependency: {name}'
    offset, size, compressed = entries['TLO.runtimeconfig.json']
    config = exe[offset:offset + (compressed or size)]
    if compressed:
        config = zlib.decompress(config, -15)
    runtime = json.loads(config)['runtimeOptions']
    assert 'includedFrameworks' in runtime and 'frameworks' not in runtime, 'Runtime is not self-contained'
    icon = (root / 'src/TarkovOverlay/app.ico').read_bytes()
    for i in range(struct.unpack_from('<H', icon, 4)[0]):
        image_size, image_offset = struct.unpack_from('<II', icon, 6 + 16 * i + 8)
        assert icon[image_offset:image_offset + image_size] in exe, 'Executable icon frame missing'
    catalog = json.loads(package.read(prefix + 'tiles/maps.json'))
    tile_count = 0
    for map_info in catalog['maps']:
        for level, info in map_info['levels'].items():
            for x in range(info['cols']):
                for y in range(info['rows']):
                    name = f"{prefix}tiles/{map_info['id']}/{level}/{x}_{y}.png"
                    assert name in names, f'Missing map tile: {name}'
                    tile_count += 1
    assert package.read(prefix + 'data/icons.idx') == (root / 'data/icons.idx').read_bytes()
    assert not any('/obj/' in n or '/.git/' in n or n.endswith('config.json') and n != prefix + 'TLO.runtimeconfig.json' for n in names)
expected = (archive.with_suffix('.sha256')).read_text().split()[0]
with archive.open('rb') as stream:
    assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected, 'Checksum mismatch'
print(f'PASS: single Windows x64 EXE, {len(entries)} bundled dependencies, all icon resolutions, {len(catalog["maps"])} maps / {tile_count} tiles, scanner data, licenses, ZIP CRCs and SHA-256.')
