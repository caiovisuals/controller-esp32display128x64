namespace OledMirror.Core.Imaging;

/// <summary>
/// Reduz a origem BGRA para 128x64 em tons de cinza, numa passada so.
///
/// Usa media de area (box filter), que e' o filtro correto para reducoes
/// grandes. Para nao ler milhoes de pixels por frame em 4K, cada caixa e'
/// amostrada em no maximo 8 x 8 pontos: o custo nao depende da resolucao.
/// </summary>
public sealed class Rescaler
{
    private const int MaxSamplesPerAxis = 8;

    private readonly int[] _x0 = new int[DisplayGeometry.Width];
    private readonly int[] _x1 = new int[DisplayGeometry.Width];

    /// <summary>
    /// Calcula o retangulo de origem que sera usado e onde ele cai no painel.
    /// </summary>
    public static void ComputeRects(int sourceWidth, int sourceHeight, ResizeMode mode,
                                    out int sx, out int sy, out int sw, out int sh,
                                    out int dx, out int dy, out int dw, out int dh)
    {
        const int W = DisplayGeometry.Width;
        const int H = DisplayGeometry.Height;
        const double PanelAspect = (double)W / H;

        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);
        double aspect = (double)sourceWidth / sourceHeight;

        sx = 0; sy = 0; sw = sourceWidth; sh = sourceHeight;
        dx = 0; dy = 0; dw = W; dh = H;

        switch (mode)
        {
            case ResizeMode.Fit:
            {
                double scale = Math.Min((double)W / sourceWidth, (double)H / sourceHeight);
                dw = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, W);
                dh = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, H);
                dx = (W - dw) / 2;
                dy = (H - dh) / 2;
                break;
            }

            case ResizeMode.Crop:
                CropToPanelAspect(sourceWidth, sourceHeight, aspect, PanelAspect, out sx, out sy, out sw, out sh);
                break;

            case ResizeMode.Letterbox:
                if (aspect > PanelAspect)
                {
                    // Mais largo que o painel: largura cheia, barras em cima e embaixo.
                    dh = Math.Clamp((int)Math.Round(W / aspect), 1, H);
                    dy = (H - dh) / 2;
                }
                else
                {
                    // Mais alto: largura cheia, corta em cima e embaixo.
                    CropToPanelAspect(sourceWidth, sourceHeight, aspect, PanelAspect, out sx, out sy, out sw, out sh);
                }
                break;
        }
    }

    private static void CropToPanelAspect(int w, int h, double aspect, double panelAspect,
                                          out int sx, out int sy, out int sw, out int sh)
    {
        if (aspect > panelAspect)
        {
            sh = h;
            sw = Math.Clamp((int)Math.Round(h * panelAspect), 1, w);
            sx = (w - sw) / 2;
            sy = 0;
        }
        else
        {
            sw = w;
            sh = Math.Clamp((int)Math.Round(w / panelAspect), 1, h);
            sx = 0;
            sy = (h - sh) / 2;
        }
    }

    /// <summary>
    /// Escreve 128x64 bytes de luminancia em <paramref name="gray"/> (linha a linha)
    /// e devolve o retangulo de destino com conteudo (fora dele sao barras).
    /// </summary>
    public (int X, int Y, int Width, int Height) ResizeToGray(CapturedFrame source, Span<byte> gray, ImageProcessorOptions options)
    {
        if (gray.Length < DisplayGeometry.PixelCount)
            throw new ArgumentException("Destino menor que 128x64.", nameof(gray));

        byte pad = options.PadWhite ? (byte)255 : (byte)0;
        gray[..DisplayGeometry.PixelCount].Fill(pad);
        if (!source.IsValid) return (0, 0, 0, 0);

        ComputeRects(source.Width, source.Height, options.ResizeMode,
                     out int sx, out int sy, out int sw, out int sh,
                     out int dx, out int dy, out int dw, out int dh);

        for (int i = 0; i < dw; i++)
        {
            int a = sx + (int)((long)i * sw / dw);
            int b = sx + (int)((long)(i + 1) * sw / dw);
            _x0[i] = a;
            _x1[i] = Math.Max(b, a + 1);
        }

        byte[] buffer = source.Buffer;
        int stride = source.Stride;

        for (int j = 0; j < dh; j++)
        {
            int y0 = sy + (int)((long)j * sh / dh);
            int y1 = Math.Max(sy + (int)((long)(j + 1) * sh / dh), y0 + 1);
            int ny = Math.Min(MaxSamplesPerAxis, y1 - y0);

            Span<byte> row = gray.Slice((dy + j) * DisplayGeometry.Width + dx, dw);

            for (int i = 0; i < dw; i++)
            {
                int x0 = _x0[i];
                int spanX = _x1[i] - x0;
                int nx = Math.Min(MaxSamplesPerAxis, spanX);

                int sum = 0;
                for (int ky = 0; ky < ny; ky++)
                {
                    int y = y0 + (2 * ky + 1) * (y1 - y0) / (2 * ny);
                    int line = y * stride;
                    for (int kx = 0; kx < nx; kx++)
                    {
                        int x = x0 + (2 * kx + 1) * spanX / (2 * nx);
                        int p = line + x * 4;
                        // Luminancia BT.601 em ponto fixo: B, G, R.
                        sum += (29 * buffer[p] + 150 * buffer[p + 1] + 77 * buffer[p + 2] + 128) >> 8;
                    }
                }

                int count = nx * ny;
                row[i] = (byte)((sum + count / 2) / count);
            }
        }

        return (dx, dy, dw, dh);
    }
}