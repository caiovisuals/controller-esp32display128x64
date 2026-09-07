using OledMirror.Core.Capture;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;
using OledMirror.Core.Simulation;

namespace OledMirror.Tests.Integration;

public class EndToEndTests {
    [Fact]
    public void Handshake_IdentifiesTheDevice()
    {
        using var rig = new SimulatedRig();
        rig.Link.Start();

        Assert.True(rig.WaitForConnected(), $"Nao conectou; estado = {rig.Link.State}");

        DeviceHello? device = rig.Link.Device;
        Assert.NotNull(device);
        Assert.Equal(ProtocolConstants.Version, device!.ProtocolVersion);
        Assert.Equal(128, device.DisplayWidth);
        Assert.Equal(64, device.DisplayHeight);
        Assert.Equal(ControllerId.Ssd1306, device.Controller);
        Assert.True(device.Capabilities.HasFlag(DeviceCapabilities.Rle));
        Assert.True(device.Capabilities.HasFlag(DeviceCapabilities.Delta));
    }

    [Fact]
    public void FramesSentByTheHost_LandExactlyOnTheDevicePanel()
    {
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        var encoder = new FrameEncoder(rig.Link.Device!.Capabilities) { SkipUnchangedFrames = false };
        var rng = new Random(77);
        byte[] lastFrame = Array.Empty<byte>();

        for (int i = 0; i < 20; i++)
        {
            var frame = new byte[DisplayGeometry.FrameBytes];
            if (i % 3 == 0) rng.NextBytes(frame);                     // frame cheio de ruido
            else { frame = (byte[])lastFrame.Clone(); frame[i * 13 % frame.Length] ^= 0xFF; }  // mudanca pequena

            Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));

