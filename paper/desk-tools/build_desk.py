# Build the HRI Simulation Paper Desk from the compiled paper + the original
# Adaptive Agent PDF Desk app (same features, swapped payload).
import gzip, re, json, base64, os
import pymupdf

SP2BP = 1.0 / 65781.76      # synctex sp -> PDF bp
DPI = 230   # 11-page build with new PNGs hit 15.8 MB at 260
JPGQ = 85
TITLE = "HRI Simulation Paper Desk"
REPO = r"d:\SEAN BACKUP\~\social_sim_unity\HRI-2026-Public-Robot-Simulation"
ORIG = r"C:\Users\4070tisuper\.claude\projects\d--SEAN-BACKUP---social-sim-unity\76c3dc06-01d0-4977-907c-72cd7596e720\tool-results\artifact-902262a9-1788449283-1ffb.html"

# ---- 1. synctex -> sync + boxes -------------------------------------------
raw = gzip.open("tex/build/main.synctex.gz", "rt", encoding="latin-1").read()
inputs = {int(m.group(1)): m.group(2).strip().lower()
          for m in re.finditer(r"Input:(\d+):(.*)", raw)}
main_tags = {t for t, p in inputs.items() if p.endswith("main.tex")}
print("main.tex synctex tags:", main_tags)

parts = re.split(r"\{(\d+)\n", raw)
pt = re.compile(r"^[kg$h](\d+),(\d+):(-?\d+),(-?\d+)")
bx = re.compile(r"^\((\d+),(\d+):(-?\d+),(-?\d+):(-?\d+),(-?\d+),(-?\d+)")
sync, boxes = [], []
for i in range(1, len(parts), 2):
    s, b = [], []
    for line in parts[i + 1].splitlines():
        if not line:
            continue
        c = line[0]
        if c in "kg$h":
            m = pt.match(line)
            if not m:
                continue
            tag, ln, x, y = (int(m.group(k)) for k in range(1, 5))
            if tag not in main_tags:
                continue
            s += [0, ln, round(x * SP2BP * 10), round(y * SP2BP * 10)]
        elif c == "(":
            m = bx.match(line)
            if not m:
                continue
            tag, ln = int(m.group(1)), int(m.group(2))
            if tag not in main_tags:
                continue
            x, y, W, H, D = (int(m.group(k)) for k in range(3, 8))
            x0, y0 = x * SP2BP, (y - H) * SP2BP
            x1, y1 = (x + W) * SP2BP, (y + D) * SP2BP
            if x1 - x0 < 0.5 or x1 - x0 > 620:
                continue
            b += [0, ln, round(x0 * 10), round(y0 * 10), round(x1 * 10), round(y1 * 10)]
    sync.append(s)
    boxes.append(b)
