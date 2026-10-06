using System.Diagnostics;
using OledMirror.Core.Imaging;

namespace OledMirror.Core.Capture;

/// <summary>
/// Um "desktop" sintetico realista: papel de parede, janelas com texto, barra de
/// tarefas com relogio e um cursor. Serve para medir a compressao com conteudo
/// parecido com o real (oledmirror bench) e para o modo simulado da interface.
/// </summary>
public sealed class MockDesktopSource : ICaptureSource
{
    private static readonly string[] Lines =
    {
        "PROJETO OLEDMIRROR",
        "ESPELHAMENTO 128X64",
        "PROTOCOLO V1 COM CRC",
        "RLE DELTA E AUTO",
        "FPS ALVO 10 A 20",
        "ESP32 I2C 800 KHZ",
        "PAINEL SSD1306",
        "BAUD 921600",
        "TESTES 100% OK",
        "TELA PARADA = 0 B/S",
    };

    private readonly Canvas _canvas;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _paintedAt = double.NaN;

    public MockDesktopSource(int width = 1920, int height = 1080)
    {
        _canvas = new Canvas(width, height);
    }

    public string Name => "Desktop simulado";
    public int SourceWidth => _canvas.Width;
    public int SourceHeight => _canvas.Height;

    /// <summary>Cursor do mouse em movimento.</summary>
    public bool AnimateCursor { get; set; } = true;

    /// <summary>Conteudo da janela principal rolando.</summary>
    public bool ScrollContent { get; set; }

    /// <summary>Instante da simulacao. null = tempo real.</summary>
    public double? FixedTimeSeconds { get; set; }

    public bool TryCapture(out CapturedFrame frame)
    {
        double t = FixedTimeSeconds ?? _clock.Elapsed.TotalSeconds;
        if (t != _paintedAt)
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
        int unit = Math.Max(1, h / 54);           // ~20 px em 1080p
        uint white = Canvas.Gray(255), black = Canvas.Gray(0);

        // Papel de parede em faixas suaves.
        for (int i = 0; i < 8; i++)
            c.FillRect(0, i * h / 8, w, h / 8 + 1, Canvas.Rgb((byte)(30 + i * 6), (byte)(40 + i * 8), (byte)(70 + i * 10)));

        // Icones na lateral.
        for (int i = 0; i < 5; i++) c.FillRect(unit * 2, unit * 2 + i * unit * 5, unit * 3, unit * 3, Canvas.Gray(200));

        // Janela principal com linhas de texto.
        int wx = w / 8, wy = h / 10, ww = w * 5 / 8, wh = h * 6 / 10;
        c.FillRect(wx, wy, ww, wh, Canvas.Gray(235));
        c.FillRect(wx, wy, ww, unit * 2, Canvas.Rgb(40, 90, 170));
        c.DrawText("EDITOR - NOTAS.TXT", wx + unit, wy + unit / 2, Math.Max(1, unit / 8 + 1), white);
        c.StrokeRect(wx, wy, ww, wh, Math.Max(1, unit / 6), Canvas.Gray(80));

        int scale = Math.Max(1, unit / 5);
        int lineHeight = 10 * scale;
        int scroll = ScrollContent ? (int)(t * 7) : 0;
        int top = wy + unit * 3;
        int visible = Math.Max(1, (wh - unit * 4) / lineHeight);
        for (int i = 0; i < visible; i++)
        {
            string line = Lines[(i + scroll) % Lines.Length];
            c.DrawText(line, wx + unit, top + i * lineHeight, scale, black);
        }

        // Janela secundaria.
        int sx = w * 11 / 16, sy = h / 4, sw = w / 4, sh = h / 3;
        c.FillRect(sx, sy, sw, sh, Canvas.Gray(210));
        c.FillRect(sx, sy, sw, unit * 2, Canvas.Gray(90));
        c.FillCircle(sx + sw / 2, sy + sh / 2 + unit, Math.Min(sw, sh) / 4, Canvas.Gray(60));

        // Barra de tarefas com relogio.
        int barHeight = unit * 3;
        c.FillRect(0, h - barHeight, w, barHeight, Canvas.Gray(25));
        c.FillRect(unit, h - barHeight + unit / 3, unit * 2, barHeight - unit * 2 / 3, Canvas.Rgb(60, 140, 230));

        int seconds = (int)Math.Floor(t);
        string clock = $"{(10 + seconds / 3600) % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        // Grande o bastante para sobreviver a reducao de ~15x, senao o cenario "relogio" do bench nao mede nada.
        int clockScale = Math.Max(1, unit / 4);
        c.DrawText(clock, w - Canvas.MeasureText(clock, clockScale) - unit, h - barHeight + (barHeight - 7 * clockScale) / 2, clockScale, white);

        // Cursor.
        if (AnimateCursor)
        {
            int cx = (int)(w * (0.5 + 0.35 * Math.Cos(t * 0.9)));
            int cy = (int)(h * (0.5 + 0.30 * Math.Sin(t * 1.3)));
            DrawCursor(c, cx, cy, unit);
        }
    }

    private static void DrawCursor(Canvas c, int x, int y, int unit)
    {
        int size = unit * 2;
        for (int row = 0; row < size; row++)
        {
            int width = row * 2 / 3 + 1;
            c.FillRect(x, y + row, width, 1, Canvas.Gray(255));
            c.SetPixel(x + width, y + row, Canvas.Gray(0));
        }
    }

    public void Dispose() { }
}