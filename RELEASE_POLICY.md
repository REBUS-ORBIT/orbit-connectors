# Release versioning

All ORBIT connectors are released **in lockstep**. Every release of "ORBIT
Connectors v<X.Y.Z>" ships installer artifacts for every supported host on
every supported OS, stamped with the same version, regardless of whether
that connector's code changed in this release.

Rationale: predictable cross-host compatibility, a single download page per
release, and a clear contract that "v0.1.2 works against the same ORBIT
server schema across all hosts".

Bumping the version is therefore a **release-pipeline action**, not a
per-connector action. Push a tag `vX.Y.Z`; CI builds and tags every
connector with that version. Patch releases (e.g. v0.1.3) ship even if only
one connector changed.

## Single source of truth

The canonical version lives at the top of
[`Directory.Build.props`](Directory.Build.props):

```xml
<OrbitConnectorVersion Condition="'$(OrbitConnectorVersion)' == ''">0.1.2</OrbitConnectorVersion>
```

Every csproj in the solution inherits `<Version>`, `<AssemblyVersion>`,
`<FileVersion>`, and `<InformationalVersion>` from this property. No
csproj sets its own version.

`OrbitConnectorPlugin.Version` (Rhino) reads the
`AssemblyInformationalVersionAttribute` baked in at build time, so the
running plugin can always tell you which release it came from.

## How a release happens

1. Update [`CHANGELOG.md`](CHANGELOG.md) with a new `## v<X.Y.Z> — <title>`
   entry describing the changes.
2. Bump the default in `Directory.Build.props` to `<X.Y.Z>` so local builds
   without `-p:OrbitConnectorVersion` produce sensibly-named output.
3. Commit and push.
4. Tag and push:

   ```bash
   git tag v<X.Y.Z>
   git push origin v<X.Y.Z>
   ```

5. The [`release.yml`](.github/workflows/release.yml) workflow fires on
   `tags: v*`. It resolves the version once (stripping the leading `v`) and
   passes it to every per-host build job:

   - **Rhino (Windows)**: `installers/rhino/build-yak.ps1 -Version $env:VERSION`
     and `ISCC /DConnectorVersion=$VERSION`. Both end up calling
     `dotnet build -p:OrbitConnectorVersion=$VERSION` so the resulting
     `.rhp` carries the matching `FileVersion` / `InformationalVersion`.
   - **Rhino (macOS)**: `installers/rhino/build-mac.sh $VERSION` and
     `installers/rhino/build-dmg.sh $VERSION`.
   - **Vectorworks (Windows / macOS)**: `installers/vectorworks/build-windows.ps1
     -Version $VERSION` / `build-macos.sh $VERSION`.
   - **UE5 (Windows / macOS)**: `installers/ue5/build-windows.ps1 -Version
     $VERSION` / `build-macos.sh $VERSION`.

6. The `release` job downloads every artifact, flattens them, and publishes
   a GitHub Release named "ORBIT Connectors v<X.Y.Z>" with the matching
   `CHANGELOG.md` section as the body.

## What "lockstep" means in practice

- All six installer artifacts in a release carry the same `vX.Y.Z` in their
  filename.
- All Windows `.exe` installers report the same `VersionInfoVersion` to
  Explorer (right-click → Properties → Details).
- All Rhino `.yak` packages have the same `version:` field in
  `manifest.yml` inside the archive.
- The Rhino plugin reports the same version through
  `OrbitConnectorPlugin.Version` and surfaces it in the Orbit Eto panel
  footer.

If you ever find a release where these don't match, that's a release-pipeline
bug — file an issue.

## What lockstep does NOT mean

- Lockstep does not mean every connector is feature-complete at every
  release. The Vectorworks and UE5 installers are still scaffold-only and
  ship placeholder payloads; they still carry the lockstep version so the
  release manifest stays internally consistent. Track real plugin progress
  in `src/OrbitConnector.<Host>/README.md`.
- Lockstep does not couple the version to the ORBIT **server** version. The
  server, the SDK, and the connectors are versioned independently; the
  connector version tracks "what build of the connector suite is this".
- Lockstep is not retroactive. v0.1.0 and v0.1.1 predate this policy; their
  per-connector versions reflect the state at the time they were tagged.
  From v0.1.2 onwards every release ships in lockstep.
