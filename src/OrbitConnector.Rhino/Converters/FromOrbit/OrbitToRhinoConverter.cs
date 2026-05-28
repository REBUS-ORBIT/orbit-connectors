using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Orbit.Sdk.Serialisation;

namespace OrbitConnector.Rhino.Converters.FromOrbit;

/// <summary>
/// Converts a deserialized ORBIT object (represented as a raw <see cref="JObject"/>)
/// back to a Rhino <see cref="GeometryBase"/>.
///
/// Dispatch is on <c>speckle_type</c>:
/// <list type="bullet">
///   <item><c>Objects.Geometry.Mesh</c>             -> <see cref="Mesh"/></item>
///   <item><c>Objects.Geometry.Brep</c>             -> native .3dm if present, else display-mesh fallback</item>
///   <item><c>Objects.Geometry.Surface</c>          -> native .3dm Surface if present, else display-mesh fallback</item>
///   <item><c>Objects.Geometry.SubD</c>             -> native .3dm SubD if present, else display-mesh fallback</item>
///   <item><c>Objects.Geometry.Extrusion</c>        -> native .3dm Extrusion if present, else display-mesh fallback</item>
///   <item><c>Objects.Geometry.PointCloud</c>       -> <see cref="PointCloud"/></item>
///   <item><c>Objects.Geometry.Line</c>             -> <see cref="LineCurve"/></item>
///   <item><c>Objects.Geometry.Polyline</c>         -> <see cref="PolylineCurve"/></item>
///   <item><c>Objects.Geometry.NurbsCurve</c>       -> <see cref="NurbsCurve"/></item>
///   <item><c>Objects.Geometry.Curve</c>            -> <see cref="NurbsCurve"/> (treated as nurbs)</item>
///   <item><c>Objects.Geometry.PolyCurve</c>        -> joined <see cref="PolyCurve"/> from nested segments</item>
///   <item><c>Objects.Geometry.Arc</c>              -> <see cref="ArcCurve"/></item>
///   <item><c>Objects.Geometry.Circle</c>           -> <see cref="ArcCurve"/> (full)</item>
///   <item><c>Objects.Geometry.Ellipse</c>          -> ellipse <see cref="NurbsCurve"/></item>
///   <item><c>Objects.Geometry.Point</c>            -> <see cref="Point"/></item>
///   <item>Contains <c>"RhinoObject"</c>            -> decoded native .3dm geometry</item>
///   <item>Has any <c>displayValue</c>              -> first supported geometry from displayValue</item>
///   <item>Unknown                                  -> null (skipped, warning logged)</item>
/// </list>
///
/// <para>
/// <b>v0.1.13 changes.</b> The v0.1.12 converter only dispatched a small
/// subset of types and only checked <c>obj["encoded"]</c> for the native
/// .3dm payload of a Brep. The live ORBIT wire format -- inherited from
/// Speckle and observed in PRISM / 3DConvert / the legacy Speckle Rhino
/// connector -- carries the native payload under either <c>encoded</c>,
/// <c>encodedValue</c>, or <c>rawEncoding.contents</c> depending on which
/// sender produced the object. The new converter tries all three. It also
/// extends type coverage to Surface / SubD / Extrusion / PointCloud /
/// PolyCurve / Curve / Ellipse, accepts <c>displayValue</c> as either a
/// JArray or a single JObject (some senders emit one, some the other),
/// falls back to a display-mesh union for unsupported native types, and
/// emits a per-object diagnostic line so the next receive bug report is
/// actionable.
/// </para>
/// </summary>
public class OrbitToRhinoConverter
{
    private readonly OrbitDeserializer _deserializer = new();

