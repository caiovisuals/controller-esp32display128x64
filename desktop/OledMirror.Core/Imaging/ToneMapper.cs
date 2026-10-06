namespace OledMirror.Core.Imaging;

/// <summary>Auto-contraste, contraste e gamma, aplicados por uma tabela de 256 entradas.</summary>
public sealed class ToneMapper
{
    private const double LowPercentile = 0.02;
    private const double HighPercentile = 0.98;

    private readonly int[] _histogram = new int[256];
    private readonly byte[] _lut = new byte[256];

    /// <summary>
    /// Aplica em <paramref name="gray"/> (128x64, linha a linha). O auto-contraste
    /// so mede o retangulo de conteudo, para as barras pretas do modo Fit nao
    /// contarem como "o escuro da imagem".
    /// </summary>
    public void Apply(Span<byte> gray, ImageProcessorOptions options, (int X, int Y, int Width, int Height) content)
    {
        bool identity = !options.AutoContrast && Math.Abs(options.Contrast - 1.0) < 1e-6 && Math.Abs(options.Gamma - 1.0) < 1e-6;
        if (identity || content.Width <= 0 || content.Height <= 0) return;

        int low = 0, high = 255;
        if (options.AutoContrast) MeasureRange(gray, content, out low, out high);

        double contrast = Math.Max(0.0, options.Contrast);
        double invGamma = 1.0 / Math.Clamp(options.Gamma, 0.05, 20.0);
        double range = high - low;

        for (int v = 0; v < 256; v++)
        {
            double x = range > 0 ? Math.Clamp((v - low) / range, 0.0, 1.0) : v / 255.0;
            x = (x - 0.5) * contrast + 0.5;
            x = Math.Clamp(x, 0.0, 1.0);
            x = Math.Pow(x, invGamma);
            _lut[v] = (byte)Math.Round(x * 255.0);
        }

        for (int y = content.Y; y < content.Y + content.Height; y++)
        {
            Span<byte> row = gray.Slice(y * DisplayGeometry.Width + content.X, content.Width);
            for (int i = 0; i < row.Length; i++) row[i] = _lut[row[i]];
        }
    }

    private void MeasureRange(ReadOnlySpan<byte> gray, (int X, int Y, int Width, int Height) content, out int low, out int high)
    {
        Array.Clear(_histogram);
        for (int y = content.Y; y < content.Y + content.Height; y++)
        {
            ReadOnlySpan<byte> row = gray.Slice(y * DisplayGeometry.Width + content.X, content.Width);
            foreach (byte v in row) _histogram[v]++;
        }

        int total = content.Width * content.Height;
        int lowCount = (int)(total * LowPercentile);
        int highCount = (int)(total * HighPercentile);

        low = 0;
        int acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += _histogram[v];
            if (acc > lowCount) { low = v; break; }
        }

        high = 255;
        acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += _histogram[v];
            if (acc >= highCount) { high = v; break; }
        }

        // Imagem praticamente lisa: esticar so amplificaria ruido.
        if (high - low < 4)
        {
            low = 0;
            high = 255;
        }
    }
}