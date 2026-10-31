# Argos Clip — versão Windows (C# / .NET 8)

Porte do **argos-clip** (Python) para um app Windows nativo em **C# / .NET 8**.
Pega **1 vídeo longo** e gera **N clipes verticais 9:16** com **legenda karaoke
queimada** — tudo **100% local** (Whisper + FFmpeg), sem enviar nada para a nuvem.

> Esta versão é um app único (servidor local + interface web). Ele sobe em
> `http://127.0.0.1:8484` e abre o navegador sozinho.

---

## Requisitos

| Item | Necessário para | Observação |
|---|---|---|
| **Windows 10/11 (x64)** | rodar | — |
| **FFmpeg + FFprobe** | rodar | precisam estar no `PATH` **ou** em `src\ArgosClip\tools\` |
| **.NET SDK 8** | apenas para **compilar** (`build.bat`) | https://dotnet.microsoft.com/download |
| .NET Runtime | — | **não** é preciso: o `build.bat` gera um `.exe` self-contained |

O **Whisper** roda embutido (`Whisper.net` / whisper.cpp), em CPU. O modelo
GGML é baixado **na primeira execução** de `https://huggingface.co/ggerganov/whisper.cpp`.

---

## Como usar

### 1. Compilar (uma vez)

```bat
build.bat
```

Gera `dist\argos-clip.exe` (self-contained — não precisa de .NET instalado).

### 2. Rodar

```bat
iniciar.bat
```

Abre o app e o navegador em `http://127.0.0.1:8484`. Feche a janela (ou `Ctrl+C`) para sair.

> Sem o `dist\argos-clip.exe`, o `iniciar.bat` cai no modo dev (`dotnet run`).

### 3. Usar

1. **Arraste** um vídeo (MP4/MOV/MKV/WEBM/AVI) **ou cole um link** (YouTube, Twitch, Vimeo, X, TikTok…).
2. Escolha a quantidade de clipes (3/5/7/10) e o idioma (ou auto-detectar).
3. Aguarde: transcrição → escolha dos melhores momentos → render 9:16 com legenda.
4. Baixe os clipes na própria tela.

### 4. Testar sem vídeo

```bat
testar.bat
```

Gera um vídeo falado com a voz do Windows, sobe o app numa porta de teste, envia
o vídeo e confere se os clipes foram gerados (smoke test de ponta a ponta).

---

## Como funciona (pipeline)

```
upload/link ──► ffprobe (duração/áudio)
             ──► ffmpeg extrai áudio 16 kHz mono WAV
             ──► Whisper (local) transcreve COM timestamps por palavra
             ──► Scoring: agrupa trechos e escolhe os N melhores
                 (ganchos, números, perguntas, densidade de fala)
             ──► B-roll opcional (Pexels/Pixabay) — só se houver chave
             ──► Reframe 9:16: rastreia o rosto (OpenCV) e corta seguindo
             ──► ffmpeg: crop + scale 1080x1920 + legenda ASS karaoke + thumb
             ──► clipes prontos (mp4 + jpg) servidos em /media
```

**Diferenças de stack em relação ao original (Python):**

| Original | Porte C# |
|---|---|
| FastAPI + uvicorn | ASP.NET Core Minimal API (Kestrel) |
| faster-whisper | Whisper.net 1.9.1 (whisper.cpp, CPU) |
| yt-dlp (lib) | `yt-dlp.exe` chamado por processo |
| opencv-python | OpenCvSharp4 |
| httpx | HttpClient |
| PyInstaller | `dotnet publish` self-contained |

Rotas e variáveis de ambiente são as mesmas do original.

---

## Variáveis de ambiente (todas opcionais)

