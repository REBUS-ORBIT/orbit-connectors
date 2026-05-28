using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using OM = Orbit.Objects.Geometry;
using Orbit.Sdk.Serialisation;

namespace OrbitConnector.Rhino.Converters.FromOrbit;

/// <summary>
/// Converts a deserialized ORBIT object (represented as a raw <see cref="JObject"/>)
/// back to a Rhino <see cref="GeometryBase"/>.
///
/// Dispatch is on <c>speckle_type</c>:
/// <list type="bullet">
///   <item><c>Objects.Geometry.Mesh</c>         → <see cref="Mesh"/></item>
///   <item><c>Objects.Geometry.Brep</c>         → display-mesh fallback (v1)</item>
///   <item><c>Objects.Geometry.Line</c>         → <see cref="LineCurve"/></item>
///   <item><c>Objects.Geometry.Polyline</c>     → <see cref="PolylineCurve"/></item>
///   <item><c>Objects.Geometry.NurbsCurve</c>   → <see cref="NurbsCurve"/></item>
///   <item><c>Objects.Geometry.Arc</c>          → <see cref="ArcCurve"/></item>
///   <item><c>Objects.Geometry.Circle</c>       → <see cref="ArcCurve"/> (full)</item>
///   <item><c>Objects.Geometry.Point</c>        → <see cref="Point"/></item>
///   <item>Contains <c>"RhinoObject"</c>        → decoded native .3dm geometry</item>
///   <item>Collection/wrapper with displayValue → mesh from displayValue</item>
///   <item>Unknown                              → null (skipped)</item>
/// </list>
/// </summary>
public class OrbitToRhinoConverter
{
    private readonly OrbitDeserializer _deserializer = new();

    /// <summary>
    /// Convert a raw ORBIT JSON object to Rhino geometry.
    /// Returns <c>null</c> if the type is not supported or conversion fails.
    /// </summary>
    public GeometryBase? Convert(JObject obj)
    {
        var speckleType = obj["speckle_type"]?.Value<string>() ?? "";

        try
        {
            if (speckleType.Contains("RhinoObject"))
                return ConvertRhinoNativeObject(obj);

            return speckleType switch
            {
                "Objects.Geometry.Mesh"      => ConvertMesh(obj),
                "Objects.Geometry.Brep"      => ConvertBrepFallback(obj),
                "Objects.Geometry.Line"      => ConvertLine(obj),
                "Objects.Geometry.Polyline"  => ConvertPolyline(obj),
                "Objects.Geometry.NurbsCurve"=> ConvertNurbsCurve(obj),
                "Objects.Geometry.Arc"       => ConvertArc(obj),
                "Objects.Geometry.Circle"    => ConvertCircle(obj),
                "Objects.Geometry.Point"     => ConvertPoint(obj),
                _                            => ConvertFromDisplayValue(obj)
            };
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[ORBIT] Convert failed for {speckleType}: {ex.Message}");
            return null;
        }
    }

    // ── Mesh ─────────────────────────────────────────────────────────────────

    private Mesh? ConvertMesh(JObject obj)
    {
        var verts = ReadDoubleArray(obj["vertices"]);
        var faces = ReadIntArray(obj["faces"]);
        if (verts == null || verts.Count == 0) return null;

        var mesh = new Mesh();

        // Vertices: flat [x0,y0,z0, x1,y1,z1, ...]
        for (int i = 0; i + 2 < verts.Count; i += 3)
            mesh.Vertices.Add(verts[i], verts[i + 1], verts[i + 2]);

        // Faces: variable-length [n, i0..i(n-1), ...]
        if (faces != null)
        {
            int fi = 0;
            while (fi < faces.Count)
            {
                int n = faces[fi++];
                if (n == 3 && fi + 2 < faces.Count)
                {
                    mesh.Faces.AddFace(faces[fi], faces[fi + 1], faces[fi + 2]);
                    fi += 3;
                }
                else if (n == 4 && fi + 3 < faces.Count)
                {
                    mesh.Faces.AddFace(faces[fi], faces[fi + 1], faces[fi + 2], faces[fi + 3]);
                    fi += 4;
                }
                else
                {
                    fi += n;
                }
            }
        }

        // Normals
        var normals = ReadDoubleArray(obj["vertexNormals"]);
        if (normals != null && normals.Count / 3 == mesh.Vertices.Count)
        {
            for (int i = 0; i + 2 < normals.Count; i += 3)
                mesh.Normals.Add((float)normals[i], (float)normals[i + 1], (float)normals[i + 2]);
        }
        else
        {
            mesh.Normals.ComputeNormals();
        }

        // UVs
        var uvs = ReadDoubleArray(obj["textureCoordinates"]);
        if (uvs != null && uvs.Count / 2 == mesh.Vertices.Count)
        {
            for (int i = 0; i + 1 < uvs.Count; i += 2)
                mesh.TextureCoordinates.Add(uvs[i], uvs[i + 1]);
        }

        // Vertex colours
        var colors = ReadIntArray(obj["colors"]);
        if (colors != null && colors.Count == mesh.Vertices.Count)
        {
            foreach (var c in colors)
                mesh.VertexColors.Add(System.Drawing.Color.FromArgb(c));
        }

        mesh.Compact();
        return mesh.IsValid ? mesh : null;
    }

