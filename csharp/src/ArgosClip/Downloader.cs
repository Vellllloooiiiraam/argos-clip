using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArgosClip;

/// <summary>
/// Download de vídeos por link via yt-dlp, com validação anti-SSRF.
/// Porta fiel do downloader.py do original.
/// </summary>
public static partial class Downloader
{
    /// <summary>Hosts permitidos — qualquer outro URL é recusado (evita SSRF).</summary>
    public static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be",
        "music.youtube.com",
        "twitch.tv", "www.twitch.tv", "clips.twitch.tv",
        "vimeo.com", "www.vimeo.com", "player.vimeo.com",
        "facebook.com", "www.facebook.com", "fb.watch",
        "twitter.com", "www.twitter.com", "x.com", "www.x.com",
        "reddit.com", "www.reddit.com", "v.redd.it",
        "tiktok.com", "www.tiktok.com", "vm.tiktok.com",
        "instagram.com", "www.instagram.com",
        "dailymotion.com", "www.dailymotion.com", "dai.ly",
        "kick.com", "www.kick.com",
        "rumble.com", "www.rumble.com",
        "odysee.com", "www.odysee.com",
        "bitchute.com", "www.bitchute.com",
        "streamable.com", "www.streamable.com",
        // arquivos diretos em CDNs comuns (mp4 avulso)
        "cdn.jsdelivr.net", "commondatastorage.googleapis.com",
        "download.blender.org", "test-videos.co.uk",
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    // Limites anti-DoS
    public const double MaxVideoDurationS = 7200;      // vídeos com mais de 2 h são recusados
    public const double MaxDownloadWallS = 30 * 60;    // download abortado após 30 min de relógio real

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%")]
    private static partial Regex ProgressRe();

    [GeneratedRegex(@"of\s+~?\s*([\d.]+)(M|G)iB")]
    private static partial Regex SizeRe();

    /// <summary>Valida o link; devolve a URL limpa ou lança ArgumentException.</summary>
    public static string ValidateUrl(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) throw new ArgumentException("Link vazio.");
        if (url.Length > 2048) throw new ArgumentException("Link longo demais.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            throw new ArgumentException("Use um link http:// ou https://.");
        }
        if (!AllowedSchemes.Contains(parsed.Scheme))
        {
            throw new ArgumentException("Use um link http:// ou https://.");
        }
        var host = parsed.Host.ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0) throw new ArgumentException("Link sem host válido.");
        if (!AllowedHosts.Contains(host))
        {
            throw new ArgumentException(
                $"Plataforma não suportada: {host}. " +
                "Aceito: YouTube, Twitch, Vimeo, Facebook, X, Reddit, TikTok, " +
                "Instagram, Dailymotion, Kick, Rumble, Odysee, Streamable.");
        }
        return url;
    }

    /// <summary>Baixa o vídeo para destDir e devolve o caminho do arquivo.</summary>
    public static FileInfo Download(string url, DirectoryInfo destDir, Action<double, string>? progressCb = null)
    {
        var ytdlp = AppPaths.Tool("yt-dlp");
        var outTemplate = Path.Combine(destDir.FullName, "source.%(ext)s");

        var psi = new ProcessStartInfo
        {
            FileName = ytdlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = destDir.FullName,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        void Arg(string a) => psi.ArgumentList.Add(a);
        Arg("-o"); Arg(outTemplate);
        Arg("-f"); Arg("bv*[ext=mp4][height<=1080]+ba[ext=m4a]/b[ext=mp4][height<=1080]/bv*[height<=1080]+ba/b");
        Arg("--merge-output-format"); Arg("mp4");
        Arg("--no-playlist");
        Arg("--max-filesize"); Arg("8589934592");
        Arg("--socket-timeout"); Arg("30");
        Arg("--retries"); Arg("3");
        Arg("--newline");
        Arg("--no-warnings");
        Arg("--progress");
        Arg(url);

        var started = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o yt-dlp.");

        void OnLine(string? line)
        {
            if (line is null || progressCb is null) return;
            if (started.Elapsed.TotalSeconds > MaxDownloadWallS)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return;
            }
            var m = ProgressRe().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                var sz = SizeRe().Match(line);
                var msg = sz.Success
                    ? $"Baixando… {m.Groups[1].Value}% ({sz.Groups[1].Value}{sz.Groups[2].Value}B)"
                    : $"Baixando… {m.Groups[1].Value}%";
                progressCb(Math.Min(99.0, pct), msg);
            }
        }

        var stderrBuf = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var exited = p.WaitForExit((int)(MaxDownloadWallS * 1000) + 60_000);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            CleanupPartials(destDir);
            throw new TimeoutException($"Download cancelado: mais de {MaxDownloadWallS / 60:F0} minutos baixando este link.");
        }
        if (started.Elapsed.TotalSeconds > MaxDownloadWallS)
        {
            CleanupPartials(destDir);
            throw new TimeoutException($"Download cancelado: mais de {MaxDownloadWallS / 60:F0} minutos baixando este link.");
        }
        if (p.ExitCode != 0)
        {
            CleanupPartials(destDir);
            var err = stderrBuf.ToString();
            throw new InvalidOperationException($"Falha no download: {Ffmpeg_T(err)}");
        }

        var files = destDir.GetFiles("source.*")
            .OrderByDescending(f => f.Length)
            .Where(f => new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".flv", ".ts" }
                .Contains(f.Extension.ToLowerInvariant()))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("O download não gerou nenhum arquivo de vídeo.");
        }
        return files[0];
    }

    private static void CleanupPartials(DirectoryInfo destDir)
    {
        foreach (var f in destDir.GetFiles("source.*"))
        {
            try { f.Delete(); } catch { }
        }
    }

    private static string Ffmpeg_T(string s) => s.Length <= 400 ? s.Trim() : s.Trim()[..400];
}
