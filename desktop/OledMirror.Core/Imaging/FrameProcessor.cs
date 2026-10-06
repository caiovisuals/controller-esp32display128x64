namespace OledMirror.Core.Imaging;

/// <summary>
/// Captura BGRA -> resize + cinza -> tone map -> dither -> pack, sem alocar por frame.
/// As opcoes sao lidas a cada chamada: alterar o objeto de opcoes (ou trocar
/// por outro) vale a partir do proximo frame.
/// </summary>
public sealed class FrameProcessor : IImageProcessor
{
    private readonly byte[] _gray = new byte[DisplayGeometry.PixelCount];
    private readonly Rescaler _rescaler = new();
    private readonly ToneMapper _toneMapper = new();
    private readonly Ditherer _ditherer = new();

    public FrameProcessor(ImageProcessorOptions? options = null)
    {
        Options = options ?? new ImageProcessorOptions();
    }

    public ImageProcessorOptions Options { get; set; }

    public void Process(CapturedFrame source, Span<byte> frame)
    {
        ImageProcessorOptions options = Options;
        var content = _rescaler.ResizeToGray(source, _gray, options);
        _toneMapper.Apply(_gray, options, content);
        _ditherer.DitherAndPack(_gray, frame, options);
    }
}