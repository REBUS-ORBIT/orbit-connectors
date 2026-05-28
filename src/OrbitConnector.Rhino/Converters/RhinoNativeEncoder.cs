using Rhino.FileIO;
using Rhino.Geometry;

namespace OrbitConnector.Rhino.Converters;

/// <summary>
/// Encodes any Rhino <see cref="GeometryBase"/> as the bytes of a single-object
/// <c>.3dm</c> file. Used to fill the <c>contents</c> field of an ORBIT
/// <see cref="Orbit.Objects.Other.RawEncoding"/> so a receiving connector can
/// reconstruct the native geometry (Brep, Extrusion, SubD, Surface, ...) byte-
/// for-byte.
/// <para>
/// This is the mechanism that makes the ORBIT connector a real round-trip
/// transport between Rhino and other CAD applications — the receiver loads the
/// returned <see cref="File3dm"/> and pulls out the first object's geometry.
/// </para>
/// </summary>
internal static class RhinoNativeEncoder
{
    /// <summary>
    /// Returns a base64-encoded single-object <c>.3dm</c> file, or <c>null</c>
    /// if encoding failed for any reason. A null return is a soft-failure
    /// signal — the converter should still produce a display-mesh-only
    /// <see cref="Orbit.Objects.Geometry.Mesh"/> so the viewer remains usable.
    /// </summary>
    public static string? Encode(GeometryBase geometry)
    {
        try
        {
            using var file3dm = new File3dm();

            // RhinoVersion 8 keeps the wire format aligned with the SDK we
            // build against. Lower versions would still load in Rhino 8
            // but may lose features (e.g. SubD precision).
            file3dm.Objects.Add(geometry, attributes: null);

            var options = new File3dmWriteOptions
            {
                Version = 8,
            };
            var bytes = file3dm.ToByteArray(options);
            if (bytes == null || bytes.Length == 0)
                return null;

            return System.Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }
}
