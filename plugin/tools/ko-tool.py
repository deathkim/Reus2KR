#!/usr/bin/env python3
"""ko-tool.py - JSON Lines working copy of ko.csv (export / import / diff / sync ...).

ko.csv (GS-delimited, multiline values) stays the game's file. This tool keeps a
readable working copy next to ko_current.csv and applies edits back safely.

Row format (one JSON object per line, same order as ko.csv):
  {"id": 1234, "key": "...", "en": "...", "ko": "..."}
  - "id" = 1-based row number in ko.csv order (stable: keys cannot be added/removed).
    Use it to point at a row (show --id, set, diff).  Do not edit or reorder it.
  - "en" is omitted when it is identical to the key (English-literal keys).
  - rows whose English value is empty carry "zh" (Chinese) instead of "en".
  - rows with neither keep only key/ko.  Newlines appear as \\n inside strings.
  - ONLY "ko" may be edited.  id/key/en/zh are reference and checked on import.

Files (plugin/translation/):
  ko-work.jsonl        working copy (edit this)
  ko-work.base.jsonl   snapshot of the last export/import (managed by the tool)

Commands:
  export [--full] [--force]   write ko-work.jsonl + base from ko.csv
  status                      pending edits / ko.csv drift / conflicts (counts only)
  diff [--limit N]            list pending edits compactly (cheap for Claude)
  show [--id 5,9,100-120] [--key S] [--ko S] [--en S] [--limit N]
                              print matching rows only (never the whole file)
  set                         read {"<id>": "new ko", ...} from stdin and update the
                              working copy (token-cheap way for Claude to edit)
  import [--force] [--dry-run]  apply pending ko edits to ko.csv (+ ko_current.csv)
  sync                        3-way refresh: pick up ko.csv changes made elsewhere
                              (e.g. by Claude) while keeping your pending edits
  verify                      round-trip check: rebuilt ko.csv == current bytes
"""
import argparse, base64, gzip, hashlib, json, re, shutil, sys
from collections import Counter
from pathlib import Path

GS = '\x1d'
HERE = Path(__file__).resolve().parent
WORK = HERE.parent                      # .Works/plugin
ROOT = WORK.parents[1]                  # game root
CSV = ROOT / 'BepInEx/plugins/Reus2KR/ko.csv'
SYNC = WORK / 'translation/ko_current.csv'
RAW = ROOT / 'BepInEx/plugins/Reus2KR/raw_GOG'
JSONL = WORK / 'translation/ko-work.jsonl'
BASE = WORK / 'translation/ko-work.base.jsonl'


# ---------------------------------------------------------------- parsing
def _multi_keys():
    cs = (WORK / 'src/Plugin.cs').read_text(encoding='utf-8-sig')
    part = cs.split('private const string MultiKeyData =', 1)[1].split(';', 1)[0]
    data = gzip.decompress(base64.b64decode(''.join(re.findall(r'"([^"]*)"', part)))).decode()
    return sorted(filter(None, data.split(GS)), key=len, reverse=True)


MULTI = None


def parse_text(text, source=False):
    """text must use '\\n' only. Returns ordered {key: value}."""
    global MULTI
    if MULTI is None:
        MULTI = _multi_keys()
    if text.endswith('\n'):
        text = text[:-1]
    bounds = []
    for m in re.finditer(GS, text):
        d = m.start()
        start = text.rfind('\n', 0, d) + 1
        for key in MULTI:
            a = d - len(key)
            if a >= 0 and (a == 0 or text[a - 1] == '\n') and text[a:d] == key:
                start = a
                break
        if source:
            floor = bounds[-1][1] + 1 if bounds else 0
            starts = [0] if floor == 0 else []
            starts += [floor + mm.end() for mm in re.finditer('\n', text[floor:d])]
            for a in starts:
                if a < start and text.startswith(text[a:d], d + 1):
                    end = d + 1 + d - a
                    if end == len(text) or text[end] == '\n':
                        start = a
                        break
        bounds.append((start, d))
    result = {}
    for i, (a, d) in enumerate(bounds):
        key = text[a:d]
        value = text[d + 1:bounds[i + 1][0] - 1 if i + 1 < len(bounds) else len(text)]
        assert key not in result, ('Duplicate key', key)
        result[key] = value
    return result