    /// <summary>If true, every dispatch decision is logged via <c>RhinoApp.WriteLine</c>.</summary>
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Convert a raw ORBIT JSON object to Rhino geometry.
    /// Returns <c>null</c> if the type is not supported or conversion fails.
    /// </summary>
    public GeometryBase? Convert(JObject obj)
    {
        var speckleType = obj["speckle_type"]?.Value<string>() ?? "";
        var orbitId = obj["id"]?.Value<string>() ?? obj["applicationId"]?.Value<string>() ?? "?";

        try
        {
            // Native Rhino round-trip dispatch: any speckle_type containing
            // "RhinoObject" carries a base64 .3dm payload that we want to
            // decode in preference to anything else (perfect round-trip).
            if (speckleType.Contains("RhinoObject"))
                return LogResult(speckleType, orbitId, ConvertRhinoNativeObject(obj));

            // The Speckle Brep / Surface / SubD / Extrusion senders emit a
            // base64 native payload alongside a display-mesh array. Prefer
            // the native payload when present.
            if (HasNativePayload(obj))
            {
                var native = TryDecodeNativeAny(obj);
                if (native != null) return LogResult(speckleType, orbitId, native);
            }

            GeometryBase? result = speckleType switch
            {
                "Objects.Geometry.Mesh"       => ConvertMesh(obj),
                "Objects.Geometry.Brep"       => ConvertBrepFallback(obj),
                "Objects.Geometry.Surface"    => ConvertBrepFallback(obj),
                "Objects.Geometry.SubD"       => ConvertBrepFallback(obj),
                "Objects.Geometry.Extrusion"  => ConvertBrepFallback(obj),
                "Objects.Geometry.PointCloud" => ConvertPointCloud(obj),
                "Objects.Geometry.Line"       => ConvertLine(obj),
                "Objects.Geometry.Polyline"   => ConvertPolyline(obj),
                "Objects.Geometry.NurbsCurve" => ConvertNurbsCurve(obj),
                "Objects.Geometry.Curve"      => ConvertNurbsCurve(obj),
                "Objects.Geometry.PolyCurve"  => ConvertPolyCurve(obj),
                "Objects.Geometry.Arc"        => ConvertArc(obj),
                "Objects.Geometry.Circle"     => ConvertCircle(obj),
                "Objects.Geometry.Ellipse"    => ConvertEllipse(obj),
                "Objects.Geometry.Point"      => ConvertPoint(obj),
                _                             => null
            };

            // Universal displayValue fallback. Triggers when the primary
            // dispatch returned null (unsupported / parse failure) or when
            // the type wasn't recognised at all. Many Speckle BuiltElements
            // (Beam, Wall, Floor, ...) only carry meaningful geometry on
            // displayValue; without this the connector silently drops them.
            if (result == null)
                result = ConvertFromDisplayValue(obj);

            return LogResult(speckleType, orbitId, result);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[ORBIT] Convert failed for type='{speckleType}' id={orbitId}: {ex.Message}");
            return null;
        }
    }

    private GeometryBase? LogResult(string speckleType, string orbitId, GeometryBase? result)
    {
        if (Verbose)
        {
            var kind = result?.GetType().Name ?? "null";
            RhinoApp.WriteLine($"[ORBIT] convert: type='{speckleType}' id={orbitId} -> {kind}");
        }
        return result;
    }

