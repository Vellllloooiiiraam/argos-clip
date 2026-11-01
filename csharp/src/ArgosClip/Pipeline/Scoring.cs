using System.Text.RegularExpressions;

namespace ArgosClip.Pipeline;

/// <summary>
/// Seleção de momentos — monta clipes em FRONTEIRAS DE FRASE (nunca no meio de
/// uma ideia) e pontua por "viralidade" (ganchos, números, perguntas, abertura
/// forte, densidade de fala).
///
/// O pipeline agora é: segmentos do Whisper → palavras → FRASES → agrupar
/// frases em clipes de [minLen, maxLen]. O mesmo conjunto de frases alimenta a
/// seleção por IA (LlmSelector).
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

    /// <summary>Pausa (s) que conta como fim de "frase" mesmo sem pontuação.</summary>
    private const double SentencePauseS = 1.4;

    // -----------------------------------------------------------------------
    //  Frases
    // -----------------------------------------------------------------------

    /// <summary>
    /// Achata os segmentos em palavras e reparte em frases. Corta em pontuação
    /// final (.!?…) ou em pausa longa. Frases maiores que <paramref name="maxLen"/>
    /// são divididas à força na fronteira de palavra mais próxima do meio.
    /// </summary>
    public static List<ArgosSentence> SplitSentences(IReadOnlyList<ArgosSegment> segments, double maxLen = 60)
    {
        var words = new List<ArgosWord>();
        foreach (var s in segments) words.AddRange(s.WordList);

        if (words.Count == 0)
        {
            // sem timestamps por palavra: usa os próprios segmentos como "frases"
            var list = new List<ArgosSentence>();
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (!string.IsNullOrWhiteSpace(s.Text))
                {
                    list.Add(new ArgosSentence(list.Count, s.Start, s.End, s.Text.Trim(), s.WordList));
                }
            }
            return list;
        }

        var sentences = new List<ArgosSentence>();
        var cur = new List<ArgosWord>();

        void Flush()
        {
            if (cur.Count == 0) return;
            var text = string.Join(" ", cur.Select(w => w.Text)).Trim();
            if (text.Length > 0)
            {
                sentences.Add(new ArgosSentence(
                    sentences.Count, cur[0].Start, cur[^1].End, text, cur.ToArray()));
            }
            cur.Clear();
        }

        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            cur.Add(w);

            var terminal = EndsSentence(w.Text);
            var gap = i + 1 < words.Count ? words[i + 1].Start - w.End : double.PositiveInfinity;
            var span = cur[^1].End - cur[0].Start;

            if (terminal || gap > SentencePauseS || span >= maxLen)
            {
                Flush();
            }
        }
        Flush();

        // divide frases ainda longas demais (Whisper às vezes não pontua)
        return SplitLongSentences(sentences, maxLen);
    }

    private static List<ArgosSentence> SplitLongSentences(List<ArgosSentence> sentences, double maxLen)
    {
        var result = new List<ArgosSentence>();
        foreach (var s in sentences)
        {
            if (s.Duration <= maxLen || s.Words.Count < 2)
            {
                result.Add(s with { Index = result.Count });
                continue;
            }

            var piece = new List<ArgosWord>();
            foreach (var w in s.Words)
            {
                piece.Add(w);
                var span = piece[^1].End - piece[0].Start;
                // corta ao passar do limite, se o próximo pedaço ainda tiver conteúdo
                if (span >= maxLen * 0.75 && EndsSentence(w.Text))
                {
                    EmitPiece(result, piece);
                    piece = new List<ArgosWord>();
                }
                else if (span >= maxLen)
                {
                    EmitPiece(result, piece);
                    piece = new List<ArgosWord>();
                }
            }
            EmitPiece(result, piece);
        }
        return result;
    }

    private static void EmitPiece(List<ArgosSentence> result, List<ArgosWord> piece)
    {
        if (piece.Count == 0) return;
        var text = string.Join(" ", piece.Select(w => w.Text)).Trim();
        if (text.Length == 0) return;
        result.Add(new ArgosSentence(result.Count, piece[0].Start, piece[^1].End, text, piece.ToArray()));
    }

    private static bool EndsSentence(string t)
    {
        var last = t.TrimEnd().TrimEnd('"', '\'', ')', ']', '»', '”', '’');
        if (last.Length == 0) return false;
        return last[^1] is '.' or '!' or '?' or '…';
    }

    // -----------------------------------------------------------------------
    //  Agrupamento em clipes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Junta frases consecutivas em clipes de duração [minLen, maxLen], mirando
    /// o meio da faixa. Nunca começa/termina no meio de uma frase.
    /// </summary>
    public static List<Clip> GroupSentences(
        IReadOnlyList<ArgosSentence> sentences, double minLen, double maxLen)
    {
        var chunks = new List<Clip>();
        if (sentences.Count == 0) return chunks;

        var preferred = Math.Clamp(minLen + (maxLen - minLen) * 0.5, minLen, maxLen);
        var cur = new List<ArgosSentence>();

        double CurDur() => cur.Count == 0 ? 0 : cur[^1].End - cur[0].Start;

        void Flush()
        {
            if (cur.Count == 0) return;
            var clip = ClipFromSentences(cur);
            if (clip.End - clip.Start >= 5.0) chunks.Add(clip);
            cur.Clear();
        }

        for (var i = 0; i < sentences.Count; i++)
        {
            var s = sentences[i];

            if (cur.Count > 0 && CurDur() + s.Duration > maxLen)
            {
                Flush();
            }

            cur.Add(s);

            if (CurDur() >= minLen)
            {
                var nextDur = i + 1 < sentences.Count ? sentences[i + 1].Duration : 0;
                if (CurDur() >= preferred || (i + 1 < sentences.Count && CurDur() + nextDur > maxLen))
                {
                    Flush();
                }
            }
        }
        Flush();

        // funde restos curtos no vizinho anterior (quando couber) e descarta migalhas
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            if (chunks[i].End - chunks[i].Start >= minLen) continue;
            if (i == 0)
            {
                if (chunks[i].End - chunks[i].Start < 8.0) chunks.RemoveAt(i);
                continue;
            }
            var prev = chunks[i - 1];
            if (prev.End - prev.Start + (chunks[i].End - chunks[i].Start) <= maxLen)
            {
                chunks[i - 1] = MergeClips(prev, chunks[i]);
                chunks.RemoveAt(i);
            }
        }

        ApplyPadding(chunks);
        return chunks;
    }

    private static Clip ClipFromSentences(IReadOnlyList<ArgosSentence> sentences)
    {
        var words = new List<ArgosWord>();
        foreach (var s in sentences) words.AddRange(s.Words);
        var text = string.Join(" ", sentences.Select(s => s.Text)).Trim();
        return new Clip(sentences[0].Start, sentences[^1].End, text, words);
    }

    private static Clip MergeClips(Clip a, Clip b)
    {
        var words = a.Words.Concat(b.Words).ToList();
        return new Clip(a.Start, b.End, a.Text + " " + b.Text, words);
    }

    /// <summary>
    /// Dá um pequeno respiro (lead-in/out) em cada clipe, sem invadir o clipe
    /// vizinho. Só usa o silêncio disponível entre as frases.
    /// </summary>
    public static void ApplyPadding(List<Clip> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var prevEnd = i > 0 ? chunks[i - 1].End : 0;
            var nextStart = i + 1 < chunks.Count ? chunks[i + 1].Start : double.PositiveInfinity;

            var leadIn = Math.Min(0.30, Math.Max(0.0, c.Start - prevEnd));
            var leadOut = Math.Min(0.35, Math.Max(0.0, nextStart - c.End));

            chunks[i] = new Clip(
                Math.Max(0.0, c.Start - leadIn),
                c.End + leadOut,
                c.Text,
                c.Words);
        }
    }

    /// <summary>
    /// Agrupamento "clássico" por segmentos — mantido por compatibilidade.
    /// </summary>
    public static List<Clip> BuildChunks(
        IReadOnlyList<ArgosSegment> segments, double minLen = 20.0, double maxLen = 60.0)
    {
        var sentences = SplitSentences(segments, maxLen);
        return GroupSentences(sentences, minLen, maxLen);
    }

    // -----------------------------------------------------------------------
    //  Pontuação
    // -----------------------------------------------------------------------

    /// <summary>Pontua o clipe e registra os motivos (para exibir na UI).</summary>
    public static void ScoreChunk(Clip clip, double? minLen = null, double? maxLen = null)
    {
        var textL = clip.Text.ToLowerInvariant();
        var dur = clip.End - clip.Start;
        var wordsPerSec = clip.Words.Count / Math.Max(0.1, dur);
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

        // gancho de abertura: peso alto, pois os 1os segundos decidem o replay
        var firstText = string.Join(" ", clip.Words.Take(10).Select(w => w.Text)).ToLowerInvariant();
        if (HookPt.Concat(HookEn).Any(k => firstText.Contains(k, StringComparison.Ordinal)))
        {
            score += 4.5;
            reasons.Add("gancho no início");
        }
        if (ReNumber.IsMatch(firstText))
        {
            score += 1.5;
            reasons.Add("número no início");
        }

        // clipe "fechado": começa e termina em frase — vale um bônus
        if (clip.Words.Count > 0 && EndsSentence(clip.Words[^1].Text))
        {
            score += 1.0;
            reasons.Add("frase completa");
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

        // prefere clipes dentro da faixa escolhida
        var lo = minLen ?? 20;
        var hi = maxLen ?? 60;
        if (dur >= lo && dur <= hi)
        {
            score += 0.8;
        }

        clip.Score = Math.Max(0.0, score);
        clip.Reasons = reasons;
    }

    /// <summary>Escolhe os N melhores chunks sem sobreposição temporal.</summary>
    public static List<Clip> SelectClips(
        IReadOnlyList<Clip> chunks, int n = 5, double minLen = 20, double maxLen = 60)
    {
        foreach (var c in chunks)
        {
            ScoreChunk(c, minLen, maxLen);
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

    /// <summary>
    /// Constrói um clipe a partir de um intervalo de frases (usado pela IA) e o
    /// pontua como o caminho heurístico, acrescentando o motivo dado pela IA.
    /// </summary>
    public static Clip? ClipFromRange(
        IReadOnlyList<ArgosSentence> sentences, int from, int to, string? note = null)
    {
        from = Math.Max(0, from);
        to = Math.Min(sentences.Count - 1, to);
        if (to < from) return null;
        var slice = sentences.Skip(from).Take(to - from + 1).ToList();
        var clip = ClipFromSentences(slice);
        ScoreChunk(clip);
        if (!string.IsNullOrWhiteSpace(note))
        {
            clip.Reasons.Insert(0, "IA: " + note!.Trim());
        }
        return clip;
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
