# -*- coding: utf-8 -*-
"""Pipeline do Argos Clip: transcrição, seleção de momentos e renderização de clipes."""
from __future__ import annotations

import json
import math
import re
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

from . import reframe as rf
from . import broll as br

# ---------------------------------------------------------------------------
# FFmpeg / ffprobe
# ---------------------------------------------------------------------------

def run(cmd: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd, cwd=str(cwd) if cwd else None,
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )


def probe(src: Path) -> dict:
    """Retorna duração, dimensões e presença de áudio via ffprobe."""
    cmd = [
        "ffprobe", "-v", "error", "-print_format", "json",
        "-show_format", "-show_streams", str(src),
    ]
    p = run(cmd)
    if p.returncode != 0:
        raise RuntimeError(f"ffprobe falhou: {p.stderr[:500]}")
    data = json.loads(p.stdout or "{}")
    duration = float(data.get("format", {}).get("duration", 0) or 0)
    width = height = 0
    has_audio = False
    for st in data.get("streams", []):
        if st.get("codec_type") == "video" and not width:
            width = int(st.get("width") or 0)
            height = int(st.get("height") or 0)
        elif st.get("codec_type") == "audio":
            has_audio = True
    return {"duration": duration, "width": width, "height": height, "has_audio": has_audio}


def extract_audio(job_dir: Path, src: Path) -> Path:
    """Extrai áudio 16 kHz mono WAV para o Whisper."""
    out = job_dir / "audio.wav"
    p = run([
        "ffmpeg", "-y", "-i", str(src),
        "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", str(out),
    ])
    if p.returncode != 0:
        raise RuntimeError(f"extração de áudio falhou: {p.stderr[:500]}")
    return out


# ---------------------------------------------------------------------------
# Transcrição (faster-whisper, local)
# ---------------------------------------------------------------------------

_MODEL = None

def get_model(name: str = "small"):
    global _MODEL
    if _MODEL is None:
        from faster_whisper import WhisperModel
        _MODEL = WhisperModel(name, device="cpu", compute_type="int8")
    return _MODEL


@dataclass
class Word:
    start: float
    end: float
    text: str


@dataclass
class Segment:
    start: float
    end: float
    text: str
    words: list[Word] = field(default_factory=list)


def transcribe(wav: Path, language: str | None, duration: float, progress_cb=None) -> list[Segment]:
    """Transcreve com timestamps por palavra; reporta progresso via callback."""
    model = get_model()
    segs, info = model.transcribe(
        str(wav), language=language or None,
        word_timestamps=True, vad_filter=True,
        beam_size=2,
    )
    out: list[Segment] = []
    for s in segs:
        words = [Word(w.start, w.end, (w.word or "").strip()) for w in (s.words or [])]
        out.append(Segment(s.start, s.end, (s.text or "").strip(), words))
        if progress_cb and duration > 0:
            progress_cb(min(99.0, s.end / duration * 100.0))
    return out


# ---------------------------------------------------------------------------
# Seleção de momentos (heurística de "viralidade")
# ---------------------------------------------------------------------------

HOOK_PT = [
    "nunca", "sempre", "segredo", "erro", "erros", "dinheiro", "grátis", "gratuito",
    "importante", "atenção", "cuidado", "como", "por que", "porquê", "motivo",
    "dica", "dicas", "método", "forma", "jeito", "resultado", "resultados",
    "prova", "comprova", "comprovei", "testei", "descobri", "ninguém", "todo mundo",
    "simples", "fácil", "rápido", "estratégia", "verdade", "mentira", "funciona",
    "aumentar", "ganhar", "perdeu", "evite", "só", "apenas", "agora", "hoje",
    "o problema", "a realidade", "você precisa", "precisa saber", "mudou tudo",
]
HOOK_EN = [
    "never", "always", "secret", "mistake", "money", "free", "important",
    "attention", "how", "why", "reason", "tip", "trick", "result", "proof",
    "tested", "discovered", "nobody", "everyone", "simple", "easy", "fast",
    "strategy", "true", "lie", "works", "increase", "earn", "stop", "avoid",
    "you need", "changed everything",
]
RE_NUMBER = re.compile(r"\d|%|r\$|us\$|mil|milh", re.I)


