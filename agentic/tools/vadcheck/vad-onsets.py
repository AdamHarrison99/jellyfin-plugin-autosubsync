# Speech onsets from a real voice-activity detector, over the windows the shipping audio check
# already plans. Exists to answer IDEA-VAD: does a trained VAD supply onsets that `Score` can
# measure on titles `silencedetect` cannot?
#
# Reads a plan on stdin and writes onsets on stdout, both JSON, so the caller stays the C# harness
# that links the shipping SyncVerifier:
#
#   {"video": "...", "ffmpeg": "...", "detector": "webrtc",
#    "windows": [{"startMs": 0, "lengthMs": 90000}, ...],
#    "gapMs": 250, "minSpeechMs": 100, "aggressiveness": 3}
#
#   {"onsets": [1234, 5678, ...], "frames": 9000, "speechFrames": 4210, "perWindow": [...]}
#
# The webrtc detector is deliberately ffsubsync's own: webrtcvad at mode 3 over 10 ms frames, which
# is what DEFAULT_VAD='subs_then_webrtc' resolves to for a video reference. That matters because
# webrtcvad already ships inside the assy-cli payload.

import hashlib
import json
import os
import subprocess
import sys

FRAME_MS = 10
RATE = 16000
BYTES_PER_SAMPLE = 2
SAMPLES_PER_FRAME = RATE // (1000 // FRAME_MS)
BYTES_PER_FRAME = SAMPLES_PER_FRAME * BYTES_PER_SAMPLE


def decode(ffmpeg, video, start_ms, length_ms):
    args = [
        ffmpeg,
        "-nostdin",
        "-loglevel",
        "fatal",
        "-ss",
        f"{start_ms / 1000.0:.3f}",
        "-t",
        f"{length_ms / 1000.0:.3f}",
        "-i",
        video,
        "-map",
        "0:a:0",
        "-vn",
        "-sn",
        "-f",
        "s16le",
        "-acodec",
        "pcm_s16le",
        "-ac",
        "1",
        "-af",
        "aresample=async=1",
        "-ar",
        str(RATE),
        "-",
    ]
    done = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if done.returncode != 0:
        return None
    return done.stdout


def webrtc_flags(pcm, aggressiveness):
    import webrtcvad

    vad = webrtcvad.Vad()
    vad.set_mode(aggressiveness)
    flags = []
    for offset in range(0, len(pcm) - BYTES_PER_FRAME + 1, BYTES_PER_FRAME):
        chunk = pcm[offset : offset + BYTES_PER_FRAME]
        try:
            flags.append(vad.is_speech(chunk, sample_rate=RATE))
        except Exception:
            flags.append(False)
    return flags


_silero = {}


def silero_flags(pcm, threshold):
    import numpy as np
    import onnxruntime

    if "session" not in _silero:
        _silero["session"] = onnxruntime.InferenceSession(
            _silero["model"], providers=["CPUExecutionProvider"]
        )

    session = _silero["session"]
    audio = np.frombuffer(pcm, dtype="<i2").astype("float32") / 32768.0

    # Silero v5 wants 512-sample chunks at 16 kHz — 32 ms against webrtc's 10 ms — each carrying
    # ! the previous 64 samples as context. Without the context the model scores everything ~0.
    chunk = 512
    context_size = 64
    context = np.zeros(context_size, dtype="float32")
    state = np.zeros((2, 1, 128), dtype="float32")
    probabilities = []

    for offset in range(0, len(audio) - chunk + 1, chunk):
        block = audio[offset : offset + chunk]
        fed = np.concatenate([context, block]).reshape(1, -1)
        context = block[-context_size:]
        out, state = session.run(
            None, {"input": fed, "state": state, "sr": np.array(RATE, dtype="int64")}
        )
        probabilities.append(float(out[0][0]))

    # Re-expressed on the 10 ms grid the rest of the harness speaks.
    per_chunk = chunk * 1000 // RATE // FRAME_MS
    flags = []
    for probability in probabilities:
        flags.extend([probability >= threshold] * per_chunk)
    return flags


