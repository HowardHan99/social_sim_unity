# HRI Simulation Paper Desk - rebuild toolchain

Desk artifact: https://claude.ai/code/artifact/549bfdd4-73df-4dea-97db-01f62b844a33
(port of the Adaptive Agent PDF Desk app; capabilities: artifact + downloads)

To rebuild after paper edits (the desk's "Recompile" flow):
1. WebFetch the artifact FIRST and pull its `#baked` JSON (comments/filetexts) and
   `#history` block - a rebuild must CARRY THEM FORWARD, not reset them:
   apply baked.filetexts to the repo sources, append the previous sources to the
   history gzip ({file:[{ts,text}...]}, gzip+base64; "_" = empty).
2. Compile: copy repo main.tex/software.bib/Figures/ + Template/ACM-Reference-Format.bst
   + this folder's acmart.cls into a build dir, then
   `tectonic.exe --synctex --keep-intermediates main.tex`
   (acmart.cls here was generated from CTAN dtx via `tectonic --pass tex acmart.ins`
   because tectonic's bundled acmart is too old for \setcopyright{cc}).
3. `py build_desk.py` (expects tex/build/{main.pdf,main.synctex.gz} relative cwd;
   edit paths at top). It parses synctex (sp->bp = /65781.76, y from page top;
   sync = [fileIdx,line,x*10,y*10] per point record k/g/$/h; boxes = 6-tuples from
   hbox '(' records, top=y-H bottom=y+D), renders pages 300dpi JPEG q85 via pymupdf,
   and assembles the HTML mirroring the app's own rebuildHtml().
4. Republish SAME file path (or url=549bfdd4...) - do NOT pass capabilities
   (omitting carries {artifact,downloads} forward).

desk_app.js here already carries two local patches vs the Adaptive Agent original:
title string in rebuildHtml, and Export using claude.use("downloads").


## Scenario schematic (Figures/scenarios.pdf)
`py schematic_fig3.py --install` regenerates the vector figure from scene_obstacles.json (kept here) and copies it into the repo Figures/. Layout is solved by hand from the crop aspect ratios so the middle column lines up with S1/S4; legend lives inside S2.