    // -- Mesh ----------------------------------------------------------------

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
                // Speckle face encoding sometimes packs the leading n as
                // (3 + actualCount) for n-gons (n==0 means triangle in some
                // legacy Speckle versions). We treat n as the literal vertex
                // count and accept 3 / 4 explicitly; any other value we step
                // over to keep the cursor advancing.
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
                else if (n > 4 && fi + n - 1 < faces.Count)
                {
                    // n-gon: fan-triangulate so Rhino still gets a usable mesh.
                    int v0 = faces[fi];
                    for (int t = 1; t < n - 1; t++)
                        mesh.Faces.AddFace(v0, faces[fi + t], faces[fi + t + 1]);
                    fi += n;
                }
                else
                {
                    fi += Math.Max(0, n);
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

    // -- Brep / Surface / SubD / Extrusion: native or display-mesh fallback --

    private GeometryBase? ConvertBrepFallback(JObject obj)
    {
        var native = TryDecodeNativeAny(obj);
        if (native != null) return native;

        // Display-mesh fallback: Speckle senders typically attach one or more
        // meshes under `displayValue` for non-Speckle hosts. Merge them all
        // so layer state and material attribute application sees a single
        // geometry. This was the v0.1.12 behaviour for Brep specifically;
        // v0.1.13 routes Surface / SubD / Extrusion through the same code
        // so they bake instead of being silently dropped.
        return MergeDisplayValueMeshes(obj);
    }

    // -- Native Rhino round-trip (RhinoDataObject) ---------------------------

    private GeometryBase? ConvertRhinoNativeObject(JObject obj)
    {
        var native = TryDecodeNativeAny(obj);
        if (native != null) return native;
        return ConvertFromDisplayValue(obj);
    }

    /// <summary>
    /// Returns true if the object carries a base64-encoded native .3dm
    /// payload anywhere we know to look (encoded / encodedValue /
    /// rawEncoding / @rawEncoding).
    /// Used by <see cref="Convert"/> to short-circuit the type dispatch when
    /// a perfect round-trip is available regardless of declared type.
    /// </summary>
    private static bool HasNativePayload(JObject obj)
    {
        if (obj["encoded"]?.Type == JTokenType.String) return true;
        if (obj["encodedValue"]?.Type == JTokenType.String) return true;
        // v0.1.14: probe both `rawEncoding` (connector shape) and
        // `@rawEncoding` (PRISM / monorepo-SDK shape, Speckle's
        // [DetachProperty] convention preserves the `@` prefix on the
        // wire and the ORBIT server stores it as-is).
        var raw = obj["rawEncoding"] ?? obj["@rawEncoding"];
        if (raw is JObject re && re["contents"]?.Type == JTokenType.String) return true;
        return false;
    }

    /// <summary>
    /// Try every known location for a base64 .3dm payload. Returns the
    /// first successfully-decoded geometry, or null.
    /// </summary>
    private static GeometryBase? TryDecodeNativeAny(JObject obj)
    {
        var encoded = obj["encoded"]?.Value<string>();
        var native = DecodeNative(encoded);
        if (native != null) return native;

        var encodedValue = obj["encodedValue"]?.Value<string>();
        native = DecodeNative(encodedValue);
        if (native != null) return native;

        // v0.1.14: accept either `rawEncoding` or `@rawEncoding`. PRISM's
        // upstream resolver in RhinoReceivePipeline already normalises
        // `@rawEncoding` -> `rawEncoding` before this converter runs, but
        // nested converters (PolyCurve.segments etc.) can re-enter Convert
        // with un-normalised payloads, so we defensively accept both here.
        if ((obj["rawEncoding"] ?? obj["@rawEncoding"]) is JObject rawEncoding)
        {
            var contents = rawEncoding["contents"]?.Value<string>();
            native = DecodeNative(contents);
            if (native != null) return native;
        }

        return null;
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

    private GeometryBase? MergeDisplayValueMeshes(JObject obj)
    {
        var items = EnumerateDisplayValueItems(obj).ToList();
        if (items.Count == 0) return null;

        // Single item: use it directly so downstream sees the exact geometry
        // rather than a 1-element merge wrapper.
        if (items.Count == 1)
        {
            var single = ConvertMesh(items[0]);
            return single?.IsValid == true ? single : null;
        }

        var joined = new Mesh();
        foreach (var item in items)
        {
            var m = ConvertMesh(item);
            if (m != null) joined.Append(m);
        }
        joined.Compact();
        return joined.IsValid ? joined : null;
    }

    // -- Curves --------------------------------------------------------------

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
        var degree  = obj["degree"]?.Value<int>() ?? 3;
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
            // Speckle knot vectors are typically (controlPoints + degree - 1)
            // long; Rhino's NurbsCurve.Knots is (controlPoints + degree - 1)
            // long too, so the array maps 1:1 with no padding. If a sender
            // ever ships a clamped Rhino-style vector with two extra knots,
            // we just take the first N entries Rhino accepts.
            for (int i = 0; i < knots.Count && i < nc.Knots.Count; i++)
                nc.Knots[i] = knots[i];
        }

        return nc.IsValid ? nc : null;
    }

