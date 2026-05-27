#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-macos.sh -- Build the placeholder ORBIT Connector for Unreal
# Engine 5 .dmg disk image.
#
# v0.1.1 SCAFFOLD. No real UE5 plug-in exists yet (see
# src/OrbitConnector.UE5/README.md). This script just stages a README.txt
# and wraps it into a .dmg via hdiutil.
#
# Usage:
#   installers/ue5/build-macos.sh <VERSION>
#
# Output:
#   installers/ue5/dist/OrbitConnector-UE5-Setup-v<VERSION>.dmg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist"
mkdir -p "$DIST_DIR"

STAGING="$(mktemp -d)/OrbitConnector-UE5-v${VERSION}"
mkdir -p "$STAGING"

cat > "$STAGING/README.txt" <<EOF
ORBIT Connector for Unreal Engine 5
====================================

This .dmg is a placeholder shipped with ORBIT Connectors v${VERSION}.

The actual UE5 plug-in is under development. There is nothing to load
yet -- this README is the entire payload.

When real source lands, this .dmg will deliver a complete .uplugin
folder for installation into:
    <UnrealEngine>/Engine/Plugins/   (engine-wide install)
    <YourProject>/Plugins/           (per-project install)

Watch https://github.com/REBUS-ORBIT/orbit-connectors for updates.
EOF

OUT="$DIST_DIR/OrbitConnector-UE5-Setup-v${VERSION}.dmg"
rm -f "$OUT"

hdiutil create \
  -volname "ORBIT Connector for Unreal Engine 5" \
  -srcfolder "$STAGING" \
  -ov \
  -format UDZO \
  "$OUT"

echo "Produced: $OUT"
ls -la "$OUT"
