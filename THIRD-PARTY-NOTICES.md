# Third-party notices

This repository's own code is MIT-licensed (see `LICENSE` and the `SPDX-License-Identifier` headers).

The third-party components below are **not redistributed** by this repository: they are not versioned here and are not
bundled in its release archives. The installer (`tools/fm26vm` `fetch-tools`/`setup`, `tools/deploy.py`) downloads them
from their official release pages (pinned URL + SHA-256) or uses a copy supplied by the user, and installs them on the
user's machine. Their licences apply to those copies.

| Component | Version | Licence | How it is used |
|---|---|---|---|
| Zstandard (`zstd`, Meta Platforms) | 1.5.7 | `BSD-3-Clause OR GPL-2.0-only` (dual licence; used here under BSD-3-Clause) | Command-line tool downloaded into the local tools folder to decompress FIFA 17 game data on the user's machine; never shipped. |
| BepInEx (BepInEx team) | 6.0.0-be.788 (Unity IL2CPP win-x64) | `LGPL-2.1` | Mod loader from the official BepInEx build (pinned SHA-256; the zip is currently supplied by the user with `--bepinex`, the pinned download URL being unset) copied into the user's Football Manager 26 folder by `tools/deploy.py install`; unmodified, removable with `uninstall`. |
| .NET runtime (Microsoft / .NET Foundation) | as embedded in the BepInEx IL2CPP build (`dotnet/`) | `MIT` | Part of the BepInEx IL2CPP archive above (runtime folder); installed with it, never shipped by this repository. |
| Unity Doorstop (`winhttp.dll`, `doorstop_config.ini`) | as embedded in the BepInEx build | `LGPL-2.1` (distributed as part of BepInEx) | Injection proxy contained in the BepInEx archive; installed and removed together with it. |

Football Manager 26 and EA SPORTS FIFA 17 game files and any data derived from them are the property of their owners.
They are never redistributed: private packs stay in the user's `%LOCALAPPDATA%/fm26-visual-mod/packs/` folder and
`tools/deploy.py` refuses to deploy them.
