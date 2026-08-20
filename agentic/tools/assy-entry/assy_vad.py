# Speech onsets from webrtcvad, exposed as the `vad` subcommand of the frozen assy-cli.
#
# webrtcvad already ships inside the payload because ffsubsync depends on it; this module is the
# only thing that makes it reachable from outside the engine. The parameters below are ffsubsync's
# own defaults for a video reference (mode 3 over 10 ms frames at 16 kHz), and the onset rule is
# the one agentic/tools/vadcheck/vad-onsets.py measured IDEA-VAD with. Keep them identical or the
# plugin's fallback stops being the thing that was measured.
#
#   assy-cli vad <video> --ffmpeg <path> --window 0:90000 --window 540000:90000 --json
#   assy-cli vad --self-test
#
# Writes one JSON object on stdout, every log line on stderr.

import argparse
import json
import subprocess
import sys
import tempfile

FRAME_MS = 10
RATE = 16000
BYTES_PER_SAMPLE = 2
SAMPLES_PER_FRAME = RATE // (1000 // FRAME_MS)
BYTES_PER_FRAME = SAMPLES_PER_FRAME * BYTES_PER_SAMPLE

# Whole windows are never held in memory: the plugin can plan one spanning hours of a film, and a
# server runs several of these at once. About 32 seconds of audio is in flight at a time.
READ_BYTES = BYTES_PER_FRAME * 3200

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_USAGE = 2


def decode_args(ffmpeg, video, start_ms, length_ms):
    """ffmpeg reading one window as mono 16 kHz signed 16-bit PCM on stdout."""
    args = [ffmpeg, "-nostdin", "-loglevel", "fatal"]

    # Ahead of -i. Behind it, ffmpeg decodes everything up to the window.
    if start_ms > 0:
        args += ["-ss", f"{start_ms / 1000.0:.3f}"]
    if length_ms > 0:
        args += ["-t", f"{length_ms / 1000.0:.3f}"]

    args += [
        "-i", video,
        "-map", "0:a:0",
        "-vn", "-sn",
        "-f", "s16le",
        "-acodec", "pcm_s16le",
        "-ac", "1",
        "-af", "aresample=async=1",
        "-ar", str(RATE),
        "-",
    ]

    return args


def detector(aggressiveness):
    import webrtcvad

    vad = webrtcvad.Vad()
    vad.set_mode(aggressiveness)
    return vad


def is_speech(vad, frame):
    try:
        return vad.is_speech(frame, sample_rate=RATE)
    except Exception:
        return False


def window_flags(ffmpeg, video, start_ms, length_ms, aggressiveness):
    """One True/False per 10 ms frame of a window, or None if ffmpeg could not read it.

    The audio is scored as it arrives; a trailing partial frame is dropped.
    """
    vad = detector(aggressiveness)
    flags = []
    pending = b""

    # A file, not a pipe: ffmpeg blocking on a full stderr pipe while nothing drains it deadlocks.
    with tempfile.TemporaryFile() as errors:
        try:
            reader = subprocess.Popen(
                decode_args(ffmpeg, video, start_ms, length_ms),
                stdout=subprocess.PIPE,
                stderr=errors,
            )
        except OSError as error:
            print(f"ffmpeg could not be run: {error}", file=sys.stderr)
            return None

        with reader.stdout as stream:
            while True:
                chunk = stream.read(READ_BYTES)
                if not chunk:
                    break

                pending = pending + chunk if pending else chunk
                whole = len(pending) // BYTES_PER_FRAME

                for index in range(whole):
                    offset = index * BYTES_PER_FRAME
                    flags.append(is_speech(vad, pending[offset : offset + BYTES_PER_FRAME]))

                pending = pending[whole * BYTES_PER_FRAME :]

        if reader.wait() != 0:
            errors.seek(0)
            tail = errors.read().decode("utf-8", "replace").strip().splitlines()[-3:]
            print(f"ffmpeg exited {reader.returncode}: {' '.join(tail)}", file=sys.stderr)
            return None

    return flags


