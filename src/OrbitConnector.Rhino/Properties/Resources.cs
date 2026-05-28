// Placeholder for a future embedded brand icon.
//
// ----------------------------------------------------------------------------
// IMPORTANT: this type must NOT reference System.Drawing.Bitmap (or any other
// type from System.Drawing.Common). That was the v0.1.0-v0.1.6 trip-hazard:
//
//   1. We compile against net8.0-windows and reference System.Drawing.Common
//      8.0.0.0 (Eto.Forms doesn't actually need this -- Resources.cs was the
//      sole consumer).
//   2. At runtime, Rhino's plug-in AssemblyLoadContext walks each type's
//      metadata when loading the .rhp. System.Drawing.Bitmap lives in the
//      legacy net6.0-style 'System.Drawing' assembly via [TypeForwardedFrom]
//      to System.Drawing.Common, and the forwarder is stamped with the
//      symbolic Version=0.0.0.0.
//   3. The custom plug-in ALC has no binding-redirect for that synthetic
//      version. It can't satisfy the request from either the bundled
//      System.Drawing.Common 8.0.0.0 sibling DLL nor from the shared
//      framework's copy (versions differ). FileNotFoundException is
//      thrown DURING TYPE LOAD, before any of our code has a chance to
//      register an AssemblyResolve handler -- so even a ModuleInitializer
//      can't rescue it.
//   4. Rhino catches the TypeLoadException, swallows the inner detail, and
//      shows the generic "Unable to load OrbitConnector.Rhino.rhp plug-in:
//      initialization failed." dialog. No stack trace surfaces. (Verified by
//      reflection-loading the v0.1.6 .rhp in an isolated net8.0-windows
//      AssemblyLoadContext: the inner FileNotFoundException for
//      'System.Drawing.Common, Version=0.0.0.0, PublicKeyToken=
//      cc7b13ffcd2ddd51' surfaces.)
//
// The fix is to NOT reference System.Drawing.* anywhere in our metadata.
// Eto.Forms uses Eto.Drawing.Bitmap and Eto.Drawing.Color for any UI
// imagery, so we never need the System.Drawing types at all.
//
// If you need to embed a future icon, expose it as a byte[] payload here
// and let the caller turn it into the platform-appropriate type via
// Eto.Drawing.Bitmap or RhinoCommon's own helpers.
// ----------------------------------------------------------------------------
namespace OrbitConnector.Rhino.Properties
{
    internal static class Resources
    {
        /// <summary>
        /// Reserved for a future embedded brand icon. Returns null today; if
        /// you need to surface a real icon, ship the bytes here and consume
        /// via Eto.Drawing.Bitmap on the call site.
        /// </summary>
        public static byte[]? OrbitIcon16Bytes => null;
    }
}
