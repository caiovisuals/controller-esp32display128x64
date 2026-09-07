using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Imaging;

/// <summary>
/// O caminho quente roda ate 30 vezes por segundo. Alocar por frame significa
/// coleta de lixo no meio do streaming, o que aparece como tremor no FPS. Estes
/// testes travam essa propriedade.
/// </summary>
public class AllocationTests {
    [Fact]
    public void FrameProcessor_DoesNotAllocatePerFrame()
    {
        var source = new TestPatternSource(TestPattern.OrbitingCircle, 1920, 1080);
        var processor = new FrameProcessor(new ImageProcessorOptions { Dithering = DitheringMode.FloydSteinberg });
        var frame = new byte[DisplayGeometry.FrameBytes];

        source.TryCapture(out CapturedFrame captured);
        for (int i = 0; i < 5; i++) processor.Process(captured, frame);   // aquece

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) processor.Process(captured, frame);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4096, $"Processar 50 frames alocou {allocated} bytes.");
    }

    [Fact]
    public void FrameEncoder_DoesNotAllocatePerFrame()
    {
        var encoder = new FrameEncoder { SkipUnchangedFrames = false };
        var frame = new byte[DisplayGeometry.FrameBytes];
        var rng = new Random(1);

        for (int i = 0; i < 5; i++) { rng.NextBytes(frame); encoder.TryEncode(frame, out _, out _); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            frame[i * 7 % frame.Length] ^= 0xFF;
            encoder.TryEncode(frame, out _, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4096, $"Codificar 50 frames alocou {allocated} bytes.");
    }

    [Fact]
    public void PacketEncoder_DoesNotAllocateWhenGivenABuffer()
    {
        var buffer = new byte[ProtocolConstants.MaxPacketSize];
        var payload = new byte[DisplayGeometry.FrameBytes];

        PacketEncoder.Encode(buffer, CommandId.FrameRaw, 0, payload);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) PacketEncoder.Encode(buffer, CommandId.FrameRaw, (byte)i, payload);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 512, $"Codificar 100 pacotes alocou {allocated} bytes.");
    }
}