# -*- coding: utf-8 -*-
"""Servidor do Argos Clip — FastAPI + worker em thread."""
from __future__ import annotations

import os
import shutil
import threading
import time
import uuid
from pathlib import Path

from contextlib import asynccontextmanager

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from . import pipeline as pl
from . import downloader as dl

BASE = Path(__file__).resolve().parent.parent
# Testes apontam ARGOS_JOBS_DIR para a pasta própria (nunca a de produção).
JOBS = Path(os.environ.get("ARGOS_JOBS_DIR") or (BASE / "jobs"))
JOBS.mkdir(exist_ok=True, parents=True)

# estado em memória: job_id -> dict
JOBS_STATE: dict[str, dict] = {}
LOCK = threading.Lock()

ALLOWED_EXT = {".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts"}
MAX_UPLOAD = 8 * 1024 * 1024 * 1024  # 8 GB

# 5.3 — fila com limite de jobs processando ao mesmo tempo (evita estouro
# de memória com vários Whisper em paralelo). Os demais ficam "na fila".
MAX_ACTIVE_JOBS = max(1, int(os.environ.get("ARGOS_MAX_ACTIVE_JOBS", "2")))
JOB_QUEUE: list = []          # (job_id, fn) aguardando slot
_ACTIVE = 0
_QUEUE_LOCK = threading.Lock()

# 5.2 — retenção de jobs em disco
JOB_TTL_HOURS = float(os.environ.get("ARGOS_JOB_TTL_HOURS", "24"))


def _cleanup_old_jobs() -> None:
    """Remove jobs terminados com mais de JOB_TTL_HOURS horas (disco)."""
    while True:
        time.sleep(300)
        cutoff = time.time() - JOB_TTL_HOURS * 3600
        for d in list(JOBS.iterdir()):
            if not d.is_dir():
                continue
            with LOCK:
                st = JOBS_STATE.get(d.name)
                terminal = st is None or st.get("status") in ("done", "error")
            if terminal and d.stat().st_mtime < cutoff:
                shutil.rmtree(d, ignore_errors=True)
                with LOCK:
                    JOBS_STATE.pop(d.name, None)


def _worker_loop() -> None:
    """Loop único: pega um job da fila quando há slot livre."""
    global _ACTIVE
    while True:
        with _QUEUE_LOCK:
            if JOB_QUEUE and _ACTIVE < MAX_ACTIVE_JOBS:
                job_id, fn = JOB_QUEUE.pop(0)
                _ACTIVE += 1
            else:
                job_id, fn = None, None
        if fn is None:
            time.sleep(0.3)
            continue
        try:
            fn()
        finally:
            with _QUEUE_LOCK:
                _ACTIVE -= 1


def _enqueue(job_id: str, fn) -> None:
    """Coloca o processamento na fila (respeitando MAX_ACTIVE_JOBS)."""
    with _QUEUE_LOCK:
        JOB_QUEUE.append((job_id, fn))


@asynccontextmanager
async def _lifespan(_app):
    threading.Thread(target=_worker_loop, daemon=True).start()
    threading.Thread(target=_cleanup_old_jobs, daemon=True).start()
    yield


app = FastAPI(title="Argos Clip", lifespan=_lifespan)


# 5.4 — cabeçalhos de segurança padrão em todas as respostas
@app.middleware("http")
async def _security_headers(request, call_next):
    resp = await call_next(request)
    resp.headers.setdefault("X-Content-Type-Options", "nosniff")
    resp.headers.setdefault("X-Frame-Options", "DENY")
    resp.headers.setdefault("Referrer-Policy", "no-referrer")
    resp.headers.setdefault("X-Permitted-Cross-Domain-Policies", "none")
    return resp


def _set(job_id: str, **kw) -> None:
    with LOCK:
        JOBS_STATE[job_id].update(kw)


def _job_dir(job_id: str) -> Path:
    return JOBS / job_id


