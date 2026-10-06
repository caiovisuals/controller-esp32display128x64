using System.Buffers.Binary;
using System.Text;
using OledMirror.Core.Protocol;

namespace OledMirror.Core.Devices;

/// <summary>Identificacao do dispositivo, do HELLO_ACK / INFO (docs/PROTOCOL.md, secao 3).</summary>
public sealed record DeviceHello
{
    public const int MinimumLength = 12;

    public byte ProtocolVersion { get; init; }
    public byte FirmwareMajor { get; init; }
    public byte FirmwareMinor { get; init; }
    public byte FirmwarePatch { get; init; }
    public DeviceCapabilities Capabilities { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public ControllerId Controller { get; init; }
    public BusId Bus { get; init; }
    public byte I2cAddress { get; init; }
    public int RxQueueDepth { get; init; }
    public string Name { get; init; } = string.Empty;

    public string FirmwareVersion => $"{FirmwareMajor}.{FirmwareMinor}.{FirmwarePatch}";

    public static bool TryParse(ReadOnlySpan<byte> payload, out DeviceHello hello)
    {
        hello = null!;
        if (payload.Length < MinimumLength) return false;

        hello = new DeviceHello
        {
            ProtocolVersion = payload[0],
            FirmwareMajor = payload[1],
            FirmwareMinor = payload[2],
            FirmwarePatch = payload[3],
            Capabilities = (DeviceCapabilities)BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2)),
            DisplayWidth = payload[6],
            DisplayHeight = payload[7],
            Controller = (ControllerId)payload[8],
            Bus = (BusId)payload[9],
            I2cAddress = payload[10],
            RxQueueDepth = payload[11],
            Name = Encoding.UTF8.GetString(payload[MinimumLength..]).TrimEnd('\0'),
        };
        return true;
    }

    /// <summary>Formato do HELLO_ACK, usado pelo dispositivo simulado.</summary>
    public byte[] ToPayload()
    {
        byte[] name = Encoding.UTF8.GetBytes(Name);
        if (name.Length > 32) name = name[..32];

        var payload = new byte[MinimumLength + name.Length];
        payload[0] = ProtocolVersion;
        payload[1] = FirmwareMajor;
        payload[2] = FirmwareMinor;
        payload[3] = FirmwarePatch;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)Capabilities);
        payload[6] = (byte)DisplayWidth;
        payload[7] = (byte)DisplayHeight;
        payload[8] = (byte)Controller;
        payload[9] = (byte)Bus;
        payload[10] = I2cAddress;
        payload[11] = (byte)RxQueueDepth;
        name.CopyTo(payload, MinimumLength);
        return payload;
    }

    public override string ToString()
    {
        string controller = Controller == ControllerId.Unknown ? "sem painel" : Controller.ToString().ToUpperInvariant();
        string bus = Bus == BusId.Spi ? "SPI" : $"I2C 0x{I2cAddress:X2}";
        string name = string.IsNullOrWhiteSpace(Name) ? "ESP32" : Name;
        return $"{name} v{FirmwareVersion} - {controller} {bus}, {DisplayWidth}x{DisplayHeight}, fila {RxQueueDepth}";
    }
}

/// <summary>Resposta do GET_STATS (docs/PROTOCOL.md, secao 5).</summary>
public sealed record DeviceStats
{
    public const int Length = 24;

    public uint FramesApplied { get; init; }
    public uint FramesDropped { get; init; }
    public uint CrcErrors { get; init; }
    public uint Resyncs { get; init; }
    public ushort LastRenderMicros { get; init; }
    public ushort FreeHeapKb { get; init; }
    public uint UptimeSeconds { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> payload, out DeviceStats stats)
    {
        stats = null!;
        if (payload.Length < Length) return false;

        stats = new DeviceStats
        {
            FramesApplied = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..]),
            FramesDropped = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            CrcErrors = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            Resyncs = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]),
            LastRenderMicros = BinaryPrimitives.ReadUInt16LittleEndian(payload[16..]),
            FreeHeapKb = BinaryPrimitives.ReadUInt16LittleEndian(payload[18..]),
            UptimeSeconds = BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]),
        };
        return true;
    }

    public byte[] ToPayload()
    {
        var p = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), FramesApplied);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), FramesDropped);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), CrcErrors);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), Resyncs);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(16), LastRenderMicros);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(18), FreeHeapKb);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20), UptimeSeconds);
        return p;
    }
}