# PyInstaller entry point for the assy-cli payload, passed to the freeze by build-assy.ps1.
#
# Upstream's main/cli.py is the whole CLI and stays the whole CLI: every argv this does not claim
# is handed to it untouched, so `sync`, `shift`, `batch`, `config` and `version` behave exactly as
# they do upstream. This wrapper exists to add subcommands of our own on top of it, which is the
# only way to reach a dependency that already sits inside the freeze (webrtcvad) without shipping
# a second interpreter.
#
# The multiprocessing runtime hook runs before this file, so an ffsubsync worker re-launch never
# reaches the dispatch below.

import sys

# Subcommands this repository adds. Upstream owns every other name.
LOCAL = ("vad",)


def main():
    argv = sys.argv[1:]

    if argv and argv[0] in LOCAL:
        if argv[0] == "vad":
            from assy_vad import run

            return run(argv[1:])

    import cli

    return cli.main()


if __name__ == "__main__":
    sys.exit(main())
