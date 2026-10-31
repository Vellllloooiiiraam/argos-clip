namespace ArgosClip.Pipeline;

public sealed record ArgosWord(double Start, double End, string Text);

public sealed record ArgosSegment(
    double Start,
    double End,
    string Text,
    IReadOnlyList<ArgosWord>? Words = null)
{
    public IReadOnlyList<ArgosWord> WordList => Words ?? Array.Empty<ArgosWord>();
}

public sealed class Clip
{
    public Clip(double start, double end, string text, IReadOnlyList<ArgosWord> words)
    {
        Start = start; End = end; Text = text; Words = words;
    }

    public double Start { get; }
    public double End { get; }
    public string Text { get; }
    public IReadOnlyList<ArgosWord> Words { get; }
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = new();
    public bool BrollUsed { get; set; }
}
