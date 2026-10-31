using OpenCvSharp;

namespace ArgosClip.Pipeline;

/// <summary>
/// Reframe por rosto: rastreia o rosto no clipe e gera crop 9:16 que segue.
/// Fluxo: sampleia frames do trecho → detecta o maior rosto (Haar) → suaviza
/// o centro → gera expressões x/y por partes para o `crop` do FFmpeg.
/// Sem rosto detectado, devolve null e o render usa o crop central (fallback).
/// </summary>
public static class Reframe
{
    internal const int SampleFps = 2;
    internal const int MaxKeypoints = 12;
    internal const int MinFaceHits = 3;
    internal const int SmoothWindow = 5;

    private static CascadeClassifier? _cascade;
    private static readonly object CascadeLock = new();

    private static CascadeClassifier? FaceCascade()
    {
        lock (CascadeLock)
        {
            if (_cascade is not null) return _cascade;
            var path = Path.Combine(AppPaths.DataDir, "haarcascade_frontalface_default.xml");
            if (!File.Exists(path)) return null;
            var c = new CascadeClassifier(path);
            if (c.Empty()) return null;
            _cascade = c;
            return c;
        }
    }

    /// <summary>
    /// Rastreia o rosto no trecho [start, start+dur].
    /// Devolve [(t_relativo, cx, cy), …] em pixels do vídeo original, ou null
    /// se o rastreio não for confiável (sem rosto / poucas detecções).
    /// </summary>
    public static List<(double T, double Cx, double Cy)>? TrackFace(string src, double start, double dur)
    {
        using var cap = new VideoCapture(src);
        if (!cap.IsOpened()) return null;
        var w = (int)cap.FrameWidth;
        var h = (int)cap.FrameHeight;
        if (w <= 0 || h <= 0) return null;
        if (cap.Fps <= 0) return null;

        var cascade = FaceCascade();
        if (cascade is null || cascade.Empty()) return null;

        var hits = new List<(double, double, double)>();
        var step = 1.0 / SampleFps;
        try
        {
            var t = 0.0;
            while (t < dur)
            {
                cap.Set(VideoCaptureProperties.PosMsec, (start + t) * 1000.0);
                using var raw = new Mat();
                if (!cap.Read(raw) || raw.Empty()) break;

                // escala p/ largura máx. 480px — detectar em resolução cheia é lento
                var scale = Math.Min(1.0, 480.0 / w);
                using var frame = scale < 1.0
                    ? raw.Resize(new Size((int)(w * scale), (int)(h * scale)))
                    : raw;
                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                var faces = cascade.DetectMultiScale(gray, 1.1, 5);
                if (faces is { Length: > 0 })
                {
                    var f = faces.OrderByDescending(x => x.Width * x.Height).First();
                    hits.Add((t, (f.X + f.Width / 2.0) / scale, (f.Y + f.Height / 2.0) / scale));
                }
                t += step;
            }
        }
        catch
        {
            // OpenCV pode falhar em vídeo esquisito — cai no crop central
            return null;
        }

        if (hits.Count < MinFaceHits) return null;

        // preenche lacunas interpolando entre detecções vizinhas
        var filled = new List<(double T, double Cx, double Cy)>();
        for (var i = 0; i < hits.Count; i++)
        {
            if (i == 0) { filled.Add(hits[0]); continue; }
            var (t0, x0, y0) = hits[i - 1];
            var (t1, x1, y1) = hits[i];
            if (t1 - t0 > step * 2.5)
            {
                var n = (int)((t1 - t0) / step);
                for (var k = 1; k < n; k++)
                {
                    var f = (double)k / n;
                    filled.Add((t0 + (t1 - t0) * f, x0 + (x1 - x0) * f, y0 + (y1 - y0) * f));
                }
            }
            filled.Add(hits[i]);
        }

        // suaviza com média móvel
        var smooth = new List<(double T, double Cx, double Cy)>();
        for (var i = 0; i < filled.Count; i++)
        {
            var from = Math.Max(0, i - SmoothWindow / 2);
            var to = Math.Min(filled.Count - 1, i + SmoothWindow / 2);
            var win = filled.Skip(from).Take(to - from + 1).ToList();
            var cx = win.Average(p => p.Cx);
            var cy = win.Average(p => p.Cy);
            smooth.Add((filled[i].T, cx, cy));
        }

        // reduz para no máximo MAX_KEYPOINTS (uniforme no tempo)
        if (smooth.Count > MaxKeypoints)
        {
            double stepI = (smooth.Count - 1.0) / (MaxKeypoints - 1);
            var reduced = new List<(double, double, double)>();
            for (var i = 0; i < MaxKeypoints; i++)
            {
                reduced.Add(smooth[(int)Math.Round(i * stepI)]);
            }
            smooth = reduced;
        }
        return smooth;
    }

    /// <summary>
    /// Gera expressões FFmpeg x(t), y(t) por partes lineares que seguem o rosto.
    /// </summary>
    public static (string XExpr, string YExpr) BuildCropExpr(
        IReadOnlyList<(double T, double Cx, double Cy)> points,
        int cw, int ch, int w, int h)
    {
        var maxX = Math.Max(0, w - cw);
        var maxY = Math.Max(0, h - ch);
        double ClampX(double v) => Math.Min(maxX, Math.Max(0.0, v));
        double ClampY(double v) => Math.Min(maxY, Math.Max(0.0, v));

        var pts = points.Select(p => (p.T, px: ClampX(p.Cx - cw / 2.0), py: ClampY(p.Cy - ch / 2.0))).ToList();

        string Piecewise(bool takeY)
        {
            // interpola linearmente entre pts[i] e pts[i+1]; antes do 1º usa o 1º
            var expr = (takeY ? pts[0].py : pts[0].px).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            for (var i = pts.Count - 1; i >= 0; i--)
            {
                var t0 = pts[i].T;
                var v0 = takeY ? pts[i].py : pts[i].px;
                if (i == pts.Count - 1)
                {
                    expr = v0.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }
                var t1 = pts[i + 1].T;
                var v1 = takeY ? pts[i + 1].py : pts[i + 1].px;
                var slope = t1 > t0 ? (v1 - v0) / (t1 - t0) : 0.0;
                var inner = $"({v0.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}+" +
                            $"{slope.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}*(t-{t0.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}))";
                expr = $"if(lt(t,{t1.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}),{inner},{expr})";
            }
            if (pts.Count > 1)
            {
                var first = (takeY ? pts[0].py : pts[0].px).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                expr = $"if(lt(t,{pts[0].T.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}),{first},{expr})";
            }
            return expr;
        }

        return (Piecewise(false), Piecewise(true));
    }

    /// <summary>Devolve a parte do filtro de crop que segue o rosto, ou null.</summary>
    public static string? ReframeVf(string src, double start, double dur, int iw, int ih)
    {
        if (iw <= 0 || ih <= 0) return null;
        var cw = Math.Min(iw, ih * 9 / 16);
        var ch = Math.Min(ih, iw * 16 / 9);
        if (cw >= iw && ch >= ih) return null; // vídeo já é (quase) 9:16

        var points = TrackFace(src, start, dur);
        if (points is null || points.Count == 0) return null;
        var (xExpr, yExpr) = BuildCropExpr(points, cw, ch, iw, ih);
        return $"crop={cw}:{ch}:x='{xExpr}':y='{yExpr}'";
    }
}
