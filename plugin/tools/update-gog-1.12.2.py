import importlib, json, re, collections, datetime, shutil, gzip, base64, sys
from pathlib import Path
m=importlib.import_module('gog-update-common')
ROOT_W=m.ROOT/'.Works'
root=m.ROOT; w=m.WORK; reports=ROOT_W/'20_검수/GOG_재검수/reports'
delta=json.loads((reports/'gog-1.12.2-delta.json').read_text(encoding='utf-8'))
decisions={}
for line in (reports/'gog-1.12.2-translations.txt').read_text(encoding='utf-8').splitlines():
    i,t=line.split('|',1); decisions[int(i)]=t.replace('\\n','\n')
assert set(decisions)==set(range(len(delta['changes'])))
variables=lambda s:collections.Counter(re.findall(r'(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})',s))
errors=[]
for i,c in enumerate(delta['changes']):
    if variables(c['english'])!=variables(decisions[i]):
        errors.append((i,dict(variables(c['english'])),dict(variables(decisions[i]))))
print('Variable mismatches:',errors)
if errors:sys.exit(1)
csv=root/'BepInEx/plugins/Reus2KR/ko.csv'; sync=w/'translation/ko_current.csv'
before=m.parse(csv); synced=m.parse(sync)
# Capture the current state, including manual edits, rather than an old review snapshot.
assert len(before)==delta['koCount']
for c in delta['changes']:assert before.get(c['key'])==c['korean'], 'Current file changed: '+c['key']
after=dict(before); edits=[]
for i,c in enumerate(delta['changes']):
    # An English typo was fixed, but the existing Korean already conveys it.
    value=before[c['key']] if i==2 else decisions[i]
    if before.get(c['key'])!=value:
        edits.append(dict(key=c['key'],english=c['english'],before=before.get(c['key']),after=value,
                          category='new_version_key' if c['before'] is None else 'changed_source' if c['before']!=c['english'] else 'previously_missing'))
    after[c['key']]=value
new=m.sources(root/'BepInEx/plugins/Reus2KR/raw_GOG','txt')
assert set(new)<=set(after) and set(before)<=set(after)
print('Will modify',sum(c['before'] is not None for c in edits),'and add',sum(c['before'] is None for c in edits),'total',len(after))
if '--apply' not in sys.argv:sys.exit(0)
stamp=datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
backup=ROOT_W/'30_백업/작업별_백업'/(stamp+'_gog-1.12.2');backup.mkdir(parents=True)
for src,name in [(csv,'ko.csv'),(sync,'ko_current.csv'),(w/'src/Plugin.cs','Plugin.cs'),(root/'BepInEx/plugins/Reus2KR/Reus2KR.dll','Reus2KR.dll')]:shutil.copy2(src,backup/name)
original=csv.read_bytes(); payload=original
def encoded(s):return s.replace('\n','\r\n').encode('utf-8')
for c in edits:
    if c['before'] is not None:
        a=encoded(c['key']+'\x1d'+c['before']);b=encoded(c['key']+'\x1d'+c['after'])
        assert payload.count(a)==1,c['key'];payload=payload.replace(a,b)
if not payload.endswith(b'\r\n'):payload+=b'\r\n'
for c in edits:
    if c['before'] is None:payload+=encoded(c['key']+'\x1d'+c['after']+'\n')
assert csv.read_bytes()==original,'Concurrent edit detected'
csv.write_bytes(payload)
m.MULTI=sorted(set(m.MULTI)|{k for k in after if '\n' in k},key=len,reverse=True)
actual=m.parse(csv)
assert actual==after
# Merge only this patch into handoff; retain its unrelated independent edits.
merged=dict(synced)
conflicts=[]
for c in edits:
    k=c['key']
    if k in merged and merged[k]!=before.get(k) and merged[k]!=c['after']:
        conflicts.append(dict(key=k,retained=merged[k],installed=c['after']))
    else:merged[k]=c['after']
if synced==before:sync.write_bytes(payload)
else:
    sync.write_bytes(encoded('\n'.join(k+'\x1d'+v for k,v in merged.items())+'\n'))
source=w/'src/Plugin.cs';text=source.read_text(encoding='utf-8-sig')
multis=sorted(set(m.MULTI)|{k for k in after if '\n' in k})
compressed=base64.b64encode(gzip.compress('\x1d'.join(multis).encode('utf-8'),mtime=0)).decode()
lines=['            "'+compressed[i:i+116]+'"' for i in range(0,len(compressed),116)]
replacement='private const string MultiKeyData =\n'+' +\n'.join(lines)+';'
text=re.sub(r'private const string MultiKeyData =[\s\S]*?;',lambda _:replacement,text,count=1)
source.write_bytes(encoded(text))
report=dict(basis='GOG v1.12.2; English takes precedence over translation_keys',backup=str(backup.relative_to(root)),
            beforeCount=len(before),afterCount=len(after),latestSourceCount=len(new),
            added=sum(c['before'] is None for c in edits),modified=sum(c['before'] is not None for c in edits),
            preservedOldKeys=[k for k in before if k not in new],removedSinceSteam=delta['removed'],
            multilineKeyCount=len(multis),changes=edits,handoffConflicts=conflicts,gameVerification='not_tested')
(reports/'gog-1.12.2-update.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Applied, backup:',backup,'handoff conflicts:',len(conflicts))
