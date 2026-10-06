using System.Diagnostics;
using OledMirror.Core.Imaging;

namespace OledMirror.Core.Capture;

public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>O que o modo texto mostra no painel.</summary>
public sealed record TextOptions
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Tamanho da fonte: 1 = 5x7 px, 2 = 10x14 px... 0 = o maior que couber.</summary>
    public int Scale { get; init; }

    public TextAlign Align { get; init; } = TextAlign.Center;

    public const int MaxScale = 4;
}

/// <summary>
/// Fonte que desenha um texto do usuario direto em 128x64, em vez de capturar a
/// tela. Quebra as linhas por palavra, centraliza na vertical e, se o texto nao
/// couber nem no menor tamanho, rola de baixo para cima.
/// <see cref="Update"/> pode ser chamado de outra thread com a captura rodando.
/// </summary>
public sealed class TextSource : ICaptureSource
{
    /// <summary>Velocidade da rolagem, em pixels do painel por segundo.</summary>
    public const double ScrollPixelsPerSecond = 12;

    private readonly Canvas _canvas = new(DisplayGeometry.Width, DisplayGeometry.Height);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private TextOptions _options;
    private TextLayout _layout;
    private int _paintedOffset = -1;

    public TextSource(TextOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _layout = TextLayout.Create(options);
    }

    public TextOptions Options { get { lock (_gate) return _options; } }

    /// <summary>Troca o texto; vale a partir da proxima captura.</summary>
    public void Update(TextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var layout = TextLayout.Create(options);
        lock (_gate)
        {
            _options = options;
            _layout = layout;
            _paintedOffset = -1;
        }
    }
    public string Name => "Texto";
    public int SourceWidth => _canvas.Width;
    public int SourceHeight => _canvas.Height;

    /// <summary>Tamanho efetivo da fonte (resolve o automatico).</summary>
    public int Scale { get { lock (_gate) return _layout.Scale; } }

    /// <summary>Linhas depois da quebra automatica.</summary>
    public IReadOnlyList<string> Lines { get { lock (_gate) return _layout.Lines; } }

    /// <summary>O texto nao cabe no painel e vai rolar.</summary>
    public bool Scrolls { get { lock (_gate) return _layout.Scrolls; } }

    /// <summary>Instante da rolagem. null = tempo real.</summary>
    public double? FixedTimeSeconds { get; set; }

    public bool TryCapture(out CapturedFrame frame)
    {
        lock (_gate) Render();
        frame = new CapturedFrame(_canvas.Buffer, _canvas.Width, _canvas.Height, _canvas.Stride);
        return true;
    }

    private void Render()
    {
        int offset = 0;
        if (_layout.Scrolls)
        {
            double t = FixedTimeSeconds ?? _clock.Elapsed.TotalSeconds;
            offset = (int)(t * ScrollPixelsPerSecond) % _layout.ScrollPeriod;
        }

        if (offset != _paintedOffset)
        {
            Paint(offset);
            _paintedOffset = offset;
        }
    }

    private void Paint(int offset)
    {
        Canvas c = _canvas;
        c.Clear(Canvas.Gray(0));

        if (_layout.Scrolls)
        {
            // Desenha o bloco duas vezes, separado por um respiro, para a volta ser continua.
            DrawBlock(-offset);
            DrawBlock(-offset + _layout.ScrollPeriod);
        }
        else
        {
            DrawBlock((c.Height - _layout.BlockHeight) / 2);
        }
    }

    private void DrawBlock(int top)
    {
        int scale = _layout.Scale;
        int lineHeight = TextLayout.LineHeight(scale);
        uint white = Canvas.Gray(255);

        for (int i = 0; i < _layout.Lines.Count; i++)
        {
            int y = top + i * lineHeight;
            if (y >= _canvas.Height || y + lineHeight <= 0) continue;

            string line = _layout.Lines[i];
            int width = Canvas.MeasureText(line, scale);
            int x = _options.Align switch
            {
                TextAlign.Left => 0,
                TextAlign.Right => _canvas.Width - width,
                _ => (_canvas.Width - width) / 2,
            };
            _canvas.DrawText(line, x, y, scale, white);
        }
    }

    public void Dispose() { }
}

/// <summary>Quebra de linhas e escolha do tamanho, separada do desenho para poder testar.</summary>
internal sealed class TextLayout
{
    private TextLayout(int scale, IReadOnlyList<string> lines)
    {
        Scale = scale;
        Lines = lines;
        BlockHeight = lines.Count == 0 ? 0 : lines.Count * LineHeight(scale) - scale;
        Scrolls = BlockHeight > DisplayGeometry.Height;
        ScrollPeriod = lines.Count * LineHeight(scale) + DisplayGeometry.Height / 2;
    }

    public int Scale { get; }
    public IReadOnlyList<string> Lines { get; }
    public int BlockHeight { get; }
    public bool Scrolls { get; }

    /// <summary>Distancia, em pixels, entre duas passagens do mesmo texto na rolagem.</summary>
    public int ScrollPeriod { get; }

    /// <summary>7 pixels de glifo + 1 de entrelinha, vezes a escala.</summary>
    public static int LineHeight(int scale) => (BitmapFont.GlyphHeight + 1) * scale;

    public static int CharsPerLine(int scale) => (DisplayGeometry.Width + scale) / (BitmapFont.Advance * scale);

    public static TextLayout Create(TextOptions options)
    {
        string text = options.Text ?? string.Empty;

        if (options.Scale > 0)
        {
            int fixedScale = Math.Clamp(options.Scale, 1, TextOptions.MaxScale);
            return new TextLayout(fixedScale, Wrap(text, CharsPerLine(fixedScale)));
        }

        // Automatico: o maior tamanho em que tudo cabe sem rolar.
        for (int scale = TextOptions.MaxScale; scale > 1; scale--)
        {
            var candidate = new TextLayout(scale, Wrap(text, CharsPerLine(scale)));
            if (!candidate.Scrolls) return candidate;
        }
        return new TextLayout(1, Wrap(text, CharsPerLine(1)));
    }

    /// <summary>Quebra por palavra; palavra maior que a linha e' cortada.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (string paragraph in normalized.Split('\n'))
        {
            string current = string.Empty;
            foreach (string rawWord in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string word = rawWord;
                while (word.Length > 0)
                {
                    if (current.Length == 0)
                    {
                        int take = Math.Min(width, word.Length);
                        current = word[..take];
                        word = word[take..];
                        if (word.Length > 0) { lines.Add(current); current = string.Empty; }
                    }
                    else if (current.Length + 1 + word.Length <= width)
                    {
                        current += " " + word;
                        word = string.Empty;
                    }
                    else
                    {
                        lines.Add(current);
                        current = string.Empty;
                    }
                }
            }
            lines.Add(current);
        }

        // Linhas vazias no fim so empurrariam o texto para cima.
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}