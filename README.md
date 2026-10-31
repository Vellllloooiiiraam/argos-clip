# Argos Clip

Ferramenta de clipes com IA que roda **100% local e gratuita** na sua máquina: transforma vídeos longos em clipes verticais com legenda.

- **Entrada:** upload de vídeo **ou link** (YouTube, Twitch, Vimeo, X, Reddit, TikTok… via yt-dlp)
- **Transcrição:** Whisper via `faster-whisper` (CPU, int8, modelo `small`)
- **Seleção de momentos:** heurística de scoring (ganchos, números, perguntas, densidade de fala)
- **Reframe por rosto:** OpenCV rastreia o falante e o crop 9:16 segue o rosto (fallback: crop central)
- **B-Roll automático (opcional):** busca clipe stock no Pexels/Pixabay e sobrepõe no meio do clipe
- **Clipes:** FFmpeg recorta para 9:16 (1080×1920) e queima legenda estilo karaoke
- **Interface:** web local com upload/link, progresso e galeria dos clipes

## Requisitos

- Python 3.11 (usado pelo venv `.venv`)
- FFmpeg/ffprobe no PATH
- ~500 MB de RAM livres para o modelo Whisper (baixado no primeiro uso)

## Uso

### Opção 1 — exe (recomendado para quem só quer usar)

1. Baixe o `argos-clip-win64.zip` em [Releases](../../releases) e descompacte
2. Dê duplo clique em `argos-clip.exe` — abre http://127.0.0.1:8484 no navegador
3. Arraste um vídeo ou cole um link; o resto é igual

Não precisa de Python nem FFmpeg — tudo já vem dentro. Modelo Whisper (~500 MB) é baixado no primeiro uso (precisa de internet uma vez).

### Opção 2 — fonte (para desenvolver)

1. Python 3.11 + FFmpeg no PATH
2. `python -m venv .venv` e instale as dependências (`pip install -r requirements.txt`)
3. Dê duplo clique em `iniciar.bat` (abre http://127.0.0.1:8484)
4. Arraste um vídeo longo (podcast, aula, live…) **ou cole um link** de plataforma suportada
5. Escolha quantos clipes e o idioma (ou auto)
6. Aguarde — em CPU, ~1 min de áudio leva de 20–60 s para transcrever
7. Baixe os clipes gerados (9:16, com legenda queimada e nota de "viralidade")

## Gerar o exe

```
build.bat
```

Gera `dist\argos-clip\argos-clip.exe` (pasta de ~800 MB com FFmpeg embutido) — compacte a pasta para distribuir.

## Testes

```
testar.bat
```

Roda a suíte de integração (rotas reais, pasta de jobs própria de teste). Regra: teste não se apaga nem se afrouxa.

## B-Roll (opcional)

Crie chaves gratuitas em [Pexels API](https://www.pexels.com/api/) e/ou [Pixabay API](https://pixabay.com/api/docs/) e exporte antes de iniciar:

```
set PEXELS_API_KEY=sua_chave
set PIXABAY_API_KEY=sua_chave
```

Sem chave (ou para desligar), o pipeline segue normalmente sem overlay. Nunca hardcoded — variáveis de ambiente only.

## Ajustes

- Modelo maior = melhor transcrição, mais lento: edite `app/pipeline.py` → `get_model("small")` para `"medium"` ou `"large-v3"`.
- Tamanho dos clipes: `build_chunks(segments, min_len=18, max_len=55)` em `app/pipeline.py`.
- Porta: `--port 8484` no `iniciar.bat`.
- Jobs simultâneos: `ARGOS_MAX_ACTIVE_JOBS` (padrão 2; os demais ficam na fila).
- Retenção de jobs: `ARGOS_JOB_TTL_HOURS` (padrão 24 h; jobs terminados antigos são apagados).
- Desligar B-Roll: `ARGOS_BROLL=0`.

## Segurança

- Somente escuta em `127.0.0.1` (não exposto na rede)
- Links validados contra lista branca de plataformas (anti-SSRF); `file://`, `ftp://` e IPs internos recusados
- Download limitado (8 GB, 2 h de vídeo, 30 min de relógio real; playlists recusadas)
- `/media` protegido contra path traversal; cabeçalhos `nosniff`, `X-Frame-Options`, `Referrer-Policy`

## Limpeza

Os vídeos processados ficam em `jobs/<id>/` e são apagados automaticamente após 24 h (ou o valor de `ARGOS_JOB_TTL_HOURS`). Apague a pasta para liberar espaço na hora.
