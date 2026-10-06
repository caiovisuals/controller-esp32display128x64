using System.Diagnostics;
using OledMirror.Core.Imaging;

namespace OledMirror.Core.Capture;

public enum TestPattern
{
    Checkerboard,
    BouncingBox,
    OrbitingCircle,
    Gradient,
    Text,
    FineLines,
    Black,
    White,
    /// <summary>Ruido novo a cada instante: o pior caso para a compressao.</summary>
    Noise,
}

/// <summary>
/// Padroes de teste sinteticos, para validar o pipeline sem captura real.
/// Padroes estaticos sao pintados uma vez so; os animados dependem do tempo, que
/// pode ser congelado em <see cref="FixedTimeSeconds"/> para resultados reprodutiveis.
/// </summary>
public sealed class TestPatternSource : ICaptureSource
{
    private readonly Canvas _canvas;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _paintedAt = double.NaN;

    public TestPatternSource(TestPattern pattern, int width = 1920, int height = 1080)
    {
        Pattern = pattern;
        _canvas = new Canvas(width, height);
    }

    public TestPattern Pattern { get; }
    public string Name => $"Padrao {Pattern}";
    public int SourceWidth => _canvas.Width;
    public int SourceHeight => _canvas.Height;

    /// <summary>Instante da animacao. null = tempo real.</summary>
    public double? FixedTimeSeconds { get; set; }

    private bool IsAnimated => Pattern is TestPattern.BouncingBox or TestPattern.OrbitingCircle or TestPattern.Noise;

    public bool TryCapture(out CapturedFrame frame)
    {
        double t = FixedTimeSeconds ?? _clock.Elapsed.TotalSeconds;
        if (double.IsNaN(_paintedAt) || (IsAnimated && t != _paintedAt))
        {
            Paint(t);
            _paintedAt = t;
        }

        frame = new CapturedFrame(_canvas.Buffer, _canvas.Width, _canvas.Height, _canvas.Stride);
        return true;
    }

    private void Paint(double t)
    {
        Canvas c = _canvas;
        int w = c.Width, h = c.Height;
        uint black = Canvas.Gray(0), white = Canvas.Gray(255);

        switch (Pattern)
        {
            case TestPattern.Checkerboard:
            {
                // 16 x 8 casas, em qualquer resolucao.
                c.Clear(black);
                for (int j = 0; j < 8; j++)
                    for (int i = 0; i < 16; i++)
                        if (((i + j) & 1) == 1)
                            c.FillRect(i * w / 16, j * h / 8, (i + 1) * w / 16 - i * w / 16, (j + 1) * h / 8 - j * h / 8, white);
                break;
            }

            case TestPattern.BouncingBox:
            {
                c.Clear(black);
                int size = Math.Max(4, Math.Min(w, h) / 4);
                double px = PingPong(t * 0.37) * (w - size);
                double py = PingPong(t * 0.53) * (h - size);
                c.FillRect((int)px, (int)py, size, size, white);
                c.StrokeRect(0, 0, w, h, Math.Max(1, h / 64), white);
                break;
            }

            case TestPattern.OrbitingCircle:
            {
                c.Clear(Canvas.Gray(20));
                int radius = Math.Max(3, Math.Min(w, h) / 8);
                double angle = t * 1.7;
                int cx = w / 2 + (int)(Math.Cos(angle) * (w / 2 - radius - 1) * 0.8);
                int cy = h / 2 + (int)(Math.Sin(angle) * (h / 2 - radius - 1) * 0.8);
                c.FillCircle(w / 2, h / 2, Math.Max(1, radius / 3), Canvas.Gray(120));
                c.FillCircle(cx, cy, radius, white);
                break;
            }

            case TestPattern.Gradient:
            {
                for (int x = 0; x < w; x++)
                    c.FillRect(x, 0, 1, h, Canvas.Gray((byte)(x * 255 / Math.Max(1, w - 1))));
                break;
            }

            case TestPattern.Text:
            {
                c.Clear(black);
                string[] lines = { "OLEDMIRROR", "ESP32 128x64", "0123456789" };
                int scale = Math.Max(1, Math.Min(w / (12 * BitmapFont.Advance + 2), h / (lines.Length * 10)));
                int lineHeight = 10 * scale;
                int y = (h - lines.Length * lineHeight) / 2 + scale;
                foreach (string line in lines)
                {
                    c.DrawText(line, (w - Canvas.MeasureText(line, scale)) / 2, y, scale, white);
                    y += lineHeight;
                }
                c.StrokeRect(0, 0, w, h, Math.Max(1, scale), white);
                break;
            }

            case TestPattern.FineLines:
            {
                c.Clear(black);
                for (int x = 0; x < w; x += 2) c.FillRect(x, 0, 1, h, white);
                break;
            }

            case TestPattern.White:
                c.Clear(white);
                break;

            case TestPattern.Noise:
            {
                var rng = new Random(unchecked((int)(t * 1000.0)));
                rng.NextBytes(c.Buffer);
                for (int i = 3; i < c.Buffer.Length; i += 4) c.Buffer[i] = 255;
                break;
            }

            default:
                c.Clear(black);
                break;
        }
    }

    /// <summary>0 -> 1 -> 0 com periodo 2.</summary>
    private static double PingPong(double v)
    {
        double f = v % 2.0;
        return f < 1.0 ? f : 2.0 - f;
    }

    public void Dispose() { }
}