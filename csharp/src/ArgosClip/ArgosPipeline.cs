using ArgosClip.Pipeline;

namespace ArgosClip;

/// <summary>
/// Orquestra o pipeline de um job: probe → áudio → transcrição → seleção →
/// B-roll → render. Porta fiel do server._process do original.
/// </summary>
public sealed class ArgosPipeline
{
    private readonly JobStore _store;

    public static int MaxActiveJobs { get; } =
        Math.Max(1, ParseInt(Environment.GetEnvironmentVariable("ARGOS_MAX_ACTIVE_JOBS"), 2));

    public static double JobTtlHours { get; } =
        ParseDouble(Environment.GetEnvironmentVariable("ARGOS_JOB_TTL_HOURS"), 24);

    public ArgosPipeline(JobStore store) => _store = store;

    private void Set(string jobId, params (string, object?)[] kv)
    {
        _store.Get(jobId)?.Set(kv);
    }

    public void Process(string jobId, FileInfo src, ClipOptions opt, string? language)
    {
        try
        {
            var jobDir = new DirectoryInfo(Path.Combine(AppPaths.JobsDir, jobId));
            jobDir.Create();

            Set(jobId, ("status", "probe"), ("stage", "Analisando vídeo..."));
            var info = Ffmpeg.Probe(src);
            if (!info.HasAudio)
            {
                throw new InvalidOperationException("O vídeo não tem faixa de áudio — não dá para transcrever.");
            }
            Set(jobId, ("duration", info.Duration), ("width", info.Width), ("height", info.Height));

            if (info.Duration > Downloader.MaxVideoDurationS)
            {
                throw new InvalidOperationException(
                    $"Vídeo muito longo ({info.Duration / 60:F0} min) — o limite é {Downloader.MaxVideoDurationS / 60:F0} min.");
            }

            Set(jobId, ("status", "audio"), ("stage", "Extraindo áudio..."));
            var wav = Ffmpeg.ExtractAudio(jobDir, src);

            Set(jobId,
                ("status", "transcribe"),
                ("stage", $"Transcrevendo com Whisper '{opt.Model}' (local)..."),
                ("progress", 0.0));

            var segments = Transcriber.Transcribe(
                wav, language, info.Duration,
                p => Set(jobId, ("progress", Math.Round(p, 1))),
                opt.Model);
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("Nenhuma fala detectada no áudio.");
            }
            Set(jobId, ("transcript_segments", segments.Count), ("stage", "Escolhendo os melhores momentos..."));

            // A: frases limpas → candidatos agrupados em [minLen, maxLen]
            var sentences = Scoring.SplitSentences(segments, opt.MaxLen);
            var chunks = Scoring.GroupSentences(sentences, opt.MinLen, opt.MaxLen);

            // C: seleção por IA (Gemini) quando habilitada; senão heurística pura.
            List<Clip> chosen;
            string selectionMode;
            var llmPicks = opt.UseLlm
                ? LlmSelector.Select(sentences, opt, chunks, msg => Console.WriteLine(msg))
                : null;
            if (llmPicks is { Count: > 0 })
            {
                chosen = llmPicks;
                selectionMode = "IA (Gemini)";
            }
            else
            {
                chosen = Scoring.SelectClips(chunks, opt.NClips, opt.MinLen, opt.MaxLen);
                selectionMode = opt.UseLlm ? "heurística (IA indisponível)" : "heurística";
            }
            if (chosen.Count == 0)
            {
                throw new InvalidOperationException("Não foi possível montar clipes a partir da transcrição.");
            }
            Set(jobId, ("selection", selectionMode));

            var maxScore = chosen.Max(c => c.Score);

            // B-roll: busca uma vez por job (melhor esforço — sem chave ou falha
            // de rede, os clipes seguem sem overlay)
            var fullText = string.Join(" ", chosen.Select(c => c.Text));
            string? brollPath = null;
            if (Environment.GetEnvironmentVariable("ARGOS_BROLL") != "0")
            {
                Set(jobId, ("stage", "Buscando B-roll (Pexels/Pixabay)..."));
                try
                {
                    brollPath = Broll.FetchBroll(fullText, jobDir.FullName);
                }
                catch
                {
                    brollPath = null;
                }
            }

            var clipsMeta = new List<Dictionary<string, object?>>();
            for (var i = 0; i < chosen.Count; i++)
            {
                var clip = chosen[i];
                var idx = i + 1;
                Set(jobId,
                    ("status", "render"),
                    ("stage", $"Renderizando clipe {idx}/{chosen.Count}..."),
                    ("progress", Math.Round((idx - 1) / (double)chosen.Count * 100, 1)));

                var (video, thumb, brollUsed) = Renderer.RenderClip(jobDir.FullName, src, clip, idx, brollPath);

                var text = clip.Text.Length > 220 ? clip.Text[..220] + "..." : clip.Text;
                clipsMeta.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["video"] = Path.GetFileName(video),
                    ["thumb"] = File.Exists(thumb) ? Path.GetFileName(thumb) : null,
                    ["start"] = Scoring.FmtTime(clip.Start),
                    ["end"] = Scoring.FmtTime(clip.End),
                    ["duration"] = Math.Round(clip.End - clip.Start, 1),
                    ["score"] = Scoring.ClipScore100(clip.Score, maxScore),
                    ["reasons"] = clip.Reasons,
                    ["text"] = text,
                    ["words"] = clip.Words.Count,
                    ["broll"] = brollUsed,
                });
            }

            try
            {
                File.Delete(wav);
            }
            catch (IOException) { }

            Set(jobId, ("status", "done"), ("stage", "Concluído!"), ("progress", 100.0), ("clips", clipsMeta));
        }
        catch (Exception e)
        {
            Set(jobId, ("status", "error"), ("stage", "Erro"), ("error", e.Message));
        }
    }

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) ? v : fallback;

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
