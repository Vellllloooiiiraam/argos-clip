# Changelog

All notable changes to Argos Clip.

The **active line is the C# / .NET 8 app** ([`csharp/`](csharp/)). The Python
version at the repository root is the **legacy client** and is frozen.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.2.1] — 2026-11-01

### Added
- **Multi-provider AI selection.** Besides native **Gemini**, any **OpenAI-compatible
  endpoint** is supported: Groq, OpenAI, OpenRouter, Together, **local Ollama**, etc.
  Configure with `ARGOS_LLM_PROVIDER`, `ARGOS_LLM_MODEL` and `ARGOS_LLM_BASE_URL`.
- The job status now shows the provider used (e.g. `IA (groq)` / `AI (groq)`).

### Changed
- The provider is only enabled by **explicit** configuration; unrelated keys already
  present in the environment no longer turn AI on by themselves.
- AI output is bounded (`maxOutputTokens`) with a leaner schema, avoiding huge/slow
  responses.

### Fixed
- AI calls no longer stall on very large model responses.
- Transcript-derived AI output (titles) is sanitized (cut at sentence end, max 8
  words) against prompt injection.
- Startup no longer crashes when the app is launched with a working directory other
  than the install folder (content root is now pinned to the executable's folder).
- `401/402/403` from the provider fail over to the heuristic immediately; `429/5xx`
  are retried.

## [1.2.0] — 2026-11-01

### Added
- **Sentence-boundary cuts:** Whisper segments are grouped into whole sentences so
  clips never start or end mid-idea.
- **Configurable Whisper model** in the UI (`tiny`/`base`/`small`/`medium`/`large-v3-turbo`).
- **Optional AI selection** via Google Gemini (`ARGOS_LLM_KEY`), with the heuristic
  as a fallback and second opinion.
- Configurable clip length (`ARGOS_MIN_CLIP_S` / `ARGOS_MAX_CLIP_S`) and `/api/config`.

### Changed
- Improved clip scoring (stronger opening hook, full-sentence bonus, speech density).

## [1.1.0] — 2026-10-31

### Added
- Native **C# / .NET 8 Windows app** (self-contained executable + installer) alongside
  the original Python version.
- Per-user installer with bundled FFmpeg and Start Menu shortcuts.

## [1.0.0] — 2026-10-31

### Added
- First release: local video-to-vertical-clips pipeline (Whisper transcription,
  sentence-aware selection, 9:16 face reframe, burned-in karaoke captions, optional
  B-roll), with a local web UI.

[1.2.1]: ../../releases/tag/v1.2.1
[1.2.0]: ../../releases/tag/v1.2
[1.1.0]: ../../releases/tag/v1.1
[1.0.0]: ../../releases/tag/v1.0.0
