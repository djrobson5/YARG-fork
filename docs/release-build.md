# Building and releasing a Windows player from CI

This fork ships a GitHub Actions workflow that builds a Windows x64 YARG player and
publishes it as a GitHub Release, so you can hand someone a `.zip` without asking them
to install Unity.

- Workflow: `.github/workflows/build-windows.yml`
- Build entry point: `Assets/Editor/Build/CIBuild.cs` → `Editor.Build.CIBuild.BuildWindows`

Upstream YARG has no Windows CI (only a stale macOS workflow pinned to Unity 2021), so
this is fork-only machinery.

---

## 1. One-time setup: Unity license secrets

GameCI needs a Unity license to run the editor in a container. Unity **no longer supports
manual activation of Personal licenses**, so the `.alf` / `activation.yml` route is dead
for us. Instead, export the license that Unity Hub already created on your machine.

### 1a. Get the `.ulf`

Activate Unity Hub normally on your Windows machine (sign in, pick the Personal license).
Unity writes the license to:

```
C:\ProgramData\Unity\Unity_lic.ulf
```

Confirm it exists:

```powershell
Get-Item C:\ProgramData\Unity\Unity_lic.ulf
```

### 1b. Set the three secrets

The whole file contents go into `UNITY_LICENSE` (it is XML; keep it verbatim, including
the trailing newline). GameCI parses the serial out of it and then activates against
Unity's servers using your account credentials.

