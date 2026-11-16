"""Apply reviewed translation changes to ko.csv and sync ko_current.csv.
usage: python apply-changes.py <scope.json> <changes.json> <label>
scope: list of {id,key,ko}; changes: {"<id>": "new value (\n = LF)"}.
Re-checks current value equals the scope snapshot (never overwrites manual edits), keeps newline/BOM style."""
import sys,re,json,shutil,importlib
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent))
m=importlib.import_module('gog-update-common')
R=m.ROOT; ko_path=R/'BepInEx/plugins/Reus2KR/ko.csv'; sync=R/'.Works/plugin/translation/ko_current.csv'
G=R/'.Works/20_검수/GOG_재검수'
scope=json.load(open(sys.argv[1],encoding='utf-8')); changes=json.load(open(sys.argv[2],encoding='utf-8')); label=sys.argv[3]
byid={x['id']:x for x in scope}; ko=m.parse(ko_path)
new={int(i):v for i,v in changes.items()}
for i,v in list(new.items()):
    cur=ko[byid[i]['key']]
    if cur!=byid[i]['ko']: print('SKIP (edited since scope)',i); del new[i]; continue
    if v==cur: del new[i]
raw=ko_path.read_bytes(); bom=raw.startswith(b'\xef\xbb\xbf'); text=raw.decode('utf-8-sig')
nl='\r\n' if '\r\n' in text else '\n'; f=lambda s:s.replace('\n',nl)
for i,v in new.items():
    x=byid[i]; old=f(x['key'])+'\x1d'+f(x['ko']); neu=f(x['key'])+'\x1d'+f(v)
    pat=re.compile('(?:(?<=\n)|^)'+re.escape(old)+'(?=\n|\r|$)')
    ms=list(pat.finditer(text))
    if len(ms)>1:  # key may also be the tail of a longer key that starts with a newline
        ms=[x for x in ms if text[max(0,x.start()-2*len(nl)):x.start()]!=nl+nl]
    assert len(ms)==1,('match',i,len(ms))
    x=ms[0]; text=text[:x.start()]+neu+text[x.end():]
ko_path.write_bytes((('\ufeff' if bom else '')+text).encode('utf-8'))
shutil.copyfile(ko_path,sync)
rep=[dict(id=i,key=byid[i]['key'],before=byid[i]['ko'],after=v) for i,v in sorted(new.items())]
json.dump(rep,open(G/'reports'/f'changes-{label}.json','w',encoding='utf-8'),ensure_ascii=False,indent=1)
from collections import Counter
g=lambda s:(Counter(re.findall(r'\{\d+\}',s)),Counter(re.findall(r'\[[A-Z_]+(?:\|[^\]]*)?\]|<color[^>]*>',s)))
bad=[r['id'] for r in rep if g(r['before'])!=g(r['after'])]
print(len(rep),'changed; var/macro mismatch:',bad)
