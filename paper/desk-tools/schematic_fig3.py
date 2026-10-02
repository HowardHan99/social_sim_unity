# Scenario schematic, v3: S1/S4 tall panels flank a middle column whose top and
# bottom line up exactly with the side panels (axes are placed by hand from the
# crop aspect ratios), start points carry small robot / person icons, and the
# legend sits inside S2's empty building block so no strip is needed below.
import json, os, shutil
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.lines import Line2D
from matplotlib.markers import MarkerStyle
from matplotlib.patches import Rectangle, Polygon
from matplotlib.path import Path
import matplotlib.transforms as mtrans

HERE = os.path.dirname(os.path.abspath(__file__))
OBS_JSON = os.path.join(HERE, "scene_obstacles.json")
OUT_TMP = os.path.join(HERE, "scenarios_v3.pdf")
OUT_PNG = os.path.join(HERE, "scenarios_v3.png")
REPO_OUT = r"D:\SEAN BACKUP\~\social_sim_unity\HRI-2026-Public-Robot-Simulation\Figures\scenarios.pdf"

C_ROBOT, C_PWD = "#2a78d6", "#a03fbf"
C_WALK, C_ROAD, C_BLDG, C_BEDGE = "#efece5", "#d7d7db", "#c8c1b2", "#a89f8d"
INK, INK2 = "#111111", "#3d3c39"
T_FS, L_FS = 6.0, 5.3          # panel title / legend font sizes (pt)

OBST = json.load(open(OBS_JSON))
BAD = ("Coffee", "Cash", "Croissant", "Buns", "Register", "Flag", "Fallen_Leaves",
       "Conditioner", "Balcony")

# ---- icons as marker paths (shared by panels and legend) --------------------
def _circle(cx, cy, r, n=24):
    t = np.linspace(0, 2 * np.pi, n, endpoint=False)
    return np.c_[cx + r * np.cos(t), cy + r * np.sin(t)]

def _poly_path(pts):
    pts = np.asarray(pts, float)
    codes = [Path.MOVETO] + [Path.LINETO] * (len(pts) - 1) + [Path.CLOSEPOLY]
    return Path(np.vstack([pts, pts[:1]]), codes)

def _rounded_rect(x0, y0, x1, y1, r, n=6):
    pts = []
    for (cx, cy, a0) in ((x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90),
                         (x0 + r, y0 + r, 180), (x1 - r, y0 + r, 270)):
        for a in np.linspace(a0, a0 + 90, n):
            pts.append((cx + r * np.cos(np.radians(a)), cy + r * np.sin(np.radians(a))))
    return _poly_path(pts)

# person: head + shoulders/body, upright, centred on the start point
PERSON = Path.make_compound_path(
    _poly_path(_circle(0.0, 0.62, 0.30)),
    _rounded_rect(-0.48, -0.85, 0.48, 0.28, 0.22))
# robot: boxy body on two wheels, with a stub antenna
ROBOT = Path.make_compound_path(
    _rounded_rect(-0.60, -0.42, 0.60, 0.50, 0.14),
    _poly_path(_circle(-0.36, -0.60, 0.20)),
    _poly_path(_circle(0.36, -0.60, 0.20)),
    _poly_path([(-0.06, 0.50), (0.06, 0.50), (0.06, 0.78), (-0.06, 0.78)]))
M_PERSON, M_ROBOT = MarkerStyle(PERSON), MarkerStyle(ROBOT)

def obstacles(ax, sc, crop, keep=None):
    x0, x1, z0, z1 = crop
    kept = []
    for o in sorted(OBST[sc], key=lambda o: o["n"]):
        if not (x0 <= o["x"] <= x1 and z0 <= o["z"] <= z1): continue
        if any(b in o["n"] for b in BAD): continue
        if keep and not keep(o["x"], o["z"]): continue
        if any(abs(o["x"] - k["x"]) < 0.5 and abs(o["z"] - k["z"]) < 0.5 for k in kept): continue
        kept.append(o)
    for o in kept:
        w = max(o["w"], 0.5); d = max(o["d"], 0.5)
        ax.add_patch(Rectangle((o["x"] - w/2, o["z"] - d/2), w, d, fc="#a89a83",
                               ec="#7c7060", lw=0.4, zorder=3))

def stub(ax, p0, toward, color, marker, L=3.4, lw=2.0, z=5, ms=9.5):
    p0 = np.asarray(p0, float); u = np.asarray(toward, float) - p0
    u = u / (np.hypot(*u) + 1e-9)
    p1 = p0 + u * L
    ax.annotate("", xy=tuple(p1), xytext=tuple(p0), zorder=z + 1,
                arrowprops=dict(arrowstyle="-|>,head_width=0.3,head_length=0.55",
                                color=color, lw=lw, mutation_scale=9,
                                shrinkA=0, shrinkB=0))
    ax.plot(*p0, marker=marker, ms=ms, mfc=color, mec="white", mew=0.7, ls="none", zorder=z + 2)

