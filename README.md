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

## Goal

The final objective: **FM26 keeps 100 % of its simulation, tactics, ball and user interface; only the in-match
player visuals are replaced** by FIFA-quality player models and animations, built locally from the user's own copy
of the donor game. The target is a broadcast-like match picture:

- correct gestures for what FM decides — ball control, pass, shot, dribble, tackle — and goalkeeper actions;
- feet planted on the pitch (no sliding), believable turns and accelerations;
- faces and kits instead of one neutral body;
- compatible with normal FM26 play (substitutions, half time, key-moments mode, fast-forward) and lightweight
  enough not to cost noticeable frame time.

The mod never changes what happens in a match, only how the players look while it happens.

## Current state (0.10.7, alpha)

Works today (tested on the maintainer's machine, Windows 11, one FIFA 17 PC build):

- **automatic start** when a 3D match begins (no key press needed), and restore of the native FM players when the
  overlay stops (match end, scene change, error);
- **substitutions**: the overlay pauses and comes back after roughly 2 s;
- **half time** and the second half;
- **key-moments mode**, including FM's automatic fast-forward between highlights;
- one **FIFA 17 player body mesh** (skeleton + mesh pieces) for every player;
- **10 FIFA 17 locomotion clips** (stand / walk / jog / run variants) retargeted to the FM skeleton and blended
  against FM's movement speed.

It is an alpha: expect visual glitches (see [Known limitations](#known-limitations)).

## Roadmap / improvements to existing work

Prioritised; nothing here is promised or scheduled. Contributions welcome (see [Contributing](#contributing)).

- [ ] **Faster restart** after a pause (substitution, set piece): ~2 s today, target ~0.5–1 s.
- [ ] **Resume consistency**: an actor re-enabled after a resume currently stays FM-native until the end of the
      segment; it should get the FIFA overlay back immediately.
- [ ] **10-player sides** (red card, injury with no substitutes left): validate and fix the overlay restart.
- [ ] **Foot sliding / foot locking**: plant the feet (foot IK / stride matching) instead of speed-only blending.
- [ ] **Gestures from FM's action intent** (pass, shot, tackle, control, dribble): first research how FM exposes its
      per-player action state, then map it to donor clips.
- [ ] **Goalkeeper actions** (saves, catches, distribution).
- [ ] **Faces and kit textures**: needs a clean-room texture decoder for the donor format.
- [ ] **More animation clips and better blending** (turns, starts/stops, idles).
- [ ] **Performance profiling** of the overlay per frame and per player.
- [ ] **Clean-machine CI / Windows Sandbox rehearsal** of `setup` → match → `uninstall`.
- [ ] **Other FIFA versions as donors**: document recipes for other PC builds the user owns (documentation only;
      donor data is never redistributed).
- [ ] **Installer polish**: implement the reserved `verify` and `rebuild-pack` subcommands.

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
- **BepInEx 6.0.0-be.788, Unity IL2CPP, win-x64** — downloaded by the installer from the official build server
  only if you pass `--download` (pinned SHA-256), or supplied as a local zip with `--bepinex` (see below).
- `zstd` 1.5.7 win64 — downloaded by the installer from the official GitHub release only if you pass
  `--download` (pinned SHA-256), or supplied as a local zip with `--zstd`.
- The plugin `FM26.VisualDiagnostics.dll` **0.10.7**: download the asset `FM26.VisualDiagnostics.dll` from the
  [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases). Its SHA-256 must be
  `0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4` (pinned in `tools/fm26vm/release.json`; `setup --plugin` refuses any other file). The build is
  deterministic and path-free: rebuilding this tree with the same .NET SDK and reference assemblies gives the same
  bytes (see [docs/BUILDING.md](docs/BUILDING.md)).

### BepInEx: automatic or manual download

With `--download`, the installer fetches the pinned official zip
(`https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip`)
with an identifying User-Agent (`fm26vm/0.1 (+https://github.com/Mbappinho/fm26-visual-mod)`), and checks its
SHA-256 **before** extracting anything. Nothing is downloaded without `--download`.

If the download fails (network, proxy, server refusal), download the zip yourself from the official BepInEx
bleeding-edge builds page (<https://builds.bepinex.dev/projects/bepinex_be>), build **#788**, artifact
**Unity IL2CPP — win x64**, and pass it with `--bepinex <zip>`:

- expected file name: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`
- expected SHA-256: `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

Check it with `certutil -hashfile <zip> SHA256` (or `Get-FileHash`). The installer re-checks the hash and refuses
any other file.

## Usage

Download `FM26.VisualDiagnostics.dll` (0.10.7) from the [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases) and check it with
`certutil -hashfile <dll> SHA256` (expected `0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4`).
Close Football Manager 26 first, then, from the repository folder (replace the paths with your downloaded files):

```bat
:: 1. full install: detect -> verify FIFA 17 -> tools (pinned zstd + BepInEx downloads) -> build the private pack
::    -> deploy -> enable -> verify
python -m tools.fm26vm setup --plugin C:\path\to\downloaded\FM26.VisualDiagnostics.dll --download

:: 1b. same, with a BepInEx zip you downloaded yourself
python -m tools.fm26vm setup --bepinex C:\path\to\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip ^
    --plugin C:\path\to\downloaded\FM26.VisualDiagnostics.dll --download

:: 2. read-only summary: detected paths, pack, plugin hash, BepInEx mode, overlay on/off
python -m tools.fm26vm status

:: 3. remove the plugin and BepInEx again (the private pack is kept unless --purge-pack)
python -m tools.fm26vm uninstall
```

Useful options: `--fm PATH` / `--fifa17 PATH` / `--steam PATH` when detection fails, `--zstd ZIP` / `--bepinex ZIP`
instead of `--download`, `--dry-run` to print the plan without writing anything, `--rebuild-pack` to rebuild the
pack. `setup` is resumable: every step is recorded in `%LOCALAPPDATA%\fm26-visual-mod\state.json`.

Lower-level commands (`detect`, `verify-fifa17`, `fetch-tools`, `build-pack`, ...) are listed by
`python -m tools.fm26vm --help`; `python tools/deploy.py --help` covers plugin-only updates.

## Known limitations

- **Foot sliding**: locomotion clips are blended against the FM movement speed; feet visibly slide at some speeds
  and on sharp turns.
- **No gestures**: passes, shots, tackles, headers, keeper saves and celebrations keep a locomotion pose.
- **No faces, kits or textures**: one neutral FIFA 17 body for every player, no team colours or likeness.
- **10-player sides** (red card, injury with no substitutes left) are not validated; report issues if the overlay
  misbehaves in that case.
- After a resume, a player re-enabled by FM can keep the native FM model until the end of the current segment.
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
| `.github/` | issue and pull-request templates |

## Tests

```bat
python -B -m unittest discover -s tests
```

The suite uses synthetic fixtures only; tests that need real game data skip themselves when it is absent.
`FM26VM_REAL_DOWNLOAD=1` enables one opt-in network test that downloads the pinned BepInEx zip into a temporary
folder and checks its SHA-256. C# policy tests: see [docs/BUILDING.md](docs/BUILDING.md).

## Contributing

Short version (details in [CONTRIBUTING.md](CONTRIBUTING.md)):

- **Never commit or attach EA / Sports Interactive assets** or anything extracted from them (models, animations,
  textures, packs, game archives, decoded binaries). Screenshots of your own game in a bug report are fine.
- **Clean-room**: do not read, copy or port code from GPL-licensed game-modding tools; contributions are MIT.
- **Test first**: every behaviour change starts with a failing test; run the Python suite, the C# policy tests and
  `tools/leak_scan.py` before opening a pull request.
- Match bugs: attach `BepInEx/visual-diagnostics/fifa-overlay-auto-*.json` and `BepInEx/LogOutput.log`
  (they contain FM player names from your save: share them privately if you prefer).

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

**Objectif** : FM26 garde 100 % de sa simulation, de ses tactiques, du ballon et de son interface ; seuls les
joueurs affichés en match sont remplacés par des modèles et animations de qualité FIFA, construits localement à
partir de votre propre copie du jeu donneur, pour un rendu proche d'une retransmission TV (gestes corrects pour
contrôle / passe / tir / dribble / tacle, actions du gardien, pieds ancrés, visages et maillots), compatible et léger.

**État actuel (0.10.7, alpha)** : démarrage automatique, remplacements (~2 s), mi-temps, mode temps forts avec
avance rapide automatique ; un corps FIFA 17 et 10 clips de locomotion.

**Feuille de route** (par priorité) : redémarrage plus rapide (~0,5–1 s) ; joueur réactivé après une reprise qui
reste natif FM jusqu'à la fin du segment ; redémarrage à 10 joueurs (carton rouge) ; glissement et verrouillage des
pieds ; gestes selon l'intention d'action de FM (après étude de l'état d'action FM) ; actions du gardien ; visages
et textures de maillots (décodage des textures) ; plus de clips et un meilleur mélange ; profilage des
performances ; répétition sur machine propre (CI / Windows Sandbox) ; autres versions de FIFA comme donneur
(documentation seulement, jamais de redistribution) ; finitions de l'installateur (`verify`, `rebuild-pack`).

**Contribuer** : voir [CONTRIBUTING.md](CONTRIBUTING.md) (en anglais). Jamais d'asset EA / SI ni de fichier extrait
dans un commit ou une issue ; règle de salle blanche (pas de code d'outils de modding sous GPL) ; un test qui échoue
d'abord ; `leak_scan` avant chaque PR.

Le plugin `FM26.VisualDiagnostics.dll` 0.10.7 se télécharge sur la page [Releases](https://github.com/Mbappinho/fm26-visual-mod/releases) (SHA-256 ci-dessus).
Utilisation à vos risques ; respectez les CLUF/EULA des jeux.
Commandes : `python -m tools.fm26vm setup --plugin <dll téléchargé> --download` (BepInEx be.788 et zstd sont
téléchargés puis vérifiés par SHA-256 ; en cas d'échec, téléchargez BepInEx à la main et passez `--bepinex <zip>`),
puis `status`, et `uninstall` pour tout retirer.
Limites : glissement des pieds, pas de gestes, pas de visages/textures, cas à 10 joueurs non validé.
