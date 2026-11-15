"""Простые low-poly модели для медиков (в стиле Castle Story): .obj + общая текстура-палитра.

Цвета берутся из клеток палитры medic_palette.png (4x4 клетки), UV каждой грани — центр клетки.
Запуск: python tools/make_medic_models.py  → CastleStoryOptimizer/Medic/*.obj, medic_palette.png
"""
import math
import os
from PIL import Image

OUT = os.path.join(os.path.dirname(__file__), "..", "CastleStoryOptimizer", "Medic")
os.makedirs(OUT, exist_ok=True)

# Палитра: имя → (клетка, цвет)
PALETTE = {
    "white": (0, (238, 238, 232)),
    "red": (1, (205, 30, 30)),
    "wood": (2, (140, 96, 58)),
    "darkwood": (3, (96, 64, 38)),
    "cloth": (4, (226, 222, 205)),
    "metal": (5, (150, 150, 155)),
}
CELLS = 4  # 4x4 клетки


def save_palette():
    size = 64
    img = Image.new("RGB", (size, size), (255, 0, 255))
    cs = size // CELLS
    for _, (idx, col) in PALETTE.items():
        x, y = idx % CELLS, idx // CELLS
        for i in range(x * cs, (x + 1) * cs):
            for j in range(y * cs, (y + 1) * cs):
                img.putpixel((i, j), col)
    img.save(os.path.join(OUT, "medic_palette.png"))


def uv_of(color):
    idx = PALETTE[color][0]
    x, y = idx % CELLS, idx // CELLS
    return ((x + 0.5) / CELLS, 1.0 - (y + 0.5) / CELLS)


class Mesh:
    def __init__(self):
        self.v, self.vt, self.vn, self.f = [], [], [], []
        self._uv = {}

    def uv(self, color):
        if color not in self._uv:
            self.vt.append(uv_of(color))
            self._uv[color] = len(self.vt)
        return self._uv[color]

    def quad(self, a, b, c, d, color):
        """Четырёхугольник a-b-c-d (против часовой при взгляде снаружи)."""
        n = normal(a, b, c)
        self.vn.append(n)
        ni = len(self.vn)
        t = self.uv(color)
        base = len(self.v)
        self.v += [a, b, c, d]
        self.f.append([(base + 1, t, ni), (base + 2, t, ni), (base + 3, t, ni), (base + 4, t, ni)])

    def tri(self, a, b, c, color):
        n = normal(a, b, c)
        self.vn.append(n)
        ni = len(self.vn)
        t = self.uv(color)
        base = len(self.v)
        self.v += [a, b, c]
        self.f.append([(base + 1, t, ni), (base + 2, t, ni), (base + 3, t, ni)])

    def box(self, x0, y0, z0, x1, y1, z1, color, skip=()):
        p = lambda x, y, z: (x, y, z)
        faces = {
            "+z": (p(x0, y0, z1), p(x1, y0, z1), p(x1, y1, z1), p(x0, y1, z1)),
            "-z": (p(x1, y0, z0), p(x0, y0, z0), p(x0, y1, z0), p(x1, y1, z0)),
            "+x": (p(x1, y0, z1), p(x1, y0, z0), p(x1, y1, z0), p(x1, y1, z1)),
            "-x": (p(x0, y0, z0), p(x0, y0, z1), p(x0, y1, z1), p(x0, y1, z0)),
            "+y": (p(x0, y1, z1), p(x1, y1, z1), p(x1, y1, z0), p(x0, y1, z0)),
            "-y": (p(x0, y0, z0), p(x1, y0, z0), p(x1, y0, z1), p(x0, y0, z1)),
        }
        for k, q in faces.items():
            if k not in skip:
                self.quad(*q, color)

    def cylinder(self, cx, cz, y0, y1, r, color, seg=10, cap_color=None, axis="y"):
        """Цилиндр вдоль Y (или X при axis='x': cx→y центр, cz→z центр, y0..y1 → x)."""
        def P(a, h):
            ca, sa = math.cos(a) * r, math.sin(a) * r
            if axis == "y":
                return (cx + ca, h, cz + sa)
            return (h, cx + ca, cz + sa)
        for i in range(seg):
            a0, a1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
            if axis == "y":
                self.quad(P(a0, y0), P(a0, y1), P(a1, y1), P(a1, y0), color)
            else:
                self.quad(P(a0, y1), P(a0, y0), P(a1, y0), P(a1, y1), color)
        cc = cap_color or color
        top = (cx, y1, cz) if axis == "y" else (y1, cx, cz)
        bot = (cx, y0, cz) if axis == "y" else (y0, cx, cz)
        for i in range(seg):
            a0, a1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
            if axis == "y":
                self.tri(top, P(a1, y1), P(a0, y1), cc)
                self.tri(bot, P(a0, y0), P(a1, y0), cc)
            else:
                self.tri(top, P(a0, y1), P(a1, y1), cc)
                self.tri(bot, P(a1, y0), P(a0, y0), cc)

    def save(self, name):
        path = os.path.join(OUT, name + ".obj")
        with open(path, "w", encoding="ascii") as fh:
            fh.write("# dodoMAKSharmony medic model: %s\n" % name)
            for v in self.v:
                fh.write("v %.4f %.4f %.4f\n" % v)
            for t in self.vt:
                fh.write("vt %.4f %.4f\n" % t)
            for n in self.vn:
                fh.write("vn %.4f %.4f %.4f\n" % n)
            for face in self.f:
                fh.write("f " + " ".join("%d/%d/%d" % c for c in face) + "\n")
        print(name, "вершин", len(self.v), "граней", len(self.f))


def normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    l = math.sqrt(nx * nx + ny * ny + nz * nz) or 1
    return (nx / l, ny / l, nz / l)


def cross_plate(m, cx, cy, z, size, th, color, facing=1):
    """Красный крест, выступающий из плоскости z (facing=+1 — смотрит в +z, -1 — в -z)."""
    a, b = size / 2, size / 6
    z1 = z + th * facing
    za, zb = (z, z1) if facing > 0 else (z1, z)
    m.box(cx - b, cy - a, za, cx + b, cy + a, zb, color)
    m.box(cx - a, cy - b, za, cx - b, cy + b, zb, color)
    m.box(cx + b, cy - b, za, cx + a, cy + b, zb, color)


def ring(m, cx, cy, z, r_out, r_in, th, color, facing=1, seg=16):
    """Красное кольцо вокруг креста (как на эмблеме)."""
    z1 = z + th * facing
    for i in range(seg):
        a0, a1 = 2 * math.pi * i / seg, 2 * math.pi * (i + 1) / seg
        o0 = (cx + math.cos(a0) * r_out, cy + math.sin(a0) * r_out)
        o1 = (cx + math.cos(a1) * r_out, cy + math.sin(a1) * r_out)
        i0 = (cx + math.cos(a0) * r_in, cy + math.sin(a0) * r_in)
        i1 = (cx + math.cos(a1) * r_in, cy + math.sin(a1) * r_in)
        if facing > 0:
            m.quad((i0[0], i0[1], z1), (o0[0], o0[1], z1), (o1[0], o1[1], z1), (i1[0], i1[1], z1), color)
            m.quad((o0[0], o0[1], z), (o1[0], o1[1], z), (o1[0], o1[1], z1), (o0[0], o0[1], z1), color)
        else:
            m.quad((i1[0], i1[1], z1), (o1[0], o1[1], z1), (o0[0], o0[1], z1), (i0[0], i0[1], z1), color)
            m.quad((o1[0], o1[1], z), (o0[0], o0[1], z), (o0[0], o0[1], z1), (o1[0], o1[1], z1), color)


def medic_bag():
    """Белый кейс, красная ручка, красный крест в круге с обеих сторон (по картинке)."""
    m = Mesh()
    W, H, D = 1.0, 0.7, 0.32
    m.box(-W / 2, 0, -D / 2, W / 2, H, D / 2, "white")
    # ручка: две стойки + перекладина
    m.box(-0.25, H, -0.05, -0.19, H + 0.12, 0.05, "red")
    m.box(0.19, H, -0.05, 0.25, H + 0.12, 0.05, "red")
    m.box(-0.25, H + 0.12, -0.05, 0.25, H + 0.18, 0.05, "red")
    for f in (1, -1):
        z = D / 2 * f
        cross_plate(m, 0, H / 2, z, 0.34, 0.02, "red", f)
        ring(m, 0, H / 2, z, 0.27, 0.23, 0.02, "red", f)
    m.save("medic_bag")


def medic_hat():
    """Белая шапочка медика с красным крестом спереди."""
    m = Mesh()
    m.cylinder(0, 0, 0, 0.35, 0.5, "white", seg=12)
    m.cylinder(0, 0, -0.04, 0.0, 0.55, "white", seg=12)  # отворот
    # крест на передней стороне (+z): плоская пластина у края
    cross_plate(m, 0, 0.18, 0.49, 0.22, 0.03, "red", 1)
    cross_plate(m, 0, 0.18, -0.49, 0.22, 0.03, "red", -1)
    m.save("medic_hat")


def bandage():
    """Рулон бинта: белый цилиндр (ось X) с красной полоской."""
    m = Mesh()
    m.cylinder(0, 0, -0.3, -0.04, 0.25, "cloth", seg=10, axis="x")
    m.cylinder(0, 0, -0.04, 0.04, 0.255, "red", seg=10, axis="x")
    m.cylinder(0, 0, 0.04, 0.3, 0.25, "cloth", seg=10, axis="x")
    m.save("bandage")


def medical_table():
    """Стол медика: деревянный стол + белый ящик с крестом и рулоны бинтов сверху."""
    m = Mesh()
    L, Wd, Ht, T = 2.0, 1.0, 0.9, 0.1
    m.box(-L / 2, Ht - T, -Wd / 2, L / 2, Ht, Wd / 2, "wood")
    for x in (-L / 2 + 0.1, L / 2 - 0.2):
        for z in (-Wd / 2 + 0.1, Wd / 2 - 0.2):
            m.box(x, 0, z, x + 0.1, Ht - T, z + 0.1, "darkwood")
    m.box(-L / 2 + 0.1, 0.25, -Wd / 2 + 0.12, L / 2 - 0.1, 0.32, Wd / 2 - 0.12, "darkwood")  # полка
    # ящик-аптечка
    m.box(-0.7, Ht, -0.25, -0.1, Ht + 0.4, 0.25, "white")
    cross_plate(m, -0.4, Ht + 0.2, 0.25, 0.24, 0.02, "red", 1)
    # бинты
    for i, x in enumerate((0.25, 0.55)):
        m.cylinder(Ht + 0.09, -0.08 + i * 0.16, x - 0.12, x + 0.12, 0.09, "cloth", seg=8, axis="x")
    # чаша/таз
    m.cylinder(0.7, 0.1, Ht, Ht + 0.12, 0.18, "metal", seg=10)
    # красный крест на передней кромке стола
    for f in (1, -1):
        m.box(-0.3, Ht - 0.5, f * Wd / 2 - 0.01, 0.3, Ht - 0.1, f * Wd / 2 + 0.01, "white")
        cross_plate(m, 0.0, Ht - 0.3, f * (Wd / 2 + 0.01), 0.3, 0.02, "red", f)
    cross_plate(m, -0.4, Ht + 0.2, -0.25, 0.24, 0.02, "red", -1)
    m.save("medical_table")


if __name__ == "__main__":
    save_palette()
    medic_bag()
    medic_hat()
    bandage()
    medical_table()
