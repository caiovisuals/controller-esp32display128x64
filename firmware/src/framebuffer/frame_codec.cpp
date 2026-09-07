#include "framebuffer/frame_codec.h"

#include <string.h>

namespace oledmirror {

    int RleDecode(const uint8_t* src, size_t src_length, uint8_t* out, size_t out_capacity) {
        size_t i = 0;
        size_t o = 0;

        while (i < src_length) {
            const uint8_t control = src[i++];

            if ((control & 0x80) == 0) {
                // Literal: os proximos (control + 1) bytes
                const size_t n = static_cast<size_t>(control) + 1;
                if (i + n > src_length) return -1; // fluxo truncado
                if (o + n > out_capacity) return -1; // estouraria o destino
                memcpy(out + o, src + i, n);
                i += n;
                o += n;
            } else {
                // Repeticao: o proximo byte, (control & 0x7F) + 2 vezes
                const size_t n = static_cast<size_t>(control & 0x7F) + 2;
                if (i >= src_length) return -1;
                if (o + n > out_capacity) return -1;
                memset(out + o, src[i], n);
                ++i;
                o += n;
            }
        }

        return static_cast<int>(o);
    }

    FrameDecodeResult ApplyFramePayload(uint8_t command, const uint8_t* payload, uint16_t length, uint8_t* framebuffer, uint8_t* scratch) {
        switch (command) {
            case kCmdFrameRaw: {
                if (length != kFrameBytes) return kDecodeBadLength;
                memcpy(framebuffer, payload, kFrameBytes);
                return kDecodeOk;
            }

            case kCmdFrameRle: {
                const int n = RleDecode(payload, length, scratch, kFrameBytes);
                if (n < 0) return kDecodeBadRle;
                if (n != static_cast<int>(kFrameBytes)) return kDecodeBadLength;
                memcpy(framebuffer, scratch, kFrameBytes);
                return kDecodeOk;
            }

            case kCmdFrameDelta:
            case kCmdFrameDeltaRle: {
                if (length < 4) return kDecodeBadLength;

                const uint8_t x0 = payload[0];
                const uint8_t x1 = payload[1];
                const uint8_t p0 = payload[2];
                const uint8_t p1 = payload[3];

                // Limites checados antes de qualquer escrita
                if (x1 < x0 || p1 < p0) return kDecodeBadRect;
                if (x1 >= kDisplayWidth || p1 >= kDisplayPages) return kDecodeBadRect;

                const uint16_t columns = static_cast<uint16_t>(x1 - x0 + 1);
                const uint16_t pages   = static_cast<uint16_t>(p1 - p0 + 1);
                const uint32_t expected = static_cast<uint32_t>(columns) * pages;

                const uint8_t* region;
                if (command == kCmdFrameDelta) {
                    if (static_cast<uint32_t>(length - 4) != expected) return kDecodeBadLength;
                    region = payload + 4;
                } else {
                    const int n = RleDecode(payload + 4, static_cast<size_t>(length - 4), scratch, kFrameBytes);
                    if (n < 0) return kDecodeBadRle;
                    if (static_cast<uint32_t>(n) != expected) return kDecodeBadLength;
                    region = scratch;
                }

                for (uint8_t p = p0; p <= p1; ++p) {
                    memcpy(framebuffer + static_cast<size_t>(p) * kDisplayWidth + x0,
                        region + static_cast<size_t>(p - p0) * columns,
                        columns);
                }
                return kDecodeOk;
            }

            default:
                return kDecodeNotAFrame;
        }
    }

    uint8_t NackReasonForDecode(FrameDecodeResult result) {
        switch (result) {
            case kDecodeBadLength: return kNackBadLength;
            case kDecodeBadRect:   return kNackBadPayload;
            case kDecodeBadRle:    return kNackBadPayload;
            case kDecodeNotAFrame: return kNackUnknownCommand;
            default:               return kNackNone;
        }
    }
}