PowerShell has no `<` input redirection (it errors with "The '<' operator is reserved for
future use"), so pipe the file in instead:

```powershell
Get-Content -Raw C:\ProgramData\Unity\Unity_lic.ulf |
  gh secret set UNITY_LICENSE --repo djrobson5/YARG-fork
gh secret set UNITY_EMAIL    --repo djrobson5/YARG-fork
gh secret set UNITY_PASSWORD --repo djrobson5/YARG-fork
```

The last two prompt for the value. Use the Unity account email and password that the
license belongs to.

> `UNITY_SERIAL` is only for Plus/Pro licenses. Do not set it for Personal.

A Personal license allows **two** activated seats. CI returns the license at the end of
each run, but a cancelled/killed run can leak a seat. If activation starts failing with
"too many activations", release seats at <https://id.unity.com/en/subscriptions>.

### 1c. Repository settings you must check

`workflow_dispatch` only appears in the Actions UI / `gh workflow run` if the workflow
file exists **on the repository's default branch**. This fork's default branch is
`master`, and the workflow currently lives on `feature/section-fc`.

So one of these has to happen before you can dispatch it:

- merge `feature/section-fc` (or at least `.github/workflows/build-windows.yml`) into
  `master`, **or**
- change the fork's default branch to `feature/section-fc` in the repo settings.

Once the file is on the default branch you can still run the build against any ref with
`--ref` (see below). Pushing a matching tag works regardless of the default branch.

---

## 2. Running a build

### By dispatch

```powershell
gh workflow run build-windows.yml `
  --repo djrobson5/YARG-fork `
  --ref feature/section-fc `
  -f version=v0.15.0-sectionfc.1 `
  -f prerelease=true
```

Watch it:

```powershell
gh run list --repo djrobson5/YARG-fork --workflow build-windows.yml --limit 1
gh run watch --repo djrobson5/YARG-fork <run-id>
```

(`gh run watch` with no run id drops into an interactive picker.)

`version` is used as the release tag, in the release title, and in the asset filename
(`YARG-SectionFC_<version>-Windows-x64.zip`). It is also stamped into
`PlayerSettings.bundleVersion`.

### By tag

Pushing a tag matching `v*-sectionfc*` triggers the same build automatically and always
publishes a pre-release:

```powershell
git tag v0.15.0-sectionfc.1
git push fork v0.15.0-sectionfc.1
```

Pick **one** of the two triggers per version. If you dispatch `v0.15.0-sectionfc.1` and then
push a tag of the same name, both runs target the same release;
`softprops/action-gh-release` updates rather than fails, so the second run just overwrites the
first one's asset. The `concurrency` group is keyed on the version string so the two runs
queue instead of racing, but you still pay for the build twice.

### How long it takes

- **Cold** (no `Library` cache — the first run, or after any change under `Assets/`,
  `Packages/` or `ProjectSettings/`): roughly **60–100 minutes**. Most of it is pulling
  the `unityci/editor:ubuntu-6000.3.5f2-windows-mono-3` image (~5.9 GB compressed, well over
  that unpacked) and importing every asset from scratch. Note that the standalone scripting
  backend is **Mono**, not IL2CPP: `ProjectSettings.asset` sets `scriptingBackend` only for
  Android, so Standalone falls through to the `Mono2x` default, and game-ci accordingly pulls
  the `windows-mono` image. There is no IL2CPP/C++ cross-compile step.
- **Warm** (cache hit): roughly **25–40 minutes**.

The job timeout is 120 minutes. The `Library` cache key hashes `Assets/**`, `Packages/**`
and `ProjectSettings/**`, so an exact hit is rare in active development; the
`restore-keys` fallback still saves most of the import time.

> Treat the cache as best-effort. GitHub gives a repository **10 GB total** of Actions cache
> and evicts least-recently-used entries; a Unity `Library/` for a project this size can
> approach or exceed that by itself, in which case the save is dropped and every run is
> effectively cold. If the logs show the cache never restoring, that is why — the build still
> works, it is just slow.

---

## 3. Installing the result

1. Download `YARG-SectionFC_<version>-Windows-x64.zip` from the release.
2. Unzip anywhere. `YARG.exe` and `YARG_Data/` are at the root of the archive; the workflow
   excludes Unity's `*_BurstDebugInformation_DoNotShip` and
   `*_BackUpThisFolder_ButDontShipItWithYourGame` sidecar folders.
3. Run `YARG.exe`.

**Not compatible with the YARC Launcher.** The launcher only manages installs it
downloaded itself; it will not see this build and cannot update it.

### Where its data lives

The CI build defines `YARG_NIGHTLY_BUILD`, so `PathHelper` puts persistent data under the
`nightly` subfolder:

```
%USERPROFILE%\AppData\LocalLow\YARC\YARG\nightly\
```

A stable/official install uses `...\YARC\YARG\release\` instead, and the editor uses
`...\dev\`. **This means the fork build does not share settings, scores or song cache
with an official YARG install** — they are fully independent. That is a safety feature
(the fork cannot corrupt your real profile), but it also means you have to re-add your
song folders the first time you run it.

If you want to point the build at a specific folder, YARG accepts a persistent-data-path
override on the command line (see `CommandLineArgs.PersistentDataPath`).

The build also shows the nightly dev watermark and reports the version string baked into
`Assets/Resources/version.txt`.

That file is gitignored, so a CI checkout does not have one.
`Assets/Editor/Build/BuildGitCommitVersion.cs` writes it from an `IPreprocessBuildWithReport`
callback, but that runs *inside* the build with a raw `File.WriteAllText` and no
`AssetDatabase` import, so on a clean checkout Unity would never register the new file as a
`TextAsset` and `GlobalVariables.LoadVersion` would fall back to the hardcoded `v0.15`.
`CIBuild.WriteVersionFile` therefore writes **and imports** it before the build starts.

The text comes from `GlobalVariables.LoadVersionFromGit`, which runs in the *editor*
assembly — where `YARG_NIGHTLY_BUILD` is not defined, because `extraScriptingDefines` only
applies to the player compile. So the string is `<branch> b<commit-count> (<short-sha>)`, not
the bare `b<commit-count> (<short-sha>)` a real nightly produces. Under `actions/checkout` the
working tree is detached, so `<branch>` reads `HEAD` — e.g. `HEAD b4213 (51d52d8)`. Cosmetic,
but do not expect it to match an official nightly's format.

---

## 4. Troubleshooting

### License failures

Symptom: the builder step logs `Licensing::Module` errors, `No valid license found`, or
exits during `activate.sh`.

- Re-export the `.ulf`. A stale secret (from a reinstalled Unity, or a re-activated Hub)
  silently stops matching.
- Check `UNITY_EMAIL` / `UNITY_PASSWORD` are the account that owns that license. If the
  account has 2FA, Unity's activation endpoint can reject the password login — in that
  case, generate an app-specific setup or switch the CI account to one without 2FA.
- "Too many activations": free a seat at <https://id.unity.com/en/subscriptions>.
- Do **not** run `activation.yml`. It requests a `.alf` for manual activation, which
  Unity's license portal now refuses for Personal licenses; it is kept only for
  Plus/Pro serials.

### NuGet deadlock / hundreds of missing-type errors

Symptom: thousands of `CS0246 The type or namespace name 'Melanchall' / 'Cysharp' /
'SQLite' could not be found`.

`Assets/Packages/` is gitignored, so the checkout has none of the NuGet DLLs. The
"[Setup] Restore NuGetForUnity packages" step must run **before** the Unity builder step
and must succeed. If it fails (network, or a `dotnet tool install` conflict because the
tool is already present), the Unity step will fail in a confusing way. Check that step's
log first, and confirm `nugetforunity restore "$GITHUB_WORKSPACE"` printed a package list.

### `MissingScriptBuildValidator` aborts the build

Symptom: `BuildFailedException: Build aborted: Found N missing script(s) on '<object>' in
'<asset path>'`.

`Assets/Editor/Build/MissingScriptBuildValidator.cs` runs at `callbackOrder = -100` and
opens every enabled scene and every prefab. A missing script usually means either a
script was deleted/renamed without fixing its references, or an asset's `.meta` was not
committed. Reproduce locally by making a build from the Unity editor (`File > Make
Nightly Build`) — the same validator runs there. It is not a CI-only failure.

### Addressables

Symptom: the game launches but menus, venues or characters are missing/pink, or the log
shows `Unable to load asset ... from the Addressables system`.

`AddressableAssetSettings.asset` has `m_BuildAddressablesWithPlayerBuild: 0`, which means
"use the per-machine editor preference" — and that preference does not exist on a CI
runner. `CIBuild.BuildWindows` therefore calls
`AddressableAssetSettings.BuildPlayerContent(out var result)` explicitly before
`BuildPipeline.BuildPlayer`, and throws if `result.Error` is non-empty. If content is
still missing, check the `[CIBuild] Building Addressables player content...` log line and
the group settings; note that `Assets/StreamingAssets/aa*` is gitignored, so the CI build
is always generating this from scratch.

### Disk space

Symptom: `No space left on device` mid-build.

The `jlumbroso/free-disk-space` step reclaims ~25 GB. If the build outgrows even that,
drop `docker-images: true` (harmless — the Unity image is pulled fresh anyway) or move
the job to a larger runner.

### Ownership / permission errors after the build

The builder runs with `runAsHostUser: true` so build output and `Library/` are owned by
the runner user. If you remove that input, the zip and cache-save steps will need `sudo
chown -R $(id -u):$(id -g) build Library` first.

---

## 5. Building locally without the GUI

The same entry point works from a local batchmode Unity, which is handy for reproducing a
CI failure. **Close the Unity editor first** (it holds a project lock).

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.3.5f2\Editor\Unity.exe" `
  -batchmode -nographics -quit `
  -projectPath "C:\Users\djrob\.gemini\antigravity\scratch\YARG-fork" `
  -buildTarget Win64 `
  -executeMethod Editor.Build.CIBuild.BuildWindows `
  -buildPath "C:\temp\yarg-build" `
  -logFile "C:\temp\yarg-build.log"
```

`-buildTarget Win64` is not optional. Addressables resolves its `[BuildTarget]` profile
variable from `EditorUserBuildSettings.activeBuildTarget`, *not* from the `BuildPlayerOptions`
handed to `BuildPipeline`, so building with the editor switched to another platform would
quietly emit the catalog under the wrong `StreamingAssets/aa/<platform>` folder.
`CIBuild.BuildWindows` refuses to run in that case rather than ship a contentless player.
(game-ci's `unity-builder` always passes `-buildTarget StandaloneWindows64`, so CI is fine.)

`CIBuild` reads, in order of preference: `-customBuildPath` (a full path to the `.exe`,
which is what game-ci passes), then `-buildPath` (a directory), defaulting to
`build/StandaloneWindows64`. The executable name comes from `-customBuildName`
(default `YARG`). `-version` / `-buildVersion` optionally override
`PlayerSettings.bundleVersion`.
