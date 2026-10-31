using System.Text;

namespace ArgosClip;

/// <summary>
/// Caminhos que funcionam tanto em desenvolvimento (dotnet run) quanto no
/// executável publicado (dotnet publish, self-contained).
/// </summary>
public static class AppPaths
{
    /// <summary>Pasta do executável (BaseDirectory). No .exe publicado é onde tudo vive.</summary>
    public static string ExeDir { get; } = AppContext.BaseDirectory;

    /// <summary>Base de jobs/ e de arquivos do usuário: no .exe é a pasta do exe;
    /// em desenvolvimento é a raiz do projeto (sobe 3 níveis de bin/Debug/TFM).</summary>
    public static string UserDir { get; }

    public static string ToolDir { get; } = Path.Combine(ExeDir, "tools");

    /// <summary>Cascata Haar do rastreio de rosto (copiada do OpenCV).</summary>
    public static string DataDir { get; } = Path.Combine(ToolDir, "data");

    public static string JobsDir { get; }

    public static string ModelsDir { get; }

    static AppPaths()
    {
        var baseDir = ExeDir;
        var dir = new DirectoryInfo(baseDir);
        // em desenvolvimento: bin/Debug/net8.0-windows → 3 níveis até a raiz do projeto
        while (dir.FullName.EndsWith("net8.0-windows", StringComparison.OrdinalIgnoreCase) && (dir.Parent?.Parent?.Parent is not null))
        {
            dir = dir.Parent!.Parent!.Parent!;
            break;
        }
        UserDir = dir.FullName;

        // Testes apontam ARGOS_JOBS_DIR para a pasta própria (nunca a de produção).
        // No .exe, jobs/ fica ao lado do executável (visível e limpo pelo usuário).
        JobsDir = Environment.GetEnvironmentVariable("ARGOS_JOBS_DIR") ?? Path.Combine(UserDir, "jobs");

        ModelsDir = Environment.GetEnvironmentVariable("ARGOS_MODELS_DIR") ?? Path.Combine(UserDir, "models");
    }

    /// <summary>
    /// Resolve um executável externo: pasta interna → dir do exe → PATH.
    /// Igual ao behaviour do paths.tool() do original (fallback = nome cru).
    /// </summary>
    public static string Tool(string name)
    {
        var candidates = new[]
        {
            Path.Combine(ToolDir, name + ".exe"),
            Path.Combine(ExeDir, name + ".exe"),
            Path.Combine(UserDir, name + ".exe"),
            Path.Combine(UserDir, "bin", name + ".exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(d.Trim('"'), name + ".exe");
                if (File.Exists(p)) return p;
            }
            catch
            {
                // ignora entradas inválidas no PATH
            }
        }
        return name; // deixa o erro claro na saída do subprocesso
    }
}
