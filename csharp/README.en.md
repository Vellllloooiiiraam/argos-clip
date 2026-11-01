# Argos Clip — Windows version (C# / .NET 8)

[Português](README.md) · **English**

A port of **argos-clip** (Python) to a native Windows app in **C# / .NET 8**.
This is the **active, maintained line** of the project — all new versions ship
from here (the Python version at the repository root is the **legacy client**,
frozen).

It takes **1 long video** and produces **N vertical 9:16 clips** with **burned-in
karaoke captions** — all **100% local** (Whisper + FFmpeg), nothing sent to the cloud.

> This version is a single app (local server + web UI) that opens in its **own
> window** (Edge WebView2), like a regular application. The server starts on
> `http://127.0.0.1:8484` only for the app's internal use.

The change history lives in the [CHANGELOG](../CHANGELOG.md).

---

## Requirements

| Item | Needed for | Note |
|---|---|---|
| **Windows 10/11 (x64)** | running | — |
| **FFmpeg + FFprobe** | running | must be on the `PATH` **or** in `src\ArgosClip\tools\` |
| **.NET SDK 8** | **building** only (`build.bat`) | https://dotnet.microsoft.com/download |
| **WebView2 Runtime** | running | already present on Windows 11 and updated Windows 10; otherwise: https://go.microsoft.com/fwlink/p/?LinkId=2124703 |
| .NET Runtime | — | **not** needed: `build.bat` produces a self-contained `.exe` |

**Whisper** runs embedded (`Whisper.net` / whisper.cpp), on CPU. The GGML model is
downloaded **on first run** from `https://huggingface.co/ggerganov/whisper.cpp`.

---

## How to use

### 1. Build (once)

```bat
build.bat
```

Produces `dist\argos-clip.exe` (self-contained — no .NET install required).

### 2. Run

```bat
iniciar.bat
```

Opens the app window. Close the window to quit.

> Without `dist\argos-clip.exe`, `iniciar.bat` falls back to dev mode (`dotnet run`).

### 3. Use it

1. **Drag** a video (MP4/MOV/MKV/WEBM/AVI) **or paste a link** (YouTube, Twitch, Vimeo, X, TikTok...).
2. Adjust the options: number of clips, language, **Whisper model**, **min/max duration** and **AI selection** (if configured).
3. Wait: transcription → best-moment selection → 9:16 render with captions.
4. Download the clips right from the page.

### 4. Test without a video

```bat
testar.bat
```

Generates a spoken video with the Windows voice, starts the app on a test port, uploads
the video and checks that clips were produced (end-to-end smoke test).

---

## Selection options

Cuts are made **on sentence boundaries** (never mid-idea): Whisper segments become
sentences and each clip groups whole sentences within
`[min duration, max duration]`.

| Option | Default | What it does |
|---|---|---|
| **Clips** | 5 | how many clips to generate (1–10) |
| **Model** | `small` | Whisper model — `tiny`/`base` are fast; `medium`/`large-v3-turbo` are more accurate (and slower) |
| **Duration (s)** | 20 to 60 | duration range of each clip |
| **AI selection** | off | uses AI (Gemini, Groq, OpenAI, Ollama...) to pick the best moments (needs a key/provider — see below) |

Without AI configured, selection uses the **local heuristic** (hooks, numbers,
questions, strong opening, speech density). With a provider configured, the AI
reads the transcript, picks the segments, and the heuristic still comes in as a
second opinion to fill the remaining slots — **if the AI fails, the job keeps going on the heuristic**.

---

## How it works (pipeline)

```
upload/link ──► ffprobe (duration/audio)
             ──► ffmpeg extracts 16 kHz mono WAV audio
             ──► Whisper (local) transcribes WITH word timestamps
             ──► sentences: split on final punctuation/pause (never mid-sentence)
             ──► selection: AI (optional) OR heuristic
                 groups whole sentences in [min, max] and picks the best N
             ──► optional B-roll (Pexels/Pixabay) — only if a key is set
             ──► 9:16 reframe: tracks the face (OpenCV) and crops to follow it
             ──► ffmpeg: crop + scale 1080x1920 + karaoke ASS captions + thumb
             ──► finished clips (mp4 + jpg) served at /media
```

**Stack differences vs. the original (Python):**

| Original | C# port |
|---|---|
| FastAPI + uvicorn | ASP.NET Core Minimal API (Kestrel) |
| faster-whisper | Whisper.net 1.9.1 (whisper.cpp, CPU) |
| yt-dlp (lib) | `yt-dlp.exe` invoked as a process |
| opencv-python | OpenCvSharp4 |
| httpx | HttpClient |
| PyInstaller | `dotnet publish` self-contained |

Routes and environment variables are the same as the original.

---

## Environment variables (all optional)

