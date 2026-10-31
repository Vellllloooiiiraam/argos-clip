using System.Text;
using System.Text.RegularExpressions;

namespace ArgosClip.Pipeline;

/// <summary>
/// Legendas estilo karaoke (ASS): frases curtas com efeito palavra a palavra
/// ("\k" em centésimos de segundo), queimadas via FFmpeg/libass.
/// </summary>
public static partial class Karaoke
{
    private const string AssHeader = """
[Script Info]
ScriptType: v4.00+
PlayResX: 1080
PlayResY: 1920
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Cap,Arial,104,&H00FFFFFF,&H0000FFFF,&H00000000,&H7F000000,-1,0,0,0,100,100,0,0,1,9,3,2,70,70,560,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
""";

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex CrLf();

    private static string AssTime(double t)
    {
        t = Math.Max(0.0, t);
        var h = (int)(t / 3600);
        var m = (int)((t % 3600) / 60);
        var s = t % 60;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:D0}:{1:D2}:{2:00.00}", h, m, s);
    }

    private static string CleanAss(string text) =>
        CrLf().Replace(text, " ").Replace('{', '(').Replace('}', ')').Trim();

    /// <summary>Gera o .ass com frases curtas e efeito karaoke palavra a palavra.</summary>
    public static void WriteAss(IReadOnlyList<ArgosWord> words, double offset, string path, int maxPhrase = 4)
    {
        var lines = new StringBuilder(AssHeader.Length + 4096);
        lines.Append(AssHeader);
        lines.Append('\n');

        var i = 0;
        var n = words.Count;
        while (i < n)
        {
            var j = i;
            while (j < n && j - i < maxPhrase)
            {
                var w = words[j];
                if (j > i && w.Start - words[j - 1].End > 0.8)
                {
                    break;
                }
                j++;
            }
            var group = words.Skip(i).Take(j - i).ToList();
            if (group.Count > 0)
            {
                var start = group[0].Start - offset - 0.05;
                var end = group[^1].End - offset + 0.30;
                var parts = new List<string>(group.Count);
                foreach (var w in group)
                {
                    var durCs = Math.Max(1, (int)Math.Round((w.End - w.Start) * 100));
                    parts.Add($"{{\\k{durCs}}}{CleanAss(w.Text.ToUpperInvariant())}");
                }
                var text = string.Join(" ", parts);
                lines.Append($"Dialogue: 0,{AssTime(start)},{AssTime(end)},Cap,,0,0,0,,{text}\n");
            }
            i = j;
        }
        File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
    }
}