def _process(job_id: str, src: Path, n_clips: int, language: str | None) -> None:
    try:
        job_dir = _job_dir(job_id)
        _set(job_id, status="probe", stage="Analisando vídeo…")
        info = pl.probe(src)
        if not info["has_audio"]:
            raise ValueError("O vídeo não tem faixa de áudio — não dá para transcrever.")
        _set(job_id, duration=info["duration"],
             width=info["width"], height=info["height"])

        _set(job_id, status="audio", stage="Extraindo áudio…")
        wav = pl.extract_audio(job_dir, src)

        _set(job_id, status="transcribe", stage="Transcrevendo com Whisper (local)…",
             progress=0.0)

        def cb(p: float) -> None:
            _set(job_id, progress=round(p, 1))

        segments = pl.transcribe(wav, language, info["duration"], progress_cb=cb)
        if not segments:
            raise ValueError("Nenhuma fala detectada no áudio.")
        _set(job_id, transcript_segments=len(segments),
             stage="Escolhendo os melhores momentos…")

        chunks = pl.build_chunks(segments)
        chosen = pl.select_clips(chunks, n=n_clips)
        if not chosen:
            raise ValueError("Não foi possível montar clipes a partir da transcrição.")

        max_score = max((c.score for c in chosen), default=1.0)

        # B-roll: busca uma vez por job (melhor esforço — sem chave ou falha
        # de rede, os clipes seguem sem overlay)
        full_text = " ".join(c.text for c in chosen)
        broll_path = None
        if os.environ.get("ARGOS_BROLL", "1") != "0":
            _set(job_id, stage="Buscando B-roll (Pexels/Pixabay)…")
            try:
                broll_path = br.fetch_broll(full_text, job_dir)
            except Exception:  # noqa: BLE001
                broll_path = None

        clips_meta = []
        for idx, clip in enumerate(chosen, start=1):
            _set(job_id, status="render",
                 stage=f"Renderizando clipe {idx}/{len(chosen)}…",
                 progress=round((idx - 1) / len(chosen) * 100, 1))
            out, thumb = pl.render_clip(job_dir, src, clip, idx, broll=broll_path)
            clips_meta.append({
                "index": idx,
                "video": out.name,
                "thumb": thumb.name if thumb.exists() else None,
                "start": pl.fmt_time(clip.start),
                "end": pl.fmt_time(clip.end),
                "duration": round(clip.end - clip.start, 1),
                "score": pl.clip_score_100(clip.score, max_score),
                "reasons": clip.reasons,
                "text": clip.text[:220] + ("…" if len(clip.text) > 220 else ""),
                "words": len(clip.words),
                "broll": bool(getattr(clip, "broll_used", False)),
            })

        try:
            wav.unlink(missing_ok=True)
        except OSError:
            pass

        _set(job_id, status="done", stage="Concluído!", progress=100.0,
             clips=clips_meta)
    except Exception as e:  # noqa: BLE001 — vira erro visível na UI
        _set(job_id, status="error", stage="Erro", error=str(e))


@app.post("/api/upload")
async def upload(file: UploadFile = File(...), n_clips: str = Form("5"),
                 language: str = Form("")):
    name = file.filename or "video"
    ext = Path(name).suffix.lower()
    if ext not in ALLOWED_EXT:
        raise HTTPException(400, f"Formato não suportado: {ext}. Use mp4/mov/mkv/webm/avi.")
    try:
        n = max(1, min(10, int(n_clips)))
    except ValueError:
        n = 5

    job_id = uuid.uuid4().hex[:12]
    job_dir = _job_dir(job_id)
    job_dir.mkdir(parents=True)
    safe_name = "source" + ext
    dst = job_dir / safe_name

    size = 0
    with dst.open("wb") as f:
        while chunk := await file.read(4 * 1024 * 1024):
            size += len(chunk)
            if size > MAX_UPLOAD:
                f.close()
                shutil.rmtree(job_dir, ignore_errors=True)
                raise HTTPException(413, "Arquivo maior que 8 GB.")
            f.write(chunk)
    if size == 0:
        shutil.rmtree(job_dir, ignore_errors=True)
        raise HTTPException(400, "Arquivo vazio.")

    with LOCK:
        JOBS_STATE[job_id] = {
            "id": job_id, "status": "queued", "stage": "Na fila…",
            "progress": 0.0, "file": name, "clips": [], "error": None,
            "created": time.time(),
        }
    _enqueue(job_id, lambda: _process(job_id, dst, n, language or None))
    return {"job_id": job_id}


@app.post("/api/link")
async def from_link(payload: dict):
    """Recebe um link de vídeo (YouTube, Twitch, etc.), baixa e processa."""
    url = payload.get("url") or ""
    try:
        url = dl.validate_url(url)
    except ValueError as e:
        raise HTTPException(400, str(e))
    try:
        n = max(1, min(10, int(payload.get("n_clips", 5))))
    except (TypeError, ValueError):
        n = 5
    language = payload.get("language") or None

    job_id = uuid.uuid4().hex[:12]
    job_dir = _job_dir(job_id)
    job_dir.mkdir(parents=True)
    with LOCK:
        JOBS_STATE[job_id] = {
            "id": job_id, "status": "download", "stage": "Baixando do link…",
            "progress": 0.0, "file": url, "clips": [], "error": None,
            "created": time.time(),
        }

    def worker() -> None:
        try:
            def cb(p: float, msg: str) -> None:
                _set(job_id, progress=round(p, 1), stage=msg)

            src = dl.download(url, job_dir, progress_cb=cb)
            _process(job_id, src, n, language)
        except Exception as e:  # noqa: BLE001
            _set(job_id, status="error", stage="Erro", error=str(e))

    _set(job_id, status="queued", stage="Na fila…")
    _enqueue(job_id, worker)
    return {"job_id": job_id}


@app.get("/api/status/{job_id}")
async def status(job_id: str):
    with LOCK:
        st = JOBS_STATE.get(job_id)
        if not st:
            raise HTTPException(404, "Job não encontrado.")
        return JSONResponse(dict(st))


@app.get("/media/{job_id}/{name}")
async def media(job_id: str, name: str):
    job_dir = _job_dir(job_id)
    path = (job_dir / name).resolve()
    if not str(path).startswith(str(job_dir.resolve())) or not path.is_file():
        raise HTTPException(404, "Arquivo não encontrado.")
    return FileResponse(path)


@app.get("/")
async def index():
    return FileResponse(Path(__file__).parent / "static" / "index.html")


app.mount("/static", StaticFiles(directory=Path(__file__).parent / "static"), name="static")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8484)
