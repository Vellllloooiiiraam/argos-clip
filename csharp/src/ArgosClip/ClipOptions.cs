using ArgosClip.Pipeline;

namespace ArgosClip;

/// <summary>
/// Opções de seleção/renderização de um job. Vêm da interface (por requisição)
/// e caem em variáveis de ambiente quando o usuário não escolhe.
/// </summary>
public sealed record ClipOptions
{
    /// <summary>Quantos clipes gerar (1–10).</summary>
    public int NClips { get; init; } = 5;

    /// <summary>Duração mínima de um clipe, em segundos.</summary>
    public double MinLen { get; init; } = 20;

    /// <summary>Duração máxima de um clipe, em segundos.</summary>
    public double MaxLen { get; init; } = 60;

    /// <summary>Modelo do Whisper (tiny/base/small/medium/large-v3-turbo...).</summary>
    public string Model { get; init; } = "small";

    /// <summary>Usar seleção por IA (Gemini) quando houver chave configurada.</summary>
    public bool UseLlm { get; init; }

    /// <summary>Padrões a partir do ambiente (usado quando a UI não manda nada).</summary>
    public static ClipOptions Defaults() => new()
    {
        NClips = 5,
        MinLen = EnvDouble("ARGOS_MIN_CLIP_S", 20),
        MaxLen = EnvDouble("ARGOS_MAX_CLIP_S", 60),
        Model = EnvString("ARGOS_WHISPER_MODEL") ?? "small",
        UseLlm = LlmSelector.Available,
    };

    /// <summary>
    /// Monta as opções de uma requisição; campos ausentes/inválidos caem no padrão.
    /// </summary>
    public static ClipOptions FromRequest(
        int nClips, string? minS, string? maxS, string? model, bool? useLlm)
    {
        var d = Defaults();
        var min = ParseDouble(minS, d.MinLen);
        var max = ParseDouble(maxS, d.MaxLen);

        // saneia: 5..180s e garante min < max
        min = Math.Clamp(min, 5, 150);
        max = Math.Clamp(max, min + 5, 180);

        var name = string.IsNullOrWhiteSpace(model) ? d.Model : model!.Trim().ToLowerInvariant();
        if (!Transcriber.IsValidModel(name)) name = d.Model;

        return new ClipOptions
        {
            NClips = Math.Clamp(nClips, 1, 10),
            MinLen = min,
            MaxLen = max,
            Model = name,
            UseLlm = useLlm ?? d.UseLlm,
        };
    }

    private static double ParseDouble(string? s, double fallback) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v : fallback;

    private static double EnvDouble(string key, double fallback) =>
        ParseDouble(Environment.GetEnvironmentVariable(key), fallback);

    private static string? EnvString(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } v ? v : null;
}
