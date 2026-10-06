namespace OledMirror.Core.Imaging;

/// <summary>
/// Binariza 128x64 tons de cinza e empacota direto no layout do painel
/// (1024 bytes). Buffers internos reutilizados: nao aloca por frame.
/// </summary>
public sealed class Ditherer
{
    private const int W = DisplayGeometry.Width;
    private const int H = DisplayGeometry.Height;

    private static readonly byte[,] Bayer4 =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    private static readonly byte[,] Bayer8 = BuildBayer8();

    // Erro acumulado em 1/16 de nivel (Floyd-Steinberg) ou 1/8 (Atkinson), com
    // margem de 2 colunas de cada lado para nao precisar testar bordas.
    private readonly int[] _error = new int[(W + 4) * 3];

    public void DitherAndPack(ReadOnlySpan<byte> gray, Span<byte> frame, ImageProcessorOptions options)
    {
        if (gray.Length < DisplayGeometry.PixelCount) throw new ArgumentException("Origem menor que 128x64.", nameof(gray));
        if (frame.Length < DisplayGeometry.FrameBytes) throw new ArgumentException("Destino menor que 1024 bytes.", nameof(frame));

        frame[..DisplayGeometry.FrameBytes].Clear();
        int threshold = Math.Clamp(options.Threshold, 0, 255);

        switch (options.Dithering)
        {
            case DitheringMode.FloydSteinberg: ErrorDiffusion(gray, frame, threshold, atkinson: false); break;
            case DitheringMode.Atkinson: ErrorDiffusion(gray, frame, threshold, atkinson: true); break;
            case DitheringMode.BayerOrdered4x4: Ordered(gray, frame, threshold, Bayer4, 4); break;
            case DitheringMode.BayerOrdered8x8: Ordered(gray, frame, threshold, Bayer8, 8); break;
            default: Threshold(gray, frame, threshold); break;
        }

        if (options.Invert)
            for (int i = 0; i < DisplayGeometry.FrameBytes; i++) frame[i] = (byte)~frame[i];
    }

    private static void Threshold(ReadOnlySpan<byte> gray, Span<byte> frame, int threshold)
    {
        for (int y = 0; y < H; y++)
        {
            ReadOnlySpan<byte> row = gray.Slice(y * W, W);
            int page = (y >> 3) * W;
            byte mask = DisplayGeometry.BitMask(y);
            for (int x = 0; x < W; x++)
                if (row[x] > threshold) frame[page + x] |= mask;
        }
    }

    private static void Ordered(ReadOnlySpan<byte> gray, Span<byte> frame, int threshold, byte[,] matrix, int n)
    {
        int levels = n * n;
        int shift = threshold - 128;

        for (int y = 0; y < H; y++)
        {
            ReadOnlySpan<byte> row = gray.Slice(y * W, W);
            int page = (y >> 3) * W;
            byte mask = DisplayGeometry.BitMask(y);
            int my = y % n;
            for (int x = 0; x < W; x++)
            {
                // Limiar no centro de cada celula da matriz, deslocado pelo limiar do usuario.
                // Fica sempre entre 0 e 254: preto puro nunca acende, branco puro sempre acende.
                int t = (matrix[my, x % n] * 2 + 1) * 256 / (2 * levels) + shift;
                t = Math.Clamp(t, 0, 254);
                if (row[x] > t) frame[page + x] |= mask;
            }
        }
    }

    private void ErrorDiffusion(ReadOnlySpan<byte> gray, Span<byte> frame, int threshold, bool atkinson)
    {
        const int Stride = W + 4;
        Span<int> err = _error;
        err.Clear();

        for (int y = 0; y < H; y++)
        {
            // Tres linhas circulares: atual, proxima e a seguinte (Atkinson alcanca y+2).
            Span<int> cur = err.Slice((y % 3) * Stride, Stride);
            Span<int> next = err.Slice(((y + 1) % 3) * Stride, Stride);
            Span<int> next2 = err.Slice(((y + 2) % 3) * Stride, Stride);

            ReadOnlySpan<byte> row = gray.Slice(y * W, W);
            int page = (y >> 3) * W;
            byte mask = DisplayGeometry.BitMask(y);

            for (int x = 0; x < W; x++)
            {
                int c = x + 2;
                byte original = row[x];
                int value = original + (atkinson ? cur[c] >> 3 : cur[c] >> 4);
                cur[c] = 0;

                bool on;
                int e;
                if (original == 0) { on = false; e = 0; }        // extremos nunca viram textura
                else if (original == 255) { on = true; e = 0; }
                else
                {
                    on = value > threshold;
                    e = value - (on ? 255 : 0);
                }

                if (on) frame[page + x] |= mask;
                if (e == 0) continue;

                if (atkinson)
                {
                    // 6/8 do erro, 1/8 para cada vizinho.
                    cur[c + 1] += e;
                    cur[c + 2] += e;
                    next[c - 1] += e;
                    next[c] += e;
                    next[c + 1] += e;
                    next2[c] += e;
                }
                else
                {
                    cur[c + 1] += e * 7;
                    next[c - 1] += e * 3;
                    next[c] += e * 5;
                    next[c + 1] += e;
                }
            }
        }
    }

    private static byte[,] BuildBayer8()
    {
        var m = new byte[8, 8];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                m[y, x] = (byte)(4 * Bayer4[y % 4, x % 4] + Bayer2(y / 4, x / 4));
        return m;

        static int Bayer2(int y, int x) => (y, x) switch { (0, 0) => 0, (0, 1) => 2, (1, 0) => 3, _ => 1 };
    }
}