class Csv:
    """Lossless view of a ko.csv-style file (BOM, newline style, trailing newline)."""

    def __init__(self, path):
        raw = path.read_bytes()
        self.bom = raw.startswith(b'\xef\xbb\xbf')
        text = raw[3:].decode('utf-8') if self.bom else raw.decode('utf-8')
        self.nl = '\r\n' if '\r\n' in text else '\n'
        norm = text.replace('\r\n', '\n').replace('\r', '\n')
        self.trailing = norm.endswith('\n')
        self.raw = raw
        self.records = parse_text(norm)

    def dump(self, records=None):
        records = self.records if records is None else records
        body = '\n'.join(k + GS + v for k, v in records.items())
        if self.trailing:
            body += '\n'
        return (b'\xef\xbb\xbf' if self.bom else b'') + body.replace('\n', self.nl).encode('utf-8')


def load_source(name, source):
    p = RAW / name
    if not p.exists():
        return {}
    text = p.read_text(encoding='utf-8-sig').replace('\r\n', '\n').replace('\r', '\n')
    return parse_text(text, source)


def reference():
    en = {}
    en.update(load_source('translation_keys.txt', True))
    en.update(load_source('en.txt', False))
    zh = load_source('zh_CN.txt', True)
    return en, zh


# ---------------------------------------------------------------- jsonl io
def row_for(rid, key, ko, en, zh, full):
    row = {'id': rid, 'key': key}
    e = en.get(key)
    if e:
        if full or e != key:
            row['en'] = e
    else:
        z = zh.get(key)
        if z:
            row['zh'] = z
    row['ko'] = ko
    return row


def write_rows(path, rows):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')


def read_rows(path):
    rows = []
    for n, line in enumerate(path.read_text(encoding='utf-8').split('\n'), 1):
        if not line.strip():
            continue
        try:
            rows.append(json.loads(line))
        except Exception as e:
            sys.exit('JSON error at %s line %d: %s' % (path.name, n, e))
    return rows


def ko_map(rows):
    out = {}
    for r in rows:
        if 'key' not in r or 'ko' not in r:
            sys.exit('row without key/ko: ' + json.dumps(r, ensure_ascii=False)[:120])
        if r['key'] in out:
            sys.exit('duplicate key in jsonl: ' + repr(r['key'][:80]))
        out[r['key']] = r['ko']
    return out


def id_map(rows):
    return {r['key']: r.get('id') for r in rows}


def short(s, n=70):
    s = s.replace('\n', '\\n')
    return s if len(s) <= n else s[:n] + '...'


def tokens(s):
    return (Counter(re.findall(r'\{\d+[^{}]*\}', s)),
            Counter(re.findall(r'\[[A-Z_]+(?:\|[^\]]*)?\]|<[^>]+>', s)))


# ---------------------------------------------------------------- commands
def cmd_export(a):
    if JSONL.exists() and BASE.exists() and not a.force:
        pend = pending()
        if pend:
            sys.exit('%d pending edit(s) in %s. Run import (or sync) first, or use --force to overwrite.'
                     % (len(pend), JSONL.name))
    csv = Csv(CSV)
    en, zh = reference()
    rows = [row_for(i, k, v, en, zh, a.full) for i, (k, v) in enumerate(csv.records.items(), 1)]
    write_rows(JSONL, rows)
    write_rows(BASE, rows)
    print('exported %d rows -> %s (%.0f KB)' % (len(rows), JSONL.name, JSONL.stat().st_size / 1024))


def need_files():
    if not JSONL.exists() or not BASE.exists():
        sys.exit('No working copy. Run: python ko-tool.py export')


