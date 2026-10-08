#!/usr/bin/env python3
"""对比两份 SicBench CSV 的中位数，输出 Markdown 表格。"""
import csv, sys, collections

def load(path):
    d = {}
    for r in csv.DictReader(open(path, encoding='utf-8')):
        k = (r['category'], r['name'], r['format'], r['tag'])
        d[k] = r
    return d

def main(a_path, b_path, a_name="A", b_name="B", filt=None, min_delta=0.0):
    A, B = load(a_path), load(b_path)
    rows = []
    for k in A:
        if k not in B: continue
        a, b = A[k], B[k]
        am, bm = float(a['median_ms']), float(b['median_ms'])
        if am <= 0 or bm <= 0: continue
        if filt and filt not in '/'.join(k): continue
        speed = am / bm
        delta = (speed - 1) * 100
        if abs(delta) < float(min_delta): continue
        rows.append((k, am, bm, speed, delta, a['stable'] == '1' and b['stable'] == '1',
                     a['hash'] == b['hash']))
    rows.sort(key=lambda r: -r[4])
    print(f"| 类别 | 测试 | tag | {a_name} ms | {b_name} ms | 加速 | 变化 | 产物一致 |")
    print("|---|---|---|---:|---:|---:|---:|:--:|")
    for k, am, bm, sp, dl, stable, same in rows:
        print(f"| {k[0]} | {k[1]}/{k[2]} | {k[3]} | {am:.2f} | {bm:.2f} | **{sp:.3f}x** | {dl:+.1f}% | {'OK' if same else 'DIFF'} |")
    print()
    print(f"共 {len(rows)} 项；产物哈希不一致 {sum(1 for r in rows if not r[6])} 项")

if __name__ == '__main__':
    main(*sys.argv[1:])
