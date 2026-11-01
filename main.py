# -*- coding: utf-8 -*-
"""Ponto de entrada do Argos Clip [CLIENTE LEGADO]: sobe o servidor e abre o navegador.

LEGADO: esta implementação em Python está congelada e não recebe mais features
nem correções. A linha ativa do projeto é a versão C# / .NET 8 em ``csharp/``.
"""
from __future__ import annotations

import threading
import time
import webbrowser

import uvicorn

HOST = "127.0.0.1"
PORT = 8484


def main() -> None:
    from app.server import app  # noqa: F811 — importa após variáveis de ambiente

    def _serve() -> None:
        uvicorn.run(app, host=HOST, port=PORT, log_level="warning")

    threading.Thread(target=_serve, daemon=True).start()

    # espera o servidor responder antes de abrir o navegador
    import urllib.request
    for _ in range(40):
        time.sleep(0.25)
        try:
            urllib.request.urlopen(f"http://{HOST}:{PORT}/", timeout=1)
            break
        except Exception:  # noqa: BLE001
            continue
    webbrowser.open(f"http://{HOST}:{PORT}")

    # mantém o processo vivo (threads do uvicorn são daemon)
    while True:
        time.sleep(3600)


if __name__ == "__main__":
    main()
