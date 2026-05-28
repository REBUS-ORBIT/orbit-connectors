#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# build-mac.sh -- Build the ORBIT Rhino connector as a Mac-targeted YAK
# package (arm64 + x64).
#
# SCAFFOLD ONLY -- has not yet been smoke-tested against Rhino 8 Mac.
# See installers/rhino/MACOS.md for the current state of the Mac story.
#
# Expected runtime: macOS 13+ with Xcode CLT, .NET 8 SDK, and a copy of YAK
# on PATH (either from a Rhino 8 install or downloaded from McNeel). GitHub
# Actions `macos-latest` satisfies the first two; the workflow installs YAK
# via Homebrew tap if available, or falls back to extracting it from a Rhino
# Mac SDK download.
#
# Usage:
#   installers/rhino/build-mac.sh [VERSION]
#
# Produces, in installers/rhino/dist/:
#   orbit-connector-<VERSION>-rh8-mac-arm64.yak
#   orbit-connector-<VERSION>-rh8-mac-x64.yak
# -----------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/src/OrbitConnector.Rhino/OrbitConnector.Rhino.csproj"
MANIFEST_SRC="$SCRIPT_DIR/yak/manifest.yml"
DIST_DIR="$SCRIPT_DIR/dist"
STAGE_ROOT="$SCRIPT_DIR/build/yak-stage-mac"

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -E '^version:' "$MANIFEST_SRC" | head -n1 | awk '{print $2}')"
fi
echo "ORBIT Connector Mac build  version=$VERSION"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet SDK not found on PATH" >&2
  exit 1
fi

YAK="${YAK:-$(command -v yak || true)}"
if [[ -z "$YAK" && -d "/Applications/Rhino 8.app" ]]; then
  YAK="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
fi
if [[ -z "$YAK" || ! -x "$YAK" ]]; then
  echo "ERROR: yak executable not found. Set \$YAK or install Rhino 8 for Mac." >&2
  exit 1
fi
echo "Using YAK: $YAK"

# The csproj currently targets net8.0-windows, which can't build on macOS.
# For now we emit a clear failure so the macOS CI job's continue-on-error
# flag picks it up instead of producing a confusing artefact.
#
# TODO: split the project into:
#   OrbitConnector.Rhino.Core      (TargetFrameworks: net8.0)            <-- shared
#   OrbitConnector.Rhino.Windows   (TargetFramework:  net8.0-windows)
#   OrbitConnector.Rhino.Mac       (TargetFramework:  net8.0)
# Then build the Mac project here instead. See MACOS.md for details.
target_framework="$(grep -E '<TargetFramework>' "$CSPROJ" | head -n1 | sed -E 's/.*<TargetFramework>([^<]+)<.*/\1/')"
if [[ "$target_framework" == *"-windows" ]]; then
  cat >&2 <<EOF
ERROR: src/OrbitConnector.Rhino is currently TargetFramework=$target_framework, which
       cannot be built on macOS. The Mac build is scaffolding only -- see
       installers/rhino/MACOS.md for the planned project split.
EOF
  exit 2
fi

mkdir -p "$DIST_DIR"
rm -rf "$STAGE_ROOT"

for arch in arm64 x64; do
  echo
  echo ">>> Building for osx-${arch}"
  # OrbitConnectorVersion is the single source of truth (Directory.Build.props);
  # the csproj inherits Version / AssemblyVersion / FileVersion / InformationalVersion
  # from it. See RELEASE_POLICY.md.
  dotnet publish "$CSPROJ" \
    -c Release \
    -r "osx-${arch}" \
    --self-contained false \
    -p:OrbitConnectorVersion="$VERSION" \
    -o "$STAGE_ROOT/${arch}/publish"

  stage="$STAGE_ROOT/${arch}/stage"
  mkdir -p "$stage"

  # YAK manifest with the resolved version baked in.
  sed -E "s/^version:.*/version: $VERSION/" "$MANIFEST_SRC" > "$stage/manifest.yml"

  # Copy the .rhp (created by the RenameToRhp build target) and dependency DLLs.
  # Rhino-provided assemblies (RhinoCommon, Eto, Rhino.UI) are deliberately
  # excluded -- Rhino loads its own copies at runtime on Mac too.
  rhp_src="$STAGE_ROOT/${arch}/publish/OrbitConnector.Rhino.rhp"
  if [[ ! -f "$rhp_src" ]]; then
    # The publish target may have skipped the RenameToRhp step; copy the dll.
    dll_src="$STAGE_ROOT/${arch}/publish/OrbitConnector.Rhino.dll"
    if [[ ! -f "$dll_src" ]]; then
      echo "ERROR: neither .rhp nor .dll found in $STAGE_ROOT/${arch}/publish/" >&2
      exit 3
    fi
    cp "$dll_src" "$stage/OrbitConnector.Rhino.rhp"
  else
    cp "$rhp_src" "$stage/OrbitConnector.Rhino.rhp"
  fi

  shopt -s nullglob
  for dep in "$STAGE_ROOT/${arch}/publish/"*.dll; do
    name="$(basename "$dep")"
    case "$name" in
      RhinoCommon.dll|Rhino.UI.dll|Eto.dll|Eto.Mac64.dll|Eto.Wpf.dll|Eto.WinForms.dll|Eto.Gtk.dll)
        continue
        ;;
    esac
    cp "$dep" "$stage/"
  done
  shopt -u nullglob

  echo "Staged files:"
  ls -1 "$stage"

  echo
  echo ">>> Running 'yak build' in $stage"
  ( cd "$stage" && "$YAK" build --platform mac )

  built="$(ls -1 "$stage"/*.yak 2>/dev/null | head -n1)"
  if [[ -z "$built" ]]; then
    echo "ERROR: yak build produced no .yak in $stage" >&2
    exit 4
  fi
  dest="$DIST_DIR/orbit-connector-${VERSION}-rh8-mac-${arch}.yak"
  mv -f "$built" "$dest"
  echo "Produced: $dest"
done

echo
echo "Done. Mac artefacts:"
ls -1 "$DIST_DIR"/orbit-connector-"${VERSION}"-rh8-mac-*.yak
