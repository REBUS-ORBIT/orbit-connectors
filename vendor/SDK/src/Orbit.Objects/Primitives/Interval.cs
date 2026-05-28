using Newtonsoft.Json;

namespace Orbit.Objects.Primitives;

public class Interval : Base.OrbitBase
{
    [JsonProperty("start")] public double Start { get; set; }
    [JsonProperty("end")]   public double End   { get; set; }

    public Interval() { }
    public Interval(double start, double end) { Start = start; End = end; }

    public double Length => Math.Abs(End - Start);
    public double Mid => (Start + End) / 2.0;
}