def speech_flags(pcm, aggressiveness):
    """One True/False per 10 ms frame of audio already in memory."""
    vad = detector(aggressiveness)

    return [
        is_speech(vad, pcm[offset : offset + BYTES_PER_FRAME])
        for offset in range(0, len(pcm) - BYTES_PER_FRAME + 1, BYTES_PER_FRAME)
    ]


def onsets_from(flags, start_ms, gap_ms, min_speech_ms):
    """A rising edge that follows real silence and opens real speech.

    Without both bounds every flicker inside a sentence becomes an onset.
    """
    gap_frames = max(1, gap_ms // FRAME_MS)
    speech_frames = max(1, min_speech_ms // FRAME_MS)
    found = []
    quiet = gap_frames

    for index, flag in enumerate(flags):
        if flag:
            if quiet >= gap_frames:
                ahead = flags[index : index + speech_frames]
                if len(ahead) == speech_frames and all(ahead):
                    found.append(start_ms + index * FRAME_MS)
            quiet = 0
        else:
            quiet += 1

    return found


def parse_window(text):
    parts = text.split(":")
    if len(parts) != 2:
        raise argparse.ArgumentTypeError(f"'{text}' is not <startMs>:<lengthMs>")

    try:
        start_ms = int(parts[0])
        length_ms = int(parts[1])
    except ValueError:
        raise argparse.ArgumentTypeError(f"'{text}' is not <startMs>:<lengthMs>") from None

    if start_ms < 0 or length_ms < 0:
        raise argparse.ArgumentTypeError(f"'{text}' has a negative bound")

    return (start_ms, length_ms)


def build_parser():
    parser = argparse.ArgumentParser(
        prog="assy-cli vad",
        description="Speech onsets from a voice-activity detector, in milliseconds.",
    )
    parser.add_argument("video", nargs="?", help="Media file to read the audio of")
    parser.add_argument("--ffmpeg", default="ffmpeg", help="ffmpeg to decode with")
    parser.add_argument(
        "--window",
        action="append",
        type=parse_window,
        metavar="START:LENGTH",
        help="A window to read, in milliseconds. Repeatable. Omitted means the whole file",
    )
    parser.add_argument("--aggressiveness", type=int, default=3, choices=[0, 1, 2, 3])
    parser.add_argument("--gap", type=int, default=250, help="Silence an onset must follow, ms")
    parser.add_argument("--min-speech", type=int, default=100, help="Speech an onset opens, ms")
    parser.add_argument("--json", action="store_true", help="Write JSON (the only format)")
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Run the detector over synthetic audio and report, without ffmpeg",
    )
    return parser


def self_test():
    """Proves the detector loaded and answers, with no media and no ffmpeg."""
    try:
        detector(3)
    except Exception as error:
        json.dump({"ok": False, "error": f"webrtcvad did not import: {error}"}, sys.stdout)
        return EXIT_FAILED

    quiet = b"\x00\x00" * SAMPLES_PER_FRAME * 50
    flags = speech_flags(quiet, 3)

    if len(flags) != 50:
        json.dump({"ok": False, "error": f"read {len(flags)} frames of 50"}, sys.stdout)
        return EXIT_FAILED

    json.dump({"ok": True, "frames": len(flags), "speechFrames": sum(flags)}, sys.stdout)
    return EXIT_OK


def run(argv):
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    if not args.video:
        parser.print_help(sys.stderr)
        return EXIT_USAGE

    windows = args.window or [(0, 0)]
    onsets = []
    frames = 0
    speech = 0
    per_window = []
    read = 0

    for start_ms, length_ms in windows:
        flags = window_flags(args.ffmpeg, args.video, start_ms, length_ms, args.aggressiveness)

        if not flags:
            per_window.append(0)
            continue

        read += 1
        frames += len(flags)
        speech += sum(1 for flag in flags if flag)

        found = onsets_from(flags, start_ms, args.gap, args.min_speech)
        per_window.append(len(found))
        onsets.extend(found)

    answer = {
        "ok": read > 0,
        "onsets": onsets,
        "frames": frames,
        "speechFrames": speech,
        "windowsRead": read,
        "windowsPlanned": len(windows),
        "perWindow": per_window,
    }

    json.dump(answer, sys.stdout)
    return EXIT_OK if read > 0 else EXIT_FAILED


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
