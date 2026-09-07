using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Protocol;

public class PacketParserTests {
    private static byte[] Payload(int length, byte seed = 1)
    {
        var p = new byte[length];
        for (int i = 0; i < length; i++) p[i] = (byte)(i * 31 + seed);
        return p;
    }

    [Fact]
    public void ValidPacket_IsDecoded()
    {
        byte[] payload = Payload(64);
        byte[] packet = PacketEncoder.Encode(CommandId.FrameRaw, 42, payload);

        var parser = new PacketParser();
        Assert.Equal(1, parser.Push(packet));
        Assert.True(parser.TryDequeue(out Packet decoded));

        Assert.Equal(CommandId.FrameRaw, decoded.Command);
        Assert.Equal(42, decoded.Sequence);
        Assert.Equal(payload, decoded.Payload);
        Assert.Equal(0, parser.Statistics.DiscardedBytes);
    }

    [Fact]
    public void EmptyPayload_IsValid()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.Ping, 0);
        var parser = new PacketParser();

        Assert.Equal(1, parser.Push(packet));
        Assert.True(parser.TryDequeue(out Packet decoded));
        Assert.Empty(decoded.Payload);
    }

    [Fact]
    public void PacketDeliveredOneByteAtATime_IsDecoded()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.FrameRaw, 7, Payload(1024));
        var parser = new PacketParser();

        for (int i = 0; i < packet.Length - 1; i++)
            Assert.Equal(0, parser.Push(packet.AsSpan(i, 1)));   // ainda incompleto

        Assert.Equal(1, parser.Push(packet.AsSpan(packet.Length - 1, 1)));
        Assert.True(parser.TryDequeue(out Packet decoded));
        Assert.Equal(1024, decoded.Payload.Length);
    }

    [Fact]
    public void MultiplePacketsInOneChunk_AreAllDecoded()
    {
        var stream = new List<byte>();
        for (int i = 0; i < 5; i++) stream.AddRange(PacketEncoder.Encode(CommandId.Ping, (byte)i, Payload(8, (byte)i)));

        var parser = new PacketParser();
        Assert.Equal(5, parser.Push(stream.ToArray()));

        for (int i = 0; i < 5; i++)
        {
            Assert.True(parser.TryDequeue(out Packet p));
            Assert.Equal(i, p.Sequence);
        }
    }

    [Fact]
    public void CorruptedPayloadCrc_IsRejected_AndNextPacketStillDecodes()
    {
        byte[] bad = PacketEncoder.Encode(CommandId.FrameRaw, 1, Payload(32));
        bad[^1] ^= 0xFF;                                       // estraga o CRC16
        byte[] good = PacketEncoder.Encode(CommandId.FrameRaw, 2, Payload(32, 9));

        var parser = new PacketParser();
        parser.Push(bad);
        parser.Push(good);

        Assert.True(parser.TryDequeue(out Packet p));
        Assert.Equal(2, p.Sequence);                           // so o bom passou
        Assert.False(parser.TryDequeue(out _));
        Assert.True(parser.Statistics.PayloadCrcErrors >= 1);
    }

    [Fact]
    public void CorruptedHeaderCrc_IsRejected()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.Ping, 1, Payload(4));
        packet[7] ^= 0xFF;                                     // estraga o CRC8 do cabecalho

        var parser = new PacketParser();
        Assert.Equal(0, parser.Push(packet));
        Assert.Equal(1, parser.Statistics.HeaderCrcErrors);
    }

    [Fact]
    public void CorruptedLengthField_IsCaughtByHeaderCrc_AndDoesNotStallTheParser()
    {
        // Este e' o caso que justifica o CRC8 no cabecalho: sem ele, um LENGTH
        // corrompido para 60000 faria o parser esperar por bytes que nunca virao.
        byte[] bad = PacketEncoder.Encode(CommandId.FrameRaw, 1, Payload(16));
        bad[4] = 0x60; bad[5] = 0xEA;                          // LENGTH = 60000
        byte[] good = PacketEncoder.Encode(CommandId.Ping, 99, Payload(4));

        var parser = new PacketParser();
        parser.Push(bad);
        parser.Push(good);

        Assert.True(parser.TryDequeue(out Packet p));
        Assert.Equal(99, p.Sequence);
    }

    [Fact]
    public void LengthAboveMaximum_IsRejectedWithoutAllocating()
    {
        // Cabecalho com CRC8 valido mas LENGTH acima do maximo permitido.
        var header = new byte[ProtocolConstants.HeaderSize];
        header[0] = ProtocolConstants.Sof0;
        header[1] = ProtocolConstants.Sof1;
        header[2] = ProtocolConstants.Version;
        header[3] = (byte)CommandId.FrameRaw;
        header[4] = 0xFF; header[5] = 0xFF;                    // LENGTH = 65535
        header[6] = 0;
        header[7] = Crc.Crc8(header.AsSpan(0, 7));

        var parser = new PacketParser();
        parser.Push(header);

        Assert.Equal(1, parser.Statistics.LengthErrors);
        Assert.Equal(0, parser.Statistics.PacketsDecoded);
    }

    [Fact]
    public void UnsupportedVersion_IsRejected()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.Ping, 1, Payload(4));
        packet[2] = 0x7E;                                      // versao desconhecida
        packet[7] = Crc.Crc8(packet.AsSpan(0, 7));             // cabecalho volta a ser valido

        var parser = new PacketParser();
        parser.Push(packet);

        Assert.Equal(1, parser.Statistics.VersionErrors);
        Assert.Equal(0, parser.Statistics.PacketsDecoded);
    }

    [Fact]
    public void GarbageBeforePacket_IsDiscarded_AndPacketIsFound()
    {
        var stream = new List<byte>();
        stream.AddRange(new byte[] { 0x00, 0xFF, 0xAA, 0x12, 0x55, 0xAA, 0x99 });  // lixo, com SOFs falsos
        stream.AddRange(PacketEncoder.Encode(CommandId.Pong, 5, Payload(8)));

        var parser = new PacketParser();
        parser.Push(stream.ToArray());

        Assert.True(parser.TryDequeue(out Packet p));
        Assert.Equal(5, p.Sequence);
        Assert.True(parser.Statistics.DiscardedBytes > 0);
        Assert.True(parser.Statistics.Resyncs >= 1);
    }

    [Fact]
    public void TrailingGarbage_DoesNotAffectAlreadyDecodedPackets()
    {
        var stream = new List<byte>();
        stream.AddRange(PacketEncoder.Encode(CommandId.Ack, 3, Payload(2)));
        stream.AddRange(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var parser = new PacketParser();
        parser.Push(stream.ToArray());

        Assert.True(parser.TryDequeue(out Packet p));
        Assert.Equal(3, p.Sequence);
        Assert.False(parser.TryDequeue(out _));
    }

    [Fact]
    public void HeavilyCorruptedStream_NeverYieldsAWrongPacket_AndKeepsDecoding()
    {
        // 200 pacotes com 60 bits trocados aleatoriamente. Duas propriedades:
        //   1. nenhum pacote entregue pode ter conteudo errado (o CRC tem que pegar);
        //   2. o parser nao pode travar - depois do lixo, um pacote limpo passa.
        // Nao exigimos um numero de sobreviventes: com 60 corrupcoes em 200
        // pacotes, perder ~55 e' o resultado aritmeticamente esperado.
        var rng = new Random(2024);
        var stream = new List<byte>();
        for (int i = 0; i < 200; i++)
            stream.AddRange(PacketEncoder.Encode(CommandId.FrameRaw, (byte)i, Payload(64, (byte)i)));

        byte[] data = stream.ToArray();
        for (int i = 0; i < 60; i++) data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));

        var parser = new PacketParser();
        parser.Push(data);

        int decoded = 0;
        while (parser.TryDequeue(out Packet p))
        {
            decoded++;
            // O payload e' derivado da sequencia; se o CRC deixou passar algo
            // corrompido, esta comparacao falha.
            Assert.Equal(Payload(64, p.Sequence), p.Payload);
        }

        Assert.True(decoded >= 130, $"Recuperacao baixa demais: {decoded} de 200.");

        // O parser continua utilizavel depois de todo esse lixo.
        parser.Push(PacketEncoder.Encode(CommandId.Pong, 200, Payload(8, 200)));
        Assert.True(parser.TryDequeue(out Packet after));
        Assert.Equal(CommandId.Pong, after.Command);
    }

    [Fact]
    public void Reset_ClearsPartialState()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.FrameRaw, 1, Payload(100));
        var parser = new PacketParser();

        parser.Push(packet.AsSpan(0, 50));                     // metade de um pacote
        parser.Reset();
        parser.Push(packet.AsSpan(50));                        // a outra metade, agora orfã

        Assert.Equal(0, parser.Statistics.PacketsDecoded);

        parser.Push(packet);                                   // um pacote inteiro passa normalmente
        Assert.True(parser.TryDequeue(out _));
    }

    [Fact]
    public void MaximumPayload_RoundTrips()
    {
        byte[] payload = Payload(ProtocolConstants.MaxPayloadSize);
        byte[] packet = PacketEncoder.Encode(CommandId.FrameRle, 0, payload);

        var parser = new PacketParser();
        parser.Push(packet);

        Assert.True(parser.TryDequeue(out Packet p));
        Assert.Equal(payload, p.Payload);
    }

    [Fact]
    public void PayloadAboveMaximum_ThrowsOnEncode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketEncoder.Encode(CommandId.FrameRaw, 0, new byte[ProtocolConstants.MaxPayloadSize + 1]));
    }

    [Fact]
    public void RawFramePacket_Is1034Bytes()
    {
        // 1024 de payload + 8 de cabecalho + 2 de CRC.
        Assert.Equal(1034, PacketEncoder.PacketSize(1024));
    }
}