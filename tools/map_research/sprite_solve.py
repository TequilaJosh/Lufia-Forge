import os, json
from lz import decompress,res_ptr,ROM
CAP="C:/Users/Admin/AppData/Local/Temp/claude/D--Lufia-Forge/de85b578-325b-4922-8bd7-d0ea2372d399/scratchpad/trace/capture_all/"
def w16(b,o): return b[o]|b[o+1]<<8
BASE=0x38000; BLK=0x180
# index ROM sprite tiles: tile bytes -> rom offset (only bank 07 area)
romtiles={}
for o in range(BASE, 0x40000, 32):
    t=ROM[o:o+32]
    if t.count(0)<24: romtiles.setdefault(t,o)
maps=json.load(open('map_catalog.json'))
data={}
for e in maps:
    m=e['map']; fn=CAP+'map%02X_vram.bin'%m
    if not os.path.exists(fn): continue
    V=open(fn,'rb').read()
    blocks=set()
    for o in range(0x8000,0x10000,32):
        r=romtiles.get(V[o:o+32])
        if r is not None: blocks.add((r-BASE)//BLK)
    md,_=decompress(res_ptr(0xB2+ROM[0xE19B+m])); ob=int.from_bytes(md[0x30:0x34],'little'); t=md[ob:]
    c=t[w16(t,12):]
    sprites=set(c[i*14] for i in range(t[2]) if c[i*14]!=0xFF)
    data[m]=(blocks,sprites)
always=set.intersection(*[b for b,s in data.values() if b]) if data else set()
print('blocks in every map (party):',sorted(always))
mapping={}
changed=True
while changed:
    changed=False
    for m,(blocks,sprites) in data.items():
        rest_b=set(blocks)-always-set(mapping.values())
        rest_s=set(sprites)-set(mapping)
        # each sprite needs at least one block; if one unknown sprite and its blocks
        if len(rest_s)==1 and len(rest_b)>=1:
            s=next(iter(rest_s))
            # a sprite set may span consecutive blocks; take the smallest block
            mapping[s]=min(rest_b); changed=True
print('solved', len(mapping), {hex(k):v for k,v in sorted(mapping.items())})
allsprites=set().union(*[s for b,s in data.values()])
print('all sprite ids', len(allsprites), sorted(hex(x) for x in allsprites))
for m,(b,s) in list(data.items())[:12]: print(hex(m), sorted(b), sorted(hex(x) for x in s))

print('--- identity check')
for m,(blocks,sprites) in sorted(data.items()):
    sets=set(b//3 for b in blocks)-{0,1}
    unexplained_sprites=sorted(s for s in sprites if s not in sets)
    extra_sets=sorted(x for x in sets if x not in sprites)
    if unexplained_sprites or extra_sets:
        print(hex(m),'sprites not = set:',[hex(x) for x in unexplained_sprites],' sets without sprite:',[hex(x) for x in extra_sets])

conf=collections.Counter() if False else {}
import collections
confirmed=collections.Counter(); contra=collections.Counter()
for m,(blocks,sprites) in data.items():
    sets=set(b//3 for b in blocks)
    for s in sprites:
        if s in sets: confirmed[s]+=1
print('confirmed identity ids:', sorted(hex(k) for k in confirmed))
