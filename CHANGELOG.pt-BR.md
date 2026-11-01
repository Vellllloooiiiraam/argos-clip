# Changelog

Todas as mudanças relevantes do Argos Clip.

A **linha ativa é o app em C# / .NET 8** ([`csharp/`](csharp/)). A versão Python na
raiz do repositório é o **cliente legado** e está congelada.

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e
versionamento seguindo [Semantic Versioning](https://semver.org/).

[English](CHANGELOG.md) · **Português**

## [1.2.3] — 2026-11-01

### Adicionado
- **Janela nativa do app.** A interface agora abre na **própria janela** (Edge
  WebView2) em vez de uma aba do navegador — sem barra de endereço, barra de título
  escura, e lembra tamanho e posição.
- Diálogo nativo de **“Salvar clipe”** (escolher onde salvar, como em qualquer app).
- `ARGOS_HEADLESS=1` roda só o servidor, sem janela (usado pelo teste de ponta a ponta).
- O instalador agora instala a runtime do **WebView2** automaticamente quando falta.

### Alterado
- O app passou a ser um **executável de janela** (sem janela de console).
- Links externos abrem no navegador padrão; a navegação fica travada na interface local.
- Erros de startup vão para `%APPDATA%\ArgosClip\logs\argos.log` e aparecem numa caixa
  de diálogo (não existe mais console).

## [1.2.2] — 2026-11-01

### Adicionado
- **Configuração da IA dentro do app.** O botão **“⚙️ Configurar IA (grátis)”** abre
  um painel para escolher o provedor (Gemini, Groq, OpenAI, OpenRouter ou Ollama
  local), colar a chave e testá-la — sem terminal e sem variáveis de ambiente.
- **“Salvar e testar”** chama o provedor e diz se a chave funciona (chave inválida,
  cota esgotada, modelo/URL errados etc.).

### Alterado
- A chave é guardada **criptografada com DPAPI do Windows** (por usuário) em
  `%APPDATA%\ArgosClip\settings.json` e nunca é devolvida à interface.
- As variáveis de ambiente continuam funcionando como **override avançado**, mas
  deixam de ser necessárias para usar a IA.
- Quando a IA é configurada pela interface, chaves soltas de outros serviços no
  ambiente são ignoradas (evita usar a chave de um provedor em outro).

### Atualizando de 1.2.1
- Instale o `ArgosClip-Setup-1.2.2.exe` **por cima** da versão anterior (por
  usuário, sem admin; seus `jobs\` e `models\` são preservados).
- Para ligar a IA não precisa mais do terminal: use o botão **“⚙️ Configurar IA”**
  no app. As variáveis de ambiente continuam valendo e têm prioridade sobre a
  config salva.

## [1.2.1] — 2026-11-01

### Adicionado
- **Seleção por IA multi-provedor.** Além do **Gemini** nativo, qualquer **endpoint
  compatível com OpenAI**: Groq, OpenAI, OpenRouter, Together, **Ollama local**, etc.
  Configure com `ARGOS_LLM_PROVIDER`, `ARGOS_LLM_MODEL` e `ARGOS_LLM_BASE_URL`.
- O status do job agora mostra o provedor usado (ex.: `IA (groq)`).

### Alterado
- O provedor só é ligado com configuração **explícita**; chaves de outros serviços
  presentes no ambiente não ativam mais a IA sozinhas.
- A saída da IA é limitada (`maxOutputTokens`) com schema mais enxuto, evitando
  respostas gigantes/lentas.

### Corrigido
- As chamadas de IA não travam mais em respostas muito grandes do modelo.
- O texto vindo da IA (títulos) é sanitizado (corta no fim da frase, máx. 8 palavras)
  contra prompt-injection.
- O app não quebra mais no startup quando iniciado com o diretório atual diferente da
  pasta de instalação (o content root agora é fixado na pasta do executável).
- `401/402/403` do provedor caem na heurística imediatamente; `429/5xx` são repetidos.

## [1.2.0] — 2026-11-01

### Adicionado
- **Cortes em fronteiras de frase:** os segmentos do Whisper são agrupados em frases
  inteiras, então os clipes nunca começam ou terminam no meio de uma ideia.
- **Modelo do Whisper configurável** na interface (`tiny`/`base`/`small`/`medium`/`large-v3-turbo`).
- **Seleção por IA opcional** via Google Gemini (`ARGOS_LLM_KEY`), com a heurística
  como fallback e segunda opinião.
- Duração de clipe configurável (`ARGOS_MIN_CLIP_S` / `ARGOS_MAX_CLIP_S`) e `/api/config`.

### Alterado
- Scoring dos clipes melhorado (gancho de abertura mais forte, bônus de frase
  completa, densidade de fala).

## [1.1.0] — 2026-10-31

### Adicionado
- App Windows nativo em **C# / .NET 8** (executável self-contained + instalador) junto
  da versão Python original.
- Instalador por usuário, com FFmpeg embutido e atalhos no Menu Iniciar.

## [1.0.0] — 2026-10-31

### Adicionado
- Primeira versão: pipeline local de vídeo para clipes verticais (transcrição pelo
  Whisper, seleção ciente de frases, reframe 9:16 por rosto, legenda karaoke queimada,
  B-roll opcional), com interface web local.

[1.2.3]: ../../releases/tag/v1.2.3
[1.2.2]: ../../releases/tag/v1.2.2
[1.2.1]: ../../releases/tag/v1.2.1
[1.2.0]: ../../releases/tag/v1.2
[1.1.0]: ../../releases/tag/v1.1
[1.0.0]: ../../releases/tag/v1.0.0