| Variable | Default | Purpose |
|---|---|---|
| `ARGOS_WHISPER_MODEL` | `small` | `tiny` \| `base` \| `small` \| `medium` \| `large-v3-turbo` |
| `ARGOS_MIN_CLIP_S` | `20` | minimum duration of each clip (s) |
| `ARGOS_MAX_CLIP_S` | `60` | maximum duration of each clip (s) |
| `ARGOS_LLM_KEY` | — | AI key; **enables AI selection**. For Gemini, `ARGOS_GEMINI_KEY`, `GEMINI_API_KEY` or `GOOGLE_API_KEY` also work |
| `ARGOS_LLM_PROVIDER` | `gemini` | AI provider: `gemini`, `groq`, `openai`, `openrouter` or `ollama` |
| `ARGOS_LLM_MODEL` | depends on the provider | model used for selection (e.g. `gemini-flash-lite-latest`, `llama-3.3-70b-versatile`) |
| `ARGOS_LLM_BASE_URL` | depends on the provider | OpenAI-compatible endpoint (e.g. `http://127.0.0.1:11434/v1` for Ollama) |
| `ARGOS_PORT` | `8484` | app port. **If unset, the app uses 8484 and, if busy, picks the next free one by itself** |
| `ARGOS_MAX_ACTIVE_JOBS` | `2` | jobs processing at the same time |
| `ARGOS_JOB_TTL_HOURS` | `24` | hours until finished jobs are deleted |
| `ARGOS_JOBS_DIR` | `jobs\` | working folder for jobs |
| `ARGOS_MODELS_DIR` | `models\` | folder for the GGML models |
| `ARGOS_BROLL` | on | `0` disables B-roll |
| `PEXELS_API_KEY` | — | free B-roll from Pexels |
| `PIXABAY_API_KEY` | — | free B-roll from Pixabay |

Example: run faster on a short video

```bat
set ARGOS_WHISPER_MODEL=base
iniciar.bat
```

### Enable AI from the UI (recommended)

On the home screen, click **⚙️ Configure AI (free)**, pick a provider (`Gemini`,
`Groq`, `OpenAI`, `OpenRouter` or local `Ollama`), paste the key and click
**Save and test**. The key is stored **encrypted** (DPAPI, per Windows user) in
`%APPDATA%\ArgosClip\settings.json` — no terminal needed.

> Free keys: Gemini at https://aistudio.google.com/apikey · Groq at
> https://console.groq.com/keys. **Ollama** runs locally and needs no key.

### Enable AI via environment variables (advanced)

The variables below also work and take **precedence** over the saved config.

Example: enable AI selection (Gemini)

```bat
set ARGOS_LLM_KEY=your_google_ai_studio_key
iniciar.bat
```

> Get a free Gemini key at https://aistudio.google.com/apikey. With it set, the
> **"AI selection"** checkbox is already checked in the UI.

Example: use **Groq** (free tier) or any OpenAI-compatible endpoint

```bat
set ARGOS_LLM_PROVIDER=groq
set ARGOS_LLM_KEY=your_groq_key
iniciar.bat
```

Example: run **100% locally** with Ollama (no key, no cloud)

```bat
set ARGOS_LLM_PROVIDER=ollama
set ARGOS_LLM_BASE_URL=http://127.0.0.1:11434/v1
set ARGOS_LLM_MODEL=llama3.1
iniciar.bat
```

> AI selection is always **optional**. If the key is missing, the quota runs out
> (HTTP 402) or the provider is down, the job keeps going on the **local
> heuristic** — it never fails because of the AI.

---

## Build the installer (to distribute)

```bat
winget install JRSoftware.InnoSetup   :: once
build-installer.bat
```

Produces `installer\out\ArgosClip-Setup-1.2.5.exe` (~194 MB). The installer:

- installs **per user** (no admin prompt) into `%LocalAppData%\Programs\Argos Clip`;
- creates Start Menu (and optional Desktop) shortcuts;
- bundles **FFmpeg** inside `tools\` (doesn't depend on the other person's PC);
- installs the **WebView2** runtime if it's missing (native window);
- includes an uninstaller; the app is self-contained (no .NET install required).

> The first run still downloads the Whisper model (~466 MB with `small`).

The package bundles a full FFmpeg; for a smaller installer, swap
`dist\tools\ffmpeg.exe` for an FFmpeg *essentials* build.

---

## Structure

```
argos-clip-csharp\
├─ build.bat / iniciar.bat / testar.bat
├─ build-installer.bat          # produces setup.exe (Inno Setup)
├─ installer\
│  ├─ argos-clip.iss            # installer script
│  └─ out\ArgosClip-Setup-*.exe # ready installer
├─ dist\                        # published app (self-contained)
├─ scripts\
│  ├─ make-test-video.ps1      # generates a spoken test video
│  └─ run-smoke-test.ps1       # end-to-end test
└─ src\ArgosClip\
   ├─ Program.cs               # Kestrel + headers + cleanup + window (WebView2)
   ├─ MainWindow.cs            # native window (WebView2) hosting the UI
   ├─ Endpoints.cs             # /api/upload, /api/link, /api/status, /media
   ├─ Jobs.cs                  # job state + queue
   ├─ ArgosPipeline.cs         # orchestrates the pipeline
   ├─ ClipOptions.cs           # job options (clips/duration/model/AI)
   ├─ Downloader.cs            # yt-dlp + anti-SSRF
   ├─ AppPaths.cs              # paths (dev and published)
   ├─ wwwroot\index.html       # UI (same as the original)
   ├─ tools\                   # yt-dlp.exe + Haar cascade
   └─ Pipeline\
      ├─ Ffmpeg.cs  Transcriber.cs  Scoring.cs  LlmSelector.cs
      ├─ Karaoke.cs  Reframe.cs  Renderer.cs  Broll.cs  Models.cs
```

---

## Common problems

- **"ffmpeg not found"** → install FFmpeg and put it on the `PATH`, or copy
  `ffmpeg.exe`/`ffprobe.exe` into `src\ArgosClip\tools\` (and `dist\tools\` after building).
- **Port busy** → the app handles it: if 8484 is in use it picks the next free
  port and opens the app there. To force a specific port:
  `set ARGOS_PORT=8600` before `iniciar.bat`.
- **First run is slow** → it's downloading the Whisper model (a few hundred MB).
- **Captions missing / no speech** → check that the video has an audio track and
  that the language is right (or leave it on auto-detect).
