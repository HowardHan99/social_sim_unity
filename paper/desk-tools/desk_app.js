
(function(){
"use strict";
const P = JSON.parse(document.getElementById("payload").textContent);
const BAKED = JSON.parse(document.getElementById("baked").textContent);
const LS_KEY = "aa-pdf-desk-overlay-v3";   // v3: whole-file edits (CodeMirror desk)
const CK = "aa-pdf-desk-composer";   // a comment mid-typing, so a reload can't eat it

if (!window.CodeMirror){
  document.getElementById("app").innerHTML =
    '<div class="bootfail"><b>The editor library failed to load.</b><br>' +
    'This page pulls CodeMirror from cdnjs.cloudflare.com — check the network and reload.</div>';
  return;
}
const MODE = (CodeMirror.modes && CodeMirror.modes.stex) ? "stex" : "text/plain";

// ---- state -----------------------------------------------------------------
// Edits are whole-file: filetexts[file] = {was, now, at}. `was` is the text the
// edit was made against, so a rebuild from newer .tex can reconcile hunk by
// hunk instead of silently mismatching.
let overlay = {drafts: [], deletedIds: [], settings: {}, filetexts: {},
               strandedLocal: [], resolvedIds: [], unresolvedIds: []};
try { overlay = Object.assign(overlay, JSON.parse(localStorage.getItem(LS_KEY) || "{}")); } catch(e){}
// Anything the baked block already carries must leave the overlay, or it would
// render twice after the reload that follows a successful save.
try {
  overlay.drafts = (overlay.drafts || []).filter(d => !BAKED.comments.some(c => c.id === d.id));
} catch(e){}
const effBase = f => {
  const b = BAKED.filetexts && BAKED.filetexts[f];
  return b && typeof b.now === "string" ? b.now : P.sources[f];
};
// Prune the overlay against the baked layer: an entry whose text the bake
// already carries is spent; one made against an older version can't be applied
// (the base moved) — keep its text aside so Export can still hand it off.
overlay.filetexts = overlay.filetexts || {};
overlay.strandedLocal = overlay.strandedLocal || [];
Object.keys(overlay.filetexts).forEach(f => {
  const o = overlay.filetexts[f];
  if (!o || typeof o.now !== "string" || o.now === effBase(f)){ delete overlay.filetexts[f]; return; }
  if (o.was !== effBase(f)){
    overlay.strandedLocal.push({file: f, now: o.now, at: o.at});
    if (overlay.strandedLocal.length > 5) overlay.strandedLocal.shift();
    delete overlay.filetexts[f];
  }
});
const settings = () => Object.assign({}, BAKED.settings, overlay.settings);
const comments = () =>
  BAKED.comments.filter(c => !overlay.deletedIds.includes(c.id))
    .map(c => Object.assign({baked: true}, c))
    .concat(overlay.drafts.map(c => Object.assign({baked: false}, c)))
    .map(c => Object.assign({}, c, {resolved:
      (c.resolved || (overlay.resolvedIds || []).includes(c.id)) &&
      !(overlay.unresolvedIds || []).includes(c.id)}))
    .sort((a, b) => a.file === b.file ? a.line - b.line : (a.file < b.file ? -1 : 1));
// A resolved comment is done: it leaves the inline flow and lives in the dock.
const active = () => comments().filter(c => !c.resolved);
const resolvedList = () => comments().filter(c => c.resolved);
const stranded = () => (BAKED.stranded || []).concat(overlay.strandedLocal || []);
const dirty = () => overlay.drafts.length + overlay.deletedIds.length > 0 ||
  Object.keys(overlay.filetexts || {}).length > 0 ||
  (overlay.resolvedIds || []).length > 0 ||
  (overlay.unresolvedIds || []).length > 0 ||
  Object.keys(overlay.settings).length > 0;
const saveOverlay = () => {
  try { localStorage.setItem(LS_KEY, JSON.stringify(overlay)); } catch(e){}
  scheduleAutosave();   // browser storage is per-version and fragile — bake soon
};

let zoom = 1, curFile = null, readOnly = false, composerAt = null, editing = true;  // editing is the default mode

// ---- view persistence ------------------------------------------------------
// Every save/publish live-reloads this page into the new version; without this
// the reader lands back at the top of everything. File, both scroll positions,
// the cursor and zoom are stashed (sessionStorage survives the same-tab reload;
// localStorage is the cross-tab fallback) and restored on boot.
const VIEW_KEY = "aa-pdf-desk-view";
let viewT = 0;
function saveView(){
  clearTimeout(viewT);
  viewT = setTimeout(() => {
    const sc = cm ? cm.getScrollInfo() : {top: 0, left: 0};
    const v = JSON.stringify({file: curFile, edTop: sc.top, edLeft: sc.left,
      cur: cm ? cm.getCursor() : null,
      st: stage.scrollTop, sl: stage.scrollLeft, zoom: zoom});
    try { sessionStorage.setItem(VIEW_KEY, v); } catch(e){}
    try { localStorage.setItem(VIEW_KEY, v); } catch(e){}
  }, 200);
}
function loadView(){
  let raw = null;
  try { raw = sessionStorage.getItem(VIEW_KEY); } catch(e){}
  if (!raw) try { raw = localStorage.getItem(VIEW_KEY); } catch(e){}
  try { return JSON.parse(raw || "null"); } catch(e){ return null; }
}

// forward index: file -> line -> [[page,x,y],...]
const fwd = P.files.map(() => ({}));
P.sync.forEach((a, page) => {
  for (let i = 0; i < a.length; i += 4){
    const m = fwd[a[i]], ln = a[i+1];
    (m[ln] = m[ln] || []).push([page, a[i+2]/10, a[i+3]/10]);
  }
});

// ---- skeleton --------------------------------------------------------------
const app = document.getElementById("app");
app.innerHTML = `
  <div class="bar" role="toolbar" aria-label="Workbench tools">
    <span class="name">PDF Desk</span>
    <select id="filesel" aria-label="Source file"></select>
    <button id="m-review" aria-pressed="false">&#128172; Review <span id="cmtcount">0</span></button>
    <button id="m-edit" aria-pressed="true" title="Edit the LaTeX source in place">&#9998; Edit <span id="editcount">0</span></button>
    <button id="m-push" class="primary" title="Save now and ask Claude to apply the stacked comments immediately">&#8686; Push</button>
    <button id="m-recompile" title="Save your source edits and have Claude recompile — this page refreshes with the new PDF">&#10227; Recompile</button>
    <button id="m-sync" title="Rebuild this desk from the latest repo sources, even with no local edits — Claude picks it up and republishes">&#10226; Sync</button>
    <button id="m-hist" aria-pressed="false" title="Earlier versions of this file, from every published desk">&#128336; History</button>
    <button id="m-dock" aria-pressed="false" title="Resolved comments live here">&#128452; Done <span id="donecount">0</span></button>
    <span class="dirty" id="dirtylbl" hidden>unsaved</span>
    <span class="pending" id="pendinglbl" hidden>&#10227; PDF rebuilding&hellip;</span>
    <span class="spacer"></span>
    <button id="m-export-top" title="Export comments and source edits as Markdown">&#8595; Export</button>
    <span class="grp">
      <button id="z-out" title="Zoom out (−)">−</button>
      <span class="zoomlvl" id="z-lvl">100%</span>
      <button id="z-in" title="Zoom in (+)">+</button>
      <button id="z-fit" title="Fit page width">Fit</button>
    </span>
    <span class="pgind" id="pgind"></span>
  </div>
  <div class="main">
    <div class="left"><div class="ed" id="ed"></div></div>
    <div class="div" id="divider" role="separator" aria-label="Resize panes"></div>
    <div class="right"><div class="stage" id="stage" tabindex="0"></div></div>
  </div>
  <aside class="review" id="review" aria-label="Review">
    <h2>Review</h2>
    <div class="body" id="reviewbody"></div>
    <div class="foot">
      <button id="m-save" class="primary" style="border:none">Save to artifact</button>
      <button id="m-export">Export .md</button>
      <button id="m-set">Settings</button>
    </div>
  </aside>
  <aside class="review hist" id="hist" aria-label="Version history">
    <h2>History &mdash; <span id="histfile"></span></h2>
    <div class="body" id="histbody"></div>
  </aside>
  <aside class="review dock" id="dock" aria-label="Resolved comments">
    <h2>Done</h2>
    <div class="body" id="dockbody"></div>
  </aside>
  <div class="toast" id="toast" role="status"></div>`;
const $ = id => document.getElementById(id);
const stage = $("stage");

// file selector
P.files.forEach(f => {
  const o = document.createElement("option");
  o.value = f; o.textContent = f;
  $("filesel").appendChild(o);
});
$("filesel").onchange = () => openFile($("filesel").value);

// pdf pages — canvases redrawn at device resolution so text stays crisp at
// every zoom level; only pages near the viewport hold full-res backing stores.
const imgs = P.pages.map(pg => { const im = new Image(); im.src = pg.img; return im; });
const rendered = P.pages.map(() => "");
const pgEls = P.pages.map((pg, i) => {
  const wrap = document.createElement("div");
  wrap.className = "pgwrap";
  wrap.innerHTML = `<div class="pg" data-page="${i}">
      <canvas aria-label="page ${i+1}"></canvas>
      <div class="marks"></div></div>`;
  stage.appendChild(wrap);
  imgs[i].onload = () => scheduleRender();
  return wrap.firstElementChild;
});
function cssSize(i){
  const w = P.pages[i].w * 96/72 * zoom;
  return {w, h: w * P.pages[i].h / P.pages[i].w};
}
let renderT = 0;
function scheduleRender(){ clearTimeout(renderT); renderT = setTimeout(renderVisible, 60); }
function renderVisible(){
  const dpr = Math.min(2.5, window.devicePixelRatio || 1);
  const top = stage.scrollTop - stage.clientHeight,
        bot = stage.scrollTop + stage.clientHeight * 2;
  pgEls.forEach((pg, i) => {
    const el = pg.parentElement, cv = pg.querySelector("canvas");
    const near = el.offsetTop + el.offsetHeight > top && el.offsetTop < bot;
    if (!near){
      if (cv.width > 1){ cv.width = 1; cv.height = 1; rendered[i] = ""; }
      return;
    }
    const im = imgs[i];
    if (!im.complete || !im.naturalWidth) return;
    const {w, h} = cssSize(i);
    const key = Math.round(w * dpr) + "x" + Math.round(h * dpr);
    if (rendered[i] === key) return;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    const ctx = cv.getContext("2d");
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(im, 0, 0, cv.width, cv.height);
    rendered[i] = key;
  });
}

// ---- the editor ------------------------------------------------------------
function esc(s){ return s.replace(/&/g,"&amp;").replace(/</g,"&lt;"); }
const docs = {};        // file -> CodeMirror.Doc (text + undo history survive switching)
const fileScroll = {};  // file -> {left, top}
const decorState = {};  // file -> {widgets, bg, edBg}
const cmtHandles = {};  // comment id -> {file, doc, h, hEnd}: anchors that follow edits
let dotLines = new Set(), plusLine = null, composerWidget = null;

const currentText = f => docs[f] ? docs[f].getValue()
  : (overlay.filetexts[f] ? overlay.filetexts[f].now : effBase(f));
const fileChanged = f => currentText(f) !== P.sources[f];
const initialText = f => overlay.filetexts[f] ? overlay.filetexts[f].now : effBase(f);

// Line diff (vs the sources this PDF was built from): common prefix/suffix
// trim, then an LCS walk. Powers the change bars, per-hunk revert and Export.
function lineDiff(A, B){
  let s = 0;
  while (s < A.length && s < B.length && A[s] === B[s]) s++;
  let e = 0;
  while (e < A.length - s && e < B.length - s && A[A.length-1-e] === B[B.length-1-e]) e++;
  const a = A.slice(s, A.length - e), b = B.slice(s, B.length - e);
  const n = a.length, m = b.length;
  if (!n && !m) return [];
  if (n * m > 250000) return [{aStart: s, aCount: n, bStart: s, bCount: m}];
  const W = m + 1, dp = new Uint32Array((n + 1) * W);
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      dp[i*W+j] = a[i] === b[j] ? dp[(i+1)*W+j+1] + 1 : Math.max(dp[(i+1)*W+j], dp[i*W+j+1]);
  const hunks = [];
  let i = 0, j = 0;
  while (i < n || j < m){
    if (i < n && j < m && a[i] === b[j]){ i++; j++; continue; }
    const ai = i, bj = j;
    while (i < n || j < m){
      if (i < n && j < m && a[i] === b[j]) break;
      if (i < n && (j >= m || dp[(i+1)*W+j] >= dp[i*W+j+1])) i++;
      else j++;
    }
    hunks.push({aStart: s + ai, aCount: i - ai, bStart: s + bj, bCount: j - bj});
  }
  return hunks;
}
function editCount(){
  let n = 0;
  P.files.forEach(f => {
    if (!fileChanged(f)) return;
    lineDiff(P.sources[f].split("\n"), currentText(f).split("\n"))
      .forEach(h => n += Math.max(h.aCount, h.bCount));
  });
  return n;
}

const cm = CodeMirror($("ed"), {
  value: "", mode: MODE, theme: "desk",
  lineNumbers: true, lineWrapping: true,
  gutters: ["CodeMirror-linenumbers", "gut-cmt", "gut-edit"],
  indentUnit: 2, tabSize: 4, undoDepth: 500, viewportMargin: 30,
  spellcheck: false, autocorrect: false, autocapitalize: false,
  extraKeys: {
    "Cmd-/": toggleCommentCM, "Ctrl-/": toggleCommentCM,
    "Cmd-B": () => wrapCM("textbf"), "Ctrl-B": () => wrapCM("textbf"),
    "Cmd-I": () => wrapCM("emph"), "Ctrl-I": () => wrapCM("emph"),
    "Cmd-M": commentAtCursor, "Ctrl-M": commentAtCursor,
  }
});

function openFile(f, thenLine){
  if (curFile !== f){
    if (curFile && docs[curFile]) fileScroll[curFile] = cm.getScrollInfo();
    curFile = f;
    $("filesel").value = f;
    let d = docs[f];
    if (!d){ d = CodeMirror.Doc(initialText(f), MODE); docs[f] = d; }
    cm.swapDoc(d);
    decorComments();
    decorEdits();
    const sc = fileScroll[f];
    if (sc) cm.scrollTo(sc.left, sc.top);
    if ($("hist").classList.contains("open")){ histSel = null; renderHist(); }
  }
  if (thenLine) revealLine(thenLine);
  saveView();
}
function revealLine(n, flash = true){
  const l = Math.min(Math.max(1, n), cm.lastLine() + 1) - 1;
  cm.scrollIntoView({line: l, ch: 0}, Math.round(cm.getScrollInfo().clientHeight / 2 - 20));
  if (flash){
    const h = cm.addLineClass(l, "background", "cm-flash");
    setTimeout(() => { try { cm.removeLineClass(h, "background", "cm-flash"); } catch(e){} }, 1900);
  }
}
/** Where a comment sits NOW: its line handle has followed every edit above it. */
function liveAnchor(c){
  const rec = cmtHandles[c.id];
  if (!rec) return c;
  let l = null, le = null;
  try { l = rec.doc.getLineNumber(rec.h); } catch(e){}
  if (l == null) return c;
  const out = Object.assign({}, c, {line: l + 1});
  if (rec.hEnd){
    try { le = rec.doc.getLineNumber(rec.hEnd); } catch(e){}
    if (le != null && le + 1 > out.line) out.lineEnd = le + 1; else delete out.lineEnd;
  }
  return out;
}
function cmtHtml(c){
  const range = c.lineEnd && c.lineEnd > c.line ? " · lines " + c.line + "–" + c.lineEnd : "";
  return `<div class="cmt" data-id="${c.id}">
    ${c.quote ? `<div class="quote">&#8220;${esc(c.quote)}&#8221;</div>` : ""}
    <div class="txt">${esc(c.text)}</div>
    <div class="who">${esc(c.author || "anonymous")} · ${c.at ? c.at.slice(0,10) : ""}${range}${c.baked ? "" : '<span class="badge">unsaved</span>'}</div>
    <div class="acts">
      <button data-act="resolve" style="color:var(--ok)">&#10003; Resolve</button>
      <button data-act="del" style="color:var(--danger)">Delete</button></div>
  </div>`;
}
function widgetClick(e){
  const res = e.target.closest("button[data-act='resolve']");
  if (res){
    setResolved(res.closest(".cmt").dataset.id, true);
    toast("Resolved — moved to the Done dock");
    return;
  }
  const del = e.target.closest("button[data-act='del']");
  if (del){
    const id = del.closest(".cmt").dataset.id;
    if (overlay.drafts.some(c => c.id === id))
      overlay.drafts = overlay.drafts.filter(c => c.id !== id);
    else overlay.deletedIds.push(id);
    saveOverlay(); refresh();
  }
}
function setResolved(id, on){
  overlay.resolvedIds = overlay.resolvedIds || [];
  overlay.unresolvedIds = overlay.unresolvedIds || [];
  overlay.resolvedIds = overlay.resolvedIds.filter(x => x !== id);
  overlay.unresolvedIds = overlay.unresolvedIds.filter(x => x !== id);
  const baked = BAKED.comments.find(c => c.id === id);
  if (on && !(baked && baked.resolved)) overlay.resolvedIds.push(id);
  if (!on && baked && baked.resolved) overlay.unresolvedIds.push(id);
  const d = overlay.drafts.find(c => c.id === id);
  if (d) d.resolved = on || undefined;
  saveOverlay(); refresh();
}

// Comment decorations: inline cards as line widgets (they ride along as the
// text is edited), a tinted background across the covered range, a ● in the
// comment gutter. Rebuilt per refresh; the composer widget is kept out of it.
function decorComments(){
  const f = curFile;
  if (!f) return;
  const st = decorState[f] || (decorState[f] = {widgets: [], bg: [], edBg: []});
  st.widgets.forEach(w => { try { w.clear(); } catch(e){} });
  st.bg.forEach(x => { try { cm.removeLineClass(x, "background", "cm-line-cmtl"); } catch(e){} });
  st.widgets = []; st.bg = [];
  cm.clearGutter("gut-cmt");
  dotLines = new Set(); plusLine = null;
  const cs = active().filter(c => c.file === f).map(liveAnchor);
  Object.keys(cmtHandles).forEach(id => { if (cmtHandles[id].file === f) delete cmtHandles[id]; });
  const last = cm.lastLine() + 1;
  const byLine = {}, covered = new Set();
  cs.forEach(c => {
    const line = Math.min(Math.max(1, c.line), last);
    const end = Math.min(c.lineEnd && c.lineEnd > line ? c.lineEnd : line, last);
    (byLine[end] = byLine[end] || []).push(c);
    for (let n = line; n <= end; n++) covered.add(n);
    cmtHandles[c.id] = {file: f, doc: cm.getDoc(), h: cm.getLineHandle(line - 1),
      hEnd: end > line ? cm.getLineHandle(end - 1) : null};
  });
  covered.forEach(n => st.bg.push(cm.addLineClass(n - 1, "background", "cm-line-cmtl")));
  Object.keys(byLine).forEach(endS => {
    const end = +endS;
    const node = document.createElement("div");
    node.className = "cmts";
    node.innerHTML = byLine[end].map(cmtHtml).join("");
    node.addEventListener("click", widgetClick);
    st.widgets.push(cm.addLineWidget(end - 1, node, {}));
    dotLines.add(end);
    const dot = document.createElement("div");
    dot.className = "cmdot"; dot.textContent = "●"; dot.title = "Comments on this line";
    cm.setGutterMarker(end - 1, "gut-cmt", dot);
  });
  updatePlus();
}
// Change bars vs the PDF's sources; the gutter mark reverts that hunk.
function decorEdits(){
  const f = curFile;
  if (!f) return;
  const st = decorState[f] || (decorState[f] = {widgets: [], bg: [], edBg: []});
  st.edBg.forEach(x => { try { cm.removeLineClass(x, "background", "cm-line-edited"); } catch(e){} });
  st.edBg = [];
  cm.clearGutter("gut-edit");
  if (!fileChanged(f)) return;
  const hunks = lineDiff(P.sources[f].split("\n"), cm.getValue().split("\n"));
  hunks.forEach(h => {
    for (let l = h.bStart; l < h.bStart + h.bCount; l++)
      if (l <= cm.lastLine()) st.edBg.push(cm.addLineClass(l, "background", "cm-line-edited"));
    const mline = Math.min(h.bStart, cm.lastLine());
    const el = document.createElement("div");
    if (h.bCount){ el.className = "edbar"; el.textContent = "▎"; el.title = "Edited — click to revert this change"; }
    else { el.className = "edgap"; el.textContent = "▸"; el.title = h.aCount + " line" + (h.aCount>1?"s":"") + " deleted here — click to restore"; }
    cm.setGutterMarker(mline, "gut-edit", el);
  });
}
/** Replace `count` whole lines starting at 0-based `from` with `lines`. */
function setLines(from, count, lines){
  const lastLine = cm.lastLine();
  if (count === 0){
    if (!lines.length) return;
    if (from > lastLine)
      cm.replaceRange("\n" + lines.join("\n"), {line: lastLine, ch: cm.getLine(lastLine).length});
    else cm.replaceRange(lines.join("\n") + "\n", {line: from, ch: 0});
    return;
  }
  const to = Math.min(from + count - 1, lastLine);
  if (!lines.length){
    if (to >= lastLine && from > 0)
      cm.replaceRange("", {line: from - 1, ch: cm.getLine(from - 1).length},
                          {line: to, ch: cm.getLine(to).length});
    else cm.replaceRange("", {line: from, ch: 0}, {line: Math.min(to + 1, lastLine), ch: to >= lastLine ? cm.getLine(lastLine).length : 0});
    return;
  }
  cm.replaceRange(lines.join("\n"), {line: from, ch: 0}, {line: to, ch: cm.getLine(to).length});
}
function revertHunkAt(line0){
  const A = P.sources[curFile].split("\n");
  const hunks = lineDiff(A, cm.getValue().split("\n"));
  const h = hunks.find(h => line0 >= Math.min(h.bStart, cm.lastLine()) &&
                            line0 <= h.bStart + Math.max(h.bCount, 1) - 1);
  if (!h) return;
  setLines(h.bStart, h.bCount, A.slice(h.aStart, h.aStart + h.aCount));
  toast("Reverted to the source this PDF was built from");
}

// The comment gutter: ● where comments sit, a soft + on the cursor's line.
function updatePlus(){
  const l = cm.getCursor().line;
  if (plusLine != null && plusLine !== l && !dotLines.has(plusLine + 1)){
    try { cm.setGutterMarker(plusLine, "gut-cmt", null); } catch(e){}
  }
  plusLine = l;
  if (!dotLines.has(l + 1)){
    const el = document.createElement("div");
    el.className = "cmplus"; el.textContent = "+"; el.title = "Comment on this line (⌘M)";
    cm.setGutterMarker(l, "gut-cmt", el);
  }
}
cm.on("gutterClick", (cm_, line, gutter) => {
  if (gutter === "CodeMirror-linenumbers") forwardSync(curFile, line + 1);
  else if (gutter === "gut-cmt") openComposer(line + 1, "");
  else if (gutter === "gut-edit") revertHunkAt(line);
});

// selection -> floating Comment button (Overleaf-style); spans lines freely now
let selBtn = null;
function killSelBtn(){ if (selBtn){ selBtn.remove(); selBtn = null; } }
cm.on("cursorActivity", () => {
  updatePlus();
  killSelBtn();
  saveView();
  if (!cm.somethingSelected()) return;
  const text = cm.getSelection().replace(/\s+/g, " ").trim();
  if (!text) return;
  const from = cm.getCursor("from"), to = cm.getCursor("to");
  const co = cm.cursorCoords(from, "page");
  selBtn = document.createElement("button");
  selBtn.className = "selbtn";
  selBtn.textContent = "\u{1F4AC} Comment";
  selBtn.style.left = Math.min(Math.max(8, co.left), window.innerWidth - 120) + "px";
  selBtn.style.top = Math.max(8, co.top - 34) + "px";
  const line = from.line + 1, lineEnd = to.line > from.line ? to.line + 1 : undefined;
  selBtn.onmousedown = e2 => {
    e2.preventDefault();
    killSelBtn();
    openComposer(line, text.slice(0, 140), lineEnd);
  };
  document.body.appendChild(selBtn);
});
cm.on("scroll", () => { killSelBtn(); saveView(); });
cm.on("blur", () => setTimeout(killSelBtn, 150));

function commentAtCursor(){ openComposer(cm.getCursor().line + 1, ""); }

function openComposer(line, quote, lineEnd){
  closeComposer();
  composerAt = line;
  const anchor = Math.min(lineEnd && lineEnd > line ? lineEnd : line, cm.lastLine() + 1);
  const node = document.createElement("div");
  node.className = "cmts composer"; node.id = "composerbox";
  node.innerHTML = `<div class="cmt" style="border-left-color:var(--cmt)">
      ${quote ? `<div class="quote">&#8220;${esc(quote)}&#8221;</div>` : ""}
      <textarea id="c-text" placeholder="What should change here?"></textarea>
      <div class="acts" style="margin-top:7px">
        <button id="c-save" style="background:var(--cmt);color:#fff;border:none;font-weight:640">Comment</button>
        <button id="c-cancel">Cancel</button></div></div>`;
  node.dataset.quote = quote || "";
  node.dataset.lineEnd = lineEnd && lineEnd > line ? String(lineEnd) : "";
  composerWidget = cm.addLineWidget(anchor - 1, node, {});
  revealLine(line, false);
  $("c-text").focus();
  // every keystroke goes to storage: a publish from the other side reloads
  // this view, and the reopened page restores the composer from here
  $("c-text").oninput = () => {
    try { localStorage.setItem(CK, JSON.stringify({
      file: curFile, line,
      lineEnd: node.dataset.lineEnd ? +node.dataset.lineEnd : undefined,
      quote: node.dataset.quote || "", text: $("c-text").value})); } catch(e){}
  };
  $("c-cancel").onclick = closeComposer;
  $("c-save").onclick = () => {
    const text = $("c-text").value.trim();
    if (!text) return;
    overlay.drafts.push({
      id: "c" + Date.now().toString(36) + Math.random().toString(36).slice(2,6),
      file: curFile, line, quote: node.dataset.quote || undefined,
      lineEnd: node.dataset.lineEnd ? +node.dataset.lineEnd : undefined,
      text, author: settings().author || "", at: new Date().toISOString()});
    closeComposer();
    saveOverlay();
    refresh();
    toast("Comment added — it will be saved into the artifact automatically");
  };
}
function closeComposer(){
  if (composerWidget){ try { composerWidget.clear(); } catch(e){} composerWidget = null; }
  composerAt = null;
  try { localStorage.removeItem(CK); } catch(e){}
}

// ---- editing pipeline ------------------------------------------------------
// The editor holds the whole file, so any selection — across lines included —
// edits, deletes and retypes natively; ⌘Z is the real undo. Changes are
// captured whole-file on a short debounce.
let capT = 0;
cm.on("changes", () => {
  clearTimeout(capT);
  capT = setTimeout(captureFile, 500);
});
function captureFile(){
  clearTimeout(capT);
  const f = curFile;
  if (!f || readOnly) return;
  const txt = cm.getValue();
  if (txt === effBase(f)) delete overlay.filetexts[f];
  else overlay.filetexts[f] = {was: effBase(f), now: txt, at: new Date().toISOString()};
  try { localStorage.setItem(LS_KEY, JSON.stringify(overlay)); } catch(e){}
  scheduleAutosave();
  decorEdits();
  $("editcount").textContent = editCount();
  $("dirtylbl").hidden = !dirty();
}
// ⌘/ toggles a LaTeX % on every line the selection touches.
function toggleCommentCM(){
  if (readOnly || !editing){ toast("Editing is off"); return; }
  const from = cm.getCursor("from"), to = cm.getCursor("to");
  let a = from.line, b = to.line;
  if (b > a && to.ch === 0) b--;   // a selection ending at col 0 doesn't touch that line
  let touched = 0;
  cm.operation(() => {
    for (let n = a; n <= b; n++){
      const cur = cm.getLine(n);
      if (cur === undefined || !cur.trim()) continue;
      const m = cur.match(/^(\s*)%\s?(.*)$/);
      cm.replaceRange(m ? m[1] + m[2] : "% " + cur, {line: n, ch: 0}, {line: n, ch: cur.length});
      touched++;
    }
  });
  toast(touched ? "Toggled % on " + touched + " line" + (touched > 1 ? "s" : "") :
    "Nothing to toggle in that range");
}
// Wrap the selection in a LaTeX command: ⌘B/⌘I type the markup for you.
function wrapCM(cmd){
  if (readOnly || !editing){ toast("Editing is off"); return; }
  const picked = cm.getSelection();
  if (!picked){ toast("Select the words to wrap first"); return; }
  const lead = picked.match(/^\s*/)[0], tail = picked.match(/\s*$/)[0];
  const core = picked.slice(lead.length, picked.length - tail.length);
  if (!core) return;
  cm.replaceSelection(lead + "\\" + cmd + "{" + core + "}" + tail, "end");
}
$("m-edit").onclick = () => {
  if (readOnly && !editing){ toast("This view is read-only — Export instead"); return; }
  editing = !editing;
  $("m-edit").setAttribute("aria-pressed", String(editing));
  cm.setOption("readOnly", editing ? false : true);
  toast(editing ? "Editing the source — select anything and type" : "Editing off (selection still works)");
};

// ---- pdf pane --------------------------------------------------------------
function applyZoom(){
  P.pages.forEach((pg, i) => {
    const {w, h} = cssSize(i);
    pgEls[i].style.width = w + "px";
    const cv = pgEls[i].querySelector("canvas");
    cv.style.width = w + "px"; cv.style.height = h + "px";
  });
  $("z-lvl").textContent = Math.round(zoom * 100) + "%";
  scheduleRender();
}
function setZoom(z){
  const r = stage.scrollTop / Math.max(1, stage.scrollHeight);
  zoom = Math.min(2.5, Math.max(0.4, z));
  applyZoom();
  stage.scrollTop = r * stage.scrollHeight;
  saveView();
}
$("z-in").onclick = () => setZoom(zoom * 1.2);
$("z-out").onclick = () => setZoom(zoom / 1.2);
$("z-fit").onclick = () => setZoom((stage.clientWidth - 36) / (P.pages[0].w * 96/72));
stage.addEventListener("wheel", e => {
  if (!e.ctrlKey && !e.metaKey) return;
  e.preventDefault();
  setZoom(zoom * (e.deltaY < 0 ? 1.1 : 1/1.1));
}, {passive: false});
document.addEventListener("keydown", e => {
  if (/TEXTAREA|INPUT|SELECT/.test(e.target.tagName)) return;
  if (e.key === "+" || e.key === "=") setZoom(zoom * 1.2);
  else if (e.key === "-") setZoom(zoom / 1.2);
});
stage.addEventListener("scroll", () => {
  const mid = stage.scrollTop + stage.clientHeight * 0.4;
  let cur = 0;
  pgEls.forEach((pg, i) => { if (pg.parentElement.offsetTop <= mid) cur = i; });
  $("pgind").textContent = "p. " + (cur+1) + " / " + P.pages.length;
  scheduleRender();
  saveView();
});

// drag anywhere in the PDF pane to pan; a real click (under 5px of travel)
// still syncs to the source
let pan = null, panMoved = false;
stage.addEventListener("pointerdown", e => {
  if (e.button !== 0 || e.target.closest("button")) return;
  pan = {x: e.clientX, y: e.clientY, sl: stage.scrollLeft, st: stage.scrollTop};
  panMoved = false;
  stage.setPointerCapture(e.pointerId);
});
stage.addEventListener("pointermove", e => {
  if (!pan) return;
  const dx = e.clientX - pan.x, dy = e.clientY - pan.y;
  if (!panMoved && Math.hypot(dx, dy) < 5) return;
  panMoved = true;
  stage.classList.add("panning");
  stage.scrollLeft = pan.sl - dx;
  stage.scrollTop = pan.st - dy;
});
stage.addEventListener("pointerup", () => { pan = null; stage.classList.remove("panning"); });
stage.addEventListener("pointercancel", () => { pan = null; stage.classList.remove("panning"); });
$("pgind").textContent = "p. 1 / " + P.pages.length;

// inverse sync: click in the PDF -> jump the source pane to file:line.
// First try hbox containment (what `synctex edit` does), preferring the
// smallest box; fall back to the nearest point record in the closest
// vertical band, so two-column pages never snap across columns.
function locate(page, xbp, ybp){
  const b = P.boxes && P.boxes[page], a = P.sync[page];
  let box = -1, ba = Infinity;
  if (b && b.length){
    for (let i = 0; i < b.length; i += 6){
      const x0 = b[i+2]/10, y0 = b[i+3]/10, x1 = b[i+4]/10, y1 = b[i+5]/10;
      if (xbp < x0 || xbp > x1 || ybp < y0 || ybp > y1) continue;
      const area = (x1 - x0) * (y1 - y0);
      if (area < ba){ ba = area; box = i; }
    }
  }
  if (box >= 0 && a && a.length){
    // hboxes carry the line where the paragraph ended; the point records
    // inside the box are attributed per source line — prefer those
    const x0 = b[box+2]/10-2, y0 = b[box+3]/10-2, x1 = b[box+4]/10+2, y1 = b[box+5]/10+2;
    let best = -1, bd = Infinity;
    for (let i = 0; i < a.length; i += 4){
      const x = a[i+2]/10, y = a[i+3]/10;
      if (x < x0 || x > x1 || y < y0 || y > y1) continue;
      const d = Math.abs(y - ybp) * 4 + Math.abs(x - xbp);
      if (d < bd){ bd = d; best = i; }
    }
    if (best >= 0) return {file: P.files[a[best]], line: a[best+1]};
    return {file: P.files[b[box]], line: b[box+1]};
  }
  if (!a || !a.length) return null;
  let bestDy = Infinity;
  for (let i = 0; i < a.length; i += 4)
    bestDy = Math.min(bestDy, Math.abs(a[i+3]/10 - ybp));
  let best = -1, bd = Infinity;
  for (let i = 0; i < a.length; i += 4){
    const dy = Math.abs(a[i+3]/10 - ybp);
    if (dy > bestDy + 6) continue;
    const d = Math.abs(a[i+2]/10 - xbp) + dy * 4;
    if (d < bd){ bd = d; best = i; }
  }
  return best < 0 ? null : {file: P.files[a[best]], line: a[best+1]};
}
stage.addEventListener("click", e => {
  if (panMoved) return;          // that was a pan, not a click
  // pointer capture (set in pointerdown for panning) retargets the click to
  // the stage itself, so e.target is never inside .pg — hit-test by geometry
  let pg = e.target.closest(".pg");
  if (!pg) pg = pgEls.find(el => {
    const r = el.getBoundingClientRect();
    return e.clientX >= r.left && e.clientX <= r.right &&
           e.clientY >= r.top && e.clientY <= r.bottom;
  });
  if (!pg) return;
  const r = pg.getBoundingClientRect(), page = +pg.dataset.page;
  const xbp = (e.clientX - r.left) / r.width * P.pages[page].w;
  const ybp = (e.clientY - r.top) / r.height * P.pages[page].h;
  const loc = locate(page, xbp, ybp);
  if (!loc) return;
  drawHighlight(page, loc.file, loc.line, {x: xbp, y: ybp}, false);
  openFile(loc.file, loc.line);
});

// forward sync: highlight where file:line lands in the PDF and scroll there
function lineRows(page, file, line){
  const a = P.sync[page], fi = P.files.indexOf(file), rows = [];
  for (let i = 0; i < a.length; i += 4){
    if (a[i] !== fi || a[i+1] !== line) continue;
    const x = a[i+2]/10, y = a[i+3]/10;
    let row = rows.find(r => Math.abs(r.y - y) < 3.2);
    if (!row) rows.push(row = {y, x0: x, x1: x});
    row.x0 = Math.min(row.x0, x); row.x1 = Math.max(row.x1, x);
  }
  return rows;
}
function bestPageFor(file, line){
  const fi = P.files.indexOf(file), hits = fwd[fi] && fwd[fi][line];
  if (!hits || !hits.length) return null;
  const cnt = {};
  hits.forEach(h => cnt[h[0]] = (cnt[h[0]] || 0) + 1);
  return +Object.keys(cnt).sort((a,b) => cnt[b] - cnt[a])[0];
}
function drawHighlight(page, file, line, dotAt, scrollTo){
  pgEls.forEach(pg => pg.querySelector(".marks").innerHTML = "");
  const layer = pgEls[page].querySelector(".marks"), pg = P.pages[page];
  const rows = lineRows(page, file, line);
  rows.forEach(r => {
    const d = document.createElement("div");
    d.className = "hl";
    d.style.left = ((r.x0 - 2) / pg.w * 100) + "%";
    d.style.width = (Math.max(8, r.x1 - r.x0 + 6) / pg.w * 100) + "%";
    d.style.top = ((r.y - 9) / pg.h * 100) + "%";
    d.style.height = (11.5 / pg.h * 100) + "%";
    layer.appendChild(d);
    requestAnimationFrame(() => d.classList.add("fade"));
  });
  if (dotAt){
    const dot = document.createElement("div");
    dot.className = "dot";
    dot.style.left = (dotAt.x / pg.w * 100) + "%";
    dot.style.top = (dotAt.y / pg.h * 100) + "%";
    layer.appendChild(dot);
    setTimeout(() => dot.remove(), 2400);
  }
  if (scrollTo && rows.length){
    const el = pgEls[page], y0 = Math.min(...rows.map(r => r.y));
    stage.scrollTop = el.parentElement.offsetTop + (y0 - 60) / pg.h * el.offsetHeight;
  }
}
function forwardSync(file, line){
  const page = bestPageFor(file, line);
  if (page === null){ toast("This line produced no visible output"); return; }
  drawHighlight(page, file, line, null, true);
}

// ---- review panel ----------------------------------------------------------
function refresh(){
  decorComments();
  decorEdits();
  renderReview();
  renderDock();
  $("cmtcount").textContent = active().length;
  $("donecount").textContent = resolvedList().length;
  $("editcount").textContent = editCount();
  $("dirtylbl").hidden = !dirty();
  paintPending();
}
// A recompile asked for but not yet delivered: this build still carries the
// flag, so the PDF beside the editor is one revision behind. The builder
// strips the flag, so the badge clears itself when the rebuilt desk lands.
function paintPending(){
  const on = !!(settings().recompile);
  $("pendinglbl").hidden = !on;
  let b = $("stalebadge");
  if (on && !b){
    b = document.createElement("div");
    b.id = "stalebadge"; b.className = "stale";
    b.textContent = "⟳ Rebuild requested — this desk refreshes when the new PDF lands";
    stage.parentElement.appendChild(b);   // the pane, not the scroller: it must not scroll away
  } else if (!on && b) b.remove();
}
function renderReview(){
  const cs = active().map(liveAnchor), str = stranded();
  let h = "";
  if (!cs.length && !str.length)
    h = `<div class="hint">Select any text — across lines too — and hit <b>&#128172; Comment</b>, or press <b>&#8984;M</b> on a line, to leave a note anchored to the file and line, Overleaf-style.<br><br>
      The left pane is a real editor: select across lines, type, <b>&#8984;Z</b> undoes. <b>&#8984;/</b> toggles <b>%</b> on the selected lines, <b>&#8984;B</b>/<b>&#8984;I</b> wrap in \\textbf/\\emph. Edited lines get a purple bar — click it in the gutter to revert that change.<br><br>
      Click anywhere in the PDF to jump to its source; click a line number to light up where it lands in the PDF. Drag the PDF to pan, Ctrl/Cmd-scroll or +/&#8722; to zoom.<br><br>
      <b>Save to artifact</b> bakes comments and edits into this page as a new version, for every device and every viewer.</div>`;
  cs.forEach(c => {
    h += `<div class="cmt" data-id="${c.id}" data-file="${esc(c.file)}" data-line="${c.line}">
      <div class="loc">${esc(c.file)}:${c.line}${c.lineEnd && c.lineEnd > c.line ? "–" + c.lineEnd : ""}</div>
      ${c.quote ? `<div class="quote">&#8220;${esc(c.quote)}&#8221;</div>` : ""}
      <div class="txt">${esc(c.text)}</div>
      <div class="who">${esc(c.author || "anonymous")} · ${c.at ? c.at.slice(0,10) : ""}${c.baked ? "" : '<span class="badge">unsaved</span>'}</div>
    </div>`;
  });
  if (str.length){
    const files = [...new Set(str.map(s => s.file || "?"))].join(", ");
    h += `<div class="cmt stranded">
      <div class="loc">${esc(files)}</div>
      <div class="txt">&#9888; ${str.length} old edit${str.length > 1 ? "s" : ""} couldn't be carried onto the rewritten source. The text is kept and included in Export.</div>
    </div>`;
  }
  $("reviewbody").innerHTML = h;
}
$("reviewbody").addEventListener("click", e => {
  const card = e.target.closest(".cmt[data-file]");
  if (!card) return;
  openFile(card.dataset.file, +card.dataset.line);
  forwardSync(card.dataset.file, +card.dataset.line);
});
$("m-review").onclick = () => {
  const open = !$("review").classList.contains("open");
  $("review").classList.toggle("open", open);
  $("m-review").setAttribute("aria-pressed", String(open));
};

// ---- Done dock -------------------------------------------------------------
function renderDock(){
  const cs = resolvedList();
  let h = "";
  if (!cs.length)
    h = `<div class="hint">Nothing here yet. Resolved comments are stored in this dock instead of cluttering the source — hit <b>&#10003; Resolve</b> on a comment, or Claude marks them as it applies each one.</div>`;
  cs.forEach(c => {
    h += `<div class="cmt" data-id="${c.id}" data-file="${esc(c.file)}" data-line="${c.line}">
      <div class="loc">${esc(c.file)}:${c.line}${c.lineEnd && c.lineEnd > c.line ? "–" + c.lineEnd : ""}</div>
      ${c.quote ? `<div class="quote">&#8220;${esc(c.quote)}&#8221;</div>` : ""}
      <div class="txt">${esc(c.text)}</div>
      <div class="who">${esc(c.author || "anonymous")} · ${c.at ? c.at.slice(0,10) : ""}</div>
      <div class="acts"><button data-act="unresolve">&#8617; Restore</button></div>
    </div>`;
  });
  $("dockbody").innerHTML = h;
}
$("dockbody").addEventListener("click", e => {
  const un = e.target.closest("button[data-act='unresolve']");
  if (un){ setResolved(un.closest(".cmt").dataset.id, false); return; }
  const card = e.target.closest(".cmt[data-file]");
  if (!card) return;
  openFile(card.dataset.file, +card.dataset.line);
  forwardSync(card.dataset.file, +card.dataset.line);
});
$("m-dock").onclick = () => {
  const open = !$("dock").classList.contains("open");
  $("dock").classList.toggle("open", open);
  $("m-dock").setAttribute("aria-pressed", String(open));
};

// ---- Push: save now and flag the version so Claude applies it immediately --
$("m-push").onclick = () => {
  if (readOnly){ toast("This view is read-only"); return; }
  captureFile();
  overlay.settings = Object.assign({}, overlay.settings, {push: new Date().toISOString()});
  try { localStorage.setItem(LS_KEY, JSON.stringify(overlay)); } catch(e){}
  refresh();
  toast("Pushing — Claude will apply the stacked comments now");
  doSave(true);
};

// ---- version history ------------------------------------------------------
// Every published desk carried the sources it was built from; the build script
// collects those into one gzipped block, so this page can show what a file
// looked like at any earlier point and put it back.
let HIST = null, histSel = null;
async function loadHistory(){
  if (HIST) return HIST;
  const el = document.getElementById("history");
  const b64 = el ? el.textContent.trim() : "";
  if (!b64 || b64.charAt(0) === "_"){ HIST = {}; return HIST; }
  if (typeof DecompressionStream !== "function"){
    HIST = {}; toast("This browser can't unpack the history block"); return HIST;
  }
  try {
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const s = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
    HIST = JSON.parse(await new Response(s).text());
  } catch(e){ HIST = {}; toast("Couldn't read the version history"); }
  return HIST;
}
function ago(ts){
  const s = Math.max(0, Date.now()/1000 - ts);
  if (s < 3600) return Math.round(s/60) + "m ago";
  if (s < 86400) return Math.round(s/3600) + "h ago";
  return Math.round(s/86400) + "d ago";
}
function stamp(ts){
  const d = new Date(ts * 1000), p = n => String(n).padStart(2, "0");
  return (d.getMonth()+1) + "/" + d.getDate() + " " + p(d.getHours()) + ":" + p(d.getMinutes());
}
function renderDiff(fromText, toText){
  const A = fromText.split("\n"), B = toText.split("\n");
  const hunks = lineDiff(A, B);
  if (!hunks.length) return '<div class="hint">Identical to what is in the editor now.</div>';
  let h = '<div class="diff">';
  hunks.slice(0, 40).forEach(k => {
    h += `<span class="dsep">@@ line ${k.aStart + 1} @@</span>`;
    A.slice(k.aStart, k.aStart + k.aCount).forEach(l => {
      h += `<span class="ddel">− ${esc(l).slice(0, 400)}</span>`; });
    B.slice(k.bStart, k.bStart + k.bCount).forEach(l => {
      h += `<span class="dadd">+ ${esc(l).slice(0, 400)}</span>`; });
  });
  if (hunks.length > 40) h += `<span class="dsep">… ${hunks.length - 40} more hunks</span>`;
  return h + "</div>";
}
async function renderHist(){
  const body = $("histbody");
  $("histfile").textContent = curFile || "";
  body.innerHTML = '<div class="hint">Loading history…</div>';
  const H = await loadHistory();
  const vs = (H[curFile] || []).slice().reverse();   // newest first
  if (!vs.length){
    body.innerHTML = '<div class="hint">No earlier versions recorded for this file.</div>';
    return;
  }
  const now = cm.getValue(), nowLines = now.split("\n").length;
  let h = `<div class="hint" style="padding:2px 4px 8px">${vs.length} recorded versions, back to ${stamp(vs[vs.length-1].ts)}. Pick one to see what restoring it would change.</div>`;
  vs.forEach((v, i) => {
    const same = v.text === now;
    const d = v.text.split("\n").length - nowLines;
    h += `<div class="hv${same ? " cur" : ""}${histSel === i ? " on" : ""}" data-i="${i}">
      <div class="ht">${stamp(v.ts)}<span class="ago">${ago(v.ts)}</span></div>
      <div class="hs">${same ? "same as the editor now"
        : (d > 0 ? `<b>+${d}</b> lines` : d < 0 ? `<i>${d}</i> lines` : "same length, different text")}</div>
    </div>`;
    if (histSel === i && !same){
      h += renderDiff(now, v.text);
      h += `<div style="display:flex;gap:6px;margin:8px 0 4px">
        <button class="primary" id="h-restore" style="border:none">Restore into the editor</button>
        <button id="h-close" style="border:1px solid var(--rule2)">Close</button></div>`;
    }
  });
  body.innerHTML = h;
  const r = $("h-restore");
  if (r) r.onclick = () => {
    const v = vs[histSel];
    cm.setValue(v.text);            // a normal edit: change bars, ⌘Z and Save all apply
    captureFile(); refresh();
    toast("Restored the " + stamp(v.ts) + " version — ⌘Z undoes it, ⟳ Recompile rebuilds the PDF");
  };
  const c = $("h-close");
  if (c) c.onclick = () => { histSel = null; renderHist(); };
}
$("histbody").addEventListener("click", e => {
  const card = e.target.closest(".hv");
  if (!card || card.classList.contains("cur") || e.target.closest("button")) return;
  const i = +card.dataset.i;
  histSel = (histSel === i ? null : i);
  renderHist();
});
$("m-hist").onclick = () => {
  const open = !$("hist").classList.contains("open");
  $("hist").classList.toggle("open", open);
  $("m-hist").setAttribute("aria-pressed", String(open));
  if (open){ histSel = null; renderHist(); }
};

// ---- Recompile: save edits and flag the version so Claude rebuilds the PDF --
// Unlike Push this only asks for the source edits to be applied + recompiled;
// comments stay pending. The page live-reloads once the rebuilt desk publishes.
$("m-recompile").onclick = () => {
  if (readOnly){ toast("This view is read-only"); return; }
  captureFile();
  if (!Object.keys(overlay.filetexts || {}).length &&
      !(BAKED.filetexts && Object.keys(BAKED.filetexts).length)){
    toast("No source edits to compile yet"); return;
  }
  overlay.settings = Object.assign({}, overlay.settings, {recompile: new Date().toISOString()});
  try { localStorage.setItem(LS_KEY, JSON.stringify(overlay)); } catch(e){}
  refresh();
  toast("Recompiling — Claude applies your edits and rebuilds the PDF (a minute or two)");
  doSave(true);
};

// ---- Sync: request a rebuild from the latest repo, no local edits needed ---
// Reuses the recompile flag, so the same pending banner shows and the same
// builder-side clearing applies once the rebuilt desk publishes.
$("m-sync").onclick = () => {
  if (readOnly){ toast("This view is read-only"); return; }
  captureFile();
  overlay.settings = Object.assign({}, overlay.settings, {recompile: new Date().toISOString()});
  try { localStorage.setItem(LS_KEY, JSON.stringify(overlay)); } catch(e){}
  refresh();
  toast("Sync requested — Claude rebuilds this desk from the latest sources (a minute or two)");
  doSave(true);
};

// ---- settings --------------------------------------------------------------
$("m-set").onclick = () => {
  const s = settings();
  $("reviewbody").innerHTML = `
    <div class="field"><label for="s-author">Your name (on comments)</label>
      <input type="text" id="s-author" value="${esc(s.author || "")}"></div>
    <div class="field"><label for="s-url">Overleaf project link (optional)</label>
      <input type="text" id="s-url" placeholder="https://www.overleaf.com/project/&hellip;" value="${esc(s.overleafUrl || "")}"></div>
    <div class="hint">With a link set, an &#8220;Open in Overleaf&#8221; shortcut appears here. Overleaf can't deep-link to a line, so use the file + line this desk shows you.</div>
    ${s.overleafUrl ? `<div style="margin:6px 0"><button id="s-open" style="border:1px solid var(--rule2);background:var(--surface)">Open in Overleaf</button></div>` : ""}
    <div style="margin-top:10px;display:flex;gap:6px">
      <button class="primary" id="s-save" style="border:none">Save settings</button>
      <button id="s-back" style="border:1px solid var(--rule2)">Back</button></div>`;
  const open = $("s-open");
  if (open) open.onclick = () => window.open(settings().overleafUrl, "_blank", "noopener");
  $("s-save").onclick = () => {
    overlay.settings = Object.assign({}, overlay.settings, {
      author: $("s-author").value.trim(),
      overleafUrl: $("s-url").value.trim()});
    saveOverlay(); refresh(); toast("Settings kept");
  };
  $("s-back").onclick = renderReview;
};

// ---- persistence: bake into the artifact -----------------------------------
function rebuildHtml(newBaked){
  const css = document.getElementById("app-css").textContent;
  const payload = document.getElementById("payload").textContent;
  const histEl = document.getElementById("history");
  const hist = histEl ? histEl.textContent : "";
  const js = document.getElementById("app-js").textContent;
  const bakedJson = JSON.stringify(newBaked).replace(/</g, "\\u003c");
  const S = "script";
  const cdn = ["https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/codemirror.min.js",
               "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/mode/stex/stex.min.js"]
    .map(u => "<" + S + " src=\"" + u + "\"></" + S + ">").join("");
  return "<!doctype html>\n<html><head><meta charset=\"utf-8\">" +
    "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
    "<title>Adaptive Agent PDF Desk</title>" +
    "<style id=\"app-css\">" + css + "</style></head><body>" +
    "<div id=\"app\"></div>" +
    "<" + S + " id=\"payload\" type=\"application/json\">" + payload + "</" + S + ">" +
    "<" + S + " id=\"baked\" type=\"application/json\">" + bakedJson + "</" + S + ">" +
    (hist ? "<" + S + " id=\"history\" type=\"text/plain\">" + hist + "</" + S + ">" : "") +
    cdn +
    "<" + S + " id=\"app-js\">" + js + "</" + S + "></body></html>";
}
function goReadOnly(msg){
  readOnly = true;
  cm.setOption("readOnly", true);
  $("m-save").disabled = true;
  $("m-save").title = msg;
  toast(msg);
}
let autosaveTimer = null;
function scheduleAutosave(delay){
  if (readOnly) return;
  clearTimeout(autosaveTimer);
  autosaveTimer = setTimeout(() => {
    if (readOnly || !dirty()) return;
    // never publish out from under an open composer or a focused text field —
    // but the editor's own hidden textarea doesn't count, or an idle cursor
    // left in the source would block auto-save forever
    const ae = document.activeElement || {};
    const inCM = ae.closest && ae.closest(".CodeMirror");
    if ($("composerbox") || (!inCM && /TEXTAREA|INPUT/.test(ae.tagName || ""))){
      scheduleAutosave(4000); return;
    }
    doSave(true);
  }, delay == null ? 8000 : delay);
}
$("m-save").onclick = () => doSave(false);
async function doSave(auto){
  if (readOnly){ if (!auto) toast("This view is read-only — Export instead"); return; }
  captureFile();   // flush the keystrokes still inside the debounce window
  if (!dirty()){ if (!auto) toast("Nothing new to save"); return; }
  // resolve the self capability: prefer the member, fall back to use('self')
  // which waits for the runtime to finish initialising
  let selfNs = window.claude && window.claude.self;
  if ((!selfNs || !selfNs.publish) && window.claude && window.claude.use){
    try { selfNs = await window.claude.use("self"); } catch(e){}
  }
  if (!selfNs || !selfNs.publish){
    toast("Saving isn't available in this view — use Export to keep the comments");
    return;
  }
  const merged = {
    comments: comments().map(c => { const {baked, ...rest} = liveAnchor(c); return rest; }),
    filetexts: {},
    settings: settings()};
  P.files.forEach(f => {
    const cur = currentText(f);
    if (cur !== P.sources[f])
      merged.filetexts[f] = {was: P.sources[f], now: cur, at: new Date().toISOString()};
  });
  const str = stranded();
  if (str.length) merged.stranded = str;
  const html = rebuildHtml(merged);
  $("m-save").disabled = true;
  $("m-save").textContent = "Saving…";
  try {
    await selfNs.publish(html);
    // Clear only AFTER the publish landed — clearing first meant a reload
    // arriving mid-save (someone else published) destroyed the only copy.
    // The boot-time prune keeps a leftover overlay from double-rendering.
    try { localStorage.removeItem(LS_KEY); } catch(e){}
    // this view reloads to the new version
  } catch (err){
    $("m-save").disabled = false;
    $("m-save").textContent = "Save to artifact";
    const code = (err && err.code) || "unknown";
    if (code === "conflict"){ /* shell reloads to the winner */ }
    else if (["not_writer","not_granted","not_declared","capability_disabled",
              "capability_removed","consent_required"].includes(code))
      goReadOnly("This view can't write the artifact (" + code + ") — comments stay in this browser; Export to hand them off");
    else if (code === "too_large") toast("Too large to save (" + code + ") — Export the comments instead");
    else if (code === "rate_limited"){
      if (auto) scheduleAutosave(30000);
      else toast("Saving too often — wait a moment, then save once");
    }
    else {
      if (auto) scheduleAutosave(30000);
      else toast("Save failed (" + code + ") — try once more in a few seconds");
    }
  }
}

// ---- export ----------------------------------------------------------------
$("m-export-top").onclick = () => $("m-export").onclick();
$("m-export").onclick = async () => {
  captureFile();
  const cs = comments().map(liveAnchor);
  const changed = P.files.filter(fileChanged);
  const str = stranded();
  if (!cs.length && !changed.length && !str.length){ toast("Nothing to export yet"); return; }
  let md = "# PDF Desk — review notes\n\n", lastF = null;
  md += "## Comments (" + cs.length + ")\n\n";
  if (!cs.length) md += "_None._\n";
  cs.forEach(c => {
    if (c.file !== lastF){ md += "\n**" + c.file + "**\n\n"; lastF = c.file; }
    md += "- **" + c.file + ":" + c.line + (c.lineEnd && c.lineEnd > c.line ? "–" + c.lineEnd : "") + "**" +
      (c.quote ? " (“" + c.quote + "”)" : "") + " — " +
      c.text.replace(/\n/g, " ") + (c.author ? "  _(" + c.author + ")_" : "") +
      (c.resolved ? "  _(resolved)_" : "") + "\n";
  });
  // Source edits export as exact hunks against the .tex this PDF was built
  // from, so they apply without anyone re-reading the prose.
  md += "\n## Source edits (" + changed.length + " file" + (changed.length === 1 ? "" : "s") + ")\n\n";
  if (!changed.length) md += "_None._\n";
  changed.forEach(f => {
    const A = P.sources[f].split("\n"), B = currentText(f).split("\n");
    md += "\n### " + f + "\n\n";
    lineDiff(A, B).forEach(h => {
      md += h.aCount
        ? "**Lines " + (h.aStart + 1) + (h.aCount > 1 ? "–" + (h.aStart + h.aCount) : "") + "**\n\n"
        : "**Insert after line " + h.aStart + "**\n\n";
      if (h.aCount) md += "Was:\n\n```latex\n" + A.slice(h.aStart, h.aStart + h.aCount).join("\n") + "\n```\n\n";
      md += h.bCount
        ? "Now:\n\n```latex\n" + B.slice(h.bStart, h.bStart + h.bCount).join("\n") + "\n```\n\n"
        : "Now: _deleted_\n\n";
    });
  });
  if (str.length){
    md += "\n## Stranded edits (source moved underneath them)\n\n";
    str.forEach(s => {
      md += "### " + (s.file || "?") + "\n\n";
      if (s.was != null || s.del != null) md += "- **Was**: `" + (s.was || "") + "`\n";
      md += s.now != null && s.now.indexOf("\n") < 0
        ? "- **Wanted**: `" + s.now + "`\n\n"
        : "Full text as edited:\n\n```latex\n" + (s.now || "") + "\n```\n\n";
    });
  }
  let dl = null;
  if (window.claude && window.claude.use){
    try { dl = await window.claude.use("downloads"); } catch(e){}
  }
  if (dl && dl.save){
    try {
      await dl.save({filename: "pdf-desk-review.md", data: md});
      toast("Offered as a download");
      return;
    } catch(e){}
  }
  (navigator.clipboard ? navigator.clipboard.writeText(md) : Promise.reject())
    .then(() => toast("Review notes copied as Markdown"), () => toast("Copy failed"));
};

// ---- divider drag ----------------------------------------------------------
(function(){
  const div = $("divider"), left = document.querySelector(".left");
  let dragging = false;
  div.addEventListener("pointerdown", e => {
    dragging = true; div.classList.add("drag"); div.setPointerCapture(e.pointerId);
  });
  div.addEventListener("pointermove", e => {
    if (!dragging) return;
    const w = Math.min(window.innerWidth - 320, Math.max(240, e.clientX));
    left.style.width = w + "px";
    cm.refresh();
  });
  div.addEventListener("pointerup", () => { dragging = false; div.classList.remove("drag"); });
})();

// ---- utils & boot ----------------------------------------------------------
let toastT;
function toast(msg){
  const t = $("toast");
  t.textContent = msg; t.classList.add("on");
  clearTimeout(toastT); toastT = setTimeout(() => t.classList.remove("on"), 2600);
}
applyZoom();
const bootView = loadView();
if (bootView && bootView.zoom) setZoom(bootView.zoom); else $("z-fit").click();
openFile(bootView && P.files.includes(bootView.file) ? bootView.file
  : (P.files.includes("sections/Findings.tex") ? "sections/Findings.tex" : P.files[0]));
refresh();
if (bootView) requestAnimationFrame(() => {
  if (bootView.cur && typeof bootView.cur.line === "number")
    try { cm.setCursor(bootView.cur, null, {scroll: false}); } catch(e){}
  if (typeof bootView.edTop === "number") cm.scrollTo(bootView.edLeft || 0, bootView.edTop);
  if (typeof bootView.st === "number") stage.scrollTop = bootView.st;
  if (typeof bootView.sl === "number") stage.scrollLeft = bootView.sl;
});
// A comment was mid-typing when the page last unloaded: reopen the composer
// with the text intact (a publish-triggered reload must not eat it).
try {
  const savedC = JSON.parse(localStorage.getItem(CK) || "null");
  if (savedC && savedC.text && P.files.includes(savedC.file)){
    openFile(savedC.file);
    openComposer(savedC.line, savedC.quote || "", savedC.lineEnd);
    const ta = $("c-text");
    if (ta){
      ta.value = savedC.text;
      ta.dispatchEvent(new Event("input"));   // re-arm the CK record
      toast("Restored the comment you were writing");
    }
  }
} catch(e){}
})();
