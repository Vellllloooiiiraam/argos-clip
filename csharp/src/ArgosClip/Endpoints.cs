using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ArgosClip;

/// <summary>
/// Rotas HTTP — mesmas do FastAPI original: /api/upload, /api/link,
/// /api/status/{id}, /media/{id}/{name} e / (index).
/// </summary>
public static class Endpoints
{
    private static readonly HashSet<string> AllowedExt =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts" };

    private const long MaxUpload = 8L * 1024 * 1024 * 1024; // 8 GB

    public static void Map(WebApplication app, JobStore store, JobQueue queue, ArgosPipeline pipeline)
    {
        app.MapPost("/api/upload", async (HttpRequest request) =>
        {
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync();
            }
            catch (Exception e)
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Upload inválido: " + e.Message }, statusCode: 400);
            }

            var file = form.Files["file"];
            if (file is null || file.Length == 0)
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Arquivo vazio." }, statusCode: 400);
            }

            var name = string.IsNullOrEmpty(file.FileName) ? "video" : file.FileName;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!AllowedExt.Contains(ext))
            {
                return Results.Json(new Dictionary<string, object?>
                {
                    ["detail"] = $"Formato não suportado: {ext}. Use mp4/mov/mkv/webm/avi.",
                }, statusCode: 400);
            }

            var n = ParseClips(form["n_clips"].ToString());
            var language = form["language"].ToString();

            var jobId = NewJobId();
            var jobDir = Directory.CreateDirectory(Path.Combine(AppPaths.JobsDir, jobId));
            var dst = Path.Combine(jobDir.FullName, "source" + ext);

            try
            {
                await using var input = file.OpenReadStream();
                await using var output = File.Create(dst);
                var buf = new byte[4 * 1024 * 1024];
                long size = 0;
                int read;
                while ((read = await input.ReadAsync(buf)) > 0)
                {
                    size += read;
                    if (size > MaxUpload)
                    {
                        output.Close();
                        TryDeleteDir(jobDir.FullName);
                        return Results.Json(new Dictionary<string, object?> { ["detail"] = "Arquivo maior que 8 GB." }, statusCode: 413);
                    }
                    await output.WriteAsync(buf.AsMemory(0, read));
                }
            }
            catch (Exception e)
            {
                TryDeleteDir(jobDir.FullName);
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Falha ao salvar o arquivo: " + e.Message }, statusCode: 500);
            }

            store.Add(jobId, NewState(jobId, "queued", "Na fila...", name));
            queue.Enqueue(jobId, () => pipeline.Process(jobId, new FileInfo(dst), n, string.IsNullOrWhiteSpace(language) ? null : language));
            return Results.Json(new Dictionary<string, object?> { ["job_id"] = jobId });
        });

        app.MapPost("/api/link", async (HttpRequest request) =>
        {
            string raw;
            using (var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8))
            {
                raw = await reader.ReadToEndAsync();
            }

            string url;
            int n;
            string? language;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return Results.Json(new Dictionary<string, object?> { ["detail"] = "Corpo inválido." }, statusCode: 400);
                }
                url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                var rawN = root.TryGetProperty("n_clips", out var nc)
                    ? (nc.ValueKind == JsonValueKind.String ? nc.GetString() : nc.ToString())
                    : "5";
                n = ParseClips(rawN);
                language = root.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String
                    ? lg.GetString()
                    : null;
            }
            catch
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Corpo inválido." }, statusCode: 400);
            }

            try
            {
                url = Downloader.ValidateUrl(url);
            }
            catch (ArgumentException e)
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = e.Message }, statusCode: 400);
            }

            var jobId = NewJobId();
            var jobDir = Directory.CreateDirectory(Path.Combine(AppPaths.JobsDir, jobId));
            store.Add(jobId, NewState(jobId, "download", "Baixando do link...", url));

            queue.Enqueue(jobId, () =>
            {
                try
                {
                    store.Get(jobId)?.Set(("status", "download"), ("stage", "Baixando do link..."));
                    var src = Downloader.Download(url, jobDir, (p, msg) =>
                        store.Get(jobId)?.Set(("progress", Math.Round(p, 1)), ("stage", msg)));
                    pipeline.Process(jobId, src, n, string.IsNullOrWhiteSpace(language) ? null : language);
                }
                catch (Exception e)
                {
                    store.Get(jobId)?.Set(("status", "error"), ("stage", "Erro"), ("error", e.Message));
                }
            });

            return Results.Json(new Dictionary<string, object?> { ["job_id"] = jobId });
        });

        app.MapGet("/api/status/{job_id}", (string job_id) =>
        {
            var st = store.Get(job_id);
            if (st is null)
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Job não encontrado." }, statusCode: 404);
            }
            return Results.Json(st.Snapshot());
        });

        app.MapGet("/media/{job_id}/{name}", (string job_id, string name) =>
        {
            var jobDir = Path.GetFullPath(Path.Combine(AppPaths.JobsDir, job_id));
            var path = Path.GetFullPath(Path.Combine(jobDir, name));
            if (!path.StartsWith(jobDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                return Results.Json(new Dictionary<string, object?> { ["detail"] = "Arquivo não encontrado." }, statusCode: 404);
            }
            return Results.File(path, ContentTypeFor(path), enableRangeProcessing: true);
        });
    }

    private static Dictionary<string, object?> NewState(string jobId, string status, string stage, string file) => new()
    {
        ["id"] = jobId,
        ["status"] = status,
        ["stage"] = stage,
        ["progress"] = 0.0,
        ["file"] = file,
        ["clips"] = new List<object>(),
        ["error"] = null,
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
    };

    private static string NewJobId() => Guid.NewGuid().ToString("N")[..12];

    private static int ParseClips(string? s)
    {
        if (int.TryParse(s, out var v)) return Math.Max(1, Math.Min(10, v));
        return 5;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
