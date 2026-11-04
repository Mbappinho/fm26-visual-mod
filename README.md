<div align="center">

# FM26 Visual Mod — FIFA 17 Local Render

[![Status](https://img.shields.io/badge/status-alpha%200.10.7-orange.svg)](#current-state-0107-alpha)
[![Game](https://img.shields.io/badge/game-Football%20Manager%2026%20(Steam)-1E293B.svg)](https://store.steampowered.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0078D6.svg)](#requirements)
[![Python](https://img.shields.io/badge/python-3.11%2B%20(tested%203.14)-3776AB.svg)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

**A BepInEx (IL2CPP) plugin for Football Manager 26 that replaces only the in-match 3D visuals of players.**

[Overview](#overview) • [Goal](#goal) • [What It Does / Does Not Do](#what-it-does--does-not-do) • [Current State](#current-state-0107-alpha) • [Requirements](#requirements) • [Quickstart & Usage](#usage) • [Roadmap](#roadmap--improvements-to-existing-work) • [Limitations](#known-limitations) • [Repository Layout](#repository-layout) • [En bref (FR) 🇫🇷](#en-bref-fr)

</div>

---

## Overview

During the 3D match engine, each Football Manager player is rendered with a FIFA 17 player body and FIFA 17 locomotion animations (walk / jog / run / stand clips) instead of the native FM model.

The match simulation, tactics, ratings, results, saves and every other part of the game are **completely untouched**: the plugin only reads the match state and draws on top of it, hiding the native player model while its overlay is active.

> [!IMPORTANT]
> ### Legal Notice & Asset Safety
> - **No EA or Sports Interactive assets are distributed by this repository.** It contains only original code (MIT) and a small numeric *recipe* (asset keys, byte ranges and hashes) that lets the installer find and verify the right data inside **your own FIFA 17 PC installation**. Every model and animation is extracted and converted **locally, on your machine, from your own copy of the game**, into a private pack under `%LOCALAPPDATA%\fm26-visual-mod\packs\`. That pack must **never** be shared or published.
> - **Fan-made & Non-commercial:** This project is an unofficial fan-made mod. It is **not affiliated with, endorsed by or sponsored by Electronic Arts, EA SPORTS, Sports Interactive or SEGA**. "FIFA", "EA SPORTS" and "Football Manager" are trademarks of their respective owners and are used here only to describe compatibility. No proprietary logos are used.
> - **Use at your own risk:** You are responsible for respecting the end-user licence agreements (EULAs) and terms of service of Football Manager 26, EA SPORTS FIFA 17, Steam and any other software you use with this mod.

---

## Goal

The final objective: **FM26 keeps 100% of its simulation, tactics, ball and user interface; only the in-match player visuals are replaced** by FIFA-quality player models and animations, built locally from the user's own copy of the donor game.

The target is a broadcast-like match picture:
- ⚽ **Authentic gestures:** Correct gestures for what FM decides — ball control, pass, shot, dribble, tackle — and goalkeeper actions.
- 👟 **Pitch contact:** Feet planted on the pitch (no sliding), believable turns and accelerations.
- 👤 **Identifiable players:** Faces and kits instead of one neutral body.
- ⚡ **Seamless integration:** Compatible with normal FM26 play (substitutions, half time, key-moments mode, fast-forward) and lightweight enough not to cost noticeable frame time.

> *The mod never changes what happens in a match, only how the players look while it happens.*

### Long-Term Vision
If the mod achieves complete visual fidelity and rock-solid stability between FM26 and FIFA 17, the intended roadmap will prioritize:
1. **Porting to Football Manager 27 (FM27) as top priority**, adapting the pipeline to the next-generation game engine.
2. **Supporting a substantially more recent FIFA / EA SPORTS FC donor version**, tapping into modern locomotion systems, richer animation sets, and higher-detail player models.

> [!NOTE]
> *This remains an ambitious long-term objective that will depend heavily on reverse-engineering progress and community collaboration.*

---

## What It Does / Does Not Do

| Scope | What it DOES ✅ | What it DOES NOT ❌ |
| :--- | :--- | :--- |
| **Detection & Files** | Detects FM26 (Steam library) and your FIFA 17 PC install in read-only mode. | Does **not** modify original game archives or save files. |
| **Verification** | Verifies that your FIFA 17 build matches the recipe (streaming hashes, read-only). | Does **not** work with unsupported/unverified FIFA builds. |
| **Asset Extraction** | Decodes needed FIFA 17 data locally (pinned `zstd`) and builds a private verified pack. | Does **not** redistribute any models, textures, animations, or game data. |
| **Engine & Loader** | Installs BepInEx 6 (be.788) and the plugin into the FM26 folder with backups and hash-checked uninstallation. | Does **not** touch or alter match simulation, tactics, ratings, or match outcomes. |
| **Rendering** | Renders FIFA 17 body + 10 locomotion clips per player in 3D matches, restoring native FM models on overlay stop. | Does **not** yet provide player faces, team kits, or non-locomotion action gestures. |

---

## Current State (0.10.7, alpha)

Tested and confirmed working on Windows 11 with one verified FIFA 17 PC build:

- [x] **Automatic start:** Launches automatically when a 3D match begins (no hotkey needed), and cleanly restores native FM players when the overlay stops (match end, scene change, error).
- [x] **Substitutions:** The overlay automatically pauses and recovers after ~2 seconds.
- [x] **Match lifecycle:** Full support for half time and the second half.
- [x] **Highlight modes:** Compatible with key-moments mode, including FM's automatic fast-forward between highlights.
- [x] **Player mesh:** Renders one FIFA 17 player body mesh (skeleton + mesh pieces) for every player on the pitch.
- [x] **Locomotion:** 10 FIFA 17 locomotion clips (stand / walk / jog / run variants) retargeted to the FM skeleton and blended against FM's movement speed.

> [!WARNING]
> This project is in an **alpha** stage. Expect visual quirks and edge cases (see [Known limitations](#known-limitations)).

---

## Requirements

- **Operating System:** Windows 10 / 11 x64.
- **Base Game:** [Football Manager 26](https://store.steampowered.com/) (Steam, Windows build).
- **Donor Game:** **EA SPORTS FIFA 17 for PC**, installed locally (your own licensed copy).
  - *The recipe targets one specific FIFA 17 build; `verify-fifa17` will confirm if yours matches.*
- **Runtime Environment:** Python standard library only (tested with **3.14**; 3.11+ expected to work).
- **Mod Loader:** **BepInEx 6.0.0-be.788, Unity IL2CPP, win-x64** (handled automatically via `--download` or supplied locally).
- **Compression Tool:** `zstd` 1.5.7 win64 (handled automatically via `--download` or supplied locally).
- **Plugin Binary:** `FM26.VisualDiagnostics.dll` **0.10.7**:
  - Download from the [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases).
  - Pinned SHA-256: `0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4`
  - *The build is deterministic and path-free: compiling this repository produces bit-for-bit identical bytes (see [docs/BUILDING.md](docs/BUILDING.md)).*

<details>
<summary><b>Details on BepInEx Download & Hash Verification</b></summary>

With `--download`, the installer fetches the pinned official archive:
```
https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip
```
The download uses an identifying User-Agent (`fm26vm/0.1 (+https://github.com/Mbappinho/fm26-visual-mod)`) and strictly validates the SHA-256 hash **before** extracting anything.

If network restrictions prevent automatic download, obtain the file manually from the [BepInEx bleeding-edge builds page](https://builds.bepinex.dev/projects/bepinex_be) (build **#788**, artifact **Unity IL2CPP — win x64**) and supply it with `--bepinex <path>`:
- **Expected File:** `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`
- **Expected SHA-256:** `f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

Verify with PowerShell or Command Prompt:
```bat
certutil -hashfile BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip SHA256
```
</details>

---

## Usage

### 1. Verification
Download `FM26.VisualDiagnostics.dll` (0.10.7) from the [Releases page](https://github.com/Mbappinho/fm26-visual-mod/releases) and verify its integrity:
```bat
certutil -hashfile FM26.VisualDiagnostics.dll SHA256
:: Expected: 0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4
```

### 2. Installation
> [!NOTE]
> Make sure **Football Manager 26 is closed** before running setup.

From the repository root, run:

```bat
:: Option A: Full automated setup (downloads pinned zstd and BepInEx, builds local pack, installs plugin)
python -m tools.fm26vm setup --plugin C:\path\to\downloaded\FM26.VisualDiagnostics.dll --download

:: Option B: Setup using a manually downloaded BepInEx archive
python -m tools.fm26vm setup --bepinex C:\path\to\BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip ^
    --plugin C:\path\to\downloaded\FM26.VisualDiagnostics.dll --download
```

### 3. Inspection & Status
Check detected paths, pack status, plugin hash, and overlay state:
```bat
python -m tools.fm26vm status
```

### 4. Uninstallation
Cleanly remove the plugin and BepInEx loader (preserves the local pack unless `--purge-pack` is passed):
```bat
python -m tools.fm26vm uninstall
```

<details>
<summary><b>Advanced Options & Diagnostic Flags</b></summary>

- **Path overrides:** `--fm PATH` / `--fifa17 PATH` / `--steam PATH` when automatic detection fails.
- **Offline archives:** `--zstd ZIP` and `--bepinex ZIP` bypass network downloads completely.
- **Dry run:** `--dry-run` displays the execution plan without writing any files to disk.
- **Rebuild pack:** `--rebuild-pack` re-extracts and rebuilds the private local pack.
- **Resumability:** Every step is recorded in `%LOCALAPPDATA%\fm26-visual-mod\state.json`.
- **Subcommands:** Run `python -m tools.fm26vm --help` for granular subcommands (`detect`, `verify-fifa17`, `fetch-tools`, `build-pack`, etc.). Run `python tools/deploy.py --help` for plugin-only deployments.
</details>

---

## Roadmap / Improvements to Existing Work

*Prioritised backlog; contributions are welcome (see [CONTRIBUTING.md](CONTRIBUTING.md)).*

### 🏃 Locomotion & Physics
- [ ] **Faster restart** after pauses (substitution, set piece): ~2 s today, target ~0.5–1 s.
- [ ] **Resume consistency**: ensure an actor re-enabled after a resume receives the overlay immediately rather than waiting for segment end.
- [ ] **Foot sliding & locking**: implement foot IK / stride matching instead of pure speed-based blending.
- [ ] **Expanded locomotion clips**: add turns, sudden starts/stops, and varied idle postures.

### 🎨 Visuals, Gestures & Likeness
- [ ] **FM action intent gestures**: map FM action states (pass, shot, tackle, control, dribble) to donor animation clips.
- [ ] **Goalkeeper actions**: dedicated save, catch, and distribution animations.
- [ ] **Faces and kit textures**: clean-room texture decoder for donor game formats.

### ⚙️ Stability, Lifecycle & Outillage
- [ ] **10-player sides**: validate and fix overlay restart on red cards or injuries without substitutes.
- [ ] **Performance profiling**: benchmark per-frame and per-player overlay overhead.
- [ ] **CI / Windows Sandbox rehearsal**: automated clean-machine rehearsals of `setup` → match → `uninstall`.
- [ ] **Additional FIFA donor recipes**: document recipes for other user-owned PC builds (documentation only; no data redistribution).
- [ ] **Installer polish**: implement the reserved `verify` and `rebuild-pack` CLI subcommands.

---

## Known Limitations

- **Foot sliding:** Locomotion clips are blended against FM movement speed; feet may slide at certain speeds or sharp turns.
- **No gameplay gestures:** Passes, shots, tackles, headers, keeper saves and celebrations currently maintain locomotion poses.
- **Uniform appearance:** One neutral FIFA 17 body for every player (no custom faces, club colours, or kit textures).
- **10-player sides:** Match scenarios with red cards or unreplaced injuries have not been fully validated.
- **Resume delay:** Re-enabled players may temporarily show native FM models until the current segment ends.
- **Strict donor version:** Only the FIFA 17 PC build matching `tools/fm26vm/recipe-fifa17-v2.json` is supported.
- **Game updates:** FM26 game updates can invalidate IL2CPP interop assemblies until the plugin is rebuilt.

---

## Repository Layout

```
fm26-visual-mod/
├── src/                          # C# plugin sources (BepInEx 6 IL2CPP, net6.0)
├── FM26.VisualDiagnostics.csproj # Plugin project file (references local BepInEx core & interop)
├── tools/
│   ├── fm26vm/                   # Installer CLI: detection, recipe verifier, clean-room extractors
│   ├── deploy.py                 # BepInEx & plugin deployment with backups & ownership hashes
│   ├── fifa_pack.py              # Private pack format generator and validator
│   ├── leak_scan.py              # Zero-leak audit tool (scans for assets, secrets, paths)
│   └── export_public.py          # Public export curation tool
├── tests/                        # Python unit tests (synthetic fixtures) & C# policy tests
├── docs/
│   └── BUILDING.md               # Build instructions, .NET SDK & deterministic compilation
└── .github/                      # Issue templates and pull request templates
```

---

## Tests & Quality Assurance

Run the Python test suite from the repository root:

```bat
python -B -m unittest discover -s tests
```

- Tests use **synthetic fixtures only**; tests requiring physical game data automatically skip themselves when assets are absent.
- `FM26VM_REAL_DOWNLOAD=1` enables an optional network test that downloads the pinned BepInEx archive to a temporary directory and validates its SHA-256 hash.
- For C# policy tests, consult [docs/BUILDING.md](docs/BUILDING.md).

---

## Contributing

Contributions are welcome! Please adhere to our core principles:

- 🚫 **Strict Asset Embargo:** Never commit or attach EA or Sports Interactive assets, extracted files, or private packs.
- 🔬 **Clean-Room Rule:** No code copied, translated, or adapted from GPL-licensed modding utilities.
- 🧪 **Test-First:** Every functional change must start with a failing test.
- 🔍 **Audit Check:** Run `python -B tools/leak_scan.py . --tracked` before submitting any PR.

See [CONTRIBUTING.md](CONTRIBUTING.md) for complete guidelines.

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE).

Third-party components (BepInEx, zstd, .NET runtime, Unity Doorstop) are downloaded or supplied directly by the user and are never distributed in this repository. Full license details can be found in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## En bref (FR)

<details open>
<summary><b>Présentation synthétique du projet en français</b></summary>

<br>

**FM26 Visual Mod** est un plugin BepInEx (IL2CPP) pour **Football Manager 26** (Steam, Windows) qui remplace **uniquement le rendu 3D des joueurs en match**. Chaque joueur FM est modélisé avec un corps et des animations de course issus de **FIFA 17**, extraits **localement depuis votre propre installation PC de FIFA 17**.

> [!NOTE]
> **Points essentiels à retenir :**
> - **100 % du jeu FM26 préservé :** La simulation, le moteur de calcul, les tactiques, la physique du ballon et l'interface utilisateur ne sont en aucun cas modifiés.
> - **Zéro asset redistribué :** Aucun fichier protégé d'EA ou de Sports Interactive n'est inclus dans ce dépôt. Le pack visuel est compilé localement sur votre ordinateur (`%LOCALAPPDATA%\fm26-visual-mod\packs\`) et ne doit jamais être partagé.
> - **Projet communautaire indépendant :** Mod de fan, non officiel et sans aucune affiliation avec Electronic Arts ou SEGA / Sports Interactive.

### État actuel (0.10.7, alpha)
- Démarrage automatique lors du coup d'envoi 3D (aucun raccourci clavier requis).
- Gestion des temps forts, mi-temps et remplacements (reprise de l'overlay en ~2 s).
- Modèle de corps FIFA 17 unique appliqué aux joueurs avec 10 animations de locomotion adaptées à la vitesse FM.

### Vision à long terme
Si le mod atteint une intégration parfaitement fluide et stable avec FM26 et FIFA 17, l'objectif sera :
1. **Un portage prioritaire sur Football Manager 27 (FM27)** dès sa disponibilité.
2. **La prise en charge d'un FIFA / EA FC beaucoup plus récent** pour bénéficier de modélisations et d'animations de locomotion plus modernes.
*(Cette ambition à long terme reste un défi technique majeur qui dépendra des contributions communautaires).*

### Installation express
1. Téléchargez la DLL `FM26.VisualDiagnostics.dll` (0.10.7) depuis la page [Releases](https://github.com/Mbappinho/fm26-visual-mod/releases) (SHA-256 : `0bd5ebf491a592b738b4acd5c442338c8ca152b6f9ff993b2b9de335aa9198b4`).
2. Fermez Football Manager 26.
3. Exécutez l'installation automatique :
   ```bat
   python -m tools.fm26vm setup --plugin C:\chemin\vers\FM26.VisualDiagnostics.dll --download
   ```
4. Vérifiez l'état avec `python -m tools.fm26vm status` (désinstallation complète possible via `python -m tools.fm26vm uninstall`).

Pour plus de détails sur la compilation et la contribution, consultez [docs/BUILDING.md](docs/BUILDING.md) et [CONTRIBUTING.md](CONTRIBUTING.md).

</details>