@dataclass
class Clip:
    start: float
    end: float
    text: str
    words: list[Word]
    score: float = 0.0
    reasons: list[str] = field(default_factory=list)


def build_chunks(segments: list[Segment], min_len: float = 18.0, max_len: float = 55.0) -> list[Clip]:
    """Agrupa segmentos consecutivos em janelas de duração de clipe."""
    chunks: list[Clip] = []
    cur: list[Segment] = []

    def flush() -> None:
        if not cur:
            return
        start = cur[0].start
        end = cur[-1].end
        words = [w for s in cur for w in s.words]
        text = " ".join(s.text for s in cur).strip()
        if end - start >= 5.0:
            chunks.append(Clip(start, end, text, words))
        cur.clear()

    for s in segments:
        cur.append(s)
        span = cur[-1].end - cur[0].start
        if span >= max_len:
            flush()
        elif span >= min_len and s.text and s.text.rstrip().endswith((".", "!", "?", "…")):
            flush()
    flush()
    return chunks


def score_chunk(clip: Clip) -> None:
    """Pontua o clipe e registra os motivos (para exibir na UI)."""
    text_l = clip.text.lower()
    words_per_sec = len(clip.words) / max(0.1, clip.end - clip.start)
    score = 0.0
    reasons: list[str] = []

    hits = [k for k in HOOK_PT + HOOK_EN if k in text_l]
    if hits:
        uniq = sorted(set(hits), key=text_l.index)[:5]
        score += min(6.0, 1.5 * len(uniq))
        reasons.append("ganchos: " + ", ".join(uniq))

    if RE_NUMBER.search(clip.text):
        score += 3.0
        reasons.append("números/dados")

    if "?" in clip.text:
        score += 2.5
        reasons.append("pergunta")
    elif "!" in clip.text:
        score += 1.0

    first_text = " ".join(s.text for s in [Segment(0, 0, w.text, []) for w in clip.words[:8]]).lower()
    if any(k in first_text for k in HOOK_PT + HOOK_EN) or RE_NUMBER.search(first_text):
        score += 4.0
        reasons.append("gancho nos 1ºs segundos")

    # densidade de fala (penaliza silêncios longos)
    gaps = [b.start - a.end for a, b in zip(clip.words, clip.words[1:])]
    long_gaps = [g for g in gaps if g > 1.2]
    if words_per_sec >= 2.2:
        score += 1.5
        reasons.append("fala densa")
    elif words_per_sec < 1.2:
        score -= 1.5
    if long_gaps:
        score -= min(2.0, 0.5 * len(long_gaps))

    if clip.end - clip.start <= 45:
        score += 0.8

    clip.score = max(0.0, score)
    clip.reasons = reasons


def select_clips(chunks: list[Clip], n: int = 5) -> list[Clip]:
    """Escolhe os N melhores chunks sem sobreposição temporal."""
    for c in chunks:
        score_chunk(c)
    picked: list[Clip] = []
    for c in sorted(chunks, key=lambda x: x.score, reverse=True):
        if len(picked) >= n:
            break
        if c.score <= 0 and picked:
            continue
        if any(not (c.end <= p.start or c.start >= p.end) for p in picked):
            continue
        picked.append(c)
    return sorted(picked, key=lambda x: x.start)


# ---------------------------------------------------------------------------
# Legendas estilo karaoke (ASS) e renderização
# ---------------------------------------------------------------------------

