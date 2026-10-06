using OledMirror.Core.Imaging;

namespace OledMirror.Core.Protocol;

public enum FrameDecodeResult
{
    Ok,
    BadLength,
    BadRect,
    BadRle,
    NotAFrame,
}

/// <summary>
/// Lado do dispositivo: aplica um payload de frame sobre um framebuffer.
/// Mesmo algoritmo e mesmas validacoes do ApplyFramePayload do firmware. Usado
/// pelo dispositivo simulado e pelos testes de ida e volta.
/// </summary>
public sealed class FrameDecoder
{
    private readonly byte[] _scratch = new byte[DisplayGeometry.FrameBytes];

    public FrameDecodeResult Apply(CommandId command, ReadOnlySpan<byte> payload, Span<byte> framebuffer)
    {
        if (framebuffer.Length < DisplayGeometry.FrameBytes)
            throw new ArgumentException("Framebuffer menor que um frame.", nameof(framebuffer));

        switch (command)
        {
            case CommandId.FrameRaw:
                if (payload.Length != DisplayGeometry.FrameBytes) return FrameDecodeResult.BadLength;
                payload.CopyTo(framebuffer);
                return FrameDecodeResult.Ok;

            case CommandId.FrameRle:
            {
                int n = Rle.Decode(payload, _scratch);
                if (n < 0) return FrameDecodeResult.BadRle;
                if (n != DisplayGeometry.FrameBytes) return FrameDecodeResult.BadLength;
                _scratch.CopyTo(framebuffer);
                return FrameDecodeResult.Ok;
            }

            case CommandId.FrameDelta:
            case CommandId.FrameDeltaRle:
            {
                if (payload.Length < 4) return FrameDecodeResult.BadLength;

                int x0 = payload[0], x1 = payload[1], p0 = payload[2], p1 = payload[3];

                // Limites checados antes de qualquer escrita.
                if (x1 < x0 || p1 < p0) return FrameDecodeResult.BadRect;
                if (x1 >= DisplayGeometry.Width || p1 >= DisplayGeometry.Pages) return FrameDecodeResult.BadRect;

                int columns = x1 - x0 + 1;
                int pages = p1 - p0 + 1;
                int expected = columns * pages;

                ReadOnlySpan<byte> region;
                if (command == CommandId.FrameDelta)
                {
                    if (payload.Length - 4 != expected) return FrameDecodeResult.BadLength;
                    region = payload[4..];
                }
                else
                {
                    int n = Rle.Decode(payload[4..], _scratch);
                    if (n < 0) return FrameDecodeResult.BadRle;
                    if (n != expected) return FrameDecodeResult.BadLength;
                    region = _scratch.AsSpan(0, n);
                }

                for (int p = p0; p <= p1; p++)
                    region.Slice((p - p0) * columns, columns).CopyTo(framebuffer[(p * DisplayGeometry.Width + x0)..]);

                return FrameDecodeResult.Ok;
            }

            default:
                return FrameDecodeResult.NotAFrame;
        }
    }

    public static NackReason NackReasonFor(FrameDecodeResult result) => result switch
    {
        FrameDecodeResult.BadLength => NackReason.BadLength,
        FrameDecodeResult.BadRect => NackReason.BadPayload,
        FrameDecodeResult.BadRle => NackReason.BadPayload,
        FrameDecodeResult.NotAFrame => NackReason.UnknownCommand,
        _ => NackReason.None,
    };
}