def pending():
    need_files()
    wr, br = read_rows(JSONL), read_rows(BASE)
    work, base = ko_map(wr), ko_map(br)
    if set(work) != set(base):
        sys.exit('Key set of %s differs from the base. Keys cannot be added/removed/renamed.' % JSONL.name)
    wi, bi = id_map(wr), id_map(br)
    bad = [k for k in base if wi[k] != bi[k]]
    if bad:
        sys.exit('id mismatch for %d row(s) (e.g. id %s vs %s): ids/keys must not be edited or reordered.'
                 % (len(bad), wi[bad[0]], bi[bad[0]]))
    return [k for k in base if work[k] != base[k]]


def drift():
    base = ko_map(read_rows(BASE))
    cur = Csv(CSV).records
    if set(cur) != set(base):
        return None
    return [k for k in base if cur[k] != base[k]]


def cmd_status(a):
    pend = pending()
    d = drift()
    print('rows: %d' % len(ko_map(read_rows(BASE))))
    print('pending edits in %s: %d' % (JSONL.name, len(pend)))
    if d is None:
        print('ko.csv key set differs from base -> run: export --force')
        return
    print('ko.csv changed since base (e.g. by Claude): %d' % len(d))
    both = [k for k in pend if k in set(d)]
    print('conflicts (edited in both): %d' % len(both))


def cmd_diff(a):
    pend = pending()
    base = ko_map(read_rows(BASE))
    work = ko_map(read_rows(JSONL))
    ids = id_map(read_rows(BASE))
    print('%d pending edit(s)' % len(pend))
    for k in pend[:a.limit]:
        print('ID', ids[k], 'KEY', json.dumps(k, ensure_ascii=False)[:90])
        print('  - ' + short(base[k], 160))
        print('  + ' + short(work[k], 160))
    if len(pend) > a.limit:
        print('... %d more (use --limit)' % (len(pend) - a.limit))


def cmd_show(a):
    need_files()
    rows = read_rows(JSONL)
    want = None
    if a.id:
        want = set()
        for part in a.id.split(','):
            m = re.match(r'^(\d+)(?:-(\d+))?$', part.strip())
            if not m:
                sys.exit('--id N, N-M or a comma list (1-based)')
            lo, hi = int(m.group(1)), int(m.group(2) or m.group(1))
            want.update(range(lo, hi + 1))
    n = 0
    for r in rows:
        if want is not None and r['id'] not in want:
            continue
        if a.key and a.key not in r['key']:
            continue
        if a.ko and a.ko not in r['ko']:
            continue
        if a.en and a.en not in (r.get('en') or r.get('zh') or r['key']):
            continue
        print(json.dumps(r, ensure_ascii=False))
        n += 1
        if n >= a.limit:
            print('... limit %d reached' % a.limit)
            break
    if n == 0:
        print('(no match)')


def cmd_set(a):
    need_files()
    try:
        patch = json.loads(sys.stdin.read())
    except Exception as e:
        sys.exit('stdin must be JSON {"<id>": "new ko", ...}: %s' % e)
    rows = read_rows(JSONL)
    by_id = {r['id']: r for r in rows}
    changed = 0
    for sid, val in patch.items():
        rid = int(sid)
        if rid not in by_id:
            sys.exit('unknown id %s' % sid)
        if not isinstance(val, str):
            sys.exit('value for id %s must be a string' % sid)
        if by_id[rid]['ko'] != val:
            by_id[rid]['ko'] = val
            changed += 1
    write_rows(JSONL, rows)
    print('set %d row(s) in %s (not applied to ko.csv until import)' % (changed, JSONL.name))


