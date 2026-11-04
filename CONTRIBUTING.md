# Contributing

Thanks for your interest. This is a small fan project; contributions are welcome as long as they keep the
repository **free of third-party game content** and keep the code **clean-room and MIT**.

## Ground rules

1. **Never commit or attach EA / Sports Interactive assets or anything extracted from them**: models, meshes,
   skeletons, animations, textures, audio, game archives (`.cas`, `.cat`, `.sb`, `.toc`, ...), decoded or converted
   binaries, private packs (`%LOCALAPPDATA%\fm26-visual-mod\packs\`), interop assemblies generated from FM26, or
   dumps of game memory. This applies to commits, pull requests, issues and comments alike.
2. **Screenshots or short clips of your own game** are fine in a bug report (they are the best way to show a visual
   problem). Do not attach extracted files instead, and do not post screenshots of other people's content.
3. **Clean-room rule**: do not read, copy, translate or port code from GPL-licensed game-modding tools, editors or
   plugins (FIFA/Frostbite editors, mesh/animation exporters and similar). Readers for donor formats in
   `tools/fm26vm/` are written from observed data and our own tests only. If you have studied such code, say so in
   your pull request and do not work on the same component.
4. **License**: by contributing you agree that your contribution is licensed under the MIT license of this
   repository ([LICENSE](LICENSE)). Only submit code you wrote yourself or that is MIT-compatible and attributed.
5. **No secrets or personal data**: no tokens, no absolute paths with your user name, no Steam IDs, no save files.

## Development setup

- Windows 10/11 x64, Football Manager 26 (Steam) and EA SPORTS FIFA 17 PC for end-to-end testing.
- **Python 3.11+** (tested with 3.14), standard library only.
- **.NET 8 SDK** (the plugin targets net6.0, the policy tests net8.0).
- **BepInEx 6.0.0-be.788 (Unity IL2CPP, win-x64)** staged locally, plus the interop assemblies BepInEx generates on
  your machine after one FM26 launch; pass them with `-p:LoaderDir=...` and `-p:InteropDir=...`.
- Full build and verification steps: [docs/BUILDING.md](docs/BUILDING.md).

## Test-first workflow

Every change of behaviour starts with a **failing test** that shows the problem or the new behaviour; then the
smallest change that makes it pass. Refactorings keep the existing tests green without editing them.

Before opening a pull request, run:

```bat
:: Python suite (synthetic data only; real-data tests skip themselves)
python -B -m unittest discover -s tests

:: C# policy tests (see docs/BUILDING.md for the -p: properties)
dotnet run --project tests/PolicyTests/PolicyTests.csproj

:: the published tree must stay free of game data, binaries, personal data and secrets
python -B tools/leak_scan.py . --tracked
```

All three must pass. Do not commit build outputs (`bin/`, `obj/`, `*.dll`).

## Reporting a match bug

Please include:

- FM26 version, plugin version (`python -m tools.fm26vm status`), Windows version;
- what happened and when (minute, substitution, half time, key-moments / fast-forward, red card, ...);
- a screenshot or short clip of your own game if the problem is visual;
- the diagnostics written by the plugin in your FM26 folder:
  - `BepInEx/visual-diagnostics/fifa-overlay-auto-*.json` (the ones from the affected match)
  - `BepInEx/LogOutput.log`

These files contain **FM player names from your save** (and local paths). Check them before posting; if you prefer,
say so in the issue and share them privately with the maintainer instead of attaching them publicly.

## Pull request checklist

- [ ] A failing test was added first (or the change is a pure refactor / documentation).
- [ ] `python -B -m unittest discover -s tests` passes.
- [ ] `tests/PolicyTests` passes (for C# changes).
- [ ] `python -B tools/leak_scan.py . --tracked` reports 0 findings.
- [ ] No EA / SI assets, extracted data, interop assemblies, binaries or personal data are included.
- [ ] Clean-room rule respected; the contribution is MIT.
- [ ] README / docs updated if user-visible behaviour changed.

## Suggested issue labels

`bug`, `match-visual` (in-match rendering), `installer`, `animation`, `performance`, `docs`, `research`
(e.g. FM action state, donor formats), `good first issue`, `needs-repro`, `wontfix-assets` (requests that would
require redistributing game content).
