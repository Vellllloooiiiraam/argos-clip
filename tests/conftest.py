# -*- coding: utf-8 -*-
"""Fixtures do Argos Clip — rotas reais, pasta de jobs própria de teste."""
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

# 1) pasta de jobs EXCLUSIVA dos testes (jamais a de produção)
TEST_JOBS = ROOT / "test-jobs"
shutil.rmtree(TEST_JOBS, ignore_errors=True)
TEST_JOBS.mkdir(exist_ok=True)
os.environ["ARGOS_JOBS_DIR"] = str(TEST_JOBS)

FIXTURES = Path(__file__).resolve().parent / "fixtures"
FIXTURES.mkdir(exist_ok=True)

# Voz sintética com palavras-gancho do domínio, ≥5 s de fala (regra do
# build_chunks não pode ser afrouxada para o teste passar).
TTS_TEXT = (
    "O segredo para editar vídeos é simples e todos precisam saber. "
    "Nunca desista de praticar todos os dias, porque a prática leva você "
    "ao resultado que você quer alcançar."
)


def _ps1_tts(wav_path: Path) -> None:
    script = FIXTURES / "tts.ps1"
    body = (
        "Add-Type -AssemblyName System.Speech\n"
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer\n"
        "$s.Rate = 0\n"
        f"$s.SetOutputToWaveFile('{wav_path}')\n"
        f"$s.Speak(@'\n{TTS_TEXT}\n'@)\n"
        "$s.Dispose()\n"
    )
    # BOM para o PowerShell 5.1 ler os acentos corretamente
    script.write_text("﻿" + body, encoding="utf-8")
    p = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if p.returncode != 0 or not wav_path.exists():
        raise RuntimeError(f"TTS falhou: {p.stderr[:400]}")


def _make_fixtures() -> tuple[Path, Path]:
    voiced = FIXTURES / "voz.mp4"
    silent = FIXTURES / "sem-audio.mp4"
    if not voiced.exists():
        wav = FIXTURES / "voz.wav"
        _ps1_tts(wav)
        p = subprocess.run([
            "ffmpeg", "-y",
            "-f", "lavfi", "-i", "color=c=0x101020:s=640x360:d=20",
            "-i", str(wav),
            "-c:v", "libx264", "-preset", "veryfast",
            "-c:a", "aac", "-shortest", str(voiced),
        ], capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0:
            raise RuntimeError(f"ffmpeg fixture falhou: {p.stderr[-400:]}")
        wav.unlink(missing_ok=True)
    if not silent.exists():
        p = subprocess.run([
            "ffmpeg", "-y",
            "-f", "lavfi", "-i", "color=c=0x301020:s=320x240:d=3",
            "-an", "-c:v", "libx264", "-preset", "veryfast", str(silent),
        ], capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0:
            raise RuntimeError(f"ffmpeg sem-audio falhou: {p.stderr[-400:]}")
    return voiced, silent


@pytest.fixture(scope="session")
def voiced_video() -> Path:
    return _make_fixtures()[0]


@pytest.fixture(scope="session")
def silent_video() -> Path:
    return _make_fixtures()[1]


@pytest.fixture(scope="session")
def client():
    from fastapi.testclient import TestClient
    from app.server import app
    with TestClient(app) as c:
        yield c


@pytest.fixture(scope="session")
def wait_job(client):
    import time

    def _wait(job_id: str, timeout: float = 300.0) -> dict:
        deadline = time.time() + timeout
        while time.time() < deadline:
            r = client.get(f"/api/status/{job_id}")
            assert r.status_code == 200, f"status sumiu: {r.status_code}"
            st = r.json()
            if st["status"] in ("done", "error"):
                return st
            time.sleep(0.4)
        raise AssertionError(f"job {job_id} não terminou em {timeout}s")

    return _wait


@pytest.fixture(scope="session")
def upload(client):
    def _upload(filename: str, content: bytes, n_clips: str = "3") -> str:
        r = client.post(
            "/api/upload",
            data={"n_clips": n_clips, "language": ""},
            files={"file": (filename, content, "video/mp4")},
        )
        assert r.status_code == 200, f"upload recusado: {r.status_code} {r.text}"
        return r.json()["job_id"]

    return _upload


@pytest.fixture(scope="session")
def done_job(upload, wait_job, voiced_video):
    """Pipeline feliz completo: upload → Whisper → clipes."""
    job_id = upload("voz.mp4", voiced_video.read_bytes(), n_clips="2")
    st = wait_job(job_id, timeout=420)
    assert st["status"] == "done", f"pipeline falhou: {st.get('error')}"
    return st
