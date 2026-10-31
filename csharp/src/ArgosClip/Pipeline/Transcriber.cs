using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.Ggml;

namespace ArgosClip.Pipeline;

/// <summary>
/// Transcrição local (Whisper via whisper.cpp) com timestamps por palavra.
/// O modelo GGML é baixado no primeiro uso (como o faster-whisper faz).
/// </summary>
public static class Transcriber
{
    private static readonly object ModelLock = new();
    private static WhisperFactory? _model;
    private static string _loadedModelName = "";

    /// <summary>Nome do modelo (small/medium/large-v3). Ajustável por env.</summary>
    public static string ModelName =>
        Environment.GetEnvironmentVariable("ARGOS_WHISPER_MODEL")?.Trim() is { Length: > 0 } m
            ? m.ToLowerInvariant()
            : "small";

    public static WhisperFactory GetModel()
    {
        var name = ModelName;
        lock (ModelLock)
        {
            if (_model is not null && string.Equals(_loadedModelName, name, StringComparison.Ordinal))
            {
                return _model;
            }
            _model?.Dispose();
            _model = null;

            var path = GetOrDownloadModel(name).FullName;
            _model = WhisperFactory.FromPath(path);
            _loadedModelName = name;
            return _model;
        }
    }

    /// <summary>
    /// Garante o GGML do modelo no disco (pasta models/ do app) e devolve o caminho.
    /// Fonte: HuggingFace (ggerganov/whisper.cpp), igual ao download do faster-whisper.
    /// </summary>
    public static FileInfo GetOrDownloadModel(string name)
    {
        var valid = new[] { "tiny", "tiny.en", "base", "base.en", "small", "small.en", "medium", "medium.en", "large-v3", "large-v3-turbo", "large-v2", "large-v1", "large" };
        var modelName = valid.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : "small";
        var dir = Directory.CreateDirectory(AppPaths.ModelsDir);
        var file = Path.Combine(dir.FullName, $"ggml-{modelName}.bin");
        if (File.Exists(file) && new FileInfo(file).Length > 0)
        {
            return new FileInfo(file);
        }

        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelName}.bin";
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromHours(6),
        };

        var tmp = file + ".part";
        using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            resp.EnsureSuccessStatusCode();
            using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = File.Create(tmp);
            var buf = new byte[1024 * 1024];
            var total = resp.Content.Headers.ContentLength ?? -1;
            long copied = 0;
            var lastReport = Stopwatch.GetTimestamp();
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, read);
                copied += read;
                if (GetElapsedMilliseconds(lastReport) > 1000)
                {
                    lastReport = Stopwatch.GetTimestamp();
                    var mb = copied / 1048576.0;
                    if (total > 0)
                    {
                        Console.WriteLine($"baixando modelo Whisper… {mb:F0}/{total / 1048576.0:F0} MB");
                    }
                    else
                    {
                        Console.WriteLine($"baixando modelo Whisper… {mb:F0} MB");
                    }
                }
            }
        }
        File.Move(tmp, file, overwrite: true);
        return new FileInfo(file);
    }

    private static double GetElapsedMilliseconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Transcreve com timestamps por palavra; reporta progresso via callback.
    /// Devolve segmentos com palavras reconstruídas dos tokens do whisper.cpp.
    /// </summary>
    public static List<ArgosSegment> Transcribe(
        string wavPath, string? language, double duration, Action<double>? progressCb = null)
    {
        var model = GetModel();
        var builder = model.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language!)
            .WithTokenTimestamps()
            .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(2));

        // progresso: whisper.cpp reporta 0..100
        builder.WithProgressHandler(pct =>
        {
            if (pct >= 0)
            {
                progressCb?.Invoke(Math.Min(99.0, pct * 0.99));
            }
        });

        using var processor = builder.Build();

        var segments = new List<ArgosSegment>();
        using var fs = File.OpenRead(wavPath);
        foreach (var seg in processor.ProcessAsync(fs).ToBlockingEnumerable())
        {
            var words = TokensToWords(seg.Tokens);
            var text = seg.Text?.Trim() ?? "";
            if (words.Count == 0 && !string.IsNullOrWhiteSpace(text))
            {
                // fallback: distribui o texto igualmente no intervalo do segmento
                words = FallbackWords(text, seg.Start.TotalSeconds, seg.End.TotalSeconds);
            }
            var s0 = seg.Start.TotalSeconds;
            var s1 = seg.End.TotalSeconds;
            segments.Add(new ArgosSegment(s0, s1, text, words));
            if (progressCb is not null && duration > 0)
            {
                progressCb(Math.Min(99.0, s1 / duration * 100.0));
            }
        }
        return segments;
    }

    /// <summary>
    /// Reconstrói palavras a partir dos tokens do whisper.cpp (com UseTokenTimestamps,
    /// o t0/t1 do token já apontam o início/fim da palavra em unidades de 10 ms).
    /// </summary>
    private static List<ArgosWord> TokensToWords(WhisperToken[] tokens)
    {
        var words = new List<ArgosWord>();
        var cur = new System.Text.StringBuilder();
        double? curStart = null;
        double curEnd = 0;

        void Flush()
        {
            if (cur.Length > 0 && curStart is not null)
            {
                var text = cur.ToString().Trim();
                if (text.Length > 0)
                {
                    var st = curStart!.Value;
                    var en = Math.Max(curEnd, st + 0.08);
                    words.Add(new ArgosWord(st, en, text));
                }
            }
            cur.Clear();
            curStart = null;
        }

        foreach (var tok in tokens)
        {
            var raw = tok.Text;
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('<') || raw.StartsWith('['))
            {
                continue;
            }
            var text = raw.Replace("▁", " ");
            var leads = text.Length > 0 && char.IsWhiteSpace(text[0]);
            var body = text.Trim();
            if (leads || cur.Length == 0)
            {
                Flush();
                if (body.Length > 0)
                {
                    cur.Append(body);
                    curStart = tok.Start / 100.0;
                    curEnd = tok.End / 100.0;
                }
            }
            else
            {
                cur.Append(body);
                curEnd = tok.End / 100.0;
            }
        }
        Flush();

        // corrige sobreposições/ordem
        for (var i = 1; i < words.Count; i++)
        {
            if (words[i].Start < words[i - 1].End)
            {
                var moved = words[i];
                words[i] = moved with { Start = words[i - 1].End };
            }
        }
        return words;
    }

    private static List<ArgosWord> FallbackWords(string text, double start, double end)
    {
        // como não há timestamps por palavra, distribui o texto em palavras uniformes
        var toks = Regex.Split(text, @"\s+").Where(t => t.Length > 0).ToList();
        if (toks.Count == 0) return new List<ArgosWord>();
        var step = Math.Max(0.1, (end - start) / toks.Count);
        var list = new List<ArgosWord>();
        for (var i = 0; i < toks.Count; i++)
        {
            list.Add(new ArgosWord(start + i * step, start + (i + 1) * step, toks[i]));
        }
        return list;
    }
}
