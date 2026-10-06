using OledMirror.Core.Imaging;

namespace OledMirror.Core.Capture;

/// <summary>
/// Uma fonte de imagem. <see cref="TryCapture"/> devolve false (sem excecao)
/// quando nao ha frame - janela minimizada, monitor removido - e o pipeline
/// apenas repete o ultimo.
/// </summary>
public interface ICaptureSource : IDisposable
{
    string Name { get; }
    int SourceWidth { get; }
    int SourceHeight { get; }

    bool TryCapture(out CapturedFrame frame);
}

public enum CaptureSourceKind
{
    Monitor,
    Window,
    Region,
    /// <summary>Desktop sintetico, sem captura real (teste e demonstracao).</summary>
    Synthetic,
}

/// <summary>Uma fonte que pode ser escolhida na interface.</summary>
public sealed record CaptureSourceDescriptor(CaptureSourceKind Kind, string Id, string Name, int Width, int Height)
{
    public override string ToString() => Width > 0 ? $"{Name} ({Width}x{Height})" : Name;
}

/// <summary>Enumera e abre fontes de captura da plataforma.</summary>
public interface ICaptureSourceProvider
{
    IReadOnlyList<CaptureSourceDescriptor> EnumerateMonitors();
    IReadOnlyList<CaptureSourceDescriptor> EnumerateWindows();
    ICaptureSource OpenMonitor(string id);
    ICaptureSource OpenWindow(string id);
    ICaptureSource OpenRegion(int x, int y, int width, int height);
}

public sealed class CaptureException : Exception
{
    public CaptureException(string message) : base(message) { }
    public CaptureException(string message, Exception inner) : base(message, inner) { }
}