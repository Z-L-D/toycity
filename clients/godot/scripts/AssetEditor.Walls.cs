using Godot;
using ToyCity.ClientModel.Voxels;
using ToyCity.ClientModel.Voxels.Assets;
using SVector3 = System.Numerics.Vector3;

namespace ToyCity.Godot;

/// <summary>
/// The wall tool (RC part 5): polygon walls drawn corner to corner, windows and doors put into them, and finishes
/// painted onto any shape. Walls are smooth and textured in the game; their voxels, in their finishes' colours, are
/// kept for when the building is wrecked.
/// </summary>
public partial class AssetEditor
{
    enum WallMode { Draw, Part, TakeOut, Paint }

    PanelContainer _wallPanel = null!;
    OptionButton _wallMode = null!, _wallFinish = null!, _partKind = null!, _frameFinish = null!;
    HSlider _wallHeight = null!, _wallThick = null!;
    Label _wallHeightLabel = null!, _wallThickLabel = null!, _wallHint = null!;
    SVector3? _wallFrom, _wallFirst;
    (SVector3, SVector3?, int, double, double)? _wallGhostKey;

    /// <summary>The finishes in the pickers, in the order listed: none (the colour chosen) first.</summary>
    static readonly string[] FinishIds = Finishes.List.Select(f => f.Id).Prepend("").ToArray();

    WallMode CurrentWallMode => (WallMode)_wallMode.GetSelectedId();
    string WallFinishNow => FinishIds[Math.Max(0, _wallFinish.Selected)];

    void BuildWallPanel(Control root)
    {
        _wallPanel = Glass(root);
        _wallPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.Minsize, 10);
        _wallPanel.OffsetLeft = 272;
        _wallPanel.OffsetBottom = -48;
        _wallPanel.GrowVertical = Control.GrowDirection.Begin;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _wallPanel.AddChild(col);
        col.AddChild(UiTheme.Heading("Walls"));
        _wallMode = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "What a click does" };
        _wallMode.AddItem("Draw walls", (int)WallMode.Draw);
        _wallMode.AddItem("Put in a window or door", (int)WallMode.Part);
        _wallMode.AddItem("Take one out", (int)WallMode.TakeOut);
        _wallMode.AddItem("Paint a finish", (int)WallMode.Paint);
        _wallMode.ItemSelected += _ => WallSettingsChanged();
        col.AddChild(_wallMode);
        var finRow = new HBoxContainer();
        col.AddChild(finRow);
        finRow.AddChild(new Label { Text = "Finish", CustomMinimumSize = new Vector2(70, 0) });
        _wallFinish = FinishPicker("What the walls are made of, or what Paint puts on");
        _wallFinish.Select(Array.IndexOf(FinishIds, "brick-red"));
        finRow.AddChild(_wallFinish);
        var hRow = new HBoxContainer();
        col.AddChild(hRow);
        _wallHeightLabel = new Label { CustomMinimumSize = new Vector2(110, 0) };
        hRow.AddChild(_wallHeightLabel);
        _wallHeight = new HSlider { MinValue = 0.5, MaxValue = 15, Step = 0.25, Value = 6.2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(160, 0) };
        _wallHeight.ValueChanged += _ => WallSettingsChanged();
        hRow.AddChild(_wallHeight);
        var tRow = new HBoxContainer();
        col.AddChild(tRow);
        _wallThickLabel = new Label { CustomMinimumSize = new Vector2(110, 0) };
        tRow.AddChild(_wallThickLabel);
        _wallThick = new HSlider { MinValue = 0.1, MaxValue = 0.6, Step = 0.05, Value = 0.3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _wallThick.ValueChanged += _ => WallSettingsChanged();
        tRow.AddChild(_wallThick);
        var pRow = new HBoxContainer();
        col.AddChild(pRow);
        pRow.AddChild(new Label { Text = "Part", CustomMinimumSize = new Vector2(70, 0) });
        _partKind = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "What goes in when you click a wall" };
        foreach (var (k, name) in Parts.List) _partKind.AddItem(name, (int)k);
        _partKind.ItemSelected += _ => WallSettingsChanged();
        pRow.AddChild(_partKind);
        var frRow = new HBoxContainer();
        col.AddChild(frRow);
        frRow.AddChild(new Label { Text = "Frames", CustomMinimumSize = new Vector2(70, 0) });
        _frameFinish = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "The windows' and doors' frames and glazing bars" };
        foreach (string id in new[] { "trim-white", "trim-black", "wood-planks", "metal-seam", "copper" }) _frameFinish.AddItem(Finishes.Get(id)!.Name);
        frRow.AddChild(_frameFinish);
        var opts = new HBoxContainer();
        col.AddChild(opts);
        Btn(opts, "Brick house", "Load the polygon example: a brick house of walls, windows and doors, textured", () =>
        {
            _doc = DemoAsset.PolyHouse(4);
            _name.Text = _doc.Name;
            Frame(); Refresh();
            Say("The brick house: walls, windows and doors as polygons, with their voxels kept for when it's wrecked");
        });
        var models = new HBoxContainer();
        col.AddChild(models);
        Btn(models, "Import a model…", "Put in a model made in a modelling program (.glb, .gltf or .obj), with its textures: drawn as it is, voxels when wrecked", () =>
            PickModel(ImportModelFile));
        Btn(models, "Export .glb", "Save the walls, shapes and roof as a glTF model, textures and all, for a modelling program", () =>
        {
            Pick(FileDialog.FileModeEnum.SaveFile, FileDialog.AccessEnum.Filesystem, null, ExportGlb);
            _dialog!.Filters = new[] { "*.glb ; glTF models" };
            _dialog.CurrentFile = System.IO.Path.ChangeExtension(_dialog.CurrentFile, ".glb");
        });
        Btn(opts, "Stop drawing", "End the run of walls (Esc)", () => { _wallFrom = _wallFirst = null; _wallGhostKey = null; WallSettingsChanged(); });
        _wallHint = new Label { Modulate = UiTheme.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        col.AddChild(_wallHint);
        _wallPanel.Visible = false;
        WallSettingsChanged();
    }

    /// <summary>A dropdown of every finish, grouped, with "Colour" (none) first.</summary>
    static OptionButton FinishPicker(string tip)
    {
        var o = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = tip, CustomMinimumSize = new Vector2(200, 0) };
        foreach (string id in FinishIds)
        {
            if (id.Length == 0) { o.AddItem("The colour chosen (no finish)"); continue; }
            var (_, name, group) = Finishes.List.First(f => f.Id == id);
            o.AddItem($"{name} · {group.ToLowerInvariant()}");
            var tex = FinishTextures.Of(id);
            if (tex is { } t) o.SetItemIcon(o.ItemCount - 1, Small(t.Albedo));
        }
        return o;
    }

    static readonly Dictionary<ImageTexture, ImageTexture> SmallIcons = new();
    static ImageTexture Small(ImageTexture big)
    {
        if (SmallIcons.TryGetValue(big, out var s)) return s;
        var img = big.GetImage();
        img.Resize(16, 16, Image.Interpolation.Lanczos);
        return SmallIcons[big] = ImageTexture.CreateFromImage(img);
    }

    void WallSettingsChanged()
    {
        _wallHeightLabel.Text = $"Height {_wallHeight.Value:0.##} m";
        _wallThickLabel.Text = $"Thickness {_wallThick.Value * 100:0} cm";
        _wallHint.Text = CurrentWallMode switch
        {
            WallMode.Draw => _wallFrom == null
                ? "Click a corner, then the next: walls run corner to corner on 25 cm steps. Go round clockwise seen from above so their fronts face out. Click on top of a wall to build up from it."
                : "Click the next corner; Esc or Stop drawing ends the run. Clicking the first corner closes it.",
            WallMode.Part => "Click a wall where the window or door goes: on the upper floor for one up there. It goes in from the side you click.",
            WallMode.TakeOut => "Click a wall by a window or door to take it out and fill its opening.",
            _ => "Click any shape to give it the finish chosen, or the colour chosen for none.",
        };
        _wallGhostKey = null;
    }

    /// <summary>Where the pointer is for the wall tool, in metres: in front of what it's over, on 25 cm steps across.</summary>
    SVector3? WallPoint()
    {
        if (ShapeAt() is not { } p) return null;
        return new SVector3(MathF.Round(p.X * 4) / 4, MathF.Round(p.Y * 4) / 4, p.Z);
    }

    Shape? WallFromTo(SVector3 a, SVector3 b)
    {
        if (SVector3.Distance(new(a.X, a.Y, 0), new(b.X, b.Y, 0)) < 0.2f) return null;
        string fin = WallFinishNow;
        var w = Parts.Wall(a.X, a.Y, b.X, b.Y, a.Z, _wallHeight.Value, _wallThick.Value, fin);
        if (fin.Length == 0) w.Colour = _colour;
        return w;
    }

    /// <summary>The wall that would go in, ghosted from the last corner to the pointer.</summary>
    void ShowWallGhost()
    {
        var at = CurrentWallMode == WallMode.Draw ? WallPoint() : null;
        _ghost.Position = Vector3.Zero;
        if (at is not { } p || _wallFrom is not { } from) { _ghost.Visible = false; return; }
        var key = (p, _wallFrom, (int)_colour, _wallHeight.Value, _wallThick.Value);
        if (_wallGhostKey != key)
        {
            _wallGhostKey = key;
            var w = WallFromTo(from, p);
            _ghost.Mesh = w != null ? BuildingModels.ToMesh(w.Mesh(1f, 1, _doc.Model.Palette)) : null;
        }
        _ghost.Visible = _ghost.Mesh != null;
    }

    /// <summary>The point in metres at the middle of the voxel the pointer is on, for picking a shape.</summary>
    SVector3? PointedAt() => _hit is { Ground: false } h ? new SVector3((h.X + 0.5f) * Vs, (h.Y + 0.5f) * Vs, (h.Z + 0.5f) * Vs) : null;

    void WallPress()
    {
        switch (CurrentWallMode)
        {
            case WallMode.Draw when WallPoint() is { } p:
                if (_wallFrom is not { } from) { _wallFrom = _wallFirst = p; Say("Now click the next corner"); break; }
                // Each corner of a run stands at the height of the first.
                p.Z = from.Z;
                if (WallFromTo(from, p) is not { } w) break;
                _doc.AddShape(w);
                bool closed = _wallFirst is { } first && MathF.Abs(first.X - p.X) < 0.01f && MathF.Abs(first.Y - p.Y) < 0.01f;
                _wallFrom = closed ? null : p;
                if (closed) _wallFirst = null;
                Say($"A wall {w.Width:0.##} m long{(closed ? "; that closes the run" : "")}");
                RoofChanged();
                break;
            case WallMode.Part when PointedAt() is { } at:
            {
                if (_doc.ShapeAt(at.X, at.Y, at.Z, IsMainWall) is not { } wall) { Say("Click a wall"); break; }
                var kind = (PartKind)_partKind.GetSelectedId();
                var (lx, ly, lz) = wall.Local(at.X, at.Y, at.Z);
                var (pw, _, sill) = Parts.SizeOf(kind);
                // On the floor clicked: a storey every 3 m.
                double floor = Math.Floor(Math.Max(0, lz) / 3.0) * 3.0;
                string frame = new[] { "trim-white", "trim-black", "wood-planks", "metal-seam", "copper" }[Math.Max(0, _frameFinish.Selected)];
                bool back = ly > wall.Depth / 2;
                Say(_doc.InsertPart(wall, kind, lx - pw / 2, floor + sill, frame, back)
                    ? $"Put in a {Parts.List.First(p => p.Kind == kind).Name.ToLowerInvariant()} (Ctrl+Z takes it out)"
                    : "It doesn't fit there: too near the wall's end or top, or over another opening");
                break;
            }
            case WallMode.TakeOut when PointedAt() is { } at:
            {
                if (_doc.ShapeAt(at.X, at.Y, at.Z, s => s.Kind == ShapeKind.Wall && s.Openings.Count > 0 && s.Depth >= 0.12) is not { } wall)
                {
                    // Clicked the part itself: the wall behind it.
                    wall = _doc.Shapes.LastOrDefault(s => IsMainWall(s) && s.Openings.Count > 0 && s.Inside(at.X, at.Y, at.Z, Vs, 1.0))!;
                }
                if (wall == null) { Say("Click a wall by a window or door"); break; }
                var (lx, _, _) = wall.Local(at.X, at.Y, at.Z);
                Say(_doc.RemovePart(wall, lx) ? "Took it out and filled the opening (Ctrl+Z puts it back)" : "That wall has no windows or doors");
                break;
            }
            case WallMode.Paint when PointedAt() is { } at:
            {
                if (_doc.ShapeAt(at.X, at.Y, at.Z) is not { } s) { Say("Click a shape: a wall, a frame, a box"); break; }
                string fin = WallFinishNow;
                if (fin.Length > 0) _doc.SetFinish(s, fin);
                else { var c = s.Clone(); c.Finish = ""; c.Colour = _colour; _doc.ReplaceShapes(new[] { s }, new[] { c }); }
                Say(fin.Length > 0 ? $"Now {Finishes.Get(fin)!.Name.ToLowerInvariant()}" : $"Now {AssetPalette.NameOf(_colour).ToLowerInvariant()}, no finish");
                RoofChanged();
                break;
            }
        }
        _wallGhostKey = null;
        WallSettingsChanged();
    }

    void PickModel(Action<string> then)
    {
        Pick(FileDialog.FileModeEnum.OpenFile, FileDialog.AccessEnum.Filesystem, null, then);
        _dialog!.Filters = new[] { "*.glb, *.gltf, *.obj ; 3D models" };
    }

    /// <summary>Reads a glTF or OBJ model, its buffers, materials and textures from beside it, into the asset.</summary>
    internal void ImportModelFile(string path)
    {
        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        byte[]? Beside(string name)
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, name));
            return full.StartsWith(System.IO.Path.GetFullPath(dir)) && File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        MeshData mesh;
        try
        {
            mesh = path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
                ? ObjFile.Read(File.ReadAllText(path), Beside)
                : GltfFile.Read(File.ReadAllBytes(path), Beside);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            Say($"Couldn't read {System.IO.Path.GetFileName(path)}: {e.Message}");
            return;
        }
        if (_doc.ImportModel(mesh, System.IO.Path.GetFileNameWithoutExtension(path)) is not { } put) { Say("That model has no triangles"); return; }
        int textures = put.Shape.FinishesUsed().Count();
        Say($"Put in {System.IO.Path.GetFileName(path)}: {mesh.Indices.Count / 3:N0} triangles, {textures} texture{(textures == 1 ? "" : "s")}" +
            (put.Shrunk < 0.999 ? $", shrunk to {put.Shrunk:P0} to fit" : "") + " (Ctrl+Z takes it out)");
        _meshed = -1;
        Refresh();
    }

    /// <summary>The asset's polygons (its shapes and roof) as a .glb, in metres.</summary>
    internal void ExportGlb(string path)
    {
        var mesh = new MeshData();
        foreach (var sh in _doc.Shapes) mesh.Append(sh.Mesh(1f, 1, _doc.Model.Palette));
        mesh.Append(_doc.RoofMesh(Vs));
        if (mesh.Positions.Count == 0) { Say("Nothing smooth to export: draw walls or shapes, or add a roof"); return; }
        File.WriteAllBytes(path, GltfFile.Write(mesh));
        Say($"Exported {System.IO.Path.GetFileName(path)}: {mesh.Indices.Count / 3:N0} triangles");
    }

    /// <summary>A wall a window can go in: thick enough to be a wall, not a frame, a sill or glass.</summary>
    static bool IsMainWall(Shape s) => s.Kind == ShapeKind.Wall && s.Depth >= 0.12 && s.Height >= 1.5 && s.Finish != "glass";
}
