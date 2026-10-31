using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArgosClip.Pipeline;

/// <summary>
/// B-roll automático: busca vídeo stock grátis (Pexels/Pixabay) por palavra-
/// chave da transcrição e devolve o arquivo para sobreposição no FFmpeg.
/// Configuração (chaves NUNCA hardcoded, só por variável de ambiente):
///   PEXELS_API_KEY=…   https://www.pexels.com/api/  (grátis)
///   PIXABAY_API_KEY=…  https://pixabay.com/api/docs/ (grátis)
/// Sem chave, ou se a busca falhar, devolve null e o clipe segue sem B-roll —
/// a falha nunca quebra o pipeline.
/// </summary>
public static partial class Broll
{
    private const int MinQueryLen = 3;
    internal const int ClipLenS = 4;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "o", "as", "os", "um", "uma", "de", "do", "da", "dos", "das",
        "e", "é", "que", "para", "por", "com", "sem", "mais", "menos", "não",
        "sim", "mas", "se", "em", "no", "na", "nos", "nas", "ao", "à", "às",
        "the", "and", "for", "with", "that", "this", "was", "were", "you",
        "your", "have", "has", "had", "not", "but", "they", "them", "his",
        "her", "she", "him", "are", "is", "it", "its", "of", "on", "in", "at",
        "to", "from", "we", "us", "our", "out", "about", "just", "like", "so",
        "what", "when", "how", "why", "who", "all", "can", "will", "would",
        "there", "here", "then", "than", "been", "being", "get", "got", "one",
    };

    [GeneratedRegex(@"[a-zA-ZÀ-ÿ]{3,}")]
    private static partial Regex WordRe();

    /// <summary>Top-`limit` palavras do conteúdo (sem stopwords), por frequência.</summary>
    public static List<string> ExtractKeywords(string text, int limit = 3)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordRe().Matches((text ?? "").ToLowerInvariant()))
        {
            var w = m.Value;
            if (Stopwords.Contains(w)) continue;
            counts[w] = counts.TryGetValue(w, out var c) ? c + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => kv.Key)
            .ToList();
    }

    private static string? SearchPexels(string query, string key)
    {
        var url = "https://api.pexels.com/videos/search?query=" + Uri.EscapeDataString(query) +
                  "&per_page=10&orientation=portrait";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", key);
        using var resp = Client.Send(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        if (!doc.RootElement.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var video in videos.EnumerateArray()
            .OrderBy(v => Math.Abs(HeightOf(v) - 1920)))
        {
            var files = new List<(string Link, double Height)>();
            if (video.TryGetProperty("video_files", out var vf) && vf.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in vf.EnumerateArray())
                {
                    if (f.TryGetProperty("link", out var l) && l.GetString() is { Length: > 0 } link
                        && f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetDouble() >= 1000)
                    {
                        files.Add((link, h.GetDouble()));
                    }
                }
            }
            if (files.Count > 0)
            {
                return files.OrderBy(f => Math.Abs(f.Height - 1920)).First().Link;
            }
        }
        return null;
    }

    private static string? SearchPixabay(string query, string key)
    {
        var url = "https://pixabay.com/api/videos/?key=" + Uri.EscapeDataString(key) +
                  "&query=" + Uri.EscapeDataString(query) + "&per_page=10";
        using var resp = Client.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(resp.Content.ReadAsStream());
        if (!doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var hit in hits.EnumerateArray()
            .OrderBy(h => Math.Abs(HeightOf(h) - 1920)))
        {
            if (hit.TryGetProperty("large", out var large) && large.TryGetProperty("url", out var u)
                && u.GetString() is { Length: > 0 } link)
            {
                return link;
            }
        }
        return null;
    }

    private static double HeightOf(JsonElement el) =>
        el.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0;

    /// <summary>Busca e baixa um clipe de B-roll relacionado; null se indisponível.</summary>
    public static string? FetchBroll(string text, string destDir, int timeoutS = 60)
    {
        var pexelsKey = Environment.GetEnvironmentVariable("PEXELS_API_KEY")?.Trim();
        var pixabayKey = Environment.GetEnvironmentVariable("PIXABAY_API_KEY")?.Trim();
        if (string.IsNullOrEmpty(pexelsKey) && string.IsNullOrEmpty(pixabayKey)) return null;

        foreach (var query in ExtractKeywords(text))
        {
            string? link = null;
            try
            {
                if (!string.IsNullOrEmpty(pexelsKey))
                {
                    link = SearchPexels(query, pexelsKey!);
                }
                link ??= string.IsNullOrEmpty(pixabayKey) ? null : SearchPixabay(query, pixabayKey!);
            }
            catch
            {
                continue; // B-roll é melhor esforço
            }
            if (string.IsNullOrEmpty(link)) continue;

            var dst = Path.Combine(destDir, "broll_stock.mp4");
            try
            {
                using var resp = Client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var src = resp.Content.ReadAsStream();
                using var fs = File.Create(dst);
                src.CopyTo(fs, 1024 * 256);
            }
            catch
            {
                continue;
            }
            if (File.Exists(dst) && new FileInfo(dst).Length > 50_000) return dst;
            try { File.Delete(dst); } catch { }
        }
        return null;
    }
}