ASS_HEADER = """[Script Info]
ScriptType: v4.00+
PlayResX: 1080
PlayResY: 1920
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Cap,Arial,104,&H00FFFFFF,&H0000FFFF,&H00000000,&H7F000000,-1,0,0,0,100,100,0,0,1,9,3,2,70,70,560,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""


def _ass_time(t: float) -> str:
    t = max(0.0, t)
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = t % 60
    return f"{h:d}:{m:02d}:{s:05.2f}"


def _clean_ass(text: str) -> str:
    return re.sub(r"[\r\n]+", " ", text).replace("{", "(").replace("}", ")").strip()


def write_ass(words: list[Word], offset: float, path: Path, max_phrase: int = 4) -> None:
    """Gera .ass com frases curtas e efeito karaoke palavra a palavra."""
    lines = [ASS_HEADER]
    i = 0
    n = len(words)
    while i < n:
        j = i
        while j < n and j - i < max_phrase:
            w = words[j]
            if j > i and (w.start - words[j - 1].end) > 0.8:
                break
            j += 1
        group = words[i:j]
        start = group[0].start - offset - 0.05
        end = group[-1].end - offset + 0.30
        parts = []
        for w in group:
            dur_cs = max(1, int(round((w.end - w.start) * 100)))
            parts.append(f"{{\\k{dur_cs}}}{_clean_ass(w.text.upper())}")
        text = " ".join(parts)
        lines.append(
            f"Dialogue: 0,{_ass_time(start)},{_ass_time(end)},Cap,,0,0,0,,{text}\n"
        )
        i = j
    path.write_text("".join(lines), encoding="utf-8")


def render_clip(job_dir: Path, src: Path, clip: Clip, idx: int,
                broll: Path | None = None) -> tuple[Path, Path]:
    """Recorta 9:16 (seguindo o rosto quando possível), escala, legenda, thumb.

    Se `broll` existir, sobrepõe o clipe stock no meio do vídeo (o áudio
    continua sendo o original).
    """
    ass = job_dir / f"clip_{idx:02d}.ass"
    write_ass(clip.words, clip.start, ass)
    out = job_dir / f"clip_{idx:02d}.mp4"
    dur = clip.end - clip.start

    info = probe(src)
    # reframe por rosto; sem rosto confiável, cai no crop central (fallback)
    crop = rf.reframe_vf(src, clip.start, dur, info["width"], info["height"])
    if crop is None:
        crop = "crop='min(iw,ih*9/16)':'min(ih,iw*16/9)'"

    if broll and broll.exists() and dur >= 6:
        # overlay de B-roll no trecho do meio; legendas ficam por cima
        ov_start = round(dur * 0.35, 2)
        ov_end = round(min(dur, ov_start + br.CLIP_LEN_S), 2)
        fc = (
            f"[0:v]{crop},scale=1080:1920:flags=lanczos[base];"
            f"[1:v]scale=1080:1920:force_original_aspect_ratio=increase,"
            f"crop=1080:1920,setsar=1[br];"
            f"[base][br]overlay=0:0:enable='between(t,{ov_start},{ov_end})'[vo];"
            f"[vo]subtitles={ass.name}[vout]"
        )
        cmd = [
            "ffmpeg", "-y", "-ss", f"{clip.start:.3f}", "-t", f"{dur:.3f}",
            "-i", src.name, "-stream_loop", "-1", "-i", str(broll),
            "-filter_complex", fc,
            "-map", "[vout]", "-map", "0:a",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
            "-c:a", "aac", "-b:a", "128k", "-t", f"{dur:.3f}",
            "-movflags", "+faststart", out.name,
        ]
        p = run(cmd, cwd=job_dir)
        if p.returncode == 0:
            clip.broll_used = True
        else:
            # fallback: renderiza sem B-roll
            broll = None

    if broll is None or not getattr(clip, "broll_used", False):
        vf = (
            f"{crop},"
            "scale=1080:1920:flags=lanczos,"
            f"subtitles={ass.name}"
        )
        p = run([
            "ffmpeg", "-y", "-ss", f"{clip.start:.3f}", "-t", f"{dur:.3f}",
            "-i", src.name, "-vf", vf,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart",
            out.name,
        ], cwd=job_dir)
    if p.returncode != 0:
        raise RuntimeError(f"render do clipe {idx} falhou: {p.stderr[-700:]}")
    thumb = job_dir / f"clip_{idx:02d}.jpg"
    run([
        "ffmpeg", "-y", "-ss", f"{max(0.1, dur / 2):.3f}", "-i", out.name,
        "-frames:v", "1", "-q:v", "3", thumb.name,
    ], cwd=job_dir)
    return out, thumb


def fmt_time(t: float) -> str:
    m, s = divmod(int(round(t)), 60)
    return f"{m}:{s:02d}"


def clip_score_100(score: float, max_score: float) -> int:
    """Normaliza a nota bruta para 0–100 de exibição."""
    if max_score <= 0:
        return 50
    return int(min(99, max(1, round(50 + 50 * score / max_score))))
