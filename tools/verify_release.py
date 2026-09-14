"""Validate the portable ZIP's runtime, every map tile, data, and checksum."""
import hashlib
import json
import pathlib
import struct
import zipfile

root = pathlib.Path(__file__).resolve().parent.parent
archive = root / 'release' / 'TLO-win-x64.zip'
prefix = 'TLO-win-x64/'
with zipfile.ZipFile(archive) as package:
    assert package.testzip() is None, 'ZIP CRC failure'
    names = set(package.namelist())
    for name in ('TLO.exe', 'TLO.dll', 'TLO.runtimeconfig.json', 'coreclr.dll',
                 'System.Windows.Forms.dll', 'WebView2Loader.dll',
                 'Microsoft.Web.WebView2.WinForms.dll', 'data/icons.idx',
                 'tiles/maps.json', 'README.md', 'THIRD-PARTY-NOTICES.md'):
        assert prefix + name in names, f'Missing runtime asset: {name}'
    for name in ('dotnet-LICENSE.txt', 'dotnet-THIRD-PARTY-NOTICES.txt',
                 'windowsdesktop-LICENSE.txt', 'webview2-LICENSE.txt', 'webview2-NOTICE.txt'):
        assert prefix + 'licenses/' + name in names, f'Missing dependency license: {name}'
    exe = package.read(prefix + 'TLO.exe')
    pe = struct.unpack_from('<I', exe, 0x3c)[0]
    assert exe[:2] == b'MZ' and exe[pe:pe+4] == b'PE\0\0'
    assert struct.unpack_from('<H', exe, pe + 4)[0] == 0x8664, 'Expected Windows x64 executable'
    runtime = json.loads(package.read(prefix + 'TLO.runtimeconfig.json'))['runtimeOptions']
    assert 'includedFrameworks' in runtime and 'frameworks' not in runtime, 'Runtime is not self-contained'
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
print(f'PASS: Windows x64 executable, bundled runtime, {len(catalog["maps"])} maps / {tile_count} tiles, scanner data, ZIP CRCs and SHA-256.')
