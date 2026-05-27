using System.Runtime.InteropServices;

// -----------------------------------------------------------------------------
// Rhino plug-in identity GUID -- stable across releases.
//
// Rhino reads this attribute from the compiled .rhp at load time and uses it
// as the plug-in's persistent ID in
//   HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\{<this-guid>}.
//
// The Inno Setup installer (installers/rhino/inno/OrbitConnector.Rhino.iss)
// writes the same GUID into that registry path so Rhino auto-discovers the
// plug-in on next start without the user having to drag-drop the .rhp first.
//
// IF YOU CHANGE THIS GUID YOU MUST ALSO UPDATE THE INSTALLER.
// Do not regenerate. Same constraint as PRISM agent's plug-in id.
// -----------------------------------------------------------------------------
[assembly: Guid("4F3A2B1C-8E5D-4A9F-B6C2-1D7E3F4A5B6C")]
