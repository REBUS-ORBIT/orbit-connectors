#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-pkg.sh -- SKELETON. Produce a macOS .pkg installer for the ORBIT
# Vectorworks connector once the plug-in source exists.
#
# STATUS: TODO. The Vectorworks connector itself is scaffolding only --
# see ../../../src/OrbitConnector.Vectorworks/README.md and ../README.md.
# The v0.1.1 release pipeline currently ships a .dmg (via build-macos.sh
# + hdiutil) wrapping a placeholder README, which is enough to validate
# the release shape.
#
# When real Vectorworks plug-in source lands, this script should:
#   1. Build the Mac plug-in (delegate to ../build-macos.sh).
#   2. Stage the plug-in payload under a Payload root at the canonical
#      Vectorworks per-user plug-ins path (typically under
#      ~/Library/Application Support/Vectorworks/<year>/Plug-ins/).
#   3. Run `pkgbuild` to produce a component .pkg.
#   4. Run `productbuild` with a Distribution.xml for welcome / licence /
#      conclusion HTML.
#   5. (Optional, prod-only) `xcrun notarytool submit` + `xcrun stapler
#      staple` once an Apple Developer ID Installer cert is available.
#
# Output: ../dist/OrbitConnector-Vectorworks-<version>.pkg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/../dist"

echo "installers/vectorworks/pkg/build-pkg.sh: skeleton only -- pkg flow not yet implemented." >&2
echo "Version requested: $VERSION" >&2
echo "Would emit: $DIST_DIR/OrbitConnector-Vectorworks-$VERSION.pkg" >&2
exit 99
