using System.Text.Json;

namespace ArgosClip;

/// <summary>
/// Configuração da seleção por IA (provedor, modelo, URL base e chave).
/// A chave é gravada **cifrada com DPAPI** (por usuário da máquina) em
/// <c>%APPDATA%\ArgosClip\settings.json</c> — o app lê daí em cada job, então
/// o usuário configura tudo pela interface, sem abrir terminal.
/// </summary>
public sealed record LlmSettings
{
    /// <summary>Id do provedor: gemini | groq | openai | openrouter | ollama.</summary>
    public string Provider { get; init; } = "gemini";

    public string? Model { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>Chave em texto puro — só existe em memória (nunca é devolvida à UI).</summary>
    public string? ApiKey { get; init; }

    public bool HasKey => !string.IsNullOrEmpty(ApiKey);
}

public static class LlmConfigStore
{
    private static readonly object Gate = new();
    private static LlmSettings? _cache;
    private static bool _loaded;

    /// <summary>Pasta da config. ARGOS_CONFIG_DIR permite isolar em testes.</summary>
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("ARGOS_CONFIG_DIR")?.Trim() is { Length: > 0 } d
            ? d
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArgosClip");

    public static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    /// <summary>True quando o usuário já configurou algo pela interface (arquivo existe).</summary>
    public static bool HasSavedConfig => File.Exists(ConfigPath);

    /// <summary>Config atual (carrega do disco na primeira vez).</summary>
    public static LlmSettings Current
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _cache ?? new LlmSettings();
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(ConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = doc.RootElement;
            _cache = new LlmSettings
            {
                Provider = Str(root, "provider") is { Length: > 0 } p ? p : "gemini",
                Model = Str(root, "model"),
                BaseUrl = Str(root, "base_url"),
                ApiKey = Dpapi.Unprotect(Str(root, "api_key_enc")),
            };
        }
        catch
        {
            // Config corrompida/ilegível: ignora e usa os padrões.
        }
    }

    public static void Save(LlmSettings s)
    {
        lock (Gate)
        {
            _cache = s;
            _loaded = true;
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var payload = new Dictionary<string, object?>
                {
                    ["provider"] = s.Provider,
                    ["model"] = string.IsNullOrWhiteSpace(s.Model) ? null : s.Model,
                    ["base_url"] = string.IsNullOrWhiteSpace(s.BaseUrl) ? null : s.BaseUrl,
                    ["api_key_enc"] = Dpapi.Protect(s.ApiKey),
                };
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Falha ao gravar não pode derrubar o app; a config em memória vale.
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _cache = new LlmSettings();
            _loaded = true;
            try
            {
                if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            }
            catch
            {
                // melhor esforço
            }
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()?.Trim()
            : null;
}
