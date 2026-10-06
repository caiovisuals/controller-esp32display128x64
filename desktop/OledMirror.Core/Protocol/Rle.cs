namespace OledMirror.Core.Protocol;

/// <summary>
/// RLE orientado a byte, identico ao do firmware (firmware/src/framebuffer/frame_codec.cpp):
/// <code>
/// C em [0x00..0x7F] -> literal: os proximos (C + 1) bytes      (1..128)
/// C em [0x80..0xFF] -> repeticao: o proximo byte, (C-0x80)+2x  (2..129)
/// </code>
/// </summary>
public static class Rle
{
    public const int MaxLiteral = 128;
    public const int MaxRepeat = 129;

    /// <summary>
    /// Corridas mais curtas que isto ficam no literal. Com 3 ou mais a repeticao
    /// sempre economiza, e e' isso que garante que o pior caso nunca passe do
    /// tamanho de um fluxo so de literais.
    /// </summary>
    private const int MinRepeat = 3;

    /// <summary>Pior caso: tudo literal, um byte de controle a cada 128.</summary>
    public static int MaxEncodedSize(int inputLength) => inputLength + (inputLength + MaxLiteral - 1) / MaxLiteral;

    /// <summary>Codifica. Devolve o numero de bytes escritos, ou -1 se o destino nao couber.</summary>
    public static int Encode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int i = 0;
        int o = 0;
        int literalStart = 0;

        while (i < input.Length)
        {
            int run = 1;
            while (i + run < input.Length && run < MaxRepeat && input[i + run] == input[i]) run++;

            if (run >= MinRepeat)
            {
                if (!FlushLiteral(input, literalStart, i, output, ref o)) return -1;
                if (o + 2 > output.Length) return -1;
                output[o++] = (byte)(0x80 | (run - 2));
                output[o++] = input[i];
                i += run;
                literalStart = i;
            }
            else
            {
                i += run;
            }
        }

        if (!FlushLiteral(input, literalStart, input.Length, output, ref o)) return -1;
        return o;
    }

    private static bool FlushLiteral(ReadOnlySpan<byte> input, int start, int end, Span<byte> output, ref int o)
    {
        while (start < end)
        {
            int n = Math.Min(MaxLiteral, end - start);
            if (o + 1 + n > output.Length) return false;
            output[o++] = (byte)(n - 1);
            input.Slice(start, n).CopyTo(output[o..]);
            o += n;
            start += n;
        }
        return true;
    }

    /// <summary>
    /// Decodifica. Devolve o numero de bytes escritos, ou -1 se o fluxo estiver
    /// truncado ou tentar escrever alem do destino. Nenhuma escrita depende de o
    /// remetente estar bem comportado.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int i = 0;
        int o = 0;

        while (i < input.Length)
        {
            byte control = input[i++];

            if ((control & 0x80) == 0)
            {
                int n = control + 1;
                if (i + n > input.Length) return -1;
                if (o + n > output.Length) return -1;
                input.Slice(i, n).CopyTo(output[o..]);
                i += n;
                o += n;
            }
            else
            {
                int n = (control & 0x7F) + 2;
                if (i >= input.Length) return -1;
                if (o + n > output.Length) return -1;
                output.Slice(o, n).Fill(input[i]);
                i++;
                o += n;
            }
        }

        return o;
    }
}