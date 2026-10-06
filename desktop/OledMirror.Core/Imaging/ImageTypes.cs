namespace OledMirror.Core.Imaging;

/// <summary>
/// Um frame capturado em BGRA 32 bits, linha 0 em cima. O buffer pertence a
/// fonte de captura e e' reutilizado: vale ate a proxima captura.
/// </summary>
public readonly struct CapturedFrame
{
    public CapturedFrame(byte[] buffer, int width, int height, int stride)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public bool IsValid => Buffer is not null && Width > 0 && Height > 0 && Stride >= Width * 4 && Buffer.Length >= Stride * (Height - 1) + Width * 4;
}

/// <summary>Como encaixar a origem no painel 2:1.</summary>
public enum ResizeMode
{
    /// <summary>Preenche 128x64, distorcendo a proporcao.</summary>
    Stretch,
    /// <summary>Mantem a proporcao sem cortar nada; sobram barras.</summary>
    Fit,
    /// <summary>Preenche 128x64 cortando a origem para 2:1.</summary>
    Crop,
    /// <summary>Usa sempre a largura inteira: corta a origem na vertical ou adiciona barras em cima e embaixo.</summary>
    Letterbox,
}

public enum DitheringMode
{
    Threshold,
    FloydSteinberg,
    BayerOrdered4x4,
    BayerOrdered8x8,
    Atkinson,
}

/// <summary>Parametros do processamento de imagem. Lidos a cada frame: pode ser alterado com o pipeline rodando.</summary>
public sealed class ImageProcessorOptions
{
    public ResizeMode ResizeMode { get; set; } = ResizeMode.Fit;
    public DitheringMode Dithering { get; set; } = DitheringMode.FloydSteinberg;

    /// <summary>Limiar de binarizacao: acende quando o valor e' maior que isto.</summary>
    public int Threshold { get; set; } = 128;

    /// <summary>Estica o histograma entre os percentis 2 % e 98 % antes de binarizar.</summary>
    public bool AutoContrast { get; set; } = true;

    /// <summary>Ganho em torno do cinza medio (1 = sem mudanca).</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>Gamma (1 = sem mudanca, maior que 1 clareia os tons medios).</summary>
    public double Gamma { get; set; } = 1.0;

    public bool Invert { get; set; }

    /// <summary>Barras do modo Fit/Letterbox acesas em vez de apagadas.</summary>
    public bool PadWhite { get; set; }

    public ImageProcessorOptions Clone() => (ImageProcessorOptions)MemberwiseClone();
}

/// <summary>Captura BGRA -> frame empacotado de 1024 bytes.</summary>
public interface IImageProcessor
{
    void Process(CapturedFrame source, Span<byte> frame);
}