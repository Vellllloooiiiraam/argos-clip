# -*- coding: utf-8 -*-
"""Download de vídeos por link via yt-dlp, com validação anti-SSRF."""
from __future__ import annotations

import threading
from pathlib import Path
from urllib.parse import urlparse

# Hosts permitidos — qualquer outro URL é recusado (evita SSRF: o servidor
# só baixa de plataformas de vídeo conhecidas, não de internos/redes privadas).
ALLOWED_HOSTS = {
    "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be",
    "music.youtube.com",
    "twitch.tv", "www.twitch.tv", "clips.twitch.tv",
    "vimeo.com", "www.vimeo.com", "player.vimeo.com",
    "facebook.com", "www.facebook.com", "fb.watch",
    "twitter.com", "www.twitter.com", "x.com", "www.x.com",
    "reddit.com", "www.reddit.com", "v.redd.it",
    "tiktok.com", "www.tiktok.com", "vm.tiktok.com",
    "instagram.com", "www.instagram.com",
    "dailymotion.com", "www.dailymotion.com", "dai.ly",
    "kick.com", "www.kick.com",
    "rumble.com", "www.rumble.com",
    "odysee.com", "www.odysee.com",
    "bitchute.com", "www.bitchute.com",
    "streamable.com", "www.streamable.com",
    # arquivos diretos em CDNs comuns (mp4 avulso)
    "cdn.jsdelivr.net", "commondatastorage.googleapis.com",
    "download.blender.org", "test-videos.co.uk",
}
ALLOWED_SCHEMES = {"http", "https"}

# Limites anti-DoS (item 5.1 do relatório de segurança):
MAX_VIDEO_DURATION_S = 7200   # vídeos com mais de 2 h são recusados
MAX_DOWNLOAD_WALL_S = 30 * 60  # download abortado após 30 min de relógio real


def _duration_ok(info, *, incomplete):
    """match_filter do yt-dlp: recusa vídeos longos demais antes de baixar."""
    dur = (info or {}).get("duration")
    if dur and dur > MAX_VIDEO_DURATION_S:
        return (f"Vídeo muito longo ({int(dur // 60)} min) — "
                f"o limite é {MAX_VIDEO_DURATION_S // 60} min.")
    return None


def validate_url(url: str) -> str:
    """Valida o link; devolve a URL limpa ou levanta ValueError."""
    url = (url or "").strip()
    if not url:
        raise ValueError("Link vazio.")
    if len(url) > 2048:
        raise ValueError("Link longo demais.")
    parsed = urlparse(url)
    if parsed.scheme not in ALLOWED_SCHEMES:
        raise ValueError("Use um link http:// ou https://.")
    host = (parsed.hostname or "").lower().rstrip(".")
    if not host:
        raise ValueError("Link sem host válido.")
    if host not in ALLOWED_HOSTS:
        raise ValueError(
            f"Plataforma não suportada: {host}. "
            "Aceito: YouTube, Twitch, Vimeo, Facebook, X, Reddit, TikTok, "
            "Instagram, Dailymotion, Kick, Rumble, Odysee, Streamable."
        )
    return url


def download(url: str, dest_dir: Path, progress_cb=None) -> Path:
    """Baixa o vídeo para dest_dir e devolve o caminho do arquivo."""
    import time

    import yt_dlp
    from yt_dlp.utils import DownloadError

    started = time.monotonic()

    def hook(d) -> None:
        # teto de relógio real: aborta downloads que demoram demais (DoS)
        if time.monotonic() - started > MAX_DOWNLOAD_WALL_S:
            raise DownloadError(
                f"Download cancelado: mais de {MAX_DOWNLOAD_WALL_S // 60} minutos "
                "baixando este link.")
        if not progress_cb:
            return
        if d.get("status") == "downloading":
            total = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
            done = d.get("downloaded_bytes") or 0
            if total:
                progress_cb(min(99.0, done / total * 100.0),
                            f"Baixando… {done // 1048576}/{total // 1048576} MB")
        elif d.get("status") == "finished":
            progress_cb(99.0, "Finalizando arquivo…")

    opts = {
        "outtmpl": str(dest_dir / "source.%(ext)s"),
        "format": "bv*[ext=mp4][height<=1080]+ba[ext=m4a]/"
                  "b[ext=mp4][height<=1080]/"
                  "bv*[height<=1080]+ba/b",
        "merge_output_format": "mp4",
        "noplaylist": True,
        "max_filesize": 8 * 1024 * 1024 * 1024,
        "socket_timeout": 30,
        "retries": 3,
        "progress_hooks": [hook],
        "match_filter": _duration_ok,
        "quiet": True,
        "no_warnings": True,
        # não executar pós-processadores externos
        "postprocessors": [],
    }
    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info(url, download=True)
    except Exception:
        # remove parciais (.part) e sobras do download abortado
        for p in dest_dir.glob("source.*"):
            p.unlink(missing_ok=True)
        raise
    if info.get("_type") == "playlist" or info.get("entries"):
        raise ValueError("Playlist não é suportada — use um vídeo único.")

    files = sorted(dest_dir.glob("source.*"),
                   key=lambda p: p.stat().st_size, reverse=True)
    files = [f for f in files if f.suffix.lower()
             in {".mp4", ".mkv", ".webm", ".mov", ".m4v", ".flv", ".ts"}]
    if not files:
        raise RuntimeError("O download não gerou nenhum arquivo de vídeo.")
    return files[0]
