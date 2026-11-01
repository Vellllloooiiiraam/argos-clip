# Argos Clip

[Português](README.md) · **English**

An **AI clip maker that runs 100% locally and for free** on your machine: it turns long videos into vertical clips with burned-in captions.

- **Input:** upload a video **or paste a link** (YouTube, Twitch, Vimeo, X, Reddit, TikTok... via yt-dlp)
- **Transcription:** Whisper via `faster-whisper` (CPU, int8, `small` model)
- **Moment selection:** cuts **on sentence boundaries** + scoring heuristic (hooks, numbers, questions, speech density) and, **optionally**, AI selection via `ARGOS_LLM_KEY`
- **Face reframe:** OpenCV tracks the speaker and the 9:16 crop follows the face (fallback: center crop)
- **Automatic B-Roll (optional):** fetches stock footage from Pexels/Pixabay and overlays it mid-clip
- **Clips:** FFmpeg crops to 9:16 (1080×1920) and burns karaoke-style captions
- **UI:** local web app with upload/link, progress and a clip gallery

## Two versions

This repository has **two implementations** of the same app (same pipeline, same UI):

- **C# / .NET 8** ([`csharp/`](csharp/)) — the **active, maintained line**. Native Windows app with its own executable and **installer**. All new versions ship from here. See [csharp/README.md](csharp/README.md).
- **Python** (repository root) — the **legacy client** (frozen). It was the original version, built on `faster-whisper`; it no longer receives new features or fixes.

Both listen on `http://127.0.0.1:8484`. Check the **[Releases](../../releases)** tab to download the exe/installer. The change history lives in the [CHANGELOG](CHANGELOG.md).

## Requirements

- Python 3.11 (used by the `.venv`)
- FFmpeg/ffprobe on the `PATH`
- ~500 MB of free RAM for the Whisper model (downloaded on first use)

## Usage

### Option 1 — exe (recommended if you just want to use it)

1. Download `argos-clip-win64.zip` from [Releases](../../releases) and unzip it
2. Double-click `argos-clip.exe` — it opens http://127.0.0.1:8484 in your browser
3. Drag a video in or paste a link; the rest is the same

You don't need Python or FFmpeg — everything ships inside. The Whisper model (~500 MB) is downloaded on first use (needs internet once).

### Option 2 — from source (for development)

1. Python 3.11 + FFmpeg on the `PATH`
2. `python -m venv .venv` and install dependencies (`pip install -r requirements.txt`)
3. Double-click `iniciar.bat` (opens http://127.0.0.1:8484)
4. Drop in a long video (podcast, class, live stream...) **or paste a supported link**
5. Pick how many clips and the language (or auto)
6. Wait — on CPU, ~1 min of audio takes 20–60 s to transcribe
7. Download the generated clips (9:16, with burned captions and a "virality" note)

## Build the exe

```
build.bat
```

Produces `dist\argos-clip\argos-clip.exe` (~800 MB folder with bundled FFmpeg) — zip the folder to distribute it.

## Tests

```
testar.bat
```

Runs the integration suite (real routes, dedicated test jobs folder). Rule: a test is never deleted or weakened.

## B-Roll (optional)

Create free keys at [Pexels API](https://www.pexels.com/api/) and/or [Pixabay API](https://pixabay.com/api/docs/) and export them before starting:

```
set PEXELS_API_KEY=your_key
set PIXABAY_API_KEY=your_key
```

Without a key (or to disable it), the pipeline runs normally without overlays. Never hardcoded — environment variables only.

## Tweaks

- Bigger model = better transcription, slower: edit `app/pipeline.py` → `get_model("small")` to `"medium"` or `"large-v3"`.
- Clip size: `build_chunks(segments, min_len=18, max_len=55)` in `app/pipeline.py`.
- Port: `--port 8484` in `iniciar.bat`.
- Concurrent jobs: `ARGOS_MAX_ACTIVE_JOBS` (default 2; the rest queue).
- Job retention: `ARGOS_JOB_TTL_HOURS` (default 24 h; old finished jobs are deleted).
- Disable B-Roll: `ARGOS_BROLL=0`.

## Security

- Listens only on `127.0.0.1` (not exposed on the network)
- Links validated against a platform allow-list (anti-SSRF); `file://`, `ftp://` and internal IPs are refused
- Download limits (8 GB, 2 h of video, 30 min wall clock; playlists refused)
- `/media` protected against path traversal; `nosniff`, `X-Frame-Options`, `Referrer-Policy` headers

## Cleanup

Processed videos live in `jobs/<id>/` and are deleted automatically after 24 h (or the value of `ARGOS_JOB_TTL_HOURS`). Delete the folder to free space immediately.
