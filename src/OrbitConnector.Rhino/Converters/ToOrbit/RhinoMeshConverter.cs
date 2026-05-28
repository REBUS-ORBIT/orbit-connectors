using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

public class RhinoMeshConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is Mesh;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var rhinoMesh = (Mesh)geometry;

        if (rhinoMesh.Normals.Count == 0)
            rhinoMesh.Normals.ComputeNormals();

        var mesh = new OM.Mesh
        {
            Units = context.Units,
            Vertices = new List<double>(rhinoMesh.Vertices.Count * 3),
            Faces    = new List<int>(),
            VertexNormals = new List<double>(rhinoMesh.Normals.Count * 3),
        };

        foreach (var v in rhinoMesh.Vertices)
        {
            mesh.Vertices.Add(v.X);
            mesh.Vertices.Add(v.Y);
            mesh.Vertices.Add(v.Z);
        }

        // Faces — ORBIT variable-length encoding: n, i0..i(n-1)
        foreach (var f in rhinoMesh.Faces)
        {
            if (f.IsTriangle)
            {
                mesh.Faces.Add(3);
                mesh.Faces.Add(f.A); mesh.Faces.Add(f.B); mesh.Faces.Add(f.C);
            }
            else
            {
                mesh.Faces.Add(4);
                mesh.Faces.Add(f.A); mesh.Faces.Add(f.B);
                mesh.Faces.Add(f.C); mesh.Faces.Add(f.D);
            }
        }

        foreach (var n in rhinoMesh.Normals)
        {
            mesh.VertexNormals.Add(n.X);
            mesh.VertexNormals.Add(n.Y);
            mesh.VertexNormals.Add(n.Z);
        }

        CopyTextureCoordinates(rhinoMesh, mesh, context);

        if (rhinoMesh.VertexColors.Count > 0)
        {
            mesh.Colors = rhinoMesh.VertexColors
                .Select(c => c.ToArgb())
                .ToList();
        }

        // Material/colour. Read from the parent Rhino object via the
        // conversion context — falls back to the layer colour when the
        // object has no override and no assigned material.
        AttachRenderMaterial(mesh, context);

        return mesh;
    }

    /// <summary>
    /// Copy per-vertex UVs from the Rhino mesh, or backfill from the parent
    /// object's render mesh when the tessellated mesh has none.
    /// </summary>
    public static void CopyTextureCoordinates(
        Mesh rhinoMesh, OM.Mesh mesh, ConversionContext context)
    {
        if (TryExtractUvs(rhinoMesh, out var uvs))
        {
            mesh.TextureCoordinates = uvs;
            return;
        }

        var obj = context.CurrentObject;
        if (obj == null) return;

        var renderMeshes = obj.GetMeshes(MeshType.Render);
        if (renderMeshes == null || renderMeshes.Length == 0 || renderMeshes[0] == null)
            return;

        var rm = renderMeshes[0];
        if (TryExtractUvs(rm, out uvs))
            mesh.TextureCoordinates = uvs;
    }

    private static bool TryExtractUvs(Mesh rhinoMesh, out List<double> uvs)
    {
        uvs = new List<double>();
        if (rhinoMesh.TextureCoordinates.Count == 0
            || rhinoMesh.TextureCoordinates.Count != rhinoMesh.Vertices.Count)
            return false;

        uvs = new List<double>(rhinoMesh.TextureCoordinates.Count * 2);
        foreach (var tc in rhinoMesh.TextureCoordinates)
        {
            uvs.Add(tc.X);
            uvs.Add(tc.Y);
        }
        return true;
    }

    /// <summary>
    /// Attach an inline <c>renderMaterial</c> to a mesh based on the
    /// <see cref="ConversionContext.CurrentObject"/>'s attributes.
    /// Safe to call on display-mesh fragments (e.g. the meshes generated
    /// by tessellating a Brep) — they pick up the same parent material.
    /// </summary>
    public static void AttachRenderMaterial(OM.Mesh mesh, ConversionContext context)
    {
        var material = context.BuildCurrentRenderMaterial();
        if (material != null)
            mesh.RenderMaterial = material;

        var resolved = context.ResolveCurrentColor();
        if (resolved.HasValue)
        {
            mesh.ColorSource = resolved.Value.source;
        }
    }
}