# A speech onset is a rising edge that follows real silence and opens real speech. Without both
# bounds every flicker inside a sentence becomes an onset, which is X3's failure exactly.
def onsets_from(flags, start_ms, gap_ms, min_speech_ms):
    gap_frames = max(1, gap_ms // FRAME_MS)
    speech_frames = max(1, min_speech_ms // FRAME_MS)
    found = []
    quiet = gap_frames

    index = 0
    while index < len(flags):
        if flags[index]:
            if quiet >= gap_frames:
                ahead = flags[index : index + speech_frames]
                if len(ahead) == speech_frames and all(ahead):
                    found.append(start_ms + index * FRAME_MS)
            quiet = 0
        else:
            quiet += 1
        index += 1

    return found


def runs(flags):
    encoded = []
    for flag in flags:
        value = 1 if flag else 0
        if encoded and encoded[-1][0] == value:
            encoded[-1][1] += 1
        else:
            encoded.append([value, 1])
    return encoded


def unruns(encoded):
    flags = []
    for value, count in encoded:
        flags.extend([value == 1] * count)
    return flags


# ! The decode is the expensive half and the media sits on a slow share, so per-frame flags are
# cached per (video, detector, window). A parameter sweep then costs nothing.
def cache_key(video, detector, aggressiveness, threshold, window):
    stamp = f"{video}|{detector}|{aggressiveness}|{threshold}|{window['startMs']}|{window['lengthMs']}"
    return hashlib.sha256(stamp.encode("utf-8")).hexdigest()


def main():
    plan = json.load(sys.stdin)
    ffmpeg = plan["ffmpeg"]
    video = plan["video"]
    detectors = plan.get("detectors") or [plan.get("detector", "webrtc")]
    gap_ms = plan.get("gapMs", 250)
    min_speech_ms = plan.get("minSpeechMs", 100)
    aggressiveness = plan.get("aggressiveness", 3)
    threshold = plan.get("threshold", 0.5)
    cache_dir = plan.get("cacheDir")
    if "model" in plan:
        _silero["model"] = plan["model"]

    if cache_dir:
        os.makedirs(cache_dir, exist_ok=True)

    answer = {name: {"onsets": [], "frames": 0, "speechFrames": 0, "perWindow": []}
              for name in detectors}
    failed = 0
    decoded = 0

    for window in plan["windows"]:
        wanted = {}
        for name in detectors:
            cached = None
            if cache_dir:
                cached = os.path.join(
                    cache_dir,
                    cache_key(video, name, aggressiveness, threshold, window) + ".json",
                )
                if os.path.exists(cached):
                    with open(cached, "r", encoding="utf-8") as handle:
                        answer[name]["_flags"] = unruns(json.load(handle))
                        continue
            wanted[name] = cached

        pcm = None
        if wanted:
            pcm = decode(ffmpeg, video, window["startMs"], window["lengthMs"])
            if pcm is None or len(pcm) < BYTES_PER_FRAME:
                failed += 1
                for name in detectors:
                    answer[name]["perWindow"].append(0)
                    answer[name].pop("_flags", None)
                continue
            decoded += 1

        for name, cached in wanted.items():
            flags = silero_flags(pcm, threshold) if name == "silero"                 else webrtc_flags(pcm, aggressiveness)
            answer[name]["_flags"] = flags
            if cached:
                with open(cached, "w", encoding="utf-8") as handle:
                    json.dump(runs(flags), handle)

        for name in detectors:
            flags = answer[name].pop("_flags", None)
            if flags is None:
                answer[name]["perWindow"].append(0)
                continue
            answer[name]["frames"] += len(flags)
            answer[name]["speechFrames"] += sum(1 for flag in flags if flag)
            found = onsets_from(flags, window["startMs"], gap_ms, min_speech_ms)
            answer[name]["perWindow"].append(len(found))
            answer[name]["onsets"].extend(found)

    for name in detectors:
        answer[name]["failedWindows"] = failed
        answer[name]["decodedWindows"] = decoded

    first = answer[detectors[0]]
    json.dump({**first, "byDetector": answer}, sys.stdout)


if __name__ == "__main__":
    main()