print("pages:", len(sync),
      "| points/page:", [len(s) // 4 for s in sync],
      "| boxes/page:", [len(b) // 6 for b in boxes])

# sanity: known lines land on the right pages, coordinates inside the page
lines_main = open(os.path.join(REPO, "main.tex"), encoding="utf-8").read().splitlines()
mk = next(i + 1 for i, l in enumerate(lines_main) if l.startswith("\\maketitle"))
concl = next(i + 1 for i, l in enumerate(lines_main)
             if l.upper().startswith("\\SECTION{CONCLUSION"))
def pages_with(ln, span=0):
    return [p for p, s2 in enumerate(sync)
            if any(abs(s2[j + 1] - ln) <= span for j in range(0, len(s2), 4))]
print(f"maketitle (line {mk}) on pages {pages_with(mk)} - expect [0]")
print(f"conclusion (line {concl} +/-4) on pages {pages_with(concl, 4)} - expect late page")
xs = [s2[j + 2] / 10 for s2 in sync for j in range(0, len(s2), 4)]
ys = [s2[j + 3] / 10 for s2 in sync for j in range(0, len(s2), 4)]
print(f"x {min(xs):.0f}..{max(xs):.0f} of 612 | y {min(ys):.0f}..{max(ys):.0f} of 792")

# ---- 2. render pages -------------------------------------------------------
doc = pymupdf.open("tex/build/main.pdf")
pages, total = [], 0
for pg in doc:
    pix = pg.get_pixmap(dpi=DPI)
    img = pix.tobytes("jpg", jpg_quality=JPGQ)
    total += len(img)
    pages.append({"w": pg.rect.width, "h": pg.rect.height,
                  "img": "data:image/jpeg;base64," + base64.b64encode(img).decode()})
print(f"rendered {len(pages)} pages @{DPI}dpi jpg q{JPGQ}: {total/1e6:.1f} MB raw, ~{total*1.37/1e6:.1f} MB b64")

# ---- 3. sources ------------------------------------------------------------
files = ["main.tex", "software.bib"]
sources = {f: open(os.path.join(REPO, f), encoding="utf-8").read() for f in files}
payload = {"dpi": DPI, "pages": pages, "files": files,
           "sync": sync, "boxes": boxes, "sources": sources}

# ---- 3b. carry forward the previous desk's history + comments --------------
# The previous published desk (same output file) is the baseline: its sources
# become History versions, its comments/settings survive, and its pending
# filetexts clear because this rebuild just compiled the canonical repo state.
hist_b64 = "_"
carried_baked = {"comments": [], "filetexts": {}, "settings": {}}
prev_path = "hri-paper-desk.html"
if os.path.exists(prev_path):
    prev = open(prev_path, encoding="utf-8").read()
    pm = re.search(r'<script id="payload" type="application/json">', prev)
    prev_payload = json.loads(prev[pm.end(): prev.index("</script>", pm.end())]
                              .replace("<\\/", "</"))
    hm = re.search(r'<script id="history" type="text/plain">(.*?)</script>', prev, re.S)
    H = {}
    old_hist = hm.group(1).strip() if hm else ""
    if old_hist and not old_hist.startswith("_"):
        H = json.loads(gzip.decompress(base64.b64decode(old_hist)).decode())
    ts = int(os.path.getmtime(prev_path))
    n_new = 0
    for f, old_text in prev_payload.get("sources", {}).items():
        if sources.get(f) != old_text:
            H.setdefault(f, []).append({"ts": ts, "text": old_text})
            n_new += 1
    if H:
        hist_b64 = base64.b64encode(gzip.compress(json.dumps(H).encode())).decode()
    bm = re.search(r'<script id="baked" type="application/json">', prev)
    if bm:
        pb = json.loads(prev[bm.end(): prev.index("</script>", bm.end())]
                        .replace("<\\/", "</"))
        carried_baked["comments"] = pb.get("comments", [])
        st = dict(pb.get("settings", {}))
        st.pop("recompile", None)   # this rebuild satisfies the request; clear the banner
        st.pop("push", None)
        carried_baked["settings"] = st
        if pb.get("stranded"):
            carried_baked["stranded"] = pb["stranded"]
    print(f"history: +{n_new} version(s) recorded, {len(carried_baked['comments'])} comment(s) carried")

# ---- 4. app css/js from the original desk ----------------------------------
orig = open(ORIG, encoding="utf-8", errors="replace").read()
m = re.search(r'<style id="app-css"[^>]*>', orig)
css = orig[m.end(): orig.index("</style>", m.end())]
js = open("desk_app.js", encoding="utf-8").read()
old_t = "<title>Adaptive Agent PDF Desk</title>"
assert old_t in js, "rebuildHtml title not found in app-js"
js = js.replace(old_t, "<title>" + TITLE + "</title>")

# ---- 5. assemble (mirror the app's own rebuildHtml) ------------------------
S = "script"
cdn = "".join('<%s src="%s"></%s>' % (S, u, S) for u in (
    "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/codemirror.min.js",
    "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/mode/stex/stex.min.js"))
esc = lambda t: t.replace("</", "<\\/")
html = ("<!doctype html>\n<html><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        "<title>" + TITLE + "</title>"
        "<style id=\"app-css\">" + css + "</style></head><body>"
        "<div id=\"app\"></div>"
        "<" + S + " id=\"payload\" type=\"application/json\">" + esc(json.dumps(payload)) + "</" + S + ">"
        "<" + S + " id=\"baked\" type=\"application/json\">"
        + esc(json.dumps(carried_baked)) + "</" + S + ">"
        "<" + S + " id=\"history\" type=\"text/plain\">" + hist_b64 + "</" + S + ">"
        + cdn +
        "<" + S + " id=\"app-js\">" + js + "</" + S + "></body></html>")
open("hri-paper-desk.html", "w", encoding="utf-8").write(html)
print("hri-paper-desk.html:", round(len(html) / 1e6, 2), "MB")
