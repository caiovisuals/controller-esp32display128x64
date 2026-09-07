#pragma once

#include <stdint.h>
#include <stddef.h>

namespace oledmirror {
    // Geometria

    static constexpr uint8_t  kDisplayWidth  = 128;
    static constexpr uint8_t  kDisplayHeight = 64;
    static constexpr uint8_t  kDisplayPages  = kDisplayHeight / 8;   // 8
    static constexpr uint16_t kFrameBytes    = 1024;                 // 128 * 64 / 8

    // Framing

    static constexpr uint8_t  kSof0 = 0xAA;
    static constexpr uint8_t  kSof1 = 0x55;
    static constexpr uint8_t  kProtocolVersion = 0x01;

    static constexpr size_t   kHeaderSize  = 8;   // SOF0 SOF1 VER CMD LEN_LO LEN_HI SEQ CRC8
    static constexpr size_t   kTrailerSize = 2;   // CRC16 do payload, little-endian
    static constexpr size_t   kOverheadSize = kHeaderSize + kTrailerSize;

    static constexpr uint16_t kMaxPayloadSize = 2048;
    static constexpr size_t   kMaxPacketSize  = kMaxPayloadSize + kOverheadSize;

    // Comandos

    enum Command : uint8_t {
        kCmdHello        = 0x01,
        kCmdPing         = 0x02,
        kCmdStreamBegin  = 0x03,
        kCmdStreamEnd    = 0x04,
        kCmdClear        = 0x05,
        kCmdSetConfig    = 0x06,
        kCmdGetInfo      = 0x07,
        kCmdGetStats     = 0x08,
        kCmdSync         = 0x09,
        kCmdText         = 0x0A,

        kCmdFrameRaw      = 0x10,
        kCmdFrameRle      = 0x11,
        kCmdFrameDelta    = 0x12,
        kCmdFrameDeltaRle = 0x13,

        kCmdHelloAck = 0x81,
        kCmdPong     = 0x82,
        kCmdAck      = 0x83,
        kCmdNack     = 0x84,
        kCmdFrameAck = 0x85,
        kCmdInfo     = 0x86,
        kCmdStats    = 0x87,
        kCmdLog      = 0x8F,
    };

    enum NackReason : uint8_t {
        kNackNone               = 0x00,
        kNackBadCrc             = 0x01,
        kNackBadLength          = 0x02,
        kNackUnknownCommand     = 0x03,
        kNackUnsupportedVersion = 0x04,
        kNackBusy               = 0x05,
        kNackBadPayload         = 0x06,
        kNackNotStreaming       = 0x07,
        kNackDisplayError       = 0x08,
    };

    enum Capability : uint16_t {
        kCapRle          = 1u << 0,
        kCapDelta        = 1u << 1,
        kCapText         = 1u << 2,
        kCapContrastCtrl = 1u << 3,
        kCapStats        = 1u << 4,
        kCapInvertRotate = 1u << 5,
    };

    enum ConfigKey : uint8_t {
        kCfgContrast      = 0x01,
        kCfgInvert        = 0x02,
        kCfgFlipVert      = 0x03,
        kCfgFlipHoriz     = 0x04,
        kCfgDisplayOn     = 0x05,
        kCfgIdleTimeoutMs = 0x06,
        kCfgController    = 0x07,
    };

    enum ControllerId : uint8_t {
        kControllerUnknown = 0,
        kControllerSsd1306 = 1,
        kControllerSh1106  = 2,
        kControllerSsd1309 = 3,
        kControllerSh1107  = 4,
    };

    enum BusId : uint8_t {
        kBusI2c = 0,
        kBusSpi = 1,
    };

    enum LogLevel : uint8_t {
        kLogDebug = 0,
        kLogInfo = 1,
        kLogWarning = 2,
        kLogError = 3,
    };

    inline bool IsFrameCommand(uint8_t command) {
        return command == kCmdFrameRaw || command == kCmdFrameRle ||
            command == kCmdFrameDelta || command == kCmdFrameDeltaRle;
    }
}