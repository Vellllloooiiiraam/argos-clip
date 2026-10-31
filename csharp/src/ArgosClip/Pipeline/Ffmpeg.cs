using System.Diagnostics;
using System.Text.Json;

namespace ArgosClip.Pipeline;

/// <summary>
/// FFmpeg/ffprobe: resolução de ferramenta, probe de mídia e extração de áudio.
/// Espelha o pipeline.run/probe/extract_audio do original.
/// </summary>
public static class Ffmpeg
{
    public static string FfmpegPath => AppPaths.Tool("ffmpeg");
    public static string FfprobePath => AppPaths.Tool("ffprobe");

    /// <summary>
    /// Roda um comando, com cwd opcional (mesmo contrato do pipeline.run do original:
    /// quando cwd é informado, os nomes dos arquivos são relativos a ele).
    /// </summary>
    public static (int Code, string StdOut, string StdErr) Run(
        IReadOnlyList<string> cmd, string? cwd = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = StandardUtf8,
            StandardErrorEncoding = StandardUtf8,
        };
        if (cwd is not null)
        {
            psi.WorkingDirectory = cwd;
        }
        for (var i = 1; i < cmd.Count; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {cmd[0]}.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30 * 60 * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>Encoding sem BOM (original usa utf-8/replace).</summary>
    public static readonly System.Text.Encoding StandardUtf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public sealed record ProbeInfo(double Duration, int Width, int Height, bool HasAudio);

    /// <summary>Retorna duração, dimensões e presença de áudio via ffprobe.</summary>
    public static ProbeInfo Probe(FileInfo src)
    {
        var cmd = new[]
        {
            FfprobePath, "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", src.FullName,
        };
        var (code, stdout, stderr) = Run(cmd);
        if (code != 0)
        {
            throw new InvalidOperationException($"ffprobe falhou: {T(stderr, 500)}");
        }

        JsonElement root = default;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "{}" : stdout);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ProbeInfo(0, 0, 0, false);
        }

        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object
            && fmt.TryGetProperty("duration", out var d))
        {
            duration = ParseDouble(d);
        }

        var width = 0;
        var height = 0;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in streams.EnumerateArray())
            {
                if (!st.TryGetProperty("codec_type", out var ct))
                {
                    continue;
                }
                var kind = ct.GetString();
                if (kind == "video" && width == 0)
                {
                    if (st.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
                    {
                        width = w.GetInt32();
                    }
                    if (st.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                    {
                        height = h.GetInt32();
                    }
                }
                else if (kind == "audio")
                {
                    hasAudio = true;
                }
            }
        }
        return new ProbeInfo(duration, width, height, hasAudio);
    }

    /// <summary>Extrai áudio 16 kHz mono WAV para o Whisper.</summary>
    public static string ExtractAudio(DirectoryInfo jobDir, FileInfo src)
    {
        var outPath = Path.Combine(jobDir.FullName, "audio.wav");
        var cmd = new[]
        {
            FfmpegPath, "-y", "-i", src.FullName,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outPath,
        };
        var (code, _, stderr) = Run(cmd);
        if (code != 0)
        {
            throw new InvalidOperationException($"extração de áudio falhou: {T(stderr, 500)}");
        }
        return outPath;
    }

    public static string T(string s, int max) => s.Length <= max ? s : s[..max];

    public static double ParseDouble(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String when double.TryParse(
            el.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };
}
