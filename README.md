# FM26 Visual Mod — FIFA 17 local render

A BepInEx (IL2CPP) plugin for **Football Manager 26** (Steam, Windows) that replaces **only the in-match 3D visuals**
of the players: during the 3D match engine, each FM player is drawn with a FIFA 17 player body and FIFA 17
locomotion animations (walk / jog / run / stand clips) instead of the native FM model.

The match simulation, tactics, ratings, results, saves and every other part of the game are untouched: the plugin
only reads the match state and draws on top of it, and hides the native player model while its overlay is active.

> **No EA or Sports Interactive assets are distributed by this repository.** It contains only original code
> (MIT) and a small numeric *recipe* (asset keys, byte ranges and hashes) that lets the installer find and verify
> the right data inside **your own FIFA 17 PC installation**. Every model and animation is extracted and converted
> **locally, on your machine, from your own copy of the game**, into a private pack under
> `%LOCALAPPDATA%\fm26-visual-mod\packs\`. That pack must never be shared or published.
>
> This project is a fan-made mod. It is **not affiliated with, endorsed by or sponsored by Electronic Arts,
> EA SPORTS, Sports Interactive or SEGA**. "FIFA", "EA SPORTS" and "Football Manager" are trademarks of their
> respective owners and are used here only to describe compatibility. No logos are used.
>
> **Use at your own risk.** You are responsible for respecting the end-user licence agreements (EULAs) and terms of
> service of Football Manager 26, EA SPORTS FIFA 17, Steam and any other software you use with this mod.

## What it does / does not do

Does:

- detects FM26 (Steam library) and your FIFA 17 PC install, read-only;
- verifies that your FIFA 17 build matches the recipe (streaming hashes, read-only);
- decodes the needed FIFA 17 data locally (pinned `zstd`) and builds a private, verified pack
  (player skeleton + mesh pieces + 10 locomotion clips retargeted to the FM skeleton);
- installs BepInEx 6 (be.788) and the plugin into the FM26 folder, with backups and a hash-checked uninstall;
- renders the FIFA 17 body with the locomotion clips on every player during 3D matches, and restores the
  native FM players when the overlay stops (match end, scene change, error).

Does not:

- change the match engine, results, or any game data / save file;
- redistribute any FIFA 17, FM26 or other third-party asset (models, textures, animations, logos);
- provide faces, kits, textures or per-player likeness (a single neutral body is used for everyone);
- play FIFA gestures (passes, shots, tackles, celebrations): only locomotion is mapped.

## Requirements

- Windows 10/11 x64.
- **Football Manager 26** (Steam, Windows build).
- **EA SPORTS FIFA 17 for PC**, installed locally (your own licensed copy). The recipe targets one specific
  FIFA 17 build; `verify-fifa17` tells you if yours differs.
- Python (standard library only) to run the installer: tested with **3.14**; 3.11+ expected to work.
- **BepInEx 6.0.0-be.788, Unity IL2CPP, win-x64** — manual download (see below).
- `zstd` 1.5.7 win64 — downloaded by the installer from the official GitHub release only if you pass
  `--download` (pinned SHA-256), or supplied as a local zip with `--zstd`.
- The plugin `FM26.VisualDiagnostics.dll` **0.10.7**: download the asset `FM26.VisualDiagnostics.dll` from the
  [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases). Its SHA-256 must be
  `26a8124073615518421a2a20a91bbeca094ff696bf2253e0c5094ca710998a1c` (pinned in `tools/fm26vm/release.json`; `setup --plugin` refuses any other file). A self-built DLL has a
  different hash (see [docs/BUILDING.md](docs/BUILDING.md)).

### BepInEx: manual download

The installer does not download BepInEx automatically (the build server refuses scripted downloads).
Download it yourself from the official BepInEx bleeding-edge builds page (<https://builds.bepinex.dev/projects/bepinex_be>),
build **#788**, artifact **Unity IL2CPP — win x64**:

- expected file name: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`
- expected SHA-256: `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

Check it with `certutil -hashfile <zip> SHA256` (or `Get-FileHash`). The installer re-checks the hash and refuses
any other file. Pass it with `--bepinex <zip>`.

## Usage

Download `FM26.VisualDiagnostics.dll` (0.10.7) from the [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases) and check it with
`certutil -hashfile <dll> SHA256` (expected `26a8124073615518421a2a20a91bbeca094ff696bf2253e0c5094ca710998a1c`).
Close Football Manager 26 first, then, from the repository folder (replace the two paths with your downloaded files):

```bat
:: 1. full install: detect -> verify FIFA 17 -> tools -> build the private pack -> deploy -> enable -> verify
python -m tools.fm26vm setup --bepinex C:\path\to\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip ^
    --plugin C:\path\to\downloaded\FM26.VisualDiagnostics.dll --download

