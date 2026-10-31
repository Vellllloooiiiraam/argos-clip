# -*- coding: utf-8 -*-
"""Testes de integração do Argos Clip — batem nas rotas reais.

Regra de ouro: nenhum teste aqui pode ser apagado, pulado ou afrouxado.
Se ficar vermelho depois de mudança no código, o erro é devolvido — não o teste.
"""
import shutil
import threading
import time
from pathlib import Path

from fastapi.testclient import TestClient


# ---------------------------------------------------------------------------
# upload
# ---------------------------------------------------------------------------

def test_upload_valido_gera_clipes(done_job):
    """quando o vídeo é válido, o job termina com clipes 9:16 e nota"""
    clips = done_job["clips"]
    assert clips, "job terminou 'done' sem clipes"
    c0 = clips[0]
    assert c0["video"] == "clip_01.mp4"
    assert 1 <= c0["score"] <= 99, f"nota fora do intervalo: {c0['score']}"
    assert c0["duration"] > 5, "clipe curto demais para ter fala"
    assert c0["text"].strip(), "transcrição vazia no clipe"
    assert c0["thumb"], "thumbnail ausente"
    assert c0["reasons"], "clipe sem motivo de seleção"


def test_recusa_extensao_nao_suportada(client):
    """quando o arquivo tem extensão não suportada, responde 400 com mensagem clara"""
    r = client.post(
        "/api/upload",
        files={"file": ("foto.txt", b"oi", "text/plain")},
    )
    assert r.status_code == 400
    assert "suportado" in r.json()["detail"].lower()


def test_recusa_arquivo_vazio(client):
    """quando o arquivo vem vazio, responde 400"""
    r = client.post(
        "/api/upload",
        files={"file": ("vazio.mp4", b"", "video/mp4")},
    )
    assert r.status_code == 400
    assert "vazio" in r.json()["detail"].lower()


# ---------------------------------------------------------------------------
# link (anti-SSRF)
# ---------------------------------------------------------------------------

def test_aceita_link_de_plataforma_permitida(client, wait_job, voiced_video, monkeypatch):
    """quando o link é de plataforma permitida, cria o job e processa até o fim"""
    import app.downloader as dl

    # rede externa é simulada (o YouTube não pode ser dependência de teste);
    # a rota, a validação e o pipeline continuam reais.
    def fake_download(url, dest_dir, progress_cb=None):
        dst = dest_dir / "source.mp4"
        shutil.copy(voiced_video, dst)
        return dst

    monkeypatch.setattr(dl, "download", fake_download)
    r = client.post("/api/link", json={
        "url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
        "n_clips": "1", "language": "",
    })
    assert r.status_code == 200, r.text
    st = wait_job(r.json()["job_id"], timeout=420)
    assert st["status"] == "done", f"processamento do link falhou: {st.get('error')}"
    assert st["clips"], "link processou mas não gerou clipes"


def test_recusa_host_fora_da_lista_ssrf(client):
    """quando o host não está na lista de plataformas, responde 400 (SSRF bloqueado)"""
    r = client.post("/api/link", json={"url": "https://evil.com/malware.mp4"})
    assert r.status_code == 400
    assert "não suportada" in r.json()["detail"]


def test_recusa_esquema_file_e_ftp(client):
    """quando o esquema não é http/https, responde 400"""
    for url in ("file:///C:/Windows/System32/calc.exe", "ftp://x.com/a.mp4"):
        r = client.post("/api/link", json={"url": url})
        assert r.status_code == 400, url
        assert "http" in r.json()["detail"]


def test_recusa_subdominio_falso(client):
    """quando o host imita uma plataforma (youtube.com.evil.com), responde 400"""
    r = client.post("/api/link", json={"url": "https://youtube.com.evil.com/x"})
    assert r.status_code == 400
    assert "não suportada" in r.json()["detail"]


