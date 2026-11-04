# Contributing to FM26 Visual Mod

Thank you for your interest in contributing! This is an open-source fan project. Contributions are warmly welcomed as long as they maintain two non-negotiable principles:
1. **The repository must remain 100% free of third-party copyrighted game assets.**
2. **All code must be developed under clean-room conditions and licensed under MIT.**

---

## Ground Rules

> [!CAUTION]
> ### 1. Strict Asset Embargo
> **Never commit or attach EA or Sports Interactive assets, or any data extracted from them.**
> This includes:
> - 3D models, meshes, skeletons, animations, textures, audio, or shaders.
> - Game archives (`.cas`, `.cat`, `.sb`, `.toc`, etc.).
> - Decoded or converted proprietary binaries.
> - Private packs generated under `%LOCALAPPDATA%\fm26-visual-mod\packs\`.
> - Interop assemblies generated from Football Manager 26.
> - Dumps of game memory or process state.
> 
> *This embargo applies equally to commits, pull requests, issues, comments, and attachments.*

> [!NOTE]
> ### 2. Visual Bug Reports
> **Screenshots or short video clips of your own game** are welcome in bug reports — they are the best way to demonstrate a visual rendering issue. Do not attach extracted files, and do not post media containing other players' copyrighted modifications.

> [!IMPORTANT]
> ### 3. Clean-Room Engineering Rule
> Do not read, copy, translate, or adapt code from GPL-licensed game-modding tools, editors, or plugins (such as Frostbite editors, proprietary mesh/animation exporters, etc.).
> 
> All file readers and parsers in `tools/fm26vm/` are written **solely from observed file structures and internal unit tests**. If you have studied GPL modding tool source code, state this transparently in your pull request and refrain from authoring code for that specific component.

### 4. License & Origin
By submitting a contribution, you agree that your work is licensed under the repository's [MIT License](LICENSE). Submit only code that you wrote yourself or that is fully MIT-compatible with explicit attribution.

### 5. Privacy & Secrets
Do not commit sensitive or personal information: no API tokens, no absolute user paths with your username, no Steam IDs, and no raw save files.

---

## Development Environment Setup

Ensure you have the following prerequisites installed:
- **OS & Games:** Windows 10/11 x64, Football Manager 26 (Steam), and EA SPORTS FIFA 17 PC (for end-to-end local testing).
- **Python:** **Python 3.11+** (standard library only; tested with 3.14).
- **.NET SDK:** **.NET 8 SDK** (builds both the `net6.0` plugin and the `net8.0` policy tests).
- **Interop Assemblies:** BepInEx 6.0.0-be.788 (Unity IL2CPP, win-x64) and interop DLLs generated locally by BepInEx on your machine after launching FM26 once. Pass their paths using `-p:LoaderDir=...` and `-p:InteropDir=...`.

> For complete compilation and environment instructions, refer to [docs/BUILDING.md](docs/BUILDING.md).

---

## Test-Driven Workflow

Every functional change must begin with a **failing test** that isolates the defect or defines the new capability, followed by the minimal code change necessary to turn the test green. Refactoring must preserve existing test coverage without altering test assertions.

Before opening a pull request, run the complete verification suite:

```bat
:: 1. Run Python test suite (synthetic fixtures only)
python -B -m unittest discover -s tests

:: 2. Run C# policy tests (see docs/BUILDING.md for MSBuild properties)
dotnet run --project tests/PolicyTests/PolicyTests.csproj

:: 3. Run zero-leak security and privacy audit
python -B tools/leak_scan.py . --tracked
```

> [!TIP]
> All three checks must pass with zero errors and zero leak findings. Never commit build artifacts (`bin/`, `obj/`, `*.dll`).

---

## Reporting a Match Bug

When reporting an in-match glitch, please provide:
1. **Environment:** FM26 version, plugin version and SHA-256 (`python -m tools.fm26vm status`), and Windows build.
2. **Context:** Exact situation (match minute, kick-off, substitution, half time, key-moments highlight, fast-forward, red card).
3. **Reproduction:** Does the issue occur in every match or intermittently?
4. **Media:** A screenshot or short video clip showing the visual anomaly.
5. **Diagnostics:** Attach the logs created by the plugin in your FM26 directory:
   - `BepInEx/visual-diagnostics/fifa-overlay-auto-*.json` (from the affected match session)
   - `BepInEx/LogOutput.log`

> [!WARNING]
> Diagnostic files contain **FM player names from your save game** and local system paths. Review them before posting; if you prefer, mention in the issue that you wish to share them privately with the maintainer.

---

## Pull Request Checklist

When submitting a PR, verify each item:

- [ ] A failing test was added first (or the PR is a pure refactoring / documentation update).
- [ ] `python -B -m unittest discover -s tests` passes completely.
- [ ] `dotnet run --project tests/PolicyTests/PolicyTests.csproj` passes (for C# changes).
- [ ] `python -B tools/leak_scan.py . --tracked` reports **0 findings**.
- [ ] No EA / Sports Interactive assets, extracted data, interop assemblies, binaries, or personal data are included.
- [ ] Clean-room principles were respected; the code is original and MIT-licensed.
- [ ] Documentation (`README.md`, `docs/`) is updated if any user-facing behavior changed.

---

## Issue Labels Reference

| Label | Description |
| :--- | :--- |
| `bug` | General software or installer issues |
| `match-visual` | In-match 3D rendering or overlay problems |
| `installer` | Setup, detection, or uninstallation issues |
| `animation` | Locomotion, retargeting, or animation clip blending |
| `performance` | Frame time, memory footprint, or optimization |
| `docs` | Documentation or guide improvements |
| `research` | FM action states, donor file formats, or reverse-engineering |
| `good first issue` | Accessible tasks for new contributors |
| `needs-repro` | Awaiting reproducible steps or diagnostic files |
| `wontfix-assets` | Requests requiring redistribution of proprietary game content |
