# 把逐候选参考输出规范化：@property 块折叠为一行名字列表（内容由 full.css 校验）
import sys,re
src=open(sys.argv[1]).read().split('\n### ')
out=[]
for i,blk in enumerate(src):
    if i==0: blk=blk[4:] if blk.startswith('### ') else blk
    name,_,body=blk.partition('\n')
    lines=body.rstrip('\n').split('\n')
    rules=[];props=[];j=0
    while j<len(lines):
        l=lines[j]
        if l.startswith('@property '):
            props.append(l[len('@property '):-2].strip())
            j+=1
            while j<len(lines) and lines[j]!='}': j+=1
            j+=1; continue
        rules.append(l); j+=1
    out.append('### '+name)
    out.extend(rules)
    if props: out.append('@property '+' '.join(props))
open(sys.argv[2],'w').write('\n'.join(out)+'\n')
