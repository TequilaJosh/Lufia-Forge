import sys; sys.path.insert(0,'.')
from lz import decompress,res_ptr,ROM
from PIL import Image
V=open('alekia_vram.bin','rb').read(); CG=open('alekia_cgram.bin','rb').read()
pal=[]
for i in range(256):
    c=CG[2*i]|CG[2*i+1]<<8
    pal.append(((c&31)<<3,((c>>5)&31)<<3,((c>>10)&31)<<3))
tilecache={}
def tile(n):
    if n in tilecache: return tilecache[n]
    b=V[0x2000+n*32:0x2000+n*32+32]; px=[]
    for y in range(8):
        p0,p1,p2,p3=b[2*y],b[2*y+1],b[16+2*y],b[16+2*y+1]
        px.append([((p0>>(7-x))&1)|(((p1>>(7-x))&1)<<1)|(((p2>>(7-x))&1)<<2)|(((p3>>(7-x))&1)<<3) for x in range(8)])
    tilecache[n]=px; return px
def render(mapid, out):
    md,_=decompress(res_ptr(0xB2+ROM[0xE19B+mapid])); ts=md[8]
    blk,_=decompress(res_ptr(0xB1+ROM[0xE140+ts]))
    w,h=md[0x12]|md[0x13]<<8, md[0x14]|md[0x15]<<8
    start=md[0x28]|md[0x29]<<8
    img=Image.new('RGB',(w*16,h*16),pal[0])
    P=img.load()
    for my in range(h):
        for mx in range(w):
            o=start+2*(my*w+mx); m=(md[o]|md[o+1]<<8)&0x3FF
            rec=blk[m*9:m*9+8]
            for q,(qx,qy) in enumerate(((0,0),(0,8),(8,0),(8,8))):
                e=rec[2*q]|rec[2*q+1]<<8
                t=tile(e&0x3FF); p=(e>>10)&7; hf=e&0x4000; vf=e&0x8000
                for y in range(8):
                    for x in range(8):
                        c=t[7-y if vf else y][7-x if hf else x]
                        if c: P[mx*16+qx+x, my*16+qy+y]=pal[p*16+c]
    img.save(out); print('saved',out,img.size)
render(int(sys.argv[1],16), sys.argv[2])