:: 2. read-only summary: detected paths, pack, plugin hash, BepInEx mode, overlay on/off
python -m tools.fm26vm status

:: 3. remove the plugin and BepInEx again (the private pack is kept unless --purge-pack)
python -m tools.fm26vm uninstall
```

Useful options: `--fm PATH` / `--fifa17 PATH` / `--steam PATH` when detection fails, `--zstd ZIP` instead of
`--download`, `--dry-run` to print the plan without writing anything, `--rebuild-pack` to rebuild the pack.
`setup` is resumable: every step is recorded in `%LOCALAPPDATA%\fm26-visual-mod\state.json`.

Lower-level commands (`detect`, `verify-fifa17`, `fetch-tools`, `build-pack`, ...) are listed by
`python -m tools.fm26vm --help`; `python tools/deploy.py --help` covers plugin-only updates.

## Known limitations

- **Foot sliding**: locomotion clips are blended against the FM movement speed; feet visibly slide at some speeds
  and on sharp turns.
- **No gestures**: passes, shots, tackles, headers, keeper saves and celebrations keep a locomotion pose.
- **No faces, kits or textures**: one neutral FIFA 17 body for every player, no team colours or likeness.
- **10-player sides** (red card, injury with no substitutes left) are not validated; report issues if the overlay
  misbehaves in that case.
- Only the FIFA 17 PC build described by `tools/fm26vm/recipe-fifa17-v2.json` is supported.
- BepInEx and the plugin are loaded into FM26; game updates can break the plugin until it is rebuilt.

## Repository layout

| Path | Content |
|---|---|
| `src/` | C# plugin (BepInEx 6 IL2CPP, net6.0) |
| `FM26.VisualDiagnostics.csproj` | plugin project (references the BepInEx core and the game interop **from your machine**) |
| `tools/fm26vm/` | installer: detection, FIFA 17 verification, clean-room readers, retargeting, setup/status/uninstall |
| `tools/deploy.py` | BepInEx + plugin deployment with backups and owned-file hashes |
| `tools/fifa_pack.py` | private pack format, builder and verifier |
| `tests/` | Python test-suite (synthetic data only) and C# policy tests |
| `tools/leak_scan.py`, `tools/export_public.py` | maintainer tools used to produce this public tree |

## Tests

```bat
python -B -m unittest discover -s tests
```

The suite uses synthetic fixtures only; tests that need real game data skip themselves when it is absent.

## License

MIT — see [LICENSE](LICENSE). Third-party components (BepInEx, zstd, .NET runtime, Unity Doorstop) are downloaded or
supplied by the user and never redistributed here: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## En bref (FR)

Plugin BepInEx pour **Football Manager 26** (Steam, Windows) qui remplace **uniquement le rendu 3D des joueurs en
match** par un corps et des animations de course de **FIFA 17**, extraits **localement depuis votre propre
installation PC de FIFA 17**. Aucun fichier EA / Sports Interactive n'est distribué : le pack est construit sur
votre machine (`%LOCALAPPDATA%\fm26-visual-mod\packs\`) et ne doit pas être partagé. Projet de fan, sans lien avec
EA ni Sports Interactive.

Le plugin `FM26.VisualDiagnostics.dll` 0.10.7 se télécharge sur la page [Releases](https://github.com/Mbappinho/fm26-visual-mod/releases) (SHA-256 ci-dessus).
Utilisation à vos risques ; respectez les CLUF/EULA des jeux.
Commandes : `python -m tools.fm26vm setup --bepinex <zip> --plugin <dll téléchargé> --download`, puis `status`, et
`uninstall` pour tout retirer. BepInEx be.788 se télécharge à la main (fichier et SHA-256 ci-dessus).
Limites : glissement des pieds, pas de gestes, pas de visages/textures, cas à 10 joueurs non validé.
