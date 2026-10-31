using System.Text.RegularExpressions;

namespace ArgosClip.Pipeline;

/// <summary>
/// Seleção de momentos — heurística de "viralidade" (ganchos, números,
/// perguntas, densidade de fala). Porta fiel do original.
/// </summary>
public static class Scoring
{
    private static readonly string[] HookPt =
    [
        "nunca", "sempre", "segredo", "erro", "erros", "dinheiro", "grátis", "gratuito",
        "importante", "atenção", "cuidado", "como", "por que", "porquê", "motivo",
        "dica", "dicas", "método", "forma", "jeito", "resultado", "resultados",
        "prova", "comprova", "comprovei", "testei", "descobri", "ninguém", "todo mundo",
        "simples", "fácil", "rápido", "estratégia", "verdade", "mentira", "funciona",
        "aumentar", "ganhar", "perdeu", "evite", "só", "apenas", "agora", "hoje",
        "o problema", "a realidade", "você precisa", "precisa saber", "mudou tudo",
    ];

    private static readonly string[] HookEn =
    [
        "never", "always", "secret", "mistake", "money", "free", "important",
        "attention", "how", "why", "reason", "tip", "trick", "result", "proof",
        "tested", "discovered", "nobody", "everyone", "simple", "easy", "fast",
        "strategy", "true", "lie", "works", "increase", "earn", "stop", "avoid",
        "you need", "changed everything",
    ];

    private static readonly Regex ReNumber =
        new(@"\d|%|r\$|us\$|mil|milh", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Agrupa segmentos consecutivos em janelas de duração de clipe.</summary>
    public static List<Clip> BuildChunks(IReadOnlyList<ArgosSegment> segments, double minLen = 18.0, double maxLen = 55.0)
    {
        var chunks = new List<Clip>();
        var cur = new List<ArgosSegment>();

        void Flush()
        {
            if (cur.Count == 0) return;
            var start = cur[0].Start;
            var end = cur[^1].End;
            var words = new List<ArgosWord>();
            foreach (var s in cur)
            {
                words.AddRange(s.WordList);
            }
            var text = string.Join(" ", cur.Select(s => s.Text)).Trim();
            if (end - start >= 5.0)
            {
                chunks.Add(new Clip(start, end, text, words));
            }
            cur.Clear();
        }

        foreach (var s in segments)
        {
            cur.Add(s);
            var span = cur[^1].End - cur[0].Start;
            if (span >= maxLen)
            {
                Flush();
            }
            else if (span >= minLen && s.Text.Length > 0 && EndsSentence(s.Text))
            {
                Flush();
            }
        }
        Flush();
        return chunks;
    }

    private static bool EndsSentence(string t)
    {
        var last = t.TrimEnd();
        return last.EndsWith(".", StringComparison.Ordinal)
            || last.EndsWith("!", StringComparison.Ordinal)
            || last.EndsWith("?", StringComparison.Ordinal)
            || last.EndsWith("...", StringComparison.Ordinal);
    }

    /// <summary>Pontua o clipe e registra os motivos (para exibir na UI).</summary>
    public static void ScoreChunk(Clip clip)
    {
        var textL = clip.Text.ToLowerInvariant();
        var wordsPerSec = clip.Words.Count / Math.Max(0.1, clip.End - clip.Start);
        double score = 0;
        var reasons = new List<string>();

        var hits = HookPt.Concat(HookEn).Where(k => textL.Contains(k, StringComparison.Ordinal)).ToList();
        if (hits.Count > 0)
        {
            var uniq = hits.Distinct().Take(5).OrderBy(k => textL.IndexOf(k, StringComparison.Ordinal)).ToList();
            score += Math.Min(6.0, 1.5 * uniq.Count);
            reasons.Add("ganchos: " + string.Join(", ", uniq));
        }

        if (ReNumber.IsMatch(clip.Text))
        {
            score += 3.0;
            reasons.Add("números/dados");
        }

        if (clip.Text.Contains("?", StringComparison.Ordinal))
        {
            score += 2.5;
            reasons.Add("pergunta");
        }
        else if (clip.Text.Contains("!", StringComparison.Ordinal))
        {
            score += 1.0;
        }

        var firstText = string.Join(" ", clip.Words.Take(8).Select(w => w.Text)).ToLowerInvariant();
        if (HookPt.Concat(HookEn).Any(k => firstText.Contains(k, StringComparison.Ordinal)) || ReNumber.IsMatch(firstText))
        {
            score += 4.0;
            reasons.Add("gancho nos 1ºs segundos");
        }

        // densidade de fala (penaliza silêncios longos)
        var gaps = new List<double>();
        for (var i = 1; i < clip.Words.Count; i++)
        {
            gaps.Add(clip.Words[i].Start - clip.Words[i - 1].End);
        }
        var longGaps = gaps.Where(g => g > 1.2).ToList();
        if (wordsPerSec >= 2.2)
        {
            score += 1.5;
            reasons.Add("fala densa");
        }
        else if (wordsPerSec < 1.2)
        {
            score -= 1.5;
        }
        if (longGaps.Count > 0)
        {
            score -= Math.Min(2.0, 0.5 * longGaps.Count);
        }

        if (clip.End - clip.Start <= 45)
        {
            score += 0.8;
        }

        clip.Score = Math.Max(0.0, score);
        clip.Reasons = reasons;
    }

    /// <summary>Escolhe os N melhores chunks sem sobreposição temporal.</summary>
    public static List<Clip> SelectClips(IReadOnlyList<Clip> chunks, int n = 5)
    {
        foreach (var c in chunks)
        {
            ScoreChunk(c);
        }
        var picked = new List<Clip>();
        foreach (var c in chunks.OrderByDescending(x => x.Score).ThenByDescending(x => x.End - x.Start))
        {
            if (picked.Count >= n) break;
            if (c.Score <= 0 && picked.Count > 0) continue;
            if (picked.Any(p => !(c.End <= p.Start || c.Start >= p.End))) continue;
            picked.Add(c);
        }
        return picked.OrderBy(x => x.Start).ToList();
    }

    /// <summary>Normaliza a nota bruta para 0–100 de exibição.</summary>
    public static int ClipScore100(double score, double maxScore)
    {
        if (maxScore <= 0) return 50;
        return (int)Math.Min(99, Math.Max(1, Math.Round(50 + 50 * score / maxScore)));
    }

    /// <summary>Formata "m:ss".</summary>
    public static string FmtTime(double t)
    {
        var total = (int)Math.Round(t);
        var m = total / 60;
        var s = total % 60;
        return $"{m}:{s:D2}";
    }
}
