#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-macos.sh -- Build the placeholder ORBIT Connector for Vectorworks
# .dmg disk image.
#
# v0.1.1 SCAFFOLD. No real Vectorworks plug-in exists yet (see
# src/OrbitConnector.Vectorworks/README.md). This script just stages a
# README.txt and wraps it into a .dmg via hdiutil.
#
# Usage:
#   installers/vectorworks/build-macos.sh <VERSION>
#
# Output:
#   installers/vectorworks/dist/OrbitConnector-Vectorworks-Setup-v<VERSION>.dmg
# -----------------------------------------------------------------------------
set -euo pipefail

VERSION="${1:-0.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="$SCRIPT_DIR/dist"
mkdir -p "$DIST_DIR"

STAGING="$(mktemp -d)/OrbitConnector-Vectorworks-v${VERSION}"
mkdir -p "$STAGING"

cat > "$STAGING/README.txt" <<EOF
ORBIT Connector for Vectorworks
================================

This .dmg is a placeholder shipped with ORBIT Connectors v${VERSION}.

The actual Vectorworks plug-in is under development. There is nothing
to load yet -- this README is the entire payload.

Watch https://github.com/REBUS-ORBIT/orbit-connectors for updates.
EOF

OUT="$DIST_DIR/OrbitConnector-Vectorworks-Setup-v${VERSION}.dmg"
rm -f "$OUT"

hdiutil create \
  -volname "ORBIT Connector for Vectorworks" \
  -srcfolder "$STAGING" \
  -ov \
  -format UDZO \
  "$OUT"

echo "Produced: $OUT"
ls -la "$OUT"
