using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArgosClip.Pipeline;

/// <summary>
/// Seleção dos melhores momentos por IA (Google Gemini). É OPCIONAL: só roda
/// quando existe uma chave configurada (ARGOS_LLM_KEY / ARGOS_GEMINI_KEY /
/// GEMINI_API_KEY). Se algo falhar, o pipeline cai na heurística local — nunca
/// quebra o job.
///
/// A IA recebe a transcrição fatiada em frases numeradas e devolve intervalos
/// (índices de frase) que viram clipes. A heurística ainda entra como "segunda
/// opinião": preenche as vagas restantes quando a IA devolve menos clipes.
/// </summary>
public static class LlmSelector
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public static string? ApiKey =>
        FirstEnv("ARGOS_LLM_KEY", "ARGOS_GEMINI_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY");

    public static string Model => FirstEnv("ARGOS_LLM_MODEL") ?? "gemini-2.0-flash";

    public static bool Available => ApiKey is { Length: > 0 };

    /// <summary>
    /// Devolve os clipes escolhidos pela IA (completados pela heurística) ou
    /// null se a IA não estiver disponível/falhar em tudo.
    /// </summary>
    public static List<Clip>? Select(
        IReadOnlyList<ArgosSentence> sentences,
        ClipOptions opt,
        IReadOnlyList<Clip> heuristicChunks,
        Action<string>? log = null)
    {
        if (!Available || sentences.Count == 0) return null;

        List<Clip>? picks;
        try
        {
            picks = AskGemini(sentences, opt, log);
        }
        catch (Exception e)
        {
            log?.Invoke("[LLM] falhou: " + e.Message);
            return null;
        }
        if (picks is null || picks.Count == 0) return null;

        // "segunda opinião": junta com a heurística sem sobrepor, respeitando N.
        var chosen = new List<Clip>();
        foreach (var c in picks.OrderByDescending(x => x.Score))
        {
            if (chosen.Count >= opt.NClips) break;
            if (chosen.Any(p => !(c.End <= p.Start || c.Start >= p.End))) continue;
            chosen.Add(c);
        }

        if (chosen.Count < opt.NClips)
        {
            foreach (var c in heuristicChunks
                         .Select(c => { Scoring.ScoreChunk(c, opt.MinLen, opt.MaxLen); return c; })
                         .OrderByDescending(x => x.Score))
            {
                if (chosen.Count >= opt.NClips) break;
                if (c.Score <= 0) continue;
                if (chosen.Any(p => !(c.End <= p.Start || c.Start >= p.End))) continue;
                chosen.Add(c);
            }
        }

        if (chosen.Count == 0) return null;
        return chosen.OrderBy(x => x.Start).ToList();
    }

    private static List<Clip>? AskGemini(
        IReadOnlyList<ArgosSentence> sentences, ClipOptions opt, Action<string>? log)
    {
        var prompt = BuildPrompt(sentences, opt);
        var body = BuildBody(prompt);
        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent" +
            $"?key={Uri.EscapeDataString(ApiKey!)}";

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = Http.PostAsync(url, content).GetAwaiter().GetResult();
        var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!resp.IsSuccessStatusCode)
        {
            log?.Invoke($"[LLM] HTTP {(int)resp.StatusCode}: {Truncate(json, 200)}");
            return null;
        }

        var text = ExtractText(json);
        if (string.IsNullOrWhiteSpace(text))
        {
            log?.Invoke("[LLM] resposta vazia");
            return null;
        }

        var picks = ParsePicks(text);
        if (picks.Count == 0)
        {
            log?.Invoke("[LLM] nenhum intervalo válido na resposta");
            return null;
        }

        var clips = new List<Clip>();
        foreach (var (from, rawTo, note) in picks)
        {
            var to = rawTo;
            var clip = Scoring.ClipFromRange(sentences, from, to, note);
            if (clip is null) continue;
            // respeita o teto de duração: encurta pelo fim, frase a frase
            while (clip.End - clip.Start > opt.MaxLen && to > from)
            {
                to--;
                var shorter = Scoring.ClipFromRange(sentences, from, to, note);
                if (shorter is null) break;
                clip = shorter;
            }
            if (clip.End - clip.Start < opt.MinLen * 0.5) continue;
            clips.Add(clip);
        }

        Scoring.ApplyPadding(clips);
        return clips;
    }

    private static string BuildPrompt(IReadOnlyList<ArgosSentence> sentences, ClipOptions opt)
    {
        var sb = new StringBuilder(sentences.Count * 40);
        sb.AppendLine("Você é um editor de cortes virais para TikTok/Reels/Shorts.");
        sb.AppendLine($"Escolha os {opt.NClips} melhores MOMENTOS da transcrição abaixo.");
        sb.AppendLine("Regras:");
        sb.AppendLine("- Cada momento é um intervalo CONTÍNUO de frases.");
        sb.AppendLine($"- Duração entre {opt.MinLen:0} e {opt.MaxLen:0} segundos.");
        sb.AppendLine("- Precisa ter começo, meio e fim (frase completa), sem depender de contexto antes.");
        sb.AppendLine("- Priorize: gancho forte no início, história/explicação fechada, números, polêmica, emoção.");
        sb.AppendLine("- Não repita o mesmo trecho. Intervalos não podem se sobrepor.");
        sb.AppendLine("Responda SOMENTE com JSON no formato:");
        sb.AppendLine("{\"clips\":[{\"start_sentence\":int,\"end_sentence\":int,\"titulo\":string,\"motivo\":string}]}");
        sb.AppendLine();
        sb.AppendLine("TRANSCRIÇÃO (índice | tempo | texto):");
        foreach (var s in sentences)
        {
            sb.Append(s.Index).Append(" | ")
              .Append(Scoring.FmtTime(s.Start)).Append('-').Append(Scoring.FmtTime(s.End))
              .Append(" | ").AppendLine(s.Text);
        }
        return sb.ToString();
    }

    private static string BuildBody(string prompt)
    {
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["clips"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["start_sentence"] = new JsonObject { ["type"] = "INTEGER" },
                            ["end_sentence"] = new JsonObject { ["type"] = "INTEGER" },
                            ["titulo"] = new JsonObject { ["type"] = "STRING" },
                            ["motivo"] = new JsonObject { ["type"] = "STRING" },
                        },
                        ["required"] = new JsonArray { "start_sentence", "end_sentence" },
                    },
                },
            },
            ["required"] = new JsonArray { "clips" },
        };

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } },
                },
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.4,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema,
            },
        };
        return body.ToJsonString();
    }

    private static string? ExtractText(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var parts = root?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
            if (parts is null) return null;
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                var t = p?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(t)) sb.Append(t);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<(int From, int To, string Note)> ParsePicks(string text)
    {
        var result = new List<(int, int, string)>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(ExtractJson(text));
        }
        catch
        {
            return result;
        }
        if (root is null) return result;

        var arr = root is JsonArray a ? a : root["clips"]?.AsArray();
        if (arr is null) return result;

        foreach (var item in arr)
        {
            if (item is null) continue;
            var from = (int?)item["start_sentence"] ?? (int?)item["start"] ?? -1;
            var to = (int?)item["end_sentence"] ?? (int?)item["end"] ?? -1;
            if (from < 0 || to < 0) continue;
            var note = item["titulo"]?.GetValue<string>() ?? item["motivo"]?.GetValue<string>(); 
            result.Add((from, to, note ?? "momento escolhido"));
        }
        return result;
    }

    /// <summary>Isola o primeiro bloco JSON da resposta (o modelo às vezes adiciona texto).</summary>
    private static string ExtractJson(string text)
    {
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return text;
        var open = text[start];
        var close = open == '{' ? '}' : ']';
        var end = text.LastIndexOf(close);
        return end > start ? text[start..(end + 1)] : text[start..];
    }

    private static string? FirstEnv(params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Environment.GetEnvironmentVariable(k)?.Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
