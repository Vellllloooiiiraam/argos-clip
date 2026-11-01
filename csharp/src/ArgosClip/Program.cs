using System.Diagnostics;
using ArgosClip;
using Microsoft.AspNetCore.Http.Features;

// ---------------------------------------------------------------------------
// Ponto de entrada do Argos Clip (C#): sobe o servidor local e abre o navegador.
// Porta fiel do main.py + server.py do original.
// ---------------------------------------------------------------------------

const string Host = "127.0.0.1";
const int DefaultPort = 8484;
const long MaxBody = 8L * 1024 * 1024 * 1024; // 8 GB

// A seleção por IA faz sync-over-async (GetResult). Com o ThreadPool no mínimo
// padrão isso pode travar a 1ª chamada HTTP (starvation). Subir o mínimo evita.
ThreadPool.SetMinThreads(
    Math.Max(Environment.ProcessorCount * 4, 32),
    Math.Max(Environment.ProcessorCount * 4, 32));

// Porta: ARGOS_PORT manda; sem ela, usa 8484 e, se estiver ocupada, acha a
// próxima livre sozinho (nada de brigar com outro app).
static bool IsPortFree(int p)
{
    try
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
        l.Start();
        l.Stop();
        return true;
    }
    catch
    {
        return false;
    }
}

static int NextFreePort(int start, int tries = 50)
{
    for (var p = start; p < start + tries; p++)
    {
        if (IsPortFree(p)) return p;
    }
    return start;
}

var port = int.TryParse(Environment.GetEnvironmentVariable("ARGOS_PORT"), out var p) && p > 0
    ? p
    : NextFreePort(DefaultPort);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Fixa o content root na pasta do executável: sem isso, iniciar o app com
    // outro diretório atual (ex.: duplo-clique fora da pasta) quebra o startup.
    ContentRootPath = AppPaths.ExeDir,
    WebRootPath = Path.Combine(AppPaths.ExeDir, "wwwroot"),
});

builder.WebHost.UseUrls($"http://{Host}:{port}");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxBody);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxBody;
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartHeadersLengthLimit = int.MaxValue;
});

var app = builder.Build();

// cabeçalhos de segurança padrão em todas as respostas (item 5.4 do original)
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h.TryAdd("X-Content-Type-Options", "nosniff");
        h.TryAdd("X-Frame-Options", "DENY");
        h.TryAdd("Referrer-Policy", "no-referrer");
        h.TryAdd("X-Permitted-Cross-Domain-Policies", "none");
        return Task.CompletedTask;
    });
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// estado compartilhado
var store = new JobStore();
var queue = new JobQueue(ArgosPipeline.MaxActiveJobs);
var pipeline = new ArgosPipeline(store);
Directory.CreateDirectory(AppPaths.JobsDir);

// retenção de jobs em disco (item 5.2): remove os terminados há mais de TTL
var cleanup = new Thread(() => CleanupLoop(store)) { IsBackground = true, Name = "argos-cleanup" };
cleanup.Start();

Endpoints.Map(app, store, queue, pipeline);

// abre o navegador quando o servidor responder
_ = Task.Run(async () =>
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var up = false;
    for (var i = 0; i < 40; i++)
    {
        await Task.Delay(250);
        try
        {
            var r = await http.GetAsync($"http://{Host}:{port}/");
            if (r.IsSuccessStatusCode) { up = true; break; }
        }
        catch
        {
            // ainda subindo
        }
    }
    if (up)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://{Host}:{port}") { UseShellExecute = true });
        }
        catch
        {
            // sem navegador padrão — o usuário abre manualmente
        }
    }
});

Console.WriteLine($"Argos Clip rodando em http://{Host}:{port}  (Ctrl+C para sair)");
app.Run();

static void CleanupLoop(JobStore store)
{
    while (true)
    {
        Thread.Sleep(TimeSpan.FromMinutes(5));
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-ArgosPipeline.JobTtlHours);
            if (!Directory.Exists(AppPaths.JobsDir)) continue;
            foreach (var d in Directory.GetDirectories(AppPaths.JobsDir))
            {
                var name = Path.GetFileName(d);
                var st = store.Get(name);
                var status = st?.Get("status") as string;
                var terminal = st is null || status is "done" or "error";
                if (terminal && Directory.GetLastWriteTimeUtc(d) < cutoff)
                {
                    try { Directory.Delete(d, recursive: true); } catch { }
                    store.Remove(name);
                }
            }
        }
        catch
        {
            // limpeza é melhor esforço
        }
    }
}