    // ── Brep → display mesh fallback ─────────────────────────────────────────

    private GeometryBase? ConvertBrepFallback(JObject obj)
    {
        // v1: try to decode the base64-encoded native Brep, fall back to display meshes.
        var encoded = obj["encoded"]?.Value<string>();
        if (!string.IsNullOrEmpty(encoded))
        {
            var native = DecodeNative(encoded);
            if (native != null) return native;
        }

        var displayValue = obj["displayValue"] as JArray;
        if (displayValue == null || displayValue.Count == 0) return null;

        // Merge all display meshes into one
        var joined = new Mesh();
        foreach (var item in displayValue)
        {
            if (item is JObject meshObj)
            {
                var m = ConvertMesh(meshObj);
                if (m != null) joined.Append(m);
            }
        }
        joined.Compact();
        return joined.IsValid ? joined : null;
    }

    // ── Native Rhino round-trip (RhinoDataObject) ─────────────────────────────

    private GeometryBase? ConvertRhinoNativeObject(JObject obj)
    {
        // Try rawEncoding (the native .3dm payload) first — perfect round-trip.
        // rawEncoding may be inline or a reference stub resolved by the pipeline.
        var rawEncoding = obj["rawEncoding"] as JObject;
        if (rawEncoding != null)
        {
            var contents = rawEncoding["contents"]?.Value<string>();
            var native = DecodeNative(contents);
            if (native != null) return native;
        }

        // Fall back to display meshes for non-Rhino originators or decode failures.
        return ConvertFromDisplayValue(obj);
    }

