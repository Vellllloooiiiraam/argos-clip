using ArgosClip;
using Microsoft.AspNetCore.Http.Features;

// ---------------------------------------------------------------------------
// Ponto de entrada do Argos Clip (C#): sobe o servidor local e abre a JANELA
// nativa do app (WebView2). Porta fiel do main.py + server.py do original.
//
// O app é um WinExe (sem console). Por isso qualquer falha de startup é
// registrada em log e, se possível, mostrada numa caixa de diálogo.
// ---------------------------------------------------------------------------

const string Host = "127.0.0.1";
const int DefaultPort = 8484;
const long MaxBody = 8L * 1024 * 1024 * 1024; // 8 GB

// A seleção por IA faz sync-over-async (GetResult). Com o ThreadPool no mínimo
// padrão isso pode travar a 1a chamada HTTP (starvation). Subir o mínimo evita.
ThreadPool.SetMinThreads(
    Math.Max(Environment.ProcessorCount * 4, 32),
    Math.Max(Environment.ProcessorCount * 4, 32));

try
{
    // Porta: ARGOS_PORT manda; sem ela, usa 8484 e, se estiver ocupada, acha a
    // próxima livre sozinho (nada de brigar com outro app).
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

    // sobe o servidor (StartAsync só retorna com o Kestrel já escutando)
    await app.StartAsync();
    var url = $"http://{Host}:{port}";
    Console.WriteLine($"Argos Clip rodando em {url}");
    Log($"Argos Clip rodando em {url}");

    // Modo headless (testes / smoke test): só o servidor, sem janela.
    if (Environment.GetEnvironmentVariable("ARGOS_HEADLESS") == "1")
    {
        await app.WaitForShutdownAsync();
    }
    else
    {
        // Janela nativa (WebView2). WinForms exige uma thread STA.
        var ui = new Thread(() => MainWindow.Run(url)) { Name = "argos-ui" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join(); // fecha a janela → encerra o servidor e o app
    }

    await app.StopAsync();
}
catch (Exception ex)
{
    // Sem console (WinExe): registra em log e avisa o usuário na tela.
    Log("FATAL: " + ex);
    if (Environment.GetEnvironmentVariable("ARGOS_HEADLESS") != "1")
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(
                "Não foi possível iniciar o Argos Clip:\n\n" + ex.Message +
                "\n\nDetalhes em:\n" + LogPath(),
                "Argos Clip", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch
        {
            // sem UI disponível — o log já registrou
        }
    }
    Environment.ExitCode = 1;
}

// ---------------------------------------------------------------------------
// Funções auxiliares (top-level local functions)
// ---------------------------------------------------------------------------

static string LogPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "ArgosClip", "logs", "argos.log");

static void Log(string message)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath())!);
        File.AppendAllText(LogPath(), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    }
    catch
    {
        // melhor esforço: nunca falha por causa do log
    }
}

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
        catch (Exception e)
        {
            // limpeza é melhor esforço, mas registramos a falha
            Log("cleanup: " + e.Message);
        }
    }
}
