"""Parse each map's setup script (pointer at file 0x18000 + map*2, interpreter $01:98AF) and list
the treasure assignments: opcode C1+n sets object-section-E record n = (item, kind, flag)."""
import json
from lz import ROM
LEN = {0:1,1:2,2:2,3:2,4:3,5:2,6:2,7:2,8:2,9:2,0xA:2,0xB:3,0xC:2,0xD:2}
def item_name(i):
    o=0x55800+(ROM[0x55800+i*2]|ROM[0x55801+i*2]<<8); return ROM[o:o+12].decode('latin1').strip().replace('@',' ')
def parse(m):
    t=ROM[0x18000+m*2]|ROM[0x18001+m*2]<<8; p=0x18000+t+2; out=[]; depth=0; n=0
    while n<400:
        n+=1; op=ROM[p]
        if op<0x20:
            if op not in LEN: return out,'bad op %02X at %X'%(op,p)
            if op==0: return out,None
            if op==4: out.append(('jump',ROM[p+1]|ROM[p+2]<<8))
            if 5<=op<=0xA: out.append(('cond',op,ROM[p+1]))
            p+=LEN[op]
        elif op<0x40: p+=4
        elif op<0x80: p+=1
        elif op<0xC0: p+=2
        else:
            out.append(('E',op-0xC1,ROM[p+1],ROM[p+2],ROM[p+3])); p+=4
    return out,'too long'
if __name__=='__main__':
    import collections
    cat=json.load(open('map_catalog.json')); kinds=collections.Counter(); errs=[]
    for e in cat:
        m=e['map']; ops,err=parse(m)
        if err: errs.append((hex(m),err))
        for o in ops:
            if o[0]=='E': kinds[o[3]]+=1
        es=[o for o in ops if o[0]=='E']
        if m in (0x0D,0x1A,0x0F,0x07): print('%02X'%m,[(o[1],item_name(o[2]) if o[3]>=0xFC else o[2]|o[3]<<8,hex(o[3]),o[4]) for o in es])
    print(kinds); print(errs)
