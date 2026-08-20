# PyInstaller runtime hook, passed to the freeze by build-assy.ps1.
#
# Windows has no fork(). Python's multiprocessing "spawn" method therefore starts a worker by
# re-launching sys.executable, which in a frozen app is assy-cli.exe itself, with an argv of:
#
#     assy-cli.exe --multiprocessing-fork parent_pid=<pid> pipe_handle=<handle>
#
# multiprocessing.freeze_support() is what recognizes that argv, runs the worker, and exits. If it
# is never called, the re-launched process instead falls through to upstream's argparse, which
# rejects "parent_pid=<pid>" as an unknown subcommand and dies with exit 2. The parent is left
# blocked forever on a pipe from a worker that never connected: zero CPU, zero disk, no output,
# until something kills it. ffsubsync uses multiprocessing, so every ffsubsync run hit this.
#
# Runtime hooks execute before the entry point, which is the only place this can be fixed without
# patching upstream source. In the real main process freeze_support() returns immediately, so this
# is a no-op there. On Linux the start method is fork and it is a no-op outright.

import multiprocessing

multiprocessing.freeze_support()
