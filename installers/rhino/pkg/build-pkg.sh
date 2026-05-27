#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-pkg.sh -- SKELETON. Produce a macOS .pkg installer for the ORBIT
# Rhino connector, for users who don't use YAK / Rhino's Package Manager.
#
# STATUS: TODO. Not validated on a Mac. Not wired into CI.
# See ../MACOS.md for context.
#
# When implemented this script should:
#   1. Build the Mac connector (delegate to ../build-mac.sh or share its
#      staging step).
#   2. Stage the .rhp + dep DLLs under a Payload root that lays out the
#      files at the per-user Rhino plug-ins path:
#        ~/Library/Application Support/McNeel/Rhinoceros/packages/8.0/
#          OrbitConnector/<version>/
#   3. Run `pkgbuild` to produce a component .pkg.
#   4. Run `productbuild` with a Distribution.xml that adds welcome /
#      licence / conclusion HTML.
#   5. (Optional, prod-only) `xcrun notarytool submit` + `xcrun stapler
#      staple` once an Apple Developer ID Installer cert is available.
#
# Output: ../dist/OrbitConnector-Rhino-<version>.pkg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/../dist"

echo "build-pkg.sh: skeleton only -- pkg flow not yet implemented." >&2
echo "Version requested: $VERSION" >&2
echo "Would emit: $DIST_DIR/OrbitConnector-Rhino-$VERSION.pkg" >&2
exit 99
