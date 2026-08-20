# Overrides the pyinstaller-hooks-contrib hook of the same name, which fails the build.
#
# ffsubsync depends on webrtcvad-wheels: it installs the module "webrtcvad" but its distribution
# metadata is named "webrtcvad_wheels". The contrib hook calls copy_metadata('webrtcvad'), finds no
# distribution under that name, and raises, which PyInstaller reports as the confusing
# "Failed to import module __PyInstaller_hooks_0_webrtcvad".
#
# A directory passed with --additional-hooks-dir is searched before the contrib hooks, so this file
# wins. It does the same job against the name the distribution is actually registered under.

from PyInstaller.utils.hooks import copy_metadata

datas = copy_metadata('webrtcvad-wheels')
