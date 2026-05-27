# macOS build — current state

> **TL;DR:** the macOS half of the release pipeline is **scaffolding only** in
> v0.1.0. It is wired up in CI (`build-macos` job) but is marked
> `continue-on-error: true` because the project is not yet structured to
> compile on macOS. A first stable Mac release requires the work in the
> "Required follow-up" section below, on a real Mac with Rhino 8 installed.

## What ships in v0.1.0

| Target                              | Source              | Status      |
|-------------------------------------|---------------------|-------------|
| `orbit-connector-<v>-rh8-win.yak`   | YAK (Windows)       | **Working** |
| `OrbitConnector-Rhino-Setup-v<v>.exe` | Inno Setup (Win)  | **Working** |
| `orbit-connector-<v>-rh8-mac-arm64.yak` | YAK (macOS)     | Scaffold    |
| `orbit-connector-<v>-rh8-mac-x64.yak`   | YAK (macOS)     | Scaffold    |
| `OrbitConnector-Rhino-<v>.pkg`      | `productbuild`      | Skeleton    |

## Why the Mac build is scaffolded, not real

`src/OrbitConnector.Rhino/OrbitConnector.Rhino.csproj` currently targets
`net8.0-windows`. That target framework pulls in Windows-only types
(`System.Windows.Forms`, parts of `System.Drawing` on Windows specifically)
and **cannot be restored or built on macOS** by `dotnet`. `build-mac.sh`
detects this and exits with a clear error rather than producing a broken
package.

## Required follow-up to make Mac real

1. **Split the project** into a shared core + per-platform host:

   ```text
   src/OrbitConnector.Rhino.Core/      TargetFramework=net8.0
       (geometry conversion, networking, models — no UI)
   src/OrbitConnector.Rhino.Windows/   TargetFramework=net8.0-windows
       (Eto.Wpf, Windows-specific bits)
   src/OrbitConnector.Rhino.Mac/       TargetFramework=net8.0
       (Eto.Mac64, Mac-specific bits)
   ```

   The bulk of the converter code under `Converters/ToOrbit/` is already
   platform-agnostic and only needs to lose its `net8.0-windows` baggage.

2. **Cross-compilation note.** Rhino's Mac plug-in surface uses the same
   .NET-based plug-in model and the **same `.rhp` extension** as Windows.
   The actual binary, however, references `Eto.Mac64` (and possibly Apple
   frameworks via `Xamarin.Mac`-style bindings) which are only resolvable
   on macOS. Practically: the Mac `.rhp` must be built on a macOS runner.

3. **Bundle layout.** Modern Rhino 8 Mac accepts a single Mac-built `.rhp`
   file just like Windows; the older `.macrhp` bundle directory format is
   no longer required for plug-ins delivered through YAK. Stick to plain
   `.rhp` unless McNeel docs say otherwise for your specific binding.

4. **Smoke test on a real Mac.** Once the project split is done:

   ```bash
   installers/rhino/build-mac.sh 0.1.0
   open installers/rhino/dist/orbit-connector-0.1.0-rh8-mac-arm64.yak
   ```

   then load the package in Rhino 8 Mac and verify the panel opens.

5. **Apple notarisation.** Plug-ins distributed **through YAK / Rhino's
   Package Manager** inherit Rhino's own notarisation context and do not
   need to be individually notarised. Plug-ins distributed as standalone
   `.pkg` (see `pkg/build-pkg.sh`) **do** need notarisation when shipped
   outside the Mac App Store, which requires:

   - An Apple Developer ID Installer certificate (paid Apple Developer
     account, ~$99/year).
   - `xcrun notarytool submit` + `xcrun stapler staple` in CI.

   This is the same posture as Windows code signing — both are parked for
   v0.1.x.

## References

- [Rhino 8 Mac SDK](https://developer.rhino3d.com/guides/rhinocommon/your-first-plugin-mac/)
- [YAK manifest reference](https://developer.rhino3d.com/guides/yak/the-anatomy-of-a-package/)
- [Rhino plug-in target frameworks](https://developer.rhino3d.com/guides/rhinocommon/installing-tools-mac/)
- [Apple Developer ID + notarytool](https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution)

## TODO

- [ ] Split csproj into Core / Windows / Mac as described above.
- [ ] Confirm the correct macOS YAK CLI download URL. The v0.1.0 release
      pipeline tried `https://files.mcneel.com/yak/tools/latest/yak` and
      got HTTP 404, so `build-macos` failed before reaching `build-mac.sh`.
      McNeel may only ship YAK bundled inside Rhino for Mac (so the right
      path is to pull `yak` out of `/Applications/Rhino 8.app/Contents/
      Resources/bin/yak`), or there may be a different release URL.
- [ ] Re-run `build-mac.sh` on `macos-latest` and confirm both arm64 and x64
      `.yak` files are produced.
- [ ] Load the YAK on a real Rhino 8 Mac install and validate the panel and
      a round-trip send/receive against a dev ORBIT server.
- [ ] Decide whether the optional `.pkg` flow is worth keeping; if yes, fund
      an Apple Developer ID + wire notarytool into `pkg/build-pkg.sh`.
- [ ] Flip the `build-macos` job's `continue-on-error` off once the Mac
      build succeeds reliably.
