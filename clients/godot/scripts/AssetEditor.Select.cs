using Godot;
using ToyCity.ClientModel.Voxels.Assets;

namespace ToyCity.Godot;

/// <summary>
/// The select tool (9): click a voxel to pick the patch of its colour showing on that side, joined across the face
/// (Shift+click for every patch of that colour facing that way), then pull it out with + or push it in with −, a
/// voxel a press. A patch pushed in keeps its colour, and the wall behind is never holed.
/// </summary>
public partial class AssetEditor
{
    List<(int X, int Y, int Z)> _selection = new();
    (int X, int Y, int Z) _selNormal;
    MeshInstance3D _selMesh = null!;

    void MakeSelectionMesh()
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.35f, 0.85f, 1f, 0.5f), CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = false,
        };
        AddChild(_selMesh = new MeshInstance3D { MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    void Select(VoxHit h, bool everywhere)
    {
        _selection = _doc.SelectFace(h.X, h.Y, h.Z, h.Nx, h.Ny, h.Nz, everywhere);
        _selNormal = (h.Nx, h.Ny, h.Nz);
        ShowSelection();
        Say(_selection.Count == 0 ? "Nothing to select there" : $"Selected {_selection.Count:N0} voxels of {AssetPalette.NameOf(_doc[h.X, h.Y, h.Z])}: + pulls out, − pushes in");
    }

    void ClearSelection() { _selection.Clear(); ShowSelection(); }

    /// <summary>Pulls the selection out (+1) or pushes it in (−1) a voxel.</summary>
    void Push(int steps)
    {
        if (_selection.Count == 0) { Say("Select a patch first: click it with the select tool"); return; }
        var (nx, ny, nz) = _selNormal;
        var moved = _doc.Extrude(_selection, nx, ny, nz, steps);
        if (moved.Count == 0) { Say(steps > 0 ? "No room: it's at the model's edge or against other voxels" : "Can't go further in without holing the wall"); return; }
        if (moved.Count < _selection.Count) Say($"{_selection.Count - moved.Count:N0} voxels couldn't move");
        _selection = moved;
        ShowSelection();
        Refresh();
    }

    /// <summary>A see-through sheet over each selected voxel's open face.</summary>
    void ShowSelection()
    {
        if (_selection.Count == 0) { _selMesh.Mesh = null; return; }
        var (nx, ny, nz) = _selNormal;
        // Model axes (x east, y south, z up) to Godot's (x east, y up, z south).
        var n = new Vector3(nx, nz, ny);
        var (u, v) = nx != 0 ? (new Vector3(0, 0, 1), new Vector3(0, 1, 0)) : ny != 0 ? (new Vector3(1, 0, 0), new Vector3(0, 1, 0)) : (new Vector3(1, 0, 0), new Vector3(0, 0, 1));
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (x, y, z) in _selection)
        {
            var c = new Vector3(x + 0.5f, z + 0.5f, y + 0.5f) + n * 0.52f;
            Vector3 P(float a, float b) => (c + u * a + v * b) * Vs;
            im.SurfaceAddVertex(P(-0.5f, -0.5f)); im.SurfaceAddVertex(P(0.5f, -0.5f)); im.SurfaceAddVertex(P(0.5f, 0.5f));
            im.SurfaceAddVertex(P(-0.5f, -0.5f)); im.SurfaceAddVertex(P(0.5f, 0.5f)); im.SurfaceAddVertex(P(-0.5f, 0.5f));
        }
        im.SurfaceEnd();
        _selMesh.Mesh = im;
    }
}
