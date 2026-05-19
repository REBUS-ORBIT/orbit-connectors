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

        // Ensure normals are computed
        if (rhinoMesh.Normals.Count == 0)
            rhinoMesh.Normals.ComputeNormals();

        var mesh = new OM.Mesh
        {
            Units = context.Units,
            Vertices = new List<double>(rhinoMesh.Vertices.Count * 3),
            Faces    = new List<int>(),
            VertexNormals = new List<double>(rhinoMesh.Normals.Count * 3),
        };

        // Vertices
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

        // Normals
        foreach (var n in rhinoMesh.Normals)
        {
            mesh.VertexNormals.Add(n.X);
            mesh.VertexNormals.Add(n.Y);
            mesh.VertexNormals.Add(n.Z);
        }

        // Vertex colours (ARGB int)
        if (rhinoMesh.VertexColors.Count > 0)
        {
            mesh.Colors = rhinoMesh.VertexColors
                .Select(c => c.ToArgb())
                .ToList();
        }

        return mesh;
    }
}
