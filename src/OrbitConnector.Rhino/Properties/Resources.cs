// Placeholder - replace OrbitIcon16 with an actual embedded PNG resource.
// Add your icon as Resources/orbit-icon-16.png with Build Action = EmbeddedResource,
// then generate this file via Project > Properties > Resources in Visual Studio.
namespace OrbitConnector.Rhino.Properties
{
    internal static class Resources
    {
        // System.Drawing.Bitmap is Windows-only on .NET 8+.
        // On Windows (where the plugin actually runs) the real type is used.
        // On Linux / CI without Rhino, we expose object? so the rest of the code
        // can still reference OrbitIcon16 without a platform guard everywhere.
#if WINDOWS
        public static System.Drawing.Bitmap? OrbitIcon16 => null; // TODO: embed icon
#else
        public static object? OrbitIcon16 => null;
#endif
    }
}
