# SPDX-License-Identifier: MIT
import importlib.util
from pathlib import Path
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / "tools" / "deploy.py"
if MODULE.exists():
    spec = importlib.util.spec_from_file_location("deploy", MODULE)
    deploy = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(deploy)
else:
    deploy = None


class DeploymentTests(unittest.TestCase):
    def test_owned_plugin_update_can_be_rolled_back(self):
        self.assertTrue(hasattr(deploy, "update_plugin"), "Verified plugin update implementation is missing")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game, stage = root / "game", root / "stage"
            game.mkdir()
            (game / "fm.exe").write_bytes(b"original executable")
            (stage / "BepInEx" / "core").mkdir(parents=True)
            (stage / "BepInEx" / "core" / "loader.dll").write_bytes(b"loader")
            state = root / "install-state.json"
            deploy.install(stage, game, state)
            old = root / "old" / "FM26.VisualDiagnostics.dll"
            new = root / "new" / "FM26.VisualDiagnostics.dll"
            old.parent.mkdir(); new.parent.mkdir()
            old.write_bytes(b"version one"); new.write_bytes(b"version two")
            target = deploy.add_plugin(old, game, state)
            backup = deploy.update_plugin(new, game, state)
            self.assertEqual(target.read_bytes(), b"version two")
            self.assertEqual(backup.read_bytes(), b"version one")
            deploy.update_plugin(backup, game, state)
            self.assertEqual(target.read_bytes(), b"version one")
            self.assertEqual((game / "fm.exe").read_bytes(), b"original executable")

    def test_install_then_uninstall_preserves_original_game_files(self):
        self.assertIsNotNone(deploy, "Reversible deployment implementation is missing")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game = root / "game"
            stage = root / "stage"
            game.mkdir()
            (game / "fm.exe").write_bytes(b"original executable")
            (game / "fm_Data").mkdir()
            (game / "fm_Data" / "original.bundle").write_bytes(b"original bundle")
            (stage / "BepInEx" / "core").mkdir(parents=True)
            (stage / "BepInEx" / "core" / "loader.dll").write_bytes(b"loader")
            (stage / "winhttp.dll").write_bytes(b"proxy")
            state = root / "install-state.json"
            deploy.install(stage, game, state)
            self.assertEqual((game / "winhttp.dll").read_bytes(), b"proxy")
            self.assertEqual((game / "fm.exe").read_bytes(), b"original executable")
            # Preserve new/generated files by moving them into a backup, not deleting.
            (game / "BepInEx" / "generated.log").write_text("generated")
            backup = deploy.uninstall(game, state, root / "backups")
            self.assertFalse((game / "winhttp.dll").exists())
            self.assertFalse((game / "BepInEx").exists())
            self.assertEqual((game / "fm_Data" / "original.bundle").read_bytes(), b"original bundle")
            self.assertEqual((backup / "BepInEx" / "generated.log").read_text(), "generated")
            self.assertEqual(sorted(path.name for path in game.iterdir()), ["fm.exe", "fm_Data"])


if __name__ == "__main__":
    unittest.main()