    private GeometryBase? ConvertPolyCurve(JObject obj)
    {
        // PolyCurve is a sequence of subordinate curves under "segments".
        // Each segment is itself a Curve / Line / Arc / etc; we recurse via
        // Convert to reuse the full dispatch logic, then join.
        if (obj["segments"] is not JArray segments || segments.Count == 0)
            return null;

        var pc = new PolyCurve();
        foreach (var seg in segments)
        {
            if (seg is not JObject segObj) continue;
            var geom = Convert(segObj);
            if (geom is Curve curve)
            {
                pc.Append(curve);
            }
        }
        if (pc.SegmentCount == 0) return null;
        return pc;
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

        var arc = new Arc(rhinoPlane.Value, radius, endAngle - startAngle)
        {
            StartAngle = startAngle,
            EndAngle   = endAngle,
        };
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

    private GeometryBase? ConvertEllipse(JObject obj)
    {
        var planeObj = obj["plane"] as JObject;
        if (planeObj == null) return null;
        var rhinoPlane = ReadPlane(planeObj);
        if (rhinoPlane == null) return null;

        var radius1 = obj["firstRadius"]?.Value<double>()  ?? obj["radius1"]?.Value<double>() ?? 1.0;
        var radius2 = obj["secondRadius"]?.Value<double>() ?? obj["radius2"]?.Value<double>() ?? 1.0;
        var ellipse = new Ellipse(rhinoPlane.Value, radius1, radius2);
        return ellipse.ToNurbsCurve();
    }

    // -- Point / PointCloud --------------------------------------------------

    private GeometryBase? ConvertPoint(JObject obj)
    {
        var x = obj["x"]?.Value<double>() ?? 0;
        var y = obj["y"]?.Value<double>() ?? 0;
        var z = obj["z"]?.Value<double>() ?? 0;
        return new Point(new Point3d(x, y, z));
    }

    private GeometryBase? ConvertPointCloud(JObject obj)
    {
        // Speckle PointCloud: points = flat [x,y,z, x,y,z, ...]; optional
        // colors = [argb, argb, ...] (one per point).
        var pts = ReadPoint3dArray(obj["points"]);
        if (pts == null || pts.Count == 0) return null;

        var pc = new PointCloud();
        var colors = ReadIntArray(obj["colors"]);
        for (int i = 0; i < pts.Count; i++)
        {
            if (colors != null && i < colors.Count)
                pc.Add(pts[i], System.Drawing.Color.FromArgb(colors[i]));
            else
                pc.Add(pts[i]);
        }
        return pc.Count > 0 ? pc : null;
    }

    // -- Generic displayValue fallback ---------------------------------------

    private GeometryBase? ConvertFromDisplayValue(JObject obj)
    {
        foreach (var item in EnumerateDisplayValueItems(obj))
        {
            var type = item["speckle_type"]?.Value<string>() ?? "";
            // Recurse so nested displayValue meshes / breps / curves all
            // route through the full type dispatch (gives e.g. PolyCurve
            // displayValue arrays, surface displayValue meshes, etc.).
            if (type.StartsWith("Objects.Geometry."))
            {
                var nested = Convert(item);
                if (nested != null) return nested;
            }
        }

        // Last resort: treat displayValue as a list of meshes and merge.
        return MergeDisplayValueMeshes(obj);
    }

    /// <summary>
    /// Yield every JObject under <c>displayValue</c>, regardless of whether
    /// the sender emitted it as an array (Speckle Rhino connector, PRISM,
    /// 3DConvert) or a single object (some Speckle Python / JS variants).
    ///
    /// <para>
    /// v0.1.14: also probes <c>@displayValue</c>. PRISM / the monorepo SDK
    /// marks <c>displayValue</c> as <c>[DetachProperty]</c> on
    /// <c>RhinoDataObject</c>, and Speckle's serialiser preserves the <c>@</c>
    /// prefix on the wire — the ORBIT server stores it as-is. The receive
    /// pipeline normalises this back to <c>displayValue</c> before invoking
    /// the converter, but nested converters (PolyCurve segments, Brep
    /// fallback recursion) re-enter <see cref="Convert"/> with un-normalised
    /// payloads, so we accept both names defensively here.
    /// </para>
    /// </summary>
    private static IEnumerable<JObject> EnumerateDisplayValueItems(JObject obj)
    {
        var dv = obj["displayValue"] ?? obj["@displayValue"];
        if (dv == null) yield break;
        switch (dv)
        {
            case JArray arr:
                foreach (var item in arr)
                    if (item is JObject jo) yield return jo;
                break;
            case JObject one:
                yield return one;
                break;
        }
    }

    // -- Helpers -------------------------------------------------------------

    private static List<double>? ReadDoubleArray(JToken? token)
    {
        if (token is not JArray arr) return null;
        return arr.Select(v => v.Value<double>()).ToList();
    }

    private static List<int>? ReadIntArray(JToken? token)
    {
        if (token is not JArray arr) return null;
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
        if (originToken == null) return null;
        var xdirToken   = obj["xdir"]   as JObject;
        var ydirToken   = obj["ydir"]   as JObject;

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