| Variável | Padrão | Para que serve |
|---|---|---|
| `ARGOS_WHISPER_MODEL` | `small` | `tiny` \| `base` \| `small` \| `medium` \| `large-v3` |
| `ARGOS_PORT` | `8484` | porta do app. **Sem ela, o app usa a 8484 e, se estiver ocupada, escolhe a próxima livre sozinho** |
| `ARGOS_MAX_ACTIVE_JOBS` | `2` | jobs processando ao mesmo tempo |
| `ARGOS_JOB_TTL_HOURS` | `24` | horas até apagar jobs concluídos |
| `ARGOS_JOBS_DIR` | `jobs\` | pasta de trabalho dos jobs |
| `ARGOS_MODELS_DIR` | `models\` | pasta dos modelos GGML |
| `ARGOS_BROLL` | ligado | `0` desliga o B-roll |
| `PEXELS_API_KEY` | — | B-roll grátis do Pexels |
| `PIXABAY_API_KEY` | — | B-roll grátis do Pixabay |

Exemplo: rodar mais rápido num vídeo curto

```bat
set ARGOS_WHISPER_MODEL=base
iniciar.bat
```

---

## Gerar o instalador (para distribuir)

```bat
winget install JRSoftware.InnoSetup   :: uma vez só
build-installer.bat
```

Gera `installer\out\ArgosClip-Setup-1.1.0.exe` (~194 MB). O instalador:

- instala **por usuário** (não pede admin) em `%LocalAppData%\Programs\Argos Clip`;
- cria atalhos no Menu Iniciar (e Desktop, opcional);
- inclui o **FFmpeg** dentro de `tools\` (não depende do PC da outra pessoa);
- traz desinstalador; o app é self-contained (não exige .NET instalado).

> A primeira execução ainda baixa o modelo do Whisper (~466 MB com `small`).

O pacote zera o FFmpeg full junto; se quiser um instalador menor, troque
`dist\tools\ffmpeg.exe` por um build *essentials* do FFmpeg.

---

## Estrutura

```
argos-clip-csharp\
├─ build.bat / iniciar.bat / testar.bat
├─ build-installer.bat          # gera o setup.exe (Inno Setup)
├─ installer\
│  ├─ argos-clip.iss            # script do instalador
│  └─ out\ArgosClip-Setup-*.exe # instalador pronto
├─ dist\                        # app publicado (self-contained)
├─ scripts\
│  ├─ make-test-video.ps1      # gera vídeo de teste com fala
│  └─ run-smoke-test.ps1       # teste de ponta a ponta
└─ src\ArgosClip\
   ├─ Program.cs               # Kestrel + cabeçalhos + limpeza + abrir navegador
   ├─ Endpoints.cs             # /api/upload, /api/link, /api/status, /media
   ├─ Jobs.cs                  # estado + fila de jobs
   ├─ ArgosPipeline.cs         # orquestra o pipeline
   ├─ Downloader.cs            # yt-dlp + anti-SSRF
   ├─ AppPaths.cs              # caminhos (dev e publicado)
   ├─ wwwroot\index.html       # interface (a mesma do original)
   ├─ tools\                   # yt-dlp.exe + cascata Haar
   └─ Pipeline\
      ├─ Ffmpeg.cs  Transcriber.cs  Scoring.cs
      ├─ Karaoke.cs  Reframe.cs  Renderer.cs  Broll.cs  Models.cs
```

---

## Problemas comuns

- **“ffmpeg não encontrado”** → instale o FFmpeg e coloque no `PATH`, ou copie
  `ffmpeg.exe`/`ffprobe.exe` para `src\ArgosClip\tools\` (e para `dist\tools\` após o build).
- **Porta ocupada** → o app já resolve sozinho: se a 8484 estiver em uso, ele usa
  a próxima livre e abre o navegador nela. Para forçar uma porta específica:
  `set ARGOS_PORT=8600` antes do `iniciar.bat`.
- **Primeira execução demora** → está baixando o modelo Whisper (algumas centenas de MB).
- **Legenda some / sem fala** → verifique se o vídeo tem faixa de áudio e se o
  idioma está certo (ou deixe em auto-detectar).
