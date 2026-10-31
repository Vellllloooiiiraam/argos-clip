# -*- mode: python ; coding: utf-8 -*-
"""Spec do PyInstaller — Argos Clip (onedir: pasta com argos-clip.exe).

Build: .venv\\Scripts\\pyinstaller.exe --noconfirm argos.spec
Ou rode build.bat.
"""
import shutil
import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_all, collect_data_files, collect_submodules

ROOT = Path(SPECPATH)

# --- ffmpeg/ffprobe do sistema entram no pacote -----------------------------
binaries = []
for tool in ("ffmpeg", "ffprobe"):
    src = shutil.which(tool)
    if not src:
        sys.exit(f"ERRO: {tool} nao encontrado no PATH. Instale o FFmpeg antes de buildar.")
    binaries.append((src, "."))

# --- coleta de pacotes com dados/submódulos dinâmicos -----------------------
datas, hiddenimports = [], []
for pkg in ("faster_whisper", "ctranslate2", "tokenizers", "huggingface_hub", "yt_dlp"):
    try:
        d, b, h = collect_all(pkg)
        datas += d
        hiddenimports += h
    except Exception:  # noqa: BLE001 — pacote opcional/dinâmico
        pass

# cascata do OpenCV (reframe por rosto)
try:
    import cv2.data
    cascade = Path(cv2.data.haarcascades) / "haarcascade_frontalface_default.xml"
    if cascade.is_file():
        datas.append((str(cascade), "cv2/data"))
except Exception:  # noqa: BLE001
    pass

datas += collect_data_files("uvicorn")
hiddenimports += collect_submodules("uvicorn.logging")
hiddenimports += collect_submodules("uvicorn.loops")
hiddenimports += collect_submodules("uvicorn.protocols")
hiddenimports += collect_submodules("uvicorn.supervisors")

# arquivos estáticos da UI
datas.append((str(ROOT / "app" / "static"), "app/static"))

a = Analysis(
    [str(ROOT / "main.py")],
    pathex=[str(ROOT)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=["tkinter", "matplotlib", "PIL", "numpy.f2py"],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="argos-clip",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,          # console fica para ver erros; a UI abre no navegador
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="argos-clip",
)