def cmd_import(a):
    pend = pending()
    if not pend:
        print('nothing to import')
        return
    base = ko_map(read_rows(BASE))
    work = ko_map(read_rows(JSONL))
    csv = Csv(CSV)
    if set(csv.records) != set(base):
        sys.exit('ko.csv key set differs from base -> run: export --force')
    conflicts = [k for k in pend if csv.records[k] != base[k] and csv.records[k] != work[k]]
    if conflicts and not a.force:
        print('%d conflict(s): ko.csv changed since export for the same key(s). Run sync, or --force:' % len(conflicts))
        for k in conflicts[:10]:
            print('  ', json.dumps(k, ensure_ascii=False)[:80])
        sys.exit(1)
    new = dict(csv.records)
    warn = 0
    for k in pend:
        old_tok, new_tok = tokens(csv.records[k]), tokens(work[k])
        if old_tok != new_tok:
            warn += 1
            print('WARN placeholder/macro/tag change:', json.dumps(k, ensure_ascii=False)[:60])
        new[k] = work[k]
    if a.dry_run:
        print('dry-run: %d edit(s), %d warning(s); nothing written' % (len(pend), warn))
        return
    data = csv.dump(new)
    verify = Csv_from_bytes(data)
    if verify != new:
        sys.exit('internal check failed: rebuilt file does not parse back to the same records')
    CSV.write_bytes(data)
    shutil.copyfile(CSV, SYNC)
    csv2 = Csv(CSV)
    rows = rebuild_rows(csv2.records)
    write_rows(JSONL, rows)
    write_rows(BASE, rows)
    print('imported %d edit(s), %d warning(s). ko.csv and ko_current.csv updated.' % (len(pend), warn))
    print('Next: run 검증.ps1; restart the game to apply.')


def Csv_from_bytes(data):
    text = data[3:].decode('utf-8') if data.startswith(b'\xef\xbb\xbf') else data.decode('utf-8')
    return parse_text(text.replace('\r\n', '\n').replace('\r', '\n'))


def rebuild_rows(records):
    en, zh = reference()
    full = False
    return [row_for(i, k, v, en, zh, full) for i, (k, v) in enumerate(records.items(), 1)]


def cmd_sync(a):
    need_files()
    base = ko_map(read_rows(BASE))
    work = ko_map(read_rows(JSONL))
    cur = Csv(CSV).records
    if set(cur) != set(base) or set(work) != set(base):
        sys.exit('key sets differ -> run: export --force')
    merged, conflicts, taken = {}, [], 0
    for k in base:
        if work[k] == base[k]:
            merged[k] = cur[k]
            taken += cur[k] != base[k]
        elif cur[k] == base[k] or cur[k] == work[k]:
            merged[k] = work[k]
        else:
            merged[k] = work[k]
            conflicts.append(k)
    en, zh = reference()
    write_rows(JSONL, [row_for(i, k, merged[k], en, zh, False) for i, k in enumerate(base, 1)])
    write_rows(BASE, [row_for(i, k, cur[k], en, zh, False) for i, k in enumerate(base, 1)])
    print('sync: took %d change(s) from ko.csv; kept your %d pending edit(s); %d conflict(s)'
          % (taken, len(pending()), len(conflicts)))
    for k in conflicts[:10]:
        print('  conflict (your edit kept; ko.csv value differs):', json.dumps(k, ensure_ascii=False)[:70])


def cmd_verify(a):
    csv = Csv(CSV)
    ok = csv.dump() == csv.raw
    print('round-trip ko.csv: %s (%d records, bom=%s, nl=%r)' % ('OK' if ok else 'MISMATCH', len(csv.records), csv.bom, csv.nl))
    if SYNC.exists():
        print('ko_current.csv identical to ko.csv:', SYNC.read_bytes() == csv.raw)
    sys.exit(0 if ok else 1)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('export'); p.add_argument('--full', action='store_true'); p.add_argument('--force', action='store_true'); p.set_defaults(f=cmd_export)
    sub.add_parser('status').set_defaults(f=cmd_status)
    p = sub.add_parser('diff'); p.add_argument('--limit', type=int, default=40); p.set_defaults(f=cmd_diff)
    p = sub.add_parser('show')
    p.add_argument('--id'); p.add_argument('--key'); p.add_argument('--ko'); p.add_argument('--en')
    p.add_argument('--limit', type=int, default=30); p.set_defaults(f=cmd_show)
    p = sub.add_parser('import'); p.add_argument('--force', action='store_true'); p.add_argument('--dry-run', action='store_true'); p.set_defaults(f=cmd_import)
    sub.add_parser('sync').set_defaults(f=cmd_sync)
    sub.add_parser('set').set_defaults(f=cmd_set)
    sub.add_parser('verify').set_defaults(f=cmd_verify)
    a = ap.parse_args()
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding='utf-8')
        except Exception:
            pass
    a.f(a)


if __name__ == '__main__':
    main()
