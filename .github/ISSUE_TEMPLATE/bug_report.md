---
name: Bug report
about: Something goes wrong in a match or during setup/uninstall
title: "[bug] "
labels: bug
---

<!--
Do NOT attach game files or anything extracted from them (models, animations, textures, packs, game archives,
interop DLLs). Screenshots / short clips of your own game are fine.
The diagnostics files below contain FM player names from your save: check them first, or say you will share them
privately.
-->

**What happened**
A clear description. What did you expect instead?

**When**
Minute / situation (kick-off, substitution, half time, set piece, red card, key-moments mode, fast-forward, ...).
Does it happen every match?

**Versions**
- FM26 version:
- Plugin version / sha256 (`python -m tools.fm26vm status`):
- Windows version:
- Installed with `setup` (full) or plugin-only:

**Diagnostics** (from your FM26 folder)
- [ ] `BepInEx/visual-diagnostics/fifa-overlay-auto-*.json` from the affected match
- [ ] `BepInEx/LogOutput.log`
- [ ] screenshot or short clip (optional)

**Setup/uninstall problems**
Paste the full command and its output (remove your user name from paths if you like).
