# -*- coding: utf-8 -*-
"""Caminhos que funcionam tanto no venv quanto no .exe (PyInstaller).

No modo congelado:
- binários (ffmpeg/ffprobe) vêm em _internal/ junto ao exe;
- dados estáticos também ficam em _internal/;
- jobs/ fica ao lado do exe (área que o usuário vê e pode limpar).
"""
from __future__ import annotations

import shutil
import sys
from pathlib import Path


def frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def app_dir() -> Path:
    """Raiz da aplicação: pasta do exe (frozen) ou raiz do projeto (venv)."""
    if frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent.parent


def internal_dir() -> Path:
    """Onde o PyInstaller colocou binários e dados (só no modo frozen)."""
    if frozen():
        # _MEIPASS aponta para a pasta de dados (_internal no onedir)
        return Path(getattr(sys, "_MEIPASS", app_dir()))
    return Path(__file__).resolve().parent.parent


def static_dir() -> Path:
    """Pasta dos arquivos estáticos: junto do módulo (venv) ou _internal (exe)."""
    here = Path(__file__).resolve().parent / "static"
    if here.is_dir():
        return here
    return internal_dir() / "app" / "static"


def tool(name: str) -> str:
    """Resolve um executável externo: pasta interna → dir do exe → PATH."""
    candidates = [
        internal_dir() / f"{name}.exe",
        app_dir() / f"{name}.exe",
        app_dir() / "bin" / f"{name}.exe",
    ]
    for c in candidates:
        if c.is_file():
            return str(c)
    found = shutil.which(name)
    if found:
        return found
    return name  # deixa o erro claro no stderr do subprocesso
