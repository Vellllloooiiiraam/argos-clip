namespace ArgosClip.Pipeline;

/// <summary>Atalho B-roll: comprimento padrão do overlay (4 s).</summary>
public static class BrollConstants
{
    public const int ClipLenS = 4;
}

/// <summary>
/// Renderização dos clipes: recorte 9:16 (seguindo o rosto quando possível),
/// escala para 1080×1920, legenda karaoke queimada e miniatura.
/// </summary>
public static class Renderer
{
    /// <summary>
    /// Recorta 9:16, escala, legenda, thumb. Se broll existir e couber, sobrepõe
    /// o clipe stock no meio (o áudio continua do original).
    /// </summary>
    public static (string Video, string Thumb, bool BrollUsed) RenderClip(
        string jobDir, FileInfo src, Clip clip, int idx, string? broll = null)
    {
        var job = new DirectoryInfo(jobDir);
        var assName = $"clip_{idx:D2}.ass";
        var assPath = Path.Combine(job.FullName, assName);
        Karaoke.WriteAss(clip.Words, clip.Start, assPath);

        var outName = $"clip_{idx:D2}.mp4";
        var outPath = Path.Combine(job.FullName, outName);
        var dur = clip.End - clip.Start;

        var info = Ffmpeg.Probe(src);
        var crop = Reframe.ReframeVf(src.FullName, clip.Start, dur, info.Width, info.Height)
                   ?? "crop='min(iw,ih*9/16)':'min(ih,iw*16/9)'";

        var brollUsed = false;
        (int Code, string StdOut, string StdErr) p = (0, "", "");
        if (broll is not null && File.Exists(broll) && dur >= 6)
        {
            // overlay de B-roll no trecho do meio; legendas ficam por cima
            var ovStart = Math.Round(dur * 0.35, 2);
            var ovEnd = Math.Round(Math.Min(dur, ovStart + BrollConstants.ClipLenS), 2);
            var fc =
                "[0:v]" + crop + ",scale=1080:1920:flags=lanczos[base];" +
                "[1:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1[br];" +
                $"[base][br]overlay=0:0:enable='between(t,{ovStart.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)},{ovEnd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})'[vo];" +
                $"[vo]subtitles={assName}[vout]";
            var cmd = new List<string>
            {
                Ffmpeg.FfmpegPath, "-y",
                "-ss", clip.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-t", dur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", src.Name,
                "-stream_loop", "-1",
                "-i", broll!,
                "-filter_complex", fc,
                "-map", "[vout]", "-map", "0:a",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
                "-c:a", "aac", "-b:a", "128k",
                "-t", dur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-movflags", "+faststart", outName,
            };
            p = Ffmpeg.Run(cmd, job.FullName);
            if (p.Code == 0)
            {
                brollUsed = true;
            }
        }

        if (!brollUsed)
        {
            var vf = crop + ",scale=1080:1920:flags=lanczos,subtitles=" + assName;
            var cmd = new[]
            {
                Ffmpeg.FfmpegPath, "-y",
                "-ss", clip.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-t", dur.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", src.Name,
                "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
                "-c:a", "aac", "-b:a", "128k",
                "-movflags", "+faststart", outName,
            };
            p = Ffmpeg.Run(cmd, job.FullName);
        }

        if (p.Code != 0)
        {
            throw new InvalidOperationException(
                $"render do clipe {idx} falhou: {Ffmpeg.T(p.StdErr.Length > 700 ? p.StdErr[^700..] : p.StdErr, 700)}");
        }

        var thumbName = $"clip_{idx:D2}.jpg";
        var thumbCmd = new[]
        {
            Ffmpeg.FfmpegPath, "-y",
            "-ss", Math.Max(0.1, dur / 2).ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            "-i", outName, "-frames:v", "1", "-q:v", "3", thumbName,
        };
        Ffmpeg.Run(thumbCmd, job.FullName);
        var thumb = Path.Combine(job.FullName, thumbName);

        return (outPath, thumb, brollUsed);
    }
}
