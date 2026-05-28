using Rhino.Geometry;
using Orbit.Objects.Base;
using OM = Orbit.Objects.Geometry;

namespace OrbitConnector.Rhino.Converters.ToOrbit;

/// <summary>
/// Converts a Rhino <see cref="PointCloud"/> to an ORBIT
/// <see cref="OM.PointCloud"/>. Per-point colours are forwarded when present.
/// </summary>
public class RhinoPointCloudConverter : IRhinoToOrbitConverter
{
    public bool CanConvert(GeometryBase geometry) => geometry is PointCloud;

    public OrbitBase Convert(GeometryBase geometry, ConversionContext context)
    {
        var pc = (PointCloud)geometry;

        var points  = new List<double>(pc.Count * 3);
        var normals = pc.ContainsNormals ? new List<double>(pc.Count * 3) : null;
        var colors  = pc.ContainsColors  ? new List<int>(pc.Count) : null;

        foreach (var p in pc)
        {
            points.Add(p.Location.X);
            points.Add(p.Location.Y);
            points.Add(p.Location.Z);

            if (normals != null)
            {
                normals.Add(p.Normal.X);
                normals.Add(p.Normal.Y);
                normals.Add(p.Normal.Z);
            }

            if (colors != null)
                colors.Add(p.Color.ToArgb());
        }

        return new OM.PointCloud
        {
            Points  = points,
            Normals = normals,
            Colors  = colors,
            Units   = context.Units,
        };
    }
}
