#!/usr/bin/env python3
"""RDP1 델타 생성기: 원본 파일 → 수정본 파일의 차이를 설치기가 적용할 수 있는 형식으로 만든다.

형식: b"RDP1" | int64 목표크기 | ops...   (0=COPY off:int64,len:int64 / 1=ADD len:int64,data / 2=END)
원본을 그대로 담지 않고 차이만 담으므로 게임 원본 데이터가 배포본에 들어가지 않는다.

사용: python make_delta.py <원본> <수정본> <출력.rdp>
"""
import struct
import sys

MIN_COPY = 48          # 이보다 짧은 일치는 복사로 치지 않는다
WINDOW = 48            # 재동기화 앵커 길이
SEARCH = 32 * 1024 * 1024


def _common(src, tgt, i, j):
    """src[i:], tgt[j:] 의 공통 접두 길이."""
    limit = min(len(src) - i, len(tgt) - j)
    n = 0
    step = 1 << 16
    while step >= 1:
        while n + step <= limit and src[i + n:i + n + step] == tgt[j + n:j + n + step]:
            n += step
        step >>= 1
    return n


def _back(src, tgt, pi, pj, floor_i, floor_j):
    """(pi,pj) 앞쪽으로 일치하는 길이(floor 이전으로는 확장하지 않음)."""
    n = 0
    while pi - n > floor_i and pj - n > floor_j and src[pi - n - 1] == tgt[pj - n - 1]:
        n += 1
    return n


def make_delta(src: bytes, tgt: bytes) -> bytes:
    ops = []          # ('c', off, len) | ('a', bytes)
    i = j = 0
    add_start = 0
    n_tgt = len(tgt)
    while j < n_tgt:
        m = _common(src, tgt, i, j) if i < len(src) else 0
        if m >= MIN_COPY:
            if add_start < j:
                ops.append(('a', tgt[add_start:j]))
            ops.append(('c', i, m))
            i += m
            j += m
            add_start = j
            continue
        # 재동기화: 앞으로 나아가며 tgt 의 윈도우가 src 에 있는지 찾는다.
        found = False
        k = j + 1
        step = 16
        while k + WINDOW <= n_tgt:
            w = tgt[k:k + WINDOW]
            pos = src.find(w, i, min(len(src), i + SEARCH))
            if pos >= 0:
                back = _back(src, tgt, pos, k, i, add_start)
                pi, pj = pos - back, k - back
                if add_start < pj:
                    ops.append(('a', tgt[add_start:pj]))
                add_start = pj
                i, j = pi, pj
                found = True
                break
            k += step
            if step < 4096:
                step *= 2
        if not found:
            break
    if add_start < n_tgt:
        ops.append(('a', tgt[add_start:]))

    out = bytearray(b"RDP1")
    out += struct.pack('<q', n_tgt)
    for op in ops:
        if op[0] == 'c':
            out += b'\x00' + struct.pack('<qq', op[1], op[2])
        else:
            out += b'\x01' + struct.pack('<q', len(op[1])) + op[1]
    out += b'\x02'
    return bytes(out)


def apply_delta(src: bytes, delta: bytes) -> bytes:
    """검증용(설치기의 C# 구현과 동일한 규칙)."""
    assert delta[:4] == b"RDP1"
    target = struct.unpack_from('<q', delta, 4)[0]
    p = 12
    out = bytearray()
    while True:
        op = delta[p]
        p += 1
        if op == 2:
            break
        if op == 0:
            off, ln = struct.unpack_from('<qq', delta, p)
            p += 16
            out += src[off:off + ln]
        elif op == 1:
            ln = struct.unpack_from('<q', delta, p)[0]
            p += 8
            out += delta[p:p + ln]
            p += ln
        else:
            raise ValueError("bad op")
    assert len(out) == target
    return bytes(out)


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    src = open(sys.argv[1], 'rb').read()
    tgt = open(sys.argv[2], 'rb').read()
    delta = make_delta(src, tgt)
    assert apply_delta(src, delta) == tgt, "델타 검증 실패"
    open(sys.argv[3], 'wb').write(delta)
    print("delta %d bytes (source %d, target %d)" % (len(delta), len(src), len(tgt)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
