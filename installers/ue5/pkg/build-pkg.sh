#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-pkg.sh -- SKELETON. Produce a macOS .pkg installer for the ORBIT
# Unreal Engine 5 connector once the plug-in source exists.
#
# STATUS: TODO. The UE5 connector itself is scaffolding only -- see
# ../../../src/OrbitConnector.UE5/README.md and ../README.md. The v0.1.1
# release pipeline currently ships a .dmg (via build-macos.sh + hdiutil)
# wrapping a placeholder README, which is enough to validate the release
# shape.
#
# When real UE5 plug-in source lands, this script should:
#   1. Build the Mac .uplugin (delegate to ../build-macos.sh).
#   2. Stage the .uplugin folder under a Payload root at a sensible
#      per-user location -- ~/Documents/Unreal Projects/Plugins/OrbitConnector/
#      mirrors the Windows installer default.
#   3. Run `pkgbuild` to produce a component .pkg, or alternately ship
#      the .uplugin folder inside the .dmg (a drag-to-Plugins UX is
#      usually clearer for UE plug-ins than a .pkg).
#   4. (Optional, prod-only) notarise.
#
# Output: ../dist/OrbitConnector-UE5-<version>.pkg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/../dist"

echo "installers/ue5/pkg/build-pkg.sh: skeleton only -- pkg flow not yet implemented." >&2
echo "Version requested: $VERSION" >&2
echo "Would emit: $DIST_DIR/OrbitConnector-UE5-$VERSION.pkg" >&2
exit 99