def test_recusa_playlist(client, wait_job, monkeypatch):
    """quando o link é de playlist, o job termina com erro pedindo vídeo único"""
    import yt_dlp

    class FakeYDL:
        def __init__(self, opts=None, **kw):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

        def extract_info(self, url, download=True):
            return {"_type": "playlist", "entries": [{"id": "a"}, {"id": "b"}]}

    monkeypatch.setattr(yt_dlp, "YoutubeDL", FakeYDL)
    r = client.post("/api/link", json={
        "url": "https://www.youtube.com/playlist?list=PLteste",
        "n_clips": "1",
    })
    assert r.status_code == 200, r.text
    st = wait_job(r.json()["job_id"], timeout=60)
    assert st["status"] == "error"
    assert "playlist" in st["error"].lower()


# ---------------------------------------------------------------------------
# entrega dos clipes
# ---------------------------------------------------------------------------

def test_serve_clipe_com_200_e_bytes(client, done_job):
    """quando o clipe existe, /media entrega 200 com o arquivo inteiro"""
    r = client.get(f"/media/{done_job['id']}/clip_01.mp4")
    assert r.status_code == 200
    assert len(r.content) > 50_000, f"mp4 pequeno demais: {len(r.content)} bytes"
    assert r.content[:4] in (b"\x00\x00\x00\x18", b"\x00\x00\x00\x1c",
                             b"\x00\x00\x00\x20", b"ftyp"), "não parece um mp4"


def test_bloqueia_path_traversal(client, done_job):
    """quando o nome tenta sair da pasta do job (..%5c), responde 404"""
    for evil in ("..%5c..%5cconftest.py", "..%5c..%5c..%5capp%5cserver.py"):
        r = client.get(f"/media/{done_job['id']}/{evil}")
        assert r.status_code == 404, f"path traversal passou: {evil}"


def test_status_job_inexistente_404(client):
    """quando o job não existe, /api/status responde 404"""
    r = client.get("/api/status/naoexiste123")
    assert r.status_code == 404


# ---------------------------------------------------------------------------
# robustez
# ---------------------------------------------------------------------------

def test_dois_uploads_no_mesmo_milissegundo(client, voiced_video, wait_job):
    """quando dois uploads chegam ao mesmo tempo, geram jobs distintos e independentes"""
    barrier = threading.Barrier(2)
    results: dict = {}
    errors: list = []

    def fire(key: int) -> None:
        try:
            with TestClient(__import__("app.server", fromlist=["app"]).app) as c:
                barrier.wait(timeout=10)  # os dois POSTs saem juntos
                r = c.post(
                    "/api/upload",
                    data={"n_clips": "1", "language": ""},
                    files={"file": (f"paralelo{key}.mp4", voiced_video.read_bytes(),
                                    "video/mp4")},
                )
                results[key] = (r.status_code,
                                r.json().get("job_id") if r.status_code == 200 else None)
        except Exception as e:  # noqa: BLE001
            errors.append(e)

    ts = [threading.Thread(target=fire, args=(i,)) for i in (1, 2)]
    for t in ts:
        t.start()
    for t in ts:
        t.join(timeout=30)

    assert not errors, f"uploads simultâneos quebraram: {errors}"
    ids = [v[1] for v in results.values() if v[0] == 200]
    assert len(ids) == 2, f"esperava 2 jobs 200, veio {results}"
    assert len(set(ids)) == 2, "os dois uploads viraram o mesmo job_id"

    st1 = wait_job(ids[0], timeout=420)
    st2 = wait_job(ids[1], timeout=420)
    assert st1["status"] == "done" and st2["status"] == "done", (
        f"jobs simultâneos falharam: {st1.get('error')} | {st2.get('error')}")
    assert st1["clips"] and st2["clips"]
    assert st1["id"] != st2["id"]


def test_video_sem_audio_termina_com_erro_explicado(client, upload, wait_job,
                                                    silent_video):
    """quando o vídeo não tem áudio, o job termina em erro explicando a causa"""
    job_id = upload("sem-audio.mp4", silent_video.read_bytes())
    st = wait_job(job_id, timeout=60)
    assert st["status"] == "error"
    assert "áudio" in st["error"]
