using System.Runtime.InteropServices;
using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;

namespace OledMirror.Windows.Capture;

/// <summary>
/// Superficie GDI reutilizavel: um DC de memoria com uma DIB section de 32 bits
/// top-down, que e' exatamente o layout BGRA que o pipeline consome.
///
/// Existe para que a captura nao aloque nada por frame: o bitmap so e' recriado
/// quando a resolucao da origem muda (monitor trocado de modo, janela
/// redimensionada). O <see cref="Buffer"/> e' preenchido diretamente pelo BitBlt.
/// </summary>
internal sealed class GdiSurface : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Buffer gerenciado, preenchido a partir da DIB depois de cada captura.</summary>
    public byte[] Buffer { get; private set; } = Array.Empty<byte>();

    public IntPtr MemoryDc => _memoryDc;
    public int Stride => Width * 4;

    /// <summary>Garante uma superficie do tamanho pedido. Recria so se necessario.</summary>
    public bool Ensure(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (_memoryDc != IntPtr.Zero && Width == width && Height == height) return true;

        Release();

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            _memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero) return false;

            var info = new NativeMethods.BITMAPINFO();
            info.bmiHeader.biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = width;
            // Altura negativa = bitmap top-down: a linha 0 e' a de cima, que e' o
            // que o pipeline espera. Com altura positiva a imagem sai invertida.
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = NativeMethods.BI_RGB;

            _bitmap = NativeMethods.CreateDIBSection(_memoryDc, ref info, NativeMethods.DIB_RGB_COLORS,
                                                     out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero) { Release(); return false; }

            _previousBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);

            Width = width;
            Height = height;
            Buffer = new byte[Stride * height];
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Copia os pixels da DIB para o buffer gerenciado.</summary>
    public void CopyToBuffer()
    {
        if (_bits == IntPtr.Zero || Buffer.Length == 0) return;
        Marshal.Copy(_bits, Buffer, 0, Buffer.Length);
    }

    public CapturedFrame ToFrame() => new(Buffer, Width, Height, Stride);

    private void Release()
    {
        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
            NativeMethods.SelectObject(_memoryDc, _previousBitmap);

        if (_bitmap != IntPtr.Zero) { NativeMethods.DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        if (_memoryDc != IntPtr.Zero) { NativeMethods.DeleteDC(_memoryDc); _memoryDc = IntPtr.Zero; }

        _previousBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero;
        Width = Height = 0;
        Buffer = Array.Empty<byte>();
    }

    public void Dispose() => Release();
}