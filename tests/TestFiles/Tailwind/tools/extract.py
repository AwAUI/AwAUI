import json,re,sys,glob,os
toks=[]
seen=set()
for f in sorted(glob.glob(sys.argv[1]+'/*.json')):
    d=json.load(open(f))
    if 'files' not in d: continue
    for fi in d['files']:
        c=fi.get('content','')
        for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"|\'((?:[^\'\\\n]|\\.)*)\'|`([^`]*)`',c):
            s=m.group(1) or m.group(2) or m.group(3) or ''
            for t in s.split():
                if t not in seen:
                    seen.add(t); toks.append(t)
open(sys.argv[2],'w').write('\n'.join(toks)+'\n')
print(len(toks))