def goal(ax, xy, color):
    ax.plot(*xy, marker="X", ms=6.4, mfc=color, mec="white", mew=0.8, ls="none", zorder=7)

def frame(ax, x0, x1, z0, z1, title, corner="tl"):
    ax.set_xlim(x0, x1); ax.set_ylim(z0, z1)
    ax.set_xticks([]); ax.set_yticks([])
    for s in ax.spines.values(): s.set_color("#999"); s.set_linewidth(0.6)
    # title as a boxed label inside the panel corner (no space needed above)
    tx, ha = (0.0, "left") if corner == "tl" else (1.0, "right")
    ax.text(tx, 1.0, title, transform=ax.transAxes, ha=ha, va="top",
            fontsize=T_FS, fontweight="bold", color=INK, zorder=30,
            bbox=dict(boxstyle="square,pad=0.3", fc="white", ec="#999", lw=0.5, alpha=0.95))

# ---- crops (data units) and hand-placed axes --------------------------------
CROP = {"S1": (-31.6, -23.6, -16.2, 8.0), "S4": (-31.6, -23.6, -15.4, 8.7),   # +2.8 at the top for the corner label
        "S2": (-30.6, -16.8, -35.0, -22.8), "S3": (-41.0, -24.6, -34.6, -23.8)}
asp = {k: (c[3] - c[2]) / (c[1] - c[0]) for k, c in CROP.items()}   # height / width
# unify the two tall panels so their frames match exactly
asp_side = max(asp["S1"], asp["S4"])
for k in ("S1", "S4"):
    x0, x1, z0, z1 = CROP[k]
    zc = (z0 + z1) / 2; h = (x1 - x0) * asp_side
    CROP[k] = (x0, x1, zc - h / 2, zc + h / 2)

W_FIG = 3.35                      # column width, inches
m_l, m_r, gap_x = 0.02, 0.02, 0.06
gap_y = 0.06                       # titles sit inside the panels now
top_pad = 0.02
# solve 2*w_s + w_m = usable width, and h_side = w_m*(a2+a3) + gap_y
usable = W_FIG - m_l - m_r - 2 * gap_x
a2, a3 = asp["S2"], asp["S3"]
w_s = (usable + gap_y / (a2 + a3)) / (2 + asp_side / (a2 + a3))
w_m = usable - 2 * w_s
h_side = w_s * asp_side
h2, h3 = w_m * a2, w_m * a3
assert abs((h2 + h3 + gap_y) - h_side) < 1e-6
H_FIG = h_side + top_pad + 0.02

fig = plt.figure(figsize=(W_FIG, H_FIG), dpi=300)
def place(x_in, y_in, w_in, h_in):
    return fig.add_axes([x_in / W_FIG, y_in / H_FIG, w_in / W_FIG, h_in / H_FIG])
y0 = 0.02
# both tall strips on the left, the S2/S3 stack on the right
x_mid = m_l + 2 * (w_s + gap_x)
axd = {"S1": place(m_l, y0, w_s, h_side),
       "S4": place(m_l + w_s + gap_x, y0, w_s, h_side),
       "S3": place(x_mid, y0, w_m, h3),
       "S2": place(x_mid, y0 + h3 + gap_y, w_m, h2)}

# ---- S1 narrow road ---------------------------------------------------------
ax = axd["S1"]
ax.add_patch(Rectangle((-36, -18), 6.2, 28, fc=C_ROAD, ec="none", zorder=0))
ax.add_patch(Rectangle((-29.8, -18), 4.6, 28, fc=C_WALK, ec="none", zorder=0))
for yy, h in ((-18, 10.0), (-6.5, 17.0)):
    ax.add_patch(Rectangle((-25.2, yy), 5.5, h, fc=C_BLDG, ec=C_BEDGE, lw=0.5, zorder=1))
obstacles(ax, "sidewalkNarrowroad", CROP["S1"], keep=lambda x, z: -29.8 <= x <= -25.9)
stub(ax, (-27.0, 4.0), (-27.0, -10.6), C_ROBOT, M_ROBOT)
goal(ax, (-27.0, -11.5), C_ROBOT)
stub(ax, (-28.4, -15.0), (-28.4, 3.6), C_PWD, M_PERSON)
goal(ax, (-28.4, 4.1), C_PWD)
frame(ax, *CROP["S1"], "S1\nHead-on")

# ---- S2 corner --------------------------------------------------------------
ax = axd["S2"]
ax.add_patch(Polygon([(-29.6, -22.8), (-25.0, -22.8), (-25.0, -29.9), (-16.8, -29.9),
                      (-16.8, -34.2), (-29.6, -34.2)], fc=C_WALK, ec="none", zorder=0))
ax.add_patch(Rectangle((-25.0, -29.9), 8.2, 7.1, fc=C_BLDG, ec=C_BEDGE, lw=0.5, zorder=1))
obstacles(ax, "sidewalkCornerInteraction", CROP["S2"],
          keep=lambda x, z: (-29.6 <= x <= -25.0 and -34.2 <= z <= -22.8)
                         or (-29.6 <= x <= -16.8 and -34.2 <= z <= -29.9))
