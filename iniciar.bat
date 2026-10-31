@echo off
title Argos Clip
cd /d "%~dp0"
echo Iniciando Argos Clip em http://127.0.0.1:8484 ...
start "" http://127.0.0.1:8484
.venv\Scripts\python.exe -m uvicorn app.server:app --host 127.0.0.1 --port 8484
pause
