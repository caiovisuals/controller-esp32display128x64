namespace OledMirror.Core.Protocol;

/// <summary>
/// Constantes do enquadramento. Espelham firmware/src/protocol/protocol.h e
/// precisam continuar identicas - ver docs/PROTOCOL.md.
/// </summary>
public static class ProtocolConstants
{
    public const byte Sof0 = 0xAA;
    public const byte Sof1 = 0x55;
    public const byte Version = 0x01;

    /// <summary>SOF0 SOF1 VER CMD LEN_LO LEN_HI SEQ CRC8</summary>
    public const int HeaderSize = 8;

    /// <summary>CRC16 do payload, little-endian.</summary>
    public const int TrailerSize = 2;

    public const int OverheadSize = HeaderSize + TrailerSize;
    public const int MaxPayloadSize = 2048;
    public const int MaxPacketSize = MaxPayloadSize + OverheadSize;
}

/// <summary>Comandos. 0x00-0x7F: host -> dispositivo; 0x80-0xFF: dispositivo -> host.</summary>
public enum CommandId : byte
{
    Hello = 0x01,
    Ping = 0x02,
    StreamBegin = 0x03,
    StreamEnd = 0x04,
    Clear = 0x05,
    SetConfig = 0x06,
    GetInfo = 0x07,
    GetStats = 0x08,
    Sync = 0x09,
    Text = 0x0A,

    FrameRaw = 0x10,
    FrameRle = 0x11,
    FrameDelta = 0x12,
    FrameDeltaRle = 0x13,

    HelloAck = 0x81,
    Pong = 0x82,
    Ack = 0x83,
    Nack = 0x84,
    FrameAck = 0x85,
    Info = 0x86,
    Stats = 0x87,
    Log = 0x8F,
}

public enum NackReason : byte
{
    None = 0x00,
    BadCrc = 0x01,
    BadLength = 0x02,
    UnknownCommand = 0x03,
    UnsupportedVersion = 0x04,
    Busy = 0x05,
    BadPayload = 0x06,
    NotStreaming = 0x07,
    DisplayError = 0x08,
}

/// <summary>Bits de capacidade anunciados no HELLO_ACK.</summary>
[Flags]
public enum DeviceCapabilities : ushort
{
    None = 0,
    Rle = 1 << 0,
    Delta = 1 << 1,
    Text = 1 << 2,
    ContrastControl = 1 << 3,
    Stats = 1 << 4,
    InvertRotate = 1 << 5,

    All = Rle | Delta | Text | ContrastControl | Stats | InvertRotate,
}

/// <summary>Chaves do TLV de SET_CONFIG.</summary>
public enum ConfigKey : byte
{
    Contrast = 0x01,
    Invert = 0x02,
    FlipVertical = 0x03,
    FlipHorizontal = 0x04,
    DisplayOn = 0x05,
    IdleTimeoutMs = 0x06,
    Controller = 0x07,
}

public enum ControllerId : byte
{
    Unknown = 0,
    Ssd1306 = 1,
    Sh1106 = 2,
    Ssd1309 = 3,
    Sh1107 = 4,
}

public enum BusId : byte
{
    I2c = 0,
    Spi = 1,
}

/// <summary>Nivel das mensagens LOG enviadas pelo dispositivo.</summary>
public enum DeviceLogLevel : byte
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public static class CommandIdExtensions
{
    public static bool IsFrame(this CommandId command)
        => command is CommandId.FrameRaw or CommandId.FrameRle or CommandId.FrameDelta or CommandId.FrameDeltaRle;
}