using Newtonsoft.Json;

namespace Orbit.Objects.Primitives;

public class Vector3d : Base.OrbitBase
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }

    public Vector3d() { }
    public Vector3d(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}
