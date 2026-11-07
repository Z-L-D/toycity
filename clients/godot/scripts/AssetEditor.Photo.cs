using Godot;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// The photo importer (RC part 2.2): open a photo of a building, drag four corners onto its facade, say how wide
/// and tall it is in metres, and it's built into the model on the front, straightened, cut to the asset's voxels,
/// its colours matched to the palette and its windows and doors set back.
/// </summary>
public partial class AssetEditor
{
    PanelContainer _photoPanel = null!;
    PhotoCanvas _photoCanvas = null!;
    FacadePreview _facadePreview = null!;
    SpinBox _photoWidth = null!, _photoHeight = null!, _photoDepth = null!, _photoRecess = null!, _photoColours = null!, _photoSmooth = null!, _photoOffset = null!;
    Label _photoPicked = null!;
    int _photoGroup = -1;
    OptionButton _photoSide = null!;
    SpinBox _reliefRange = null!;
    Label _reliefInfo = null!;
    Photo? _depthMap;
    double _readRecess;
    HBoxContainer _photoGroups = null!;
    Label _photoInfo = null!;
    Photo? _photo;
    Facade? _facade;
    bool _photoDirty;
    double _photoClock;

    void BuildPhotoPanel(Control root)
    {
        _photoPanel = Glass(root);
        _photoPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        _photoPanel.GrowHorizontal = Control.GrowDirection.Both;
        _photoPanel.GrowVertical = Control.GrowDirection.Both;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        _photoPanel.AddChild(col);
        var head = new HBoxContainer();
        col.AddChild(head);
        head.AddChild(UiTheme.Heading("Build from a photo"));
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        Btn(head, "Open photo…", "A photo of one face of a building: PNG, JPEG or WebP", () => Pick(FileDialog.FileModeEnum.OpenFile, FileDialog.AccessEnum.Filesystem, null, OpenPhoto));
        var views = new HBoxContainer();
        views.AddThemeConstantOverride("separation", 8);
        col.AddChild(views);
        _photoCanvas = new PhotoCanvas { CustomMinimumSize = new Vector2(560, 420), MouseFilter = Control.MouseFilterEnum.Stop, ClipContents = true };
        _photoCanvas.Changed += () => _photoDirty = true;
        _photoCanvas.Released += () => { _photoDirty = true; _photoClock = 0; };
        views.AddChild(_photoCanvas);
        _facadePreview = new FacadePreview { CustomMinimumSize = new Vector2(420, 420), TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore };
        views.AddChild(_facadePreview);

        var size = new HBoxContainer();
        size.AddThemeConstantOverride("separation", 6);
        col.AddChild(size);
        SpinBox Spin(Container row, string label, string tip, double min, double max, double step, double value, string suffix)
        {
            row.AddChild(new Label { Text = label, TooltipText = tip, MouseFilter = Control.MouseFilterEnum.Pass });
            var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, TooltipText = tip, CustomMinimumSize = new Vector2(96, 0) };
            s.ValueChanged += _ => { _photoDirty = true; _photoClock = 0; };
            row.AddChild(s);
            return s;
        }
        _photoWidth = Spin(size, "Width", "How wide the facade is between the corners, in metres. A house front is often 8-12 m; a door is about 1 m", 1, 128, 0.25, 12, " m");
        _photoHeight = Spin(size, "Height", "How tall it is, in metres: 3 m a storey. Set it so the dashed storey lines fall on the floors", 1, 128, 0.25, 9, " m");
        _photoDepth = Spin(size, "Depth", "How far back to build: 0.5 m makes a facade to put on a building; more makes a solid block", 0.0625, 64, 0.25, 0.5, " m");
        _photoRecess = Spin(size, "Set back", "How far the colours that look like windows and doors start behind the face; change any colour's own below", 0, 1, 0.0625, 0.25, " m");
        var look = new HBoxContainer();
        look.AddThemeConstantOverride("separation", 6);
        col.AddChild(look);
        _photoColours = Spin(look, "Colours", "How many colours the photo is cut down to: fewer is simpler, and meshes into fewer pieces", 2, 16, 1, 6, "");
        _photoSmooth = Spin(look, "Smooth", "Passes of the speckle filter: each tidies ragged edges and lone voxels", 0, 4, 1, 2, "");
        var groups = new HBoxContainer();
        groups.AddThemeConstantOverride("separation", 6);
        col.AddChild(groups);
        groups.AddChild(new Label { Text = "Colours (click one to move it in or out):", Modulate = UiTheme.Muted });
        _photoGroups = new HBoxContainer();
        _photoGroups.AddThemeConstantOverride("separation", 3);
        groups.AddChild(_photoGroups);
        _photoPicked = new Label { Text = "", CustomMinimumSize = new Vector2(120, 0) };
        groups.AddChild(_photoPicked);
        _photoOffset = new SpinBox
        {
            MinValue = -2, MaxValue = 2, Step = 0.0625, Suffix = " m", Editable = false, CustomMinimumSize = new Vector2(104, 0),
            TooltipText = "How far the colour picked sits from the face: below 0 is set back into the wall (glass, a doorway), above 0 stands out (a sill, a cornice)",
        };
        _photoOffset.ValueChanged += v =>
        {
            if (_facade is not { } f || _photoGroup < 0 || _photoGroup >= f.Offset.Length) return;
            f.Offset[_photoGroup] = v;
            ShowFacade();
        };
        groups.AddChild(_photoOffset);
        var relief = new HBoxContainer();
        relief.AddThemeConstantOverride("separation", 6);
        col.AddChild(relief);
        relief.AddChild(new Label { Text = "Relief:", Modulate = UiTheme.Muted });
        Btn(relief, "Open depth map…", "A greyscale picture lined up with the photo, lighter nearer: each voxel stands out or sits back by it", () =>
            Pick(FileDialog.FileModeEnum.OpenFile, FileDialog.AccessEnum.Filesystem, null, OpenDepthMap));
        // Depth from the photo itself needs a depth model on this machine; none ships yet (docs/notes/depth-from-a-photo.md).
        var estimate = Btn(relief, "Depth from the photo", "", EstimateDepth);
        estimate.Disabled = DepthEstimators.Current == null;
        estimate.TooltipText = DepthEstimators.Current is { } de
            ? $"Work out a depth map from the photo with {de.Name}"
            : "Needs a depth model on this computer, planned for a later release. Load a depth map instead";
        Btn(relief, "None", "Flat, apart from each colour's own distance", () => { _depthMap = null; ReadPhoto(); });
        _reliefRange = Spin(relief, "Up to", "How far the relief moves a voxel in or out at most", 0.0625, 2, 0.0625, 0.5, " m");
        _reliefInfo = new Label { Modulate = UiTheme.Muted };
        relief.AddChild(_reliefInfo);
        var foot = new HBoxContainer();
        col.AddChild(foot);
        _photoInfo = new Label { Modulate = UiTheme.Muted, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foot.AddChild(_photoInfo);
        Btn(foot, "Cancel", "Close without building", () => _photoPanel.Visible = false);
        _photoSide = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Which side it goes on. With voxels already in the model, it goes onto the outside of them on that side" };
        _photoSide.AddItem("South (front)", (int)FacadeSide.South);
        _photoSide.AddItem("East", (int)FacadeSide.East);
        _photoSide.AddItem("North (back)", (int)FacadeSide.North);
        _photoSide.AddItem("West", (int)FacadeSide.West);
        foot.AddChild(_photoSide);
        Btn(foot, "Build it", "Build the facade on the side chosen: one undo step", BuildPhoto);
        Btn(foot, "As a textured wall", "Put the photo on a smooth wall its size, straightened, on the side chosen: sharp as the photo in the game, its colours in voxels when wrecked", PhotoWall);
        Btn(foot, "Wrap all four sides", "Replace the model with a block the photo's width (and the depth, if over a metre) with this facade on every side", WrapPhoto);
        _photoPanel.Visible = false;
    }

