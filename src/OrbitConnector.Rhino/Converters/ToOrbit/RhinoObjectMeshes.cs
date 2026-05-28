using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Aggressive mesh extraction from a <see cref="RhinoObject"/> or raw
/// <see cref="GeometryBase"/>. Used as the last resort before dropping an object.
/// </summary>
internal static class RhinoObjectMeshes
{
    private static readonly MeshType[] MeshTypes =
    {
        MeshType.Render,
        MeshType.Default,
        MeshType.Analysis,
    };

    /// <summary>
    /// Try every meshing strategy Rhino exposes for this object.
    /// </summary>
    public static IReadOnlyList<Mesh> ExtractFromObject(RhinoObject rhinoObj, ConversionContext context)
    {
        foreach (var mt in MeshTypes)
        {
            var meshes = rhinoObj.GetMeshes(mt);
            var nonEmpty = FilterNonEmpty(meshes);
            if (nonEmpty.Count > 0)
                return nonEmpty;
        }

        if (rhinoObj.Geometry != null)
            return ExtractFromGeometry(rhinoObj.Geometry, context);

        return Array.Empty<Mesh>();
    }

    /// <summary>
    /// Tessellate geometry without a parent object (rare).
    /// </summary>
    public static IReadOnlyList<Mesh> ExtractFromGeometry(GeometryBase geometry, ConversionContext context)
    {
        if (geometry is Brep brep)
            return RhinoBrepDisplayMeshes.Extract(brep, context).ToList();

        if (geometry is Extrusion ext)
        {
            var eb = ext.ToBrep();
            return eb != null
                ? RhinoBrepDisplayMeshes.Extract(eb, context).ToList()
                : Array.Empty<Mesh>();
        }

        if (geometry is Mesh mesh && mesh.Vertices.Count > 0)
            return new[] { mesh.DuplicateMesh() ?? mesh };

        if (geometry is Surface surface)
            return TessellateSurface(surface);

        if (geometry is Curve curve)
            return ExtractFromCurve(curve);

        return Array.Empty<Mesh>();
    }

    /// <summary>
    /// Build a visible pipe mesh for curves that have no render mesh.
    /// </summary>
    public static IReadOnlyList<Mesh> ExtractFromCurve(Curve curve)
    {
        if (!curve.IsValid || curve.GetLength() <= RhinoMath.ZeroTolerance)
            return Array.Empty<Mesh>();

        try
        {
            var bbox = curve.GetBoundingBox(false);
            var radius = Math.Max(0.1, bbox.Diagonal.Length * 0.002);

            var pipeBreps = Brep.CreatePipe(curve, radius, false, PipeCapMode.Round, true, 0.01, 0.01);
            if (pipeBreps == null || pipeBreps.Length == 0)
                return Array.Empty<Mesh>();

            var meshes = new List<Mesh>();
            foreach (var pb in pipeBreps)
            {
                if (pb == null) continue;
                meshes.AddRange(RhinoBrepDisplayMeshes.TessellateBrep(pb));
            }
            return FilterNonEmpty(meshes.ToArray());
        }
        catch
        {
            return Array.Empty<Mesh>();
        }
    }

    /// <summary>
    /// Tessellate a single NURBS surface (e.g. lavender plane).
    /// </summary>
    public static IReadOnlyList<Mesh> TessellateSurface(Surface surface)
    {
        try
        {
            var brep = surface.ToBrep();
            return brep != null
                ? RhinoBrepDisplayMeshes.TessellateBrep(brep).ToList()
                : Array.Empty<Mesh>();
        }
        catch
        {
            return Array.Empty<Mesh>();
        }
    }

    /// <summary>
    /// Last-resort box mesh so the object is not invisible in the viewer.
    /// </summary>
    public static Mesh? BoundingBoxMesh(GeometryBase geometry)
    {
        try
        {
            var bbox = geometry.GetBoundingBox(false);
            if (!bbox.IsValid || bbox.Diagonal.Length <= RhinoMath.ZeroTolerance)
                return null;

            var box = new Box(bbox);
            var brep = box.ToBrep();
            if (brep == null) return null;

            var meshes = RhinoBrepDisplayMeshes.TessellateBrep(brep);
            return meshes.Count > 0 ? meshes[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<Mesh> FilterNonEmpty(Mesh[]? meshes)
    {
        if (meshes == null || meshes.Length == 0)
            return new List<Mesh>();

        return meshes
            .Where(m => m != null && m.Vertices.Count > 0)
            .Select(m => m.DuplicateMesh() ?? m)
            .ToList();
    }
}
