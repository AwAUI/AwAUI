# 全量类名语料的紧凑快照：候选列表（gzip）+ 每 64 个候选一组的 FNV-1a 64 哈希（规范化输出）
import sys,gzip
src=open(sys.argv[1]).read().split('\n### ')
entries=[]
for i,blk in enumerate(src):
    if i==0 and blk.startswith('### '): blk=blk[4:]
    name,_,body=blk.partition('\n')
    entries.append((name, body.rstrip('\n')))
def fnv(data):
    h=0xcbf29ce484222325
    for b in data:
        h^=b; h=(h*0x100000001b3)&0xffffffffffffffff
    return h
out=[]
for c in range(0,len(entries),64):
    chunk=entries[c:c+64]
    s=''.join(n+'\n'+b+'\n' for n,b in chunk)
    out.append('%d %016x' % (c//64, fnv(s.encode('utf-8'))))
open(sys.argv[2]+'/digest.txt','w').write('\n'.join(out)+'\n')
with gzip.GzipFile(sys.argv[2]+'/candidates.txt.gz','wb',mtime=0) as f:
    f.write(('\n'.join(n for n,_ in entries)+'\n').encode('utf-8'))
print(len(entries), len(out))
