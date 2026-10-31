# -*- coding: utf-8 -*-
"""Reframe por rosto: rastreia o rosto no clipe e gera crop 9:16 que segue.

Fluxo: sampleia frames do trecho → detecta o maior rosto (Haar) → suaviza o
centro → gera expressões x/y por partes para o `crop` do FFmpeg. Sem rosto
detectado, devolve None e o render usa o crop central (fallback).
"""
from __future__ import annotations

import math
from pathlib import Path

SAMPLE_FPS = 2          # frames analisados por segundo do clipe
MAX_KEYPOINTS = 12      # pontos da interpolação (mantém a expressão curta)
MIN_FACE_HITS = 3       # detecções mínimas para confiar no rastreio
SMOOTH_WINDOW = 5       # média móvel do centro


def _face_cascade():
    import cv2

    path = Path(cv2.data.haarcascades) / "haarcascade_frontalface_default.xml"
    return cv2.CascadeClassifier(str(path))


def track_face(src: Path, start: float, dur: float) -> list[tuple[float, float, float]] | None:
    """Rastreia o rosto no trecho [start, start+dur].

    Devolve [(t_relativo, cx, cy), …] em pixels do vídeo original, ou None
    se o rastreio não for confiável (sem rosto / poucas detecções).
    """
    try:
        import cv2
    except ImportError:
        return None

    cap = cv2.VideoCapture(str(src))
    if not cap.isOpened():
        return None
    W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
    if W <= 0 or H <= 0:
        cap.release()
        return None

    cascade = _face_cascade()
    if cascade.empty():
        cap.release()
        return None

    hits: list[tuple[float, float, float]] = []
    step = 1.0 / SAMPLE_FPS
    t = 0.0
    try:
        while t < dur:
            cap.set(cv2.CAP_PROP_POS_MSEC, (start + t) * 1000.0)
            ok, frame = cap.read()
            if not ok or frame is None:
                break
            # escala p/ largura máx. 480 px — detectar em resolução cheia é lento
            scale = min(1.0, 480.0 / W)
            small = cv2.resize(frame, (int(W * scale), int(H * scale))) \
                if scale < 1.0 else frame
            gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
            faces = cascade.detectMultiScale(gray, scaleFactor=1.1,
                                             minNeighbors=5,
                                             minSize=(24, 24))
            if len(faces):
                x, y, w, h = max(faces, key=lambda f: f[2] * f[3])
                hits.append((t, (x + w / 2) / scale, (y + h / 2) / scale))
            t += step
    finally:
        cap.release()

    if len(hits) < MIN_FACE_HITS:
        return None

    # preenche lacunas interpolando entre detecções vizinhas
    filled = []
    for i in range(len(hits)):
        if i == 0:
            filled.append(hits[0])
            continue
        t0, x0, y0 = hits[i - 1]
        t1, x1, y1 = hits[i]
        if t1 - t0 > step * 2.5:
            n = int((t1 - t0) / step)
            for k in range(1, n):
                f = k / n
                filled.append((t0 + (t1 - t0) * f,
                               x0 + (x1 - x0) * f, y0 + (y1 - y0) * f))
        filled.append(hits[i])

    # suaviza com média móvel
    smooth = []
    for i in range(len(filled)):
        win = filled[max(0, i - SMOOTH_WINDOW // 2): i + SMOOTH_WINDOW // 2 + 1]
        cx = sum(p[1] for p in win) / len(win)
        cy = sum(p[2] for p in win) / len(win)
        smooth.append((filled[i][0], cx, cy))

    # reduz para no máximo MAX_KEYPOINTS (uniforme no tempo)
    if len(smooth) > MAX_KEYPOINTS:
        step_i = (len(smooth) - 1) / (MAX_KEYPOINTS - 1)
        smooth = [smooth[int(round(i * step_i))] for i in range(MAX_KEYPOINTS)]
    return smooth


def build_crop_expr(points: list[tuple[float, float, float]],
                    cw: int, ch: int, W: int, H: int) -> tuple[str, str]:
    """Gera expressões FFmpeg x(t), y(t) por partes lineares que seguem o rosto."""
    max_x = max(0, W - cw)
    max_y = max(0, H - ch)

    def clamp_x(v: float) -> float:
        return min(max_x, max(0.0, v))

    def clamp_y(v: float) -> float:
        return min(max_y, max(0.0, v))

    pts = [(t, clamp_x(cx - cw / 2), clamp_y(cy - ch / 2)) for t, cx, cy in points]

    def piecewise(idx_attr: int, clamp_fn) -> str:
        # interpola linearmente entre pts[i] e pts[i+1]; antes do 1º usa o 1º
        expr = f"{pts[0][idx_attr]:.1f}"
        for i in range(len(pts) - 1, -1, -1):
            t0, x0 = pts[i][0], pts[i][idx_attr]
            if i == len(pts) - 1:
                expr = f"{x0:.1f}"
                continue
            t1, x1 = pts[i + 1][0], pts[i + 1][idx_attr]
            slope = (x1 - x0) / (t1 - t0) if t1 > t0 else 0.0
            inner = f"({x0:.1f}+{slope:.6f}*(t-{t0:.3f}))"
            expr = f"if(lt(t,{t1:.3f}),{inner},{expr})"
        # antes do primeiro keyframe, mantém o primeiro valor
        if len(pts) > 1:
            expr = f"if(lt(t,{pts[0][0]:.3f}),{pts[0][idx_attr]:.1f},{expr})"
        return expr

    return piecewise(1, clamp_x), piecewise(2, clamp_y)


def reframe_vf(src: Path, start: float, dur: float, iw: int, ih: int) -> str | None:
    """Devolve a parte do filtro de crop que segue o rosto, ou None."""
    if iw <= 0 or ih <= 0:
        return None
    cw = min(iw, ih * 9 // 16)
    ch = min(ih, iw * 16 // 9)
    if cw >= iw and ch >= ih:
        return None  # vídeo já é (quase) 9:16 — não há o que recortar
    points = track_face(src, start, dur)
    if not points:
        return None
    x_expr, y_expr = build_crop_expr(points, cw, ch, iw, ih)
    return f"crop={cw}:{ch}:x='{x_expr}':y='{y_expr}'"
