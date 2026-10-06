using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace OledMirror.Core.Imaging;

/// <summary>Utilitarios sobre frames empacotados de 1024 bytes.</summary>
public static class MonoFrameUtils
{
    public static int CountLitPixels(ReadOnlySpan<byte> frame)
    {
        int count = 0;
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(frame);
        foreach (ulong w in words) count += BitOperations.PopCount(w);
        for (int i = words.Length * 8; i < frame.Length; i++) count += BitOperations.PopCount(frame[i]);
        return count;
    }

    /// <summary>Desempacota para 1 byte por pixel, linha a linha: 255 aceso, 0 apagado.</summary>
    public static void Unpack(ReadOnlySpan<byte> frame, Span<byte> gray)
    {
        if (gray.Length < DisplayGeometry.PixelCount)
            throw new ArgumentException("Destino menor que 128x64.", nameof(gray));

        for (int y = 0; y < DisplayGeometry.Height; y++)
        {
            int row = (y >> 3) * DisplayGeometry.Width;
            byte mask = DisplayGeometry.BitMask(y);
            Span<byte> line = gray.Slice(y * DisplayGeometry.Width, DisplayGeometry.Width);
            for (int x = 0; x < DisplayGeometry.Width; x++)
                line[x] = (frame[row + x] & mask) != 0 ? (byte)255 : (byte)0;
        }
    }

    /// <summary>
    /// Desenha o frame em texto com meios-blocos Unicode: cada caractere cobre
    /// 2 linhas de pixel, entao o painel inteiro cabe em 128 x 32 caracteres.
    /// </summary>
    public static string ToHalfBlocks(ReadOnlySpan<byte> frame)
    {
        var sb = new StringBuilder((DisplayGeometry.Width + 1) * DisplayGeometry.Height / 2);
        for (int y = 0; y < DisplayGeometry.Height; y += 2)
        {
            for (int x = 0; x < DisplayGeometry.Width; x++)
            {
                bool top = DisplayGeometry.GetPixel(frame, x, y);
                bool bottom = DisplayGeometry.GetPixel(frame, x, y + 1);
                sb.Append(top ? (bottom ? '█' : '▀') : (bottom ? '▄' : ' '));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}