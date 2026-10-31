@echo off
title Argos Clip - Testes de Integracao
cd /d "%~dp0"
echo Rodando os testes de integracao do Argos Clip...
.venv\Scripts\python.exe -m pytest tests -q --tb=short
pause
