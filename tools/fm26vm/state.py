# SPDX-License-Identifier: MIT
"""Installer state file location."""


import os
from pathlib import Path


def state_path(env=None):
    env = os.environ if env is None else env
    return Path(env["LOCALAPPDATA"]) / "fm26-visual-mod" / "state.json"
