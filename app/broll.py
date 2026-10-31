# -*- coding: utf-8 -*-
"""B-roll automático: busca vídeo stock grátis (Pexels/Pixabay) por palavra-
chave da transcrição e devolve o arquivo para sobreposição no FFmpeg.

Configuração (.env ou variáveis de ambiente — chaves NUNCA hardcoded):
  PEXELS_API_KEY=…   https://www.pexels.com/api/  (grátis)
  PIXABAY_API_KEY=…  https://pixabay.com/api/docs/ (grátis)

Sem chave, ou se a busca falhar, devolve None e o clipe segue sem B-roll —
a falha nunca quebra o pipeline.
"""
from __future__ import annotations

import os
from pathlib import Path

import httpx

MIN_QUERY_LEN = 3          # palavra muito curta não vale busca
CLIP_LEN_S = 4             # segundos de B-roll sobrepostos
STOPWORDS = {
    "a", "o", "as", "os", "um", "uma", "de", "do", "da", "dos", "das",
    "e", "é", "que", "para", "por", "com", "sem", "mais", "menos", "não",
    "sim", "mas", "se", "em", "no", "na", "nos", "nas", "ao", "à", "às",
    "the", "and", "for", "with", "that", "this", "was", "were", "you",
    "your", "have", "has", "had", "not", "but", "they", "them", "his",
    "her", "she", "him", "are", "is", "it", "its", "of", "on", "in", "at",
    "to", "from", "we", "us", "our", "out", "about", "just", "like", "so",
    "what", "when", "how", "why", "who", "all", "can", "will", "would",
    "there", "here", "then", "than", "been", "being", "get", "got", "one",
}


def extract_keywords(text: str, limit: int = 3) -> list[str]:
    """Top-`limit` palavras do conteúdo (sem stopwords), por frequência."""
    import re
    from collections import Counter

    words = re.findall(r"[a-zA-ZÀ-ÿ]{%d,}" % MIN_QUERY_LEN, (text or "").lower())
    counts = Counter(w for w in words if w not in STOPWORDS)
    return [w for w, _ in counts.most_common(limit)]


def _search_pexels(query: str, key: str, want_portrait: bool) -> dict | None:
    r = httpx.get(
        "https://api.pexels.com/videos/search",
        params={"query": query, "per_page": 10, "orientation": "portrait"},
        headers={"Authorization": key},
        timeout=20,
    )
    r.raise_for_status()
    videos = r.json().get("videos") or []
    # prefere qualidade perto de 1080p, retrato
    videos.sort(key=lambda v: abs((v.get("height") or 0) - 1920))
    for v in videos:
        files = [f for f in (v.get("video_files") or [])
                 if f.get("link") and (f.get("height") or 0) >= 1000]
        files.sort(key=lambda f: abs((f.get("height") or 0) - 1920))
        if files:
            return {"url": files[0]["link"], "by": "Pexels"}
    return None


def _search_pixabay(query: str, key: str, want_portrait: bool) -> dict | None:
    r = httpx.get(
        "https://pixabay.com/api/videos/",
        params={"key": key, "query": query, "per_page": 10},
        timeout=20,
    )
    r.raise_for_status()
    hits = r.json().get("hits") or []
    hits.sort(key=lambda h: abs((h.get("height") or 0) - 1920))
    for h in hits:
        large = (h.get("large") or {}).get("url")
        if large:
            return {"url": large, "by": "Pixabay"}
    return None


def fetch_broll(text: str, dest_dir: Path, timeout_s: int = 60) -> Path | None:
    """Busca e baixa um clipe de B-roll relacionado; None se indisponível."""
    pexels = os.environ.get("PEXELS_API_KEY", "").strip()
    pixabay = os.environ.get("PIXABAY_API_KEY", "").strip()
    if not pexels and not pixabay:
        return None

    for query in extract_keywords(text):
        found = None
        try:
            if pexels:
                found = _search_pexels(query, pexels, True)
            if not found and pixabay:
                found = _search_pixabay(query, pixabay, True)
        except Exception:  # noqa: BLE001 — B-roll é melhor esforço
            continue
        if not found:
            continue
        try:
            with httpx.stream("GET", found["url"], timeout=timeout_s,
                              follow_redirects=True) as r:
                r.raise_for_status()
                dst = dest_dir / "broll_stock.mp4"
                with dst.open("wb") as f:
                    for chunk in r.iter_bytes(1024 * 256):
                        f.write(chunk)
            if dst.stat().st_size > 50_000:
                return dst
            dst.unlink(missing_ok=True)
        except Exception:  # noqa: BLE001
            continue
    return None
