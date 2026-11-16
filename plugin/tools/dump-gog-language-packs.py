"""Dump original GS-delimited language TextAssets without loading the game."""
import json
from pathlib import Path
import UnityPy

root = Path(__file__).resolve().parents[3]
source = root / 'Reus 2_Data/sharedassets0.assets'
output = root / 'BepInEx/plugins/Reus2KR/raw_GOG'
if output.exists():
    raise SystemExit('Output exists; preserve it instead of overwriting: ' + str(output))
env = UnityPy.load(str(source))
records = []
payloads = {}
for obj in env.objects:
    if obj.type.name != 'TextAsset':
        continue
    data = obj.read()
    raw = data.m_Script
    if isinstance(raw, str):
        raw = raw.encode('utf-8', errors='surrogateescape')
    else:
        raw = bytes(raw)
    if b'\x1d' not in raw:
        continue
    try:
        raw.decode('utf-8-sig')
    except UnicodeDecodeError:
        continue  # Binary animation TextAssets may also contain byte 0x1D.
    name = data.m_Name
    if not name or any(c in name for c in '<>:"/\\|?*'):
        raise ValueError('Unsafe asset name: ' + repr(name))
    filename = name + '.txt'
    if filename in payloads:
        raise ValueError('Duplicate asset name: ' + name)
    payloads[filename] = raw
    records.append(dict(name=name, file=filename, pathId=obj.path_id,
                        bytes=len(raw), separators=raw.count(b'\x1d')))
required = {'en', 'zh_CN', 'translation_keys'}
assert required <= {r['name'] for r in records}, 'Required tables missing'
output.mkdir(parents=True)
for filename, raw in payloads.items():
    target = output / filename
    target.write_bytes(raw)
    assert target.read_bytes() == raw
(output / 'manifest.json').write_text(json.dumps(dict(
    edition='GOG', source=str(source.relative_to(root)),
    format='Original TextAsset bytes, UTF-8, U+001D separator; no newline conversion',
    assets=records), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(dict(output=str(output), count=len(records), assets=records),
                 ensure_ascii=False, indent=2))
