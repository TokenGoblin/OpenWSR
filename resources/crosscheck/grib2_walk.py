import sys, gzip, struct

def u(b, o, n):
    return int.from_bytes(b[o:o+n], 'big')

def walk(name, b):
    print("="*70)
    print(name, "total bytes:", len(b))
    assert b[0:4] == b'GRIB', b[0:8]
    disc = b[6]
    ed = b[7]
    total = u(b, 8, 8)
    print(f"  Sec0: discipline={disc} edition={ed} totalLength={total}")
    o = 16
    while o < len(b) - 4:
        if b[o:o+4] == b'7777':
            print("  Sec8: end")
            o += 4
            if o < len(b) and b[o:o+4] == b'GRIB':
                print("  --- another message follows ---")
                continue
            break
        ln = u(b, o, 4)
        sn = b[o+4]
        if ln == 0 or o + ln > len(b):
            print(f"  bad section at {o}: len={ln} num={sn}")
            break
        s = b[o:o+ln]
        print(f"  Sec{sn}: len={ln}")
        if sn == 1:
            print(f"    center={u(s,5,2)} subcenter={u(s,7,2)} tabVer={s[9]} localTabVer={s[10]}")
            print(f"    refTime={u(s,12,2)}-{s[14]:02d}-{s[15]:02d} {s[16]:02d}:{s[17]:02d}:{s[18]:02d}")
        elif sn == 3:
            gdt = u(s, 12, 2)
            print(f"    numPoints={u(s,6,4)} gridDefTemplate=3.{gdt}")
            if gdt in (0, 20, 30):
                ni = u(s, 30, 4); nj = u(s, 34, 4)
                la1 = u(s, 46, 4); lo1 = u(s, 50, 4)
                la2 = u(s, 55, 4); lo2 = u(s, 59, 4)
                di = u(s, 63, 4); dj = u(s, 67, 4)
                def sgn(v):
                    return -(v & 0x7fffffff) if v & 0x80000000 else v
                print(f"    Ni={ni} Nj={nj}")
                print(f"    la1={sgn(la1)/1e6} lo1={sgn(lo1)/1e6} la2={sgn(la2)/1e6} lo2={sgn(lo2)/1e6}")
                print(f"    Di={di/1e6} Dj={dj/1e6} scanMode=0x{s[71]:02x}")
        elif sn == 4:
            pdt = u(s, 7, 2)
            print(f"    prodDefTemplate=4.{pdt} paramCategory={s[9]} paramNumber={s[10]}")
            print(f"    genProcType={s[11]} fcstTimeUnit={s[17]} fcstTime={u(s,18,4)}")
            print(f"    lvl1Type={s[22]} lvl1Scale={s[23]} lvl1Val={u(s,24,4)}")
        elif sn == 5:
            drt = u(s, 9, 2)
            print(f"    numDataPoints={u(s,5,4)} dataRepTemplate=5.{drt}  <<<< PACKING")
            R = struct.unpack('>f', s[11:15])[0]
            E = u(s, 15, 2); D = u(s, 17, 2)
            E = E - 65536 if E > 32767 else E
            D = D - 65536 if D > 32767 else D
            print(f"    R(ref)={R} E(binScale)={E} D(decScale)={D} nbits={s[19]} origFieldType={s[20]}")
            if drt == 41:
                print("    -> PNG compression (template 5.41)")
            elif drt == 40:
                print("    -> JPEG2000 (template 5.40)")
            elif drt == 0:
                print("    -> simple packing (template 5.0)")
            elif drt in (2, 3):
                print("    -> complex packing / spatial differencing")
                print(f"    groupSplit={s[21]} missingValMgmt={s[22]}")
                print(f"    primaryMissing={u(s,23,4)} secondaryMissing={u(s,27,4)}")
        elif sn == 6:
            print(f"    bitmapIndicator={s[5]} (255=no bitmap)")
        elif sn == 7:
            print(f"    dataLength={ln-5}")
            if ln > 10:
                print(f"    first bytes: {s[5:13].hex()}")
        o += ln

for path in sys.argv[1:]:
    if path.endswith('.gz'):
        with gzip.open(path, 'rb') as f:
            data = f.read()
    else:
        data = open(path, 'rb').read()
    walk(path.split('\\')[-1], data)
