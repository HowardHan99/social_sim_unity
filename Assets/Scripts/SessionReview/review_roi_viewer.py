#!/usr/bin/env python3
import json
import sys
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, ttk


class RoiViewer:
    def __init__(self, root: tk.Tk, initial_path: Path | None) -> None:
        self.root = root
        self.root.title("Review ROI Viewer")
        self.root.geometry("1280x840")
        self.default_root = find_default_export_root()

        self.data = None
        self.export_dir: Path | None = None
        self.photo = None
        self.image_size = (1, 1)
        self.image_available = False
        self.session_paths: list[Path] = []

        self.show_image = tk.BooleanVar(value=True)
        self.show_bounds = tk.BooleanVar(value=True)
        self.show_objects = tk.BooleanVar(value=True)
        self.show_agent_paths = tk.BooleanVar(value=True)
        self.show_inside_only = tk.BooleanVar(value=False)
        self.show_labels = tk.BooleanVar(value=False)

        self._build_ui()
        if initial_path is not None:
            self.load_path(initial_path)

    def _build_ui(self) -> None:
        self.root.columnconfigure(0, weight=0)
        self.root.columnconfigure(1, weight=1)
        self.root.rowconfigure(0, weight=1)

        controls = ttk.Frame(self.root, padding=12)
        controls.grid(row=0, column=0, sticky="ns")

        ttk.Label(controls, text="Review ROI Viewer", font=("Segoe UI", 14, "bold")).grid(row=0, column=0, sticky="w")
        ttk.Button(controls, text="Open export...", command=self.open_export).grid(row=1, column=0, sticky="ew", pady=(10, 4))
        ttk.Button(controls, text="Reload", command=self.reload_current).grid(row=2, column=0, sticky="ew", pady=(0, 12))

        ttk.Label(controls, text="Sessions").grid(row=3, column=0, sticky="w")
        self.session_list = tk.Listbox(controls, width=38, height=10)
        self.session_list.grid(row=4, column=0, sticky="nsew", pady=(4, 12))
        self.session_list.bind("<<ListboxSelect>>", self.on_session_selected)

        ttk.Checkbutton(controls, text="Show image overlay", variable=self.show_image, command=self.redraw).grid(row=5, column=0, sticky="w")
        ttk.Checkbutton(controls, text="Show ROI bounds", variable=self.show_bounds, command=self.redraw).grid(row=6, column=0, sticky="w")
        ttk.Checkbutton(controls, text="Show objects", variable=self.show_objects, command=self.redraw).grid(row=7, column=0, sticky="w")
        ttk.Checkbutton(controls, text="Show agent paths", variable=self.show_agent_paths, command=self.redraw).grid(row=8, column=0, sticky="w")
        ttk.Checkbutton(controls, text="Inside ROI samples only", variable=self.show_inside_only, command=self.redraw).grid(row=9, column=0, sticky="w")
        ttk.Checkbutton(controls, text="Show labels", variable=self.show_labels, command=self.redraw).grid(row=10, column=0, sticky="w")

        self.summary = tk.Text(controls, width=38, height=28, wrap="word")
        self.summary.grid(row=11, column=0, sticky="nsew", pady=(12, 0))
        self.summary.configure(state="disabled")
        controls.rowconfigure(4, weight=1)
        controls.rowconfigure(11, weight=1)

        canvas_frame = ttk.Frame(self.root, padding=(0, 12, 12, 12))
        canvas_frame.grid(row=0, column=1, sticky="nsew")
        canvas_frame.columnconfigure(0, weight=1)
        canvas_frame.rowconfigure(0, weight=1)

        self.canvas = tk.Canvas(canvas_frame, background="#101418", highlightthickness=0)
        self.canvas.grid(row=0, column=0, sticky="nsew")
        self.canvas.bind("<Configure>", lambda _event: self.redraw())

        self.refresh_session_list()

    def open_export(self) -> None:
        selected = filedialog.askopenfilename(
            title="Select review_roi_export.json",
            initialdir=str(self.default_root if self.default_root.exists() else Path.cwd()),
            filetypes=[("ROI export JSON", "review_roi_export.json"), ("JSON files", "*.json"), ("All files", "*.*")],
        )
        if selected:
            self.load_path(Path(selected))

    def reload_current(self) -> None:
        if self.export_dir is not None:
            self.load_path(self.export_dir)

    def load_path(self, path: Path) -> None:
        path = path.expanduser().resolve()
        if path.is_dir():
            json_path = path / "review_roi_export.json"
        else:
            json_path = path
            path = path.parent

        if not json_path.exists():
            self._set_summary(f"Could not find export JSON at:\n{json_path}")
            return

        with json_path.open("r", encoding="utf-8") as handle:
            self.data = json.load(handle)

        self.export_dir = path
        self._load_image()
        self.refresh_session_list()
        self._select_current_session()
        self._set_summary(self._build_summary())
        self.redraw()

    def refresh_session_list(self) -> None:
        root = self.default_root
        self.session_paths = sorted(root.glob("*/review_roi_export.json")) if root.exists() else []

        self.session_list.delete(0, tk.END)
        for json_path in self.session_paths:
            label = json_path.parent.name
            self.session_list.insert(tk.END, label)

    def _select_current_session(self) -> None:
        if self.export_dir is None:
            return

        current_json = self.export_dir / "review_roi_export.json"
        for index, json_path in enumerate(self.session_paths):
            if json_path == current_json:
                self.session_list.selection_clear(0, tk.END)
                self.session_list.selection_set(index)
                self.session_list.see(index)
                break

    def on_session_selected(self, _event=None) -> None:
        selection = self.session_list.curselection()
        if not selection:
            return

        index = selection[0]
        if 0 <= index < len(self.session_paths):
            self.load_path(self.session_paths[index])

    def _load_image(self) -> None:
        self.photo = None
        self.image_available = False
        self.image_size = (1, 1)

        if not self.data:
            return

        image_info = self.data.get("image")
        if not image_info:
            return

        image_path = self.export_dir / image_info.get("fileName", "")
        if not image_path.exists():
            return

        try:
            self.photo = tk.PhotoImage(file=str(image_path))
            self.image_size = (self.photo.width(), self.photo.height())
            self.image_available = True
        except tk.TclError:
            self.image_available = False

    def _set_summary(self, text: str) -> None:
        self.summary.configure(state="normal")
        self.summary.delete("1.0", tk.END)
        self.summary.insert("1.0", text)
        self.summary.configure(state="disabled")

    def _build_summary(self) -> str:
        if not self.data:
            return "No export loaded."

        bounds = self.data["bounds"]
        objects = self.data.get("objects", [])
        agents = self.data.get("agents", [])
        image_state = "yes" if self.image_available else "no"
        unique_paths = len({obj.get("hierarchyPath", obj.get("name", "")) for obj in objects})

        return (
            f"Folder: {self.export_dir}\n\n"
            f"Scene: {self.data.get('sceneName', '-')}\n"
            f"Trial: {self.data.get('trialName', '-')} #{self.data.get('trialNumber', '-')}\n"
            f"Timestamp: {self.data.get('exportTimestamp', '-')}\n\n"
            f"Bounds\n"
            f"  X: {bounds['minX']:.2f} .. {bounds['maxX']:.2f}\n"
            f"  Z: {bounds['minZ']:.2f} .. {bounds['maxZ']:.2f}\n"
            f"  Size: {bounds['sizeXZ']['x']:.2f} x {bounds['sizeXZ']['y']:.2f}\n\n"
            f"Objects: {len(objects)}\n"
            f"Unique hierarchy entries: {unique_paths}\n"
            f"Agents: {len(agents)}\n"
            f"Image available: {image_state}\n\n"
            f"Tips\n"
            f"  Toggle the PNG overlay on/off.\n"
            f"  Enable labels if you need names.\n"
            f"  Use inside-only to focus on in-ROI trajectory samples."
        )

    def redraw(self) -> None:
        self.canvas.delete("all")
        if not self.data:
            return

        width = max(self.canvas.winfo_width(), 200)
        height = max(self.canvas.winfo_height(), 200)
        bounds = self.data["bounds"]
        min_x = float(bounds["minX"])
        max_x = float(bounds["maxX"])
        min_z = float(bounds["minZ"])
        max_z = float(bounds["maxZ"])

        world_w = max(max_x - min_x, 0.001)
        world_h = max(max_z - min_z, 0.001)
        pad = 32

        if self.show_image.get() and self.image_available and self.photo is not None:
            display_w = self.image_size[0]
            display_h = self.image_size[1]
            offset_x = max(0.0, (width - display_w) * 0.5)
            offset_y = max(0.0, (height - display_h) * 0.5)
            scale_x = display_w / world_w
            scale_y = display_h / world_h
        else:
            scale = min((width - pad * 2) / world_w, (height - pad * 2) / world_h)
            offset_x = (width - world_w * scale) * 0.5
            offset_y = (height - world_h * scale) * 0.5
            scale_x = scale
            scale_y = scale

        def to_canvas(x: float, z: float) -> tuple[float, float]:
            px = offset_x + (x - min_x) * scale_x
            py = offset_y + (max_z - z) * scale_y
            return px, py

        left, top = to_canvas(min_x, max_z)
        right, bottom = to_canvas(max_x, min_z)

        if self.show_image.get() and self.image_available and self.photo is not None:
            self.canvas.create_image(left, top, anchor="nw", image=self.photo)

        if self.show_objects.get():
            labeled_paths = set()
            for obj in self.data.get("objects", []):
                points = obj.get("footprintXZ", [])
                if len(points) >= 3:
                    polygon = []
                    for point in points:
                        px, py = to_canvas(float(point["x"]), float(point["z"]))
                        polygon.extend([px, py])
                    self.canvas.create_polygon(
                        polygon,
                        fill="#365f58" if obj.get("isGroup") else "#6fb7a0",
                        outline="#7bf0d0" if obj.get("isGroup") else "#8fe1c8",
                        width=2 if obj.get("isGroup") else 1,
                    )
                if self.show_labels.get():
                    object_path = obj.get("hierarchyPath", obj.get("name", "?"))
                    if object_path in labeled_paths:
                        continue
                    labeled_paths.add(object_path)

                    category_path = obj.get("categoryPath", "")
                    object_name = obj.get("objectName") or obj.get("name", "?")
                    label = f"{category_path}/{object_name}" if category_path else object_name
                    label_x, label_y = to_canvas(float(obj["center"]["x"]), float(obj["center"]["z"]))
                    self.canvas.create_text(label_x + 4, label_y - 4, text=label, anchor="sw", fill="#d5f7ea")

        if self.show_agent_paths.get():
            colors = ["#ff5f56", "#37c978", "#4da3ff", "#f2c14e", "#d277ff", "#ff8c42"]
            for index, agent in enumerate(self.data.get("agents", [])):
                samples = agent.get("samplesInsideBounds") if self.show_inside_only.get() else agent.get("samples")
                if not samples:
                    continue

                coords = []
                for sample in samples:
                    px, py = to_canvas(float(sample["position"]["x"]), float(sample["position"]["z"]))
                    coords.extend([px, py])

                color = colors[index % len(colors)]
                if len(coords) >= 4:
                    self.canvas.create_line(*coords, fill=color, width=2, smooth=False)

                start = agent.get("startPosition")
                end = agent.get("endPosition")
                if start:
                    sx, sy = to_canvas(float(start["x"]), float(start["z"]))
                    self.canvas.create_oval(sx - 4, sy - 4, sx + 4, sy + 4, fill=color, outline="")
                if end:
                    ex, ey = to_canvas(float(end["x"]), float(end["z"]))
                    self.canvas.create_rectangle(ex - 4, ey - 4, ex + 4, ey + 4, fill=color, outline="")
                if self.show_labels.get():
                    label = agent.get("displayName") or agent.get("objectId", "?")
                    lx, ly = to_canvas(float(agent["endPosition"]["x"]), float(agent["endPosition"]["z"]))
                    self.canvas.create_text(lx + 6, ly - 6, text=label, anchor="sw", fill=color)

        if self.show_bounds.get():
            self.canvas.create_rectangle(left, top, right, bottom, outline="#00e5ff", width=3)
            self.canvas.create_text(left + 8, top + 8, anchor="nw", fill="#00e5ff", text="ROI bounds")


def parse_initial_path() -> Path | None:
    if len(sys.argv) > 1:
        return Path(sys.argv[1])

    default_root = find_default_export_root()
    if default_root.exists():
        exports = sorted(default_root.glob("*/review_roi_export.json"))
        if exports:
            return exports[-1]
    return None


def find_default_export_root() -> Path:
    script_path = Path(__file__).resolve()
    if script_path.parent.name.lower() == "tools" and script_path.parent.parent.name == "ReviewExports":
        return script_path.parent.parent

    return Path.cwd() / "SessionLogs" / "ReviewExports"


def main() -> None:
    root = tk.Tk()
    viewer = RoiViewer(root, parse_initial_path())
    if viewer.data is None:
        viewer._set_summary("Open a `review_roi_export.json` file or pass a path on the command line.")
    root.mainloop()


if __name__ == "__main__":
    main()