            Assert.True(SimulatedRig.WaitFor(() => rig.Link.TrySendFrame(cmd, payload.Span), 2000),
                        "A janela de controle de fluxo nunca abriu.");
            lastFrame = frame;
        }

        SimulatedDevice device = rig.CurrentDevice!;
        Assert.True(SimulatedRig.WaitFor(() => device.FramesApplied == 20), $"Aplicou {device.FramesApplied}/20.");
        Assert.Equal(lastFrame, device.SnapshotFramebuffer());
        Assert.Equal(0u, device.FramesDropped);
    }

    [Fact]
    public void FlowControl_NeverExceedsTheAdvertisedWindow()
    {
        // O dispositivo anuncia fila 2; o host nao pode ter mais que isso em voo.
        using var rig = new SimulatedRig(
            linkOptions: new DeviceLinkOptions { MaxFramesInFlight = 8, HandshakeTimeoutMs = 1000, ReconnectDelayMs = 50 },
            deviceOptions: new SimulatedDeviceOptions { RxQueueDepth = 2 });
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        Assert.Equal(2, rig.Link.EffectiveWindow);

        var payload = new byte[DisplayGeometry.FrameBytes];
        int accepted = 0;
        for (int i = 0; i < 20; i++) if (rig.Link.TrySendFrame(CommandId.FrameRaw, payload)) accepted++;

        // Sem esperar por ACK nenhum, no maximo a janela inteira e' aceita de imediato.
        Assert.True(accepted <= 2 + 1, $"Aceitou {accepted} frames de uma vez com janela 2.");
        Assert.True(rig.Link.Statistics.FramesDroppedByFlowControl > 0);
    }

    [Fact]
    public void NoisyLink_NeverCorruptsThePanel()
    {
        // 1 bit trocado a cada ~20 000: pacotes se perdem, mas nada errado pode
        // ser aplicado no painel, e o link tem que continuar funcionando.
        using var rig = new SimulatedRig(corruptionRate: 0.00005);
        rig.Link.Start();
        Assert.True(rig.WaitForConnected(6000));

        var encoder = new FrameEncoder(DeviceCapabilities.None) { SkipUnchangedFrames = false };
        var frame = new byte[DisplayGeometry.FrameBytes];

        for (int i = 0; i < 40; i++)
        {
            Array.Fill(frame, (byte)i);
            encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload);
            SimulatedRig.WaitFor(() => rig.Link.TrySendFrame(cmd, payload.Span), 1000);
        }

        SimulatedDevice device = rig.CurrentDevice!;
        Assert.True(SimulatedRig.WaitFor(() => device.FramesApplied > 20, 4000),
                    $"So {device.FramesApplied} frames chegaram inteiros.");

        // Todo frame aplicado tem que ser um dos que o host mandou: 1024 bytes iguais.
        byte[] panel = device.SnapshotFramebuffer();
        Assert.True(panel.Distinct().Count() == 1, "O painel recebeu um frame corrompido.");
    }

    [Fact]
    public void CableUnplugged_IsDetected_AndTheLinkReconnects()
    {
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        SimulatedDevice first = rig.CurrentDevice!;
        rig.YankCable();

        Assert.True(SimulatedRig.WaitFor(() => rig.Link.State != LinkState.Connected, 5000),
                    "A queda do link nao foi detectada.");
        Assert.True(rig.WaitForConnected(8000), $"Nao reconectou; estado = {rig.Link.State}");

        Assert.NotSame(first, rig.CurrentDevice);
        Assert.True(rig.Link.Statistics.Reconnects >= 1);
    }

    [Fact]
    public void PortMissingAtStartup_KeepsRetryingUntilItAppears()
    {
        using var rig = new SimulatedRig { FailNextConnection = true };
        rig.Link.Start();

        Assert.True(SimulatedRig.WaitFor(() => rig.Link.Statistics.Reconnects >= 2, 4000),
                    "Deveria continuar tentando.");
        Assert.NotEqual(LinkState.Connected, rig.Link.State);

        rig.FailNextConnection = false;   // a placa e' plugada
        Assert.True(rig.WaitForConnected(8000), $"Nao conectou depois que a porta apareceu; estado = {rig.Link.State}");
    }

    [Fact]
    public void ControlCommands_AreHonouredByTheDevice()
    {
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());
        SimulatedDevice device = rig.CurrentDevice!;

        rig.Link.SendText("HELLO WORLD");
        Assert.True(SimulatedRig.WaitFor(() => device.LastText == "HELLO WORLD"));

        rig.Link.SendStreamBegin();
        Assert.True(SimulatedRig.WaitFor(() => device.IsStreaming));

        rig.Link.SendConfig(Payloads.BuildConfigByte(ConfigKey.Contrast, 0x20));
        Assert.True(SimulatedRig.WaitFor(() => device.Contrast == 0x20));

        rig.Link.SendStreamEnd();
        Assert.True(SimulatedRig.WaitFor(() => !device.IsStreaming));

        DeviceStats? stats = null;
        rig.Link.StatsReceived += s => stats = s;
        rig.Link.RequestStats();
        Assert.True(SimulatedRig.WaitFor(() => stats is not null));
        Assert.True(stats!.FramesApplied >= 0);
    }

    [Fact]
    public void FullPipeline_MirrorsTheGeneratedImageOntoTheSimulatedPanel()
    {
        // O caminho completo: padrao sintetico -> resize -> dither -> pack ->
        // codificacao -> protocolo -> dispositivo -> painel.
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        using var pipeline = new MirrorPipeline(rig.Link)
        {
            Settings = new MirrorSettings
            {
                TargetFps = 30,
                Encoding = FrameEncoding.Auto,
                Image = new ImageProcessorOptions
                {
                    ResizeMode = ResizeMode.Fit,
                    Dithering = DitheringMode.BayerOrdered4x4,
                    AutoContrast = false,
                },
            },
        };
        // Padrao animado de proposito: com uma tela parada o pipeline pararia de
        // transmitir depois do primeiro frame (ver o teste do modo estatico abaixo).
        var source = new TestPatternSource(TestPattern.OrbitingCircle, 1920, 1080);
        pipeline.SetSource(source);
        pipeline.Start();

        SimulatedDevice device = rig.CurrentDevice!;
        Assert.True(SimulatedRig.WaitFor(() => device.FramesApplied >= 5, 6000),
                    $"So {device.FramesApplied} frames chegaram ao painel.");

        // Congela a animacao com o pipeline ainda rodando. A 30 FPS pedidos, e'
        // esperado que frames sejam descartados pelo controle de fluxo; o que nao
        // e' aceitavel e' o painel ficar preso num frame velho. Assim que a tela
        // para, o painel tem que convergir para a imagem correta.
        source.FixedTimeSeconds = 1.0;

        var hostFrame = new byte[DisplayGeometry.FrameBytes];
        Assert.True(SimulatedRig.WaitFor(() =>
        {
            if (!pipeline.TryCopyLatestFrame(hostFrame)) return false;
            return device.SnapshotFramebuffer().AsSpan().SequenceEqual(hostFrame);
        }, 5000), "O painel nao convergiu para o frame do host depois que a tela parou.");

        // O FPS efetivo e' amostrado em janelas de meio segundo; espera a primeira.
        Assert.True(SimulatedRig.WaitFor(() => pipeline.Statistics.EffectiveFps > 0, 3000),
                    "O FPS efetivo nunca foi calculado.");

        pipeline.Stop();

        Assert.True(pipeline.Statistics.FramesProcessed > 0);
        Assert.True(pipeline.Statistics.FramesSent > 0);
    }

    [Fact]
    public void PreviewOnlyMode_ProducesFramesWithoutTouchingTheDevice()
    {
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        using var pipeline = new MirrorPipeline(rig.Link)
        {
            Settings = new MirrorSettings { TargetFps = 30, PreviewOnly = true },
        };
        pipeline.SetSource(new TestPatternSource(TestPattern.BouncingBox, 1280, 720));
        pipeline.Start();

        Assert.True(SimulatedRig.WaitFor(() => pipeline.Statistics.FramesProcessed >= 5, 4000));
        pipeline.Stop();

        var frame = new byte[DisplayGeometry.FrameBytes];
        Assert.True(pipeline.TryCopyLatestFrame(frame));
        Assert.Equal(0, pipeline.Statistics.FramesSent);
        Assert.Equal(0u, rig.CurrentDevice!.FramesApplied);
    }

    [Fact]
    public void StaticScreen_StopsTransmittingAfterTheFirstFrame()
    {
        // O maior ganho de banda do projeto: tela parada = trafego proximo de zero.
        using var rig = new SimulatedRig();
        rig.Link.Start();
        Assert.True(rig.WaitForConnected());

        using var pipeline = new MirrorPipeline(rig.Link)
        {
            Settings = new MirrorSettings
            {
                TargetFps = 30,
                SkipUnchangedFrames = true,
                Image = new ImageProcessorOptions { Dithering = DitheringMode.Threshold, AutoContrast = false },
            },
        };
        pipeline.SetSource(new TestPatternSource(TestPattern.Checkerboard, 1920, 1080));
        pipeline.Start();

        Assert.True(SimulatedRig.WaitFor(() => pipeline.Statistics.FramesProcessed >= 30, 6000));
        pipeline.Stop();

        // Dezenas de frames processados, mas praticamente nada foi para o fio.
        Assert.True(pipeline.Statistics.FramesSent <= 3,
                    $"Tela parada mandou {pipeline.Statistics.FramesSent} frames.");
        Assert.True(pipeline.Statistics.FramesSkippedUnchanged > 20);

        // E o painel esta correto mesmo assim.
        var hostFrame = new byte[DisplayGeometry.FrameBytes];
        Assert.True(pipeline.TryCopyLatestFrame(hostFrame));
        Assert.True(SimulatedRig.WaitFor(
            () => rig.CurrentDevice!.SnapshotFramebuffer().AsSpan().SequenceEqual(hostFrame), 2000));
    }
}