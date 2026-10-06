ROM=open(r'D:/Lufia Forge/Lufia.sfc','rb').read()
def res_ptr(i):
    p=0x60000+3*i
    a=ROM[p]|ROM[p+1]<<8; b=ROM[p+2]
    return (b&0x7F)*0x8000+(a&0x7FFF)
class Stream:
    def __init__(s,off): s.o=off
    def get(s):
        b=ROM[s.o]; s.o+=1
        if (s.o & 0x7FFF)==0: pass   # file offsets are contiguous across LoROM banks
        return b
def decompress(off):
    st=Stream(off)
    n=st.get()|st.get()<<8
    out=bytearray()
    while len(out)<n:
        ctrl=st.get(); bits=8
        while bits>0 and len(out)<n:
            b=ROM[st.o]
            if b<0x80:
                out.append(b); st.o+=1; continue
            bit=ctrl&0x80; ctrl=(ctrl<<1)&0xFF
            if not bit:
                out.append(b); st.o+=1
            else:
                st.o+=1
                b2=ROM[st.o]
                if b2&0x0F:
                    ln=(b2&0x0F)+2
                    disp=(((b<<8)|b2)>>4)|0xF000
                    disp-=0x10000
                else:
                    st.o+=1
                    b3=ROM[st.o]
                    ln=(b3&0x3F)+3
                    v=(((b<<8)|b2)>>4)|0xF000
                    disp=((v<<2)&0xFFFF)|(b3>>6)
                    disp-=0x10000
                src=len(out)+disp
                for k in range(ln):
                    out.append(out[src+k] if src+k>=0 else 0)
                st.o+=1
            bits-=1
    return bytes(out[:n]), st.o-off
if __name__=='__main__':
    import sys
    W=open(sys.argv[1],'rb').read() if len(sys.argv)>1 else None
    for rid in (0x03,0xB3,0xB4,0xB6):
        off=res_ptr(rid); data,used=decompress(off)
        loc=W.find(data[:64]) if W else -2
        full = (W.find(data)>=0) if W else None
        print('res %02X @0x%06X -> %d bytes (from %d)  first64 in WRAM at %s  full match=%s'%(rid,off,len(data),used,hex(loc) if loc>=0 else loc,full))
