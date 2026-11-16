import re, gzip, base64
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
WORK = ROOT / '.Works' / 'plugin'
cs = (WORK/'src/Plugin.cs').read_text(encoding='utf-8-sig')
part = cs.split('private const string MultiKeyData =',1)[1].split(';',1)[0]
MULTI = sorted(filter(None,gzip.decompress(base64.b64decode(''.join(re.findall(r'"([^"]*)"',part)))).decode().split('\x1d')),key=len,reverse=True)
def parse(path, source=False):
    text = path.read_text(encoding='utf-8-sig').replace('\r\n','\n').replace('\r','\n')
    if text.endswith('\n'): text=text[:-1]
    bounds=[]
    for m in re.finditer('\x1d',text):
        d=m.start(); start=text.rfind('\n',0,d)+1
        for key in MULTI:
            a=d-len(key)
            if a>=0 and (a==0 or text[a-1]=='\n') and text[a:d]==key:
                start=a; break
        if source:
            # Supplemental English tables also have multiline literal keys
            # absent from the shipped locale's historic key list.
            floor=bounds[-1][1]+1 if bounds else 0
            starts=[0] if floor==0 else []
            starts += [floor+m.end() for m in re.finditer('\n',text[floor:d])]
            for a in starts:
                if a<start and text.startswith(text[a:d],d+1):
                    end=d+1+d-a
                    if end==len(text) or text[end]=='\n': start=a;break
        bounds.append((start,d))
    result={}
    for i,(a,d) in enumerate(bounds):
        key=text[a:d]; value=text[d+1:bounds[i+1][0]-1 if i+1<len(bounds) else len(text)]
        assert key not in result, ('Duplicate key', key)
        result[key]=value
    return result
def sources(base,ext):
    result={}
    for name in ['translation_keys','en']:result.update(parse(base/(name+'.'+ext),name=='translation_keys'))
    return result
