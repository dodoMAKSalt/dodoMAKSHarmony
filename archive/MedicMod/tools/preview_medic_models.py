"""Превью моделей медиков (z-буфер): CastleStoryOptimizer/Medic/preview.png"""
import math, os
import numpy as np
from PIL import Image
D = os.path.join(os.path.dirname(__file__), "..", "CastleStoryOptimizer", "Medic")
pal = Image.open(os.path.join(D, "medic_palette.png")).convert("RGB")

def load(n):
    v, vt, F = [], [], []
    for line in open(os.path.join(D, n + ".obj")):
        p = line.split()
        if not p: continue
        if p[0] == "v": v.append(tuple(map(float, p[1:4])))
        elif p[0] == "vt": vt.append(tuple(map(float, p[1:3])))
        elif p[0] == "f":
            idx = [tuple(int(x) for x in c.split("/")) for c in p[1:]]
            u, w = vt[idx[0][1] - 1]
            F.append(([v[i[0] - 1] for i in idx], pal.getpixel((int(u * 63), int((1 - w) * 63)))))
    return F

def render(F, S=300, yaw=145, pitch=-25):
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    def tr(p):
        x, y, z = p; x, z = x * cy - z * sy, x * sy + z * cy; y, z = y * cp - z * sp, y * sp + z * cp
        return np.array((x, y, z))
    allp = np.array([tr(p) for f, _ in F for p in f]); mn, mx = allp.min(0), allp.max(0)
    sc = S * 0.8 / max(mx[0] - mn[0], mx[1] - mn[1]); c = (mn + mx) / 2
    img = np.full((S, S, 3), 245.0); zb = np.full((S, S), 1e9)
    L = np.array((0.4, 0.8, -0.5)); L /= np.linalg.norm(L)
    for f, col in F:
        t = [tr(p) for p in f]
        n = np.cross(t[1] - t[0], t[2] - t[0]); nl = np.linalg.norm(n)
        if nl == 0 or n[2] >= 0: continue
        sh = 0.5 + 0.5 * abs(np.dot(n / nl, L))
        P = [np.array((S / 2 + (q[0] - c[0]) * sc, S / 2 - (q[1] - c[1]) * sc, q[2])) for q in t]
        for k in range(1, len(P) - 1):
            a, b, d = P[0], P[k], P[k + 1]
            x0, x1 = int(max(0, min(a[0], b[0], d[0]))), int(min(S - 1, max(a[0], b[0], d[0]))) + 1
            y0, y1 = int(max(0, min(a[1], b[1], d[1]))), int(min(S - 1, max(a[1], b[1], d[1]))) + 1
            if x1 <= x0 or y1 <= y0: continue
            X, Y = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
            den = (b[1] - d[1]) * (a[0] - d[0]) + (d[0] - b[0]) * (a[1] - d[1])
            if abs(den) < 1e-9: continue
            w1 = ((b[1] - d[1]) * (X - d[0]) + (d[0] - b[0]) * (Y - d[1])) / den
            w2 = ((d[1] - a[1]) * (X - d[0]) + (a[0] - d[0]) * (Y - d[1])) / den
            w3 = 1 - w1 - w2
            m = (w1 >= 0) & (w2 >= 0) & (w3 >= 0)
            z = w1 * a[2] + w2 * b[2] + w3 * d[2]
            sub = zb[y0:y1, x0:x1]; upd = m & (z < sub); sub[upd] = z[upd]
            img[y0:y1, x0:x1][upd] = np.array(col) * sh
    return Image.fromarray(img.clip(0, 255).astype(np.uint8))

names = ["medic_bag", "medic_hat", "bandage", "medical_table"]
out = Image.new("RGB", (1200, 300), (245, 245, 245))
for i, n in enumerate(names): out.paste(render(load(n)), (i * 300, 0))
out.save(os.path.join(D, "preview.png")); print("ok")
