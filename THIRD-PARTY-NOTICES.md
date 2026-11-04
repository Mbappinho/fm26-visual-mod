# Third-Party Notices & Licenses

The source code authored for this repository is licensed under the [MIT License](LICENSE). This applies across all repository files, whether or not individual source headers contain explicit `SPDX-License-Identifier` tags.

---

## Runtime & Staged Components (not redistributed)

The third-party components below are **not redistributed** by this repository: they are not versioned here and are not bundled in its release archives.

The installer tools (`tools/fm26vm` `fetch-tools` / `setup`, and `tools/deploy.py`) download them dynamically from official release servers (using pinned HTTPS URLs and SHA-256 hash checks) or read a copy supplied directly by the user, and stage them on the local computer. Their respective upstream licenses govern those installations:

| Component | Version | License | Usage Description |
| :--- | :--- | :--- | :--- |
| **Zstandard** (`zstd`, Meta Platforms) | 1.5.7 | `BSD-3-Clause OR GPL-2.0-only` (used under BSD-3-Clause) | Command-line utility downloaded into local tools folder to decompress FIFA 17 game data on the user's computer. Never shipped with this repository. |
| **BepInEx** (BepInEx Team) | 6.0.0-be.788 (Unity IL2CPP win-x64) | `LGPL-2.1` | Mod loader downloaded from official servers (pinned SHA-256) or supplied by the user via `--bepinex`. Staged in Football Manager 26 directory; fully removable via `uninstall`. |
| **.NET Runtime** (Microsoft / .NET Foundation) | Embedded in BepInEx IL2CPP build (`dotnet/`) | `MIT` | Embedded runtime components within the BepInEx archive; installed locally, never distributed here. |
| **Unity Doorstop** (`winhttp.dll`, `doorstop_config.ini`) | Embedded in BepInEx build | `LGPL-2.1` | Injection proxy packaged inside BepInEx; staged and uninstalled alongside the loader. |

> [!NOTE]
> Football Manager 26 and EA SPORTS FIFA 17 files, assets, and any data derived from them remain the exclusive intellectual property of their respective copyright holders.
> 
> They are never redistributed: private packs stay in the user's `%LOCALAPPDATA%/fm26-visual-mod/packs/` folder and `tools/deploy.py` refuses to deploy them.

---

## Build-time references (not redistributed)

Compiling the plugin (`FM26.VisualDiagnostics.csproj`) requires linking against the reference assemblies listed below. They are referenced from the user's own BepInEx install / unpacked BepInEx zip (`LoaderDir`) and from the interop assemblies that BepInEx generates from the user's own Football Manager 26 install (`InteropDir`).

None of them is committed to this repository or bundled in its releases; the plugin links to them dynamically at run time:

| Component | Version | License | Reference Scope |
| :--- | :--- | :--- | :--- |
| **BepInEx 6 Core** (`BepInEx.Core`, `BepInEx.Unity.Common`, `BepInEx.Unity.IL2CPP`) | 6.0.0-be.788 | `LGPL-2.1` | Compile-time reference resolved from `LoaderDir`; not redistributed. |
| **Il2CppInterop** (`Il2CppInterop.Runtime`, BepInEx) | as shipped in BepInEx be.788 | `LGPL-3.0` | Compile-time reference resolved from `LoaderDir`; not redistributed. |
| **AssetRipper.Primitives** | as shipped in BepInEx be.788 | `MIT` | Compile-time reference resolved from `LoaderDir`; not redistributed. |
| **Unity & Football Manager 26 Interop** (`UnityEngine.*`, `FM.*`, `SI.*`, `Unity.*`, `Il2Cppmscorlib`) | generated locally by BepInEx | Proprietary (owners of Unity and Football Manager 26) | Compile-time reference generated on the user's machine (`InteropDir`); never redistributed. |

> [!TIP]
> No NuGet package is referenced by `FM26.VisualDiagnostics.csproj` or `tests/PolicyTests/PolicyTests.csproj`.
