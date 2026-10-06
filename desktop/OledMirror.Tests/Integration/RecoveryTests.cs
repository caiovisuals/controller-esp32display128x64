using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;
using OledMirror.Core.Simulation;

namespace OledMirror.Tests.Integration;

/// <summary>
/// Casos em que o painel deixa de mostrar o que o host acha que ele mostra. O
/// sistema tem que perceber e se corrigir sozinho, sem o usuario reiniciar nada.
/// </summary>
public class RecoveryTests {
    [Fact]
    public void DeviceReset_WithAStaticScreen_PanelRecoversByItself()
    {
        // O ESP32 reinicia (botao EN, queda de energia) com a aplicacao conectada:
        // o link continua respondendo, mas o painel volta para a tela de espera.
        // Com a tela do PC parada nenhum frame novo seria enviado - e' o reenvio
        // periodico de um frame completo que corrige.
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        using var pipeline = new MirrorPipeline(rig.Link)
        {
            Settings = new MirrorSettings
            {
                TargetFps = 30,
                KeyframeIntervalMs = 300,
                Image = new ImageProcessorOptions { Dithering = DitheringMode.Threshold, AutoContrast = false },
            },
        };
        pipeline.SetSource(new TestPatternSource(TestPattern.Checkerboard, 640, 320));
        pipeline.Start();

        SimulatedDevice device = rig.CurrentDevice!;
        var hostFrame = new byte[DisplayGeometry.FrameBytes];
        Assert.True(SimulatedRig.WaitFor(() =>
            pipeline.TryCopyLatestFrame(hostFrame) && device.SnapshotFramebuffer().AsSpan().SequenceEqual(hostFrame)));

        device.SimulateReset();
        Assert.False(device.SnapshotFramebuffer().AsSpan().SequenceEqual(hostFrame));

        Assert.True(SimulatedRig.WaitFor(() => device.SnapshotFramebuffer().AsSpan().SequenceEqual(hostFrame), 3000),
                    "O painel nao se recuperou depois do reset do dispositivo.");
        pipeline.Stop();
    }

    [Fact]
    public void StaticScreen_KeyframesAreRare()
    {
        // O reenvio periodico nao pode virar trafego continuo.
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        using var pipeline = new MirrorPipeline(rig.Link)
        {
            Settings = new MirrorSettings { TargetFps = 30, KeyframeIntervalMs = 400 },
        };
        pipeline.SetSource(new TestPatternSource(TestPattern.Text, 1920, 1080));
        pipeline.Start();
        Thread.Sleep(1500);
        pipeline.Stop();

        long sent = pipeline.Statistics.FramesSent;
        Assert.InRange(sent, 2, 8);
        Assert.True(pipeline.Statistics.FramesSkippedUnchanged > sent * 3);
    }

    [Fact]
    public void RejectedFrame_IsReportedAsLost_AndFreesTheWindow()
    {
        // Um dispositivo sem suporte a delta recusa o frame com NACK. O host tem
        // que liberar o slot na hora (sem esperar o timeout) e avisar que o
        // painel nao recebeu aquele conteudo.
        using var rig = new SimulatedRig(deviceOptions: new SimulatedDeviceOptions { Capabilities = DeviceCapabilities.None });
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        int lost = 0;
        rig.Link.FrameLost += () => Interlocked.Increment(ref lost);

        byte[] delta = { 0, 0, 0, 0, 0xFF };
        Assert.True(rig.Link.TrySendFrame(CommandId.FrameDelta, delta));

        Assert.True(SimulatedRig.WaitFor(() => Volatile.Read(ref lost) == 1, 500), "O NACK nao foi tratado como frame perdido.");
        Assert.True(SimulatedRig.WaitFor(() => rig.Link.FramesInFlight == 0, 500));
        Assert.Equal(1, rig.Link.Statistics.Nacks);
        Assert.Equal(0, rig.Link.Statistics.FrameAckTimeouts);
    }

    [Fact]
    public void LostFrameAck_ExpiresAndFreesTheWindow()
    {
        // Um FRAME_ACK que nunca chega (ruido no caminho de volta) nao pode travar
        // a janela para sempre - e o link continua de pe, porque os PINGs seguem
        // sendo respondidos.
        using var rig = new SimulatedRig(
            linkOptions: new Core.Devices.DeviceLinkOptions
            {
                FrameAckTimeoutMs = 200,
                HandshakeTimeoutMs = 1000,
                ReconnectDelayMs = 50,
                KeepAliveIntervalMs = 250,
                InactivityTimeoutMs = 2000,
            },
            deviceOptions: new SimulatedDeviceOptions { SuppressFrameAcks = true });
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        int lost = 0;
        rig.Link.FrameLost += () => Interlocked.Increment(ref lost);

        Assert.True(rig.Link.TrySendFrame(CommandId.FrameRaw, new byte[DisplayGeometry.FrameBytes]));
        Assert.Equal(1, rig.Link.FramesInFlight);

        Assert.True(SimulatedRig.WaitFor(() => rig.Link.FramesInFlight == 0, 2000), "O slot do frame sem ACK nunca foi liberado.");
        // O evento e' disparado logo depois de o slot ser liberado, fora do lock.
        Assert.True(SimulatedRig.WaitFor(() => Volatile.Read(ref lost) == 1, 500));
        Assert.Equal(1, rig.Link.Statistics.FrameAckTimeouts);
        Assert.Equal(Core.Devices.LinkState.Connected, rig.Link.State);
        Assert.Equal(1u, rig.CurrentDevice!.FramesApplied);
    }
}