    void ShowPhotoPanel()
    {
        _photoPanel.Visible = true;
        if (_photo == null) _photoInfo.Text = "Open a photo to start.";
    }

    /// <summary>A picture file read as a photo, cut down to 2048 px if it's bigger; null (and said) if it can't be read.</summary>
    (Photo Photo, Image Image)? LoadPhoto(string path)
    {
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) { Say($"Couldn't read {System.IO.Path.GetFileName(path)} as a picture"); return null; }
        // More is slower and no sharper at voxel scale.
        int longest = Math.Max(img.GetWidth(), img.GetHeight());
        if (longest > 2048) img.Resize(img.GetWidth() * 2048 / longest, img.GetHeight() * 2048 / longest, Image.Interpolation.Cubic);
        img.Convert(Image.Format.Rgba8);
        var bytes = img.GetData();
        var px = new uint[img.GetWidth() * img.GetHeight()];
        for (int i = 0; i < px.Length; i++) px[i] = ToyCity.ClientModel.Voxels.VoxModel.Rgba(bytes[4 * i], bytes[4 * i + 1], bytes[4 * i + 2]);
        return (new Photo(img.GetWidth(), img.GetHeight(), px), img);
    }

    void OpenPhoto(string path)
    {
        if (LoadPhoto(path) is not { } p) return;
        _depthMap = null;
        ShowPhoto(p.Photo, p.Image, PhotoImport.StartCorners(p.Photo), null, null);
        Say($"Opened {System.IO.Path.GetFileName(path)}: drag the corners onto the facade's corners");
    }

    /// <summary>Shows a photo in the panel with its corners; a size in metres if known, or a guess from the corners' shape.</summary>
    void ShowPhoto(Photo photo, Image? img, (double X, double Y)[] corners, double? width, double? height)
    {
        _photo = photo;
        if (img == null)
        {
            var bytes = new byte[photo.Pixels.Length * 4];
            for (int i = 0; i < photo.Pixels.Length; i++)
            {
                var (r, g, b, _) = ToyCity.ClientModel.Voxels.VoxModel.Unpack(photo.Pixels[i]);
                bytes[4 * i] = r; bytes[4 * i + 1] = g; bytes[4 * i + 2] = b; bytes[4 * i + 3] = 255;
            }
            img = Image.CreateFromData(photo.Width, photo.Height, false, Image.Format.Rgba8, bytes);
        }
        _photoCanvas.Texture = ImageTexture.CreateFromImage(img);
        _photoCanvas.PhotoSize = new Vector2I(photo.Width, photo.Height);
        _photoCanvas.Corners = corners.ToArray();
        double w = width ?? _photoWidth.Value;
        // Without a height, guess one from how the corners sit: the mean side over the mean top and bottom.
        static double Len((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double across = (Len(corners[0], corners[1]) + Len(corners[3], corners[2])) / 2, up = (Len(corners[0], corners[3]) + Len(corners[1], corners[2])) / 2;
        double h = height ?? Math.Round(w * up / Math.Max(1, across) * 4) / 4;
        _photoWidth.SetValueNoSignal(w);
        _photoHeight.SetValueNoSignal(h);
        _photoPanel.Visible = true;
        ReadPhoto();
    }

    PhotoOptions PhotoOptionsNow() => new()
    {
        WidthMetres = _photoWidth.Value, HeightMetres = _photoHeight.Value, DepthMetres = _photoDepth.Value,
        RecessMetres = _photoRecess.Value, Colours = (int)_photoColours.Value, Smooth = (int)_photoSmooth.Value,
    };

    /// <summary>Reads the facade again from the corners and settings, and shows it.</summary>
    void ReadPhoto()
    {
        _photoDirty = false;
        if (_photo == null) return;
        var o = PhotoOptionsNow();
        _photoCanvas.WidthMetres = o.WidthMetres;
        _photoCanvas.HeightMetres = o.HeightMetres;
        _photoCanvas.QueueRedraw();
        var (w, h) = PhotoImport.VoxelsOf(o, _doc.Detail);
        if (w > 256 || h > 256)
        {
            _photoInfo.Text = $"{w} × {h} voxels at {50.0 / _doc.Detail:0.##} cm: more than a model holds (256). Make a new asset with bigger voxels.";
            _facade = null;
            return;
        }
        if (PhotoImport.Homography(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }, _photoCanvas.Corners) == null) { _photoInfo.Text = "Three corners are in a line."; return; }
        var was = _facade;
        _facade = PhotoImport.Read(_photo, _photoCanvas.Corners, _doc.Detail, o, _doc.Model.Palette);
        // The same colours read again (a corner nudged, the depth changed): keep the distances set by hand.
        if (was != null && was.Entry.SequenceEqual(_facade.Entry) && _readRecess == o.RecessMetres) Array.Copy(was.Offset, _facade.Offset, was.Offset.Length);
        _readRecess = o.RecessMetres;
        if (_depthMap != null) _facade.Relief = PhotoImport.ReliefFromDepthMap(_facade, _photo, _depthMap, _photoCanvas.Corners, _reliefRange.Value);
        _reliefInfo.Text = _depthMap != null ? "from the depth map" : "";
        ShowFacade();
    }

    void ShowFacade()
    {
        if (_facade is not { } f) return;
        _facadePreview.Show(f, _doc.Model.Palette, _photoWidth.Value, _photoHeight.Value);
        foreach (var c in _photoGroups.GetChildren()) c.QueueFree();
        if (_photoGroup >= f.Colours.Length) _photoGroup = -1;
        var group = new ButtonGroup { AllowUnpress = true };
        for (int i = 0; i < f.Colours.Length; i++)
        {
            int g = i;
            string where = f.Offset[g] < 0 ? $", set back {-f.Offset[g]:0.###} m" : f.Offset[g] > 0 ? $", out {f.Offset[g]:0.###} m" : "";
            var b = new Button
            {
                ToggleMode = true, ButtonGroup = group, ButtonPressed = g == _photoGroup, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 26),
                Icon = SwatchIcon(Swatch(f.Entry[g])), Text = f.Offset[g] < 0 ? "↓" : f.Offset[g] > 0 ? "↑" : "",
                TooltipText = $"{AssetPalette.NameOf(f.Entry[g])}, {f.Share[g]:P0} of the facade{where}",
            };
            b.Toggled += on => { _photoGroup = on ? g : -1; ShowPicked(); };
            _photoGroups.AddChild(b);
        }
        ShowPicked();
        int set = Enumerable.Range(0, f.Colours.Length).Where(f.SetBack).Sum(i => (int)Math.Round(f.Share[i] * 100));
        _photoInfo.Text = $"{f.Width} × {f.Height} voxels at {50.0 / _doc.Detail:0.##} cm, {f.Colours.Length} colours, {set}% set back. "
            + $"It goes on the model's front (south) side.";
    }

    /// <summary>The colour picked from the swatches, and its distance from the face to change.</summary>
    void ShowPicked()
    {
        bool any = _facade is { } f && _photoGroup >= 0 && _photoGroup < f.Offset.Length;
        _photoOffset.Editable = any;
        _photoPicked.Text = any ? AssetPalette.NameOf(_facade!.Entry[_photoGroup]) + ":" : "";
        _photoOffset.SetValueNoSignal(any ? _facade!.Offset[_photoGroup] : 0);
    }

    void OpenDepthMap(string path)
    {
        if (LoadPhoto(path) is not { } d) return;
        _depthMap = d.Photo;
        ReadPhoto();
        Say($"Depth map {System.IO.Path.GetFileName(path)}: lighter stands out, darker sits back");
    }

    /// <summary>A depth map worked out from the photo by the installed depth model, off the main thread.</summary>
    void EstimateDepth()
    {
        if (DepthEstimators.Current is not { } est || _photo is not { } photo) return;
        Say($"Working out depth with {est.Name}…");
        System.Threading.Tasks.Task.Run(() => est.Estimate(photo)).ContinueWith(t => Callable.From(() =>
        {
            if (t.IsFaulted) { Say($"Couldn't work out depth: {t.Exception?.GetBaseException().Message}"); return; }
            _depthMap = t.Result;
            ReadPhoto();
            Say($"Depth from {est.Name}: lighter stands out, darker sits back");
        }).CallDeferred());
    }

    void WrapPhoto()
    {
        if (_facade == null || _photoDirty) ReadPhoto();
        if (_facade is not { } f) return;
        var placed = PhotoImport.Wrap(_doc, f, PhotoOptionsNow());
        if (placed == null) { Say("Too big for a model at this voxel size"); return; }
        _photoPanel.Visible = false;
        _meshed = -1;
        Refresh();
        Frame();
        Say("Wrapped the photo round all four sides (Ctrl+Z takes it away)");
    }

    /// <summary>The photo as a polygon wall with it as its texture (RC part 5).</summary>
    void PhotoWall()
    {
        if (_photo == null) { Say("Open a photo first"); return; }
        if (PhotoImport.Homography(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }, _photoCanvas.Corners) == null) { Say("Three corners are in a line"); return; }
        var wall = PhotoImport.AsWall(_doc, _photo, _photoCanvas.Corners, PhotoOptionsNow(), (FacadeSide)_photoSide.GetSelectedId());
        if (wall == null) { Say("It doesn't fit in the model: make the model bigger or the facade smaller"); return; }
        _photoPanel.Visible = false;
        _meshed = -1;
        Refresh();
        Frame();
        Say($"Put the photo on a wall {wall.Width:0.#} × {wall.Height:0.#} m (Ctrl+Z takes it away)");
    }

    void BuildPhoto()
    {
        if (_facade == null || _photoDirty) ReadPhoto();
        if (_facade is not { } f) return;
        var placed = PhotoImport.Build(_doc, f, PhotoOptionsNow(), (FacadeSide)_photoSide.GetSelectedId());
        if (placed == null) { Say("Too big for a model at this voxel size"); return; }
        _photoPanel.Visible = false;
        _meshed = -1;
        Refresh();
        Frame();
        Say($"Built the facade: {placed.Voxels:N0} voxels{(placed.Resized ? "; the model grew to fit, so undo starts again here" : " (Ctrl+Z takes it away)")}");
    }

    /// <summary>Reads the facade again a moment after the corners or settings change, not on every frame of a drag.</summary>
    void PhotoTick(double delta)
    {
        if (!_photoPanel.Visible || !_photoDirty) return;
        if ((_photoClock += delta) < 0.15) return;
        _photoClock = 0;
        ReadPhoto();
    }
}