    private static GeometryBase? DecodeNative(string? base64Contents)
    {
        if (string.IsNullOrEmpty(base64Contents)) return null;
        try
        {
            var bytes = System.Convert.FromBase64String(base64Contents);
            var file3dm = File3dm.FromByteArray(bytes);
            if (file3dm == null || file3dm.Objects.Count == 0) return null;
            foreach (var f3dmObj in file3dm.Objects)
                return f3dmObj?.Geometry;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── Curves ───────────────────────────────────────────────────────────────

    private GeometryBase? ConvertLine(JObject obj)
    {
        var start = ReadPoint(obj["start"]);
        var end   = ReadPoint(obj["end"]);
        if (start == null || end == null) return null;
        return new LineCurve(start.Value, end.Value);
    }

    private GeometryBase? ConvertPolyline(JObject obj)
    {
        var pts = ReadPoint3dArray(obj["value"]);
        if (pts == null || pts.Count < 2) return null;
        var pl = new Polyline(pts);
        if (obj["closed"]?.Value<bool>() == true && !pl.IsClosed)
            pl.Add(pl[0]);
        return pl.ToPolylineCurve();
    }

    private GeometryBase? ConvertNurbsCurve(JObject obj)
    {
        var degree = obj["degree"]?.Value<int>() ?? 3;
        var ctrlPts = ReadPoint3dArray(obj["points"]);
        var weights = ReadDoubleArray(obj["weights"]);
        var knots   = ReadDoubleArray(obj["knots"]);

        if (ctrlPts == null || ctrlPts.Count == 0) return null;

        bool isRational = obj["rational"]?.Value<bool>() ?? false;
        var nc = new NurbsCurve(3, isRational, degree + 1, ctrlPts.Count);

        for (int i = 0; i < ctrlPts.Count; i++)
        {
            double w = (weights != null && i < weights.Count) ? weights[i] : 1.0;
            nc.Points.SetPoint(i, ctrlPts[i], w);
        }

        if (knots != null)
        {
            for (int i = 0; i < knots.Count && i < nc.Knots.Count; i++)
                nc.Knots[i] = knots[i];
        }

        return nc.IsValid ? nc : null;
    }

    private GeometryBase? ConvertArc(JObject obj)
    {
        var planeObj = obj["plane"] as JObject;
        if (planeObj == null) return null;
        var rhinoPlane = ReadPlane(planeObj);
        if (rhinoPlane == null) return null;

        var radius     = obj["radius"]?.Value<double>() ?? 1.0;
        var startAngle = obj["startAngle"]?.Value<double>() ?? 0.0;
        var endAngle   = obj["endAngle"]?.Value<double>() ?? Math.PI;

        var arc = new Arc(rhinoPlane.Value, radius, endAngle - startAngle);
        arc.StartAngle = startAngle;
        arc.EndAngle   = endAngle;
        return new ArcCurve(arc);
    }

    private GeometryBase? ConvertCircle(JObject obj)
    {
        var planeObj = obj["plane"] as JObject;
        if (planeObj == null) return null;
        var rhinoPlane = ReadPlane(planeObj);
        if (rhinoPlane == null) return null;

        var radius = obj["radius"]?.Value<double>() ?? 1.0;
        var circle = new Circle(rhinoPlane.Value, radius);
        return new ArcCurve(circle);
    }

    // ── Point ─────────────────────────────────────────────────────────────────

    private GeometryBase? ConvertPoint(JObject obj)
    {
        var x = obj["x"]?.Value<double>() ?? 0;
        var y = obj["y"]?.Value<double>() ?? 0;
        var z = obj["z"]?.Value<double>() ?? 0;
        return new Point(new Point3d(x, y, z));
    }

    // ── Generic displayValue fallback ──────────────────────────────────────────

    private GeometryBase? ConvertFromDisplayValue(JObject obj)
    {
        var displayValue = obj["displayValue"] as JArray;
        if (displayValue == null || displayValue.Count == 0) return null;

        var first = displayValue[0] as JObject;
        if (first == null) return null;

        var type = first["speckle_type"]?.Value<string>() ?? "";
        if (type == "Objects.Geometry.Mesh") return ConvertMesh(first);

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<double>? ReadDoubleArray(JToken? token)
    {
        if (token == null) return null;
        var arr = token as JArray;
        if (arr == null) return null;
        return arr.Select(v => v.Value<double>()).ToList();
    }

    private static List<int>? ReadIntArray(JToken? token)
    {
        if (token == null) return null;
        var arr = token as JArray;
        if (arr == null) return null;
        return arr.Select(v => v.Value<int>()).ToList();
    }

    private static Point3d? ReadPoint(JToken? token)
    {
        if (token is not JObject obj) return null;
        var x = obj["x"]?.Value<double>() ?? 0;
        var y = obj["y"]?.Value<double>() ?? 0;
        var z = obj["z"]?.Value<double>() ?? 0;
        return new Point3d(x, y, z);
    }

    private static List<Point3d>? ReadPoint3dArray(JToken? token)
    {
        var flat = ReadDoubleArray(token);
        if (flat == null || flat.Count < 3) return null;
        var pts = new List<Point3d>(flat.Count / 3);
        for (int i = 0; i + 2 < flat.Count; i += 3)
            pts.Add(new Point3d(flat[i], flat[i + 1], flat[i + 2]));
        return pts;
    }

    private static Plane? ReadPlane(JObject obj)
    {
        var originToken = obj["origin"] as JObject;
        var xdirToken   = obj["xdir"]   as JObject;
        var ydirToken   = obj["ydir"]   as JObject;
        if (originToken == null) return null;

        var origin = ReadPoint(originToken) ?? Point3d.Origin;
        var xAxis = xdirToken != null
            ? new Vector3d(
                xdirToken["x"]?.Value<double>() ?? 1,
                xdirToken["y"]?.Value<double>() ?? 0,
                xdirToken["z"]?.Value<double>() ?? 0)
            : Vector3d.XAxis;
        var yAxis = ydirToken != null
            ? new Vector3d(
                ydirToken["x"]?.Value<double>() ?? 0,
                ydirToken["y"]?.Value<double>() ?? 1,
                ydirToken["z"]?.Value<double>() ?? 0)
            : Vector3d.YAxis;

        return new Plane(origin, xAxis, yAxis);
    }
}
