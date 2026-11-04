# SPDX-License-Identifier: MIT
"""H239: tools/deploy.py follow-ups of the H231 review (temporary fake FM trees only; never the real game)."""
import unittest

from tests.test_deploy_h226 import AUTO_REL, PLUGIN_REL, Tmp, deploy, outcome, write  # noqa: F401


class H239UninstallFmRunningTests(Tmp):
    def test_uninstall_refuses_while_fm_runs_and_moves_nothing(self):
        self.plugin_only()
        before = self.state.read_bytes()
        error = outcome(deploy.uninstall, self.game, self.state, self.tmp / "backups", fm_running=lambda: True)
        self.assertEqual((type(error).__name__, "running" in str(error), self.state.read_bytes(),
                          (self.game / PLUGIN_REL).is_file()), ("RuntimeError", True, before, True))


class H239UnusedConstantTests(unittest.TestCase):
    def test_game_check_timeout_constant_is_gone(self):
        self.assertFalse(hasattr(deploy, "GAME_CHECK_TIMEOUT_S"))


class H239AutoFolderRemovedTests(Tmp):
    def test_plugin_only_uninstall_removes_the_auto_folder_auto_on_created(self):
        self.plugin_only()
        deploy.set_auto(self.game, self.state, True)
        deploy.uninstall(self.game, self.state, self.tmp / "backups")
        self.assertEqual(((self.game / AUTO_REL).parent.exists(),
                          (self.game / "BepInEx" / "plugins" / "Other" / "Other.dll").is_file()), (False, True))


class H239AutoFolderKeptTests(Tmp):
    def test_plugin_only_uninstall_keeps_an_auto_folder_it_did_not_create(self):
        self.plugin_only()
        (self.game / AUTO_REL).parent.mkdir(parents=True)
        deploy.set_auto(self.game, self.state, True)
        deploy.uninstall(self.game, self.state, self.tmp / "backups")
        self.assertTrue((self.game / AUTO_REL).parent.is_dir())


class H239AutoFolderUserFileTests(Tmp):
    def test_plugin_only_uninstall_keeps_user_files_in_the_auto_folder(self):
        self.plugin_only()
        deploy.set_auto(self.game, self.state, True)
        user = write((self.game / AUTO_REL).parent / "notes.txt", b"mine")
        deploy.uninstall(self.game, self.state, self.tmp / "backups")
        self.assertEqual(user.read_bytes(), b"mine")
