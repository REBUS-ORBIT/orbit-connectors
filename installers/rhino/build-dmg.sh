#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-dmg.sh -- Wrap the ORBIT Rhino connector's macOS installer payload
# into a .dmg disk image.
#
# v0.1.1 SCAFFOLD. The actual macOS connector build is documented in
# installers/rhino/MACOS.md and is still gated on a project split. Until
# that's done, this script ships a "coming soon / use YAK" README inside
# the .dmg so the release pipeline produces a valid mountable image and
# the artifact naming convention stays consistent across connectors:
#
#   dist/OrbitConnector-Rhino-Setup-v<VERSION>.dmg
#
# The .yak Mac scaffolds (orbit-connector-<v>-rh8-mac-{arm64,x64}.yak) are
# the real Mac distribution channel today; see installers/rhino/build-mac.sh.
#
# Usage:
#   installers/rhino/build-dmg.sh <VERSION>
#
# Output:
#   installers/rhino/dist/OrbitConnector-Rhino-Setup-v<VERSION>.dmg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist"
mkdir -p "$DIST_DIR"

STAGING="$(mktemp -d)/OrbitConnector-Rhino-v${VERSION}"
mkdir -p "$STAGING"

cat > "$STAGING/README.txt" <<EOF
ORBIT Connector for Rhino -- macOS distribution
================================================

This .dmg is a scaffold (v${VERSION}).

For Rhino 8 on macOS, install the connector via Rhino's Package Manager
(_PackageManager) or by running:

    yak install orbit-connector-${VERSION}-rh8-mac-arm64.yak     # Apple Silicon
    yak install orbit-connector-${VERSION}-rh8-mac-x64.yak       # Intel

The .yak files are attached to this release alongside the .dmg.

A native .pkg / .dmg installer for Rhino is tracked in
installers/rhino/MACOS.md and depends on splitting the connector
project into Core/Windows/Mac before it can produce a real Mac .rhp on
CI. Watch the release notes for v0.2.x.

  https://github.com/REBUS-ORBIT/orbit-connectors
EOF

OUT="$DIST_DIR/OrbitConnector-Rhino-Setup-v${VERSION}.dmg"
rm -f "$OUT"

hdiutil create \
  -volname "ORBIT Connector for Rhino" \
  -srcfolder "$STAGING" \
  -ov \
  -format UDZO \
  "$OUT"

echo "Produced: $OUT"
ls -la "$OUT"