stub(ax, (-27.1, -24.2), (-26.9, -28.5), C_ROBOT, M_ROBOT)
goal(ax, (-19.8, -32.0), C_ROBOT)
stub(ax, (-18.6, -32.8), (-22.4, -31.6), C_PWD, M_PERSON)
goal(ax, (-27.6, -25.8), C_PWD)
frame(ax, *CROP["S2"], "S2 \u00b7 Blind corner", corner="tr")

# legend on the empty building block of S2
handles = [
    Line2D([], [], marker=M_ROBOT, ls="", mfc=C_ROBOT, mec="white", mew=0.6, ms=7.2, label="delivery robot"),
    Line2D([], [], marker=M_PERSON, ls="", mfc=C_PWD, mec="white", mew=0.6, ms=7.2, label="pedestrian"),
    Line2D([], [], marker="X", ls="", mfc="#666", mec="white", mew=0.7, ms=5.4, label="goal"),
    Line2D([], [], marker="s", ls="", mfc="#a89a83", mec="#7c7060", mew=0.5, ms=4.6, label="street object"),
]
leg = ax.legend(handles=handles, loc="center", bbox_to_anchor=(0.705, 0.60),
                bbox_transform=ax.transAxes, ncol=1, fontsize=L_FS, frameon=True,
                fancybox=False, framealpha=0.96, edgecolor="#b8b1a3", facecolor="white",
                labelcolor=INK2, handletextpad=0.4, borderpad=0.4, labelspacing=0.35,
                handlelength=1.3, borderaxespad=0)
leg.get_frame().set_linewidth(0.5)
leg.set_zorder(20)

# ---- S3 crossroad -----------------------------------------------------------
ax = axd["S3"]
ax.add_patch(Rectangle((-41, -34.6), 3.6, 10.8, fc=C_WALK, ec="none", zorder=0))
ax.add_patch(Rectangle((-28.6, -34.6), 4.0, 10.8, fc=C_WALK, ec="none", zorder=0))
ax.add_patch(Rectangle((-37.4, -34.6), 8.8, 10.8, fc=C_ROAD, ec="none", zorder=0))
for x in np.arange(-36.8, -29.0, 1.15):
    ax.add_patch(Rectangle((x, -31.9), 0.62, 3.4, fc="white", ec="none", zorder=1, alpha=0.9))
obstacles(ax, "sidewalkCrossroad", CROP["S3"], keep=lambda x, z: x <= -37.4 or x >= -28.6)
stub(ax, (-38.6, -31.2), (-34.8, -30.6), C_ROBOT, M_ROBOT)
goal(ax, (-27.0, -29.2), C_ROBOT)
stub(ax, (-38.6, -28.8), (-34.8, -28.9), C_PWD, M_PERSON)
goal(ax, (-27.7, -30.9), C_PWD)
frame(ax, *CROP["S3"], "S3 \u00b7 Crossroad")

# ---- S4 out of store --------------------------------------------------------
ax = axd["S4"]
ax.add_patch(Rectangle((-36, -17), 6.2, 28, fc=C_ROAD, ec="none", zorder=0))
ax.add_patch(Rectangle((-29.8, -17), 4.6, 28, fc=C_WALK, ec="none", zorder=0))
ax.add_patch(Rectangle((-25.2, -17), 5.5, 16.2, fc=C_BLDG, ec=C_BEDGE, lw=0.5, zorder=1))
ax.add_patch(Rectangle((-25.2, 1.6), 5.5, 9.4, fc=C_BLDG, ec=C_BEDGE, lw=0.5, zorder=1))
ax.add_patch(Rectangle((-25.35, -0.8), 0.5, 2.4, fc=C_WALK, ec="none", zorder=2))
ax.text(-24.35, 0.4, "store", fontsize=4.4, color="#555", ha="center", va="center",
        rotation=90, fontstyle="italic", zorder=2)
obstacles(ax, "sidewalkOutofStore", CROP["S4"], keep=lambda x, z: -29.8 <= x <= -25.9)
stub(ax, (-25.6, 0.3), (-27.3, -1.5), C_ROBOT, M_ROBOT, L=2.9)
goal(ax, (-25.9, -13.0), C_ROBOT)
stub(ax, (-28.3, 3.6), (-28.3, -11.6), C_PWD, M_PERSON)
goal(ax, (-28.3, -11.9), C_PWD)
frame(ax, *CROP["S4"], "S4\nSame-direction")

fig.savefig(OUT_TMP)
fig.savefig(OUT_PNG, dpi=300)
print("figure %.2f x %.2f in" % (W_FIG, H_FIG), "| panels: side w=%.2f h=%.2f, middle w=%.2f (S2 h=%.2f, S3 h=%.2f)" % (w_s, h_side, w_m, h2, h3))
print(OUT_TMP, round(os.path.getsize(OUT_TMP) / 1024, 1), "KB")
if "--install" in os.sys.argv:
    shutil.copyfile(OUT_TMP, REPO_OUT)
    print("repo copy updated:", REPO_OUT)
