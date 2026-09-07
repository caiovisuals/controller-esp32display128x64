#include "protocol/parser.h"

#include <string.h>

#include "protocol/crc.h"

namespace oledmirror {

    void PacketParser::Reset() {
        length_ = 0;
        was_aligned_ = true;
    }

    void PacketParser::Discard(size_t count) {
        if (count == 0) return;
        if (count >= length_) {
            stats_.discarded_bytes += static_cast<uint32_t>(length_);
            length_ = 0;
        } else {
            memmove(buffer_, buffer_ + count, length_ - count);
            length_ -= count;
            stats_.discarded_bytes += static_cast<uint32_t>(count);
        }
        if (was_aligned_) {
            ++stats_.resyncs;
            was_aligned_ = false;
        }
    }

    bool PacketParser::Push(uint8_t byte, ParsedPacket* out_packet) {
        if (length_ >= sizeof(buffer_)) {
            Discard(length_);
        }

        buffer_[length_++] = byte;
        return TryParse(out_packet);
    }

    bool PacketParser::TryParse(ParsedPacket* out_packet) {
        for (;;) {
            if (length_ < 2) return false;

            // Alinhar na assinatura
            if (buffer_[0] != kSof0 || buffer_[1] != kSof1) {
                size_t scan = 1;
                while (scan < length_ && buffer_[scan] != kSof0) ++scan;
                Discard(scan);
                continue;
            }

            if (length_ < kHeaderSize) return false;

            // O cabecalho tem que estar integro antes de acreditarmos no LENGTH
            if (Crc8(buffer_, 7) != buffer_[7]) {
                ++stats_.header_crc_bad;
                Discard(1);
                continue;
            }

            if (buffer_[2] != kProtocolVersion) {
                ++stats_.version_bad;
                Discard(2);
                continue;
            }

            const uint16_t payload_length =
                static_cast<uint16_t>(buffer_[4] | (static_cast<uint16_t>(buffer_[5]) << 8));

            if (payload_length > kMaxPayloadSize) {
                ++stats_.length_bad;
                Discard(2);
                continue;
            }

            const size_t total = kHeaderSize + payload_length + kTrailerSize;
            if (length_ < total) return false;

            // Validar o payload
            const uint8_t* payload = buffer_ + kHeaderSize;
            const uint16_t expected =
                static_cast<uint16_t>(payload[payload_length] |
                                    (static_cast<uint16_t>(payload[payload_length + 1]) << 8));

            if (Crc16(payload, payload_length) != expected) {
                ++stats_.payload_crc_bad;
                Discard(2);
                continue;
            }

            out_packet->command = buffer_[3];
            out_packet->sequence = buffer_[6];
            out_packet->payload = payload;
            out_packet->length = payload_length;

            ++stats_.packets_ok;
            was_aligned_ = true;

            // O consumidor le o payload antes do proximo Push, entao pode deslocar o buffer
            const size_t remaining = length_ - total;
            if (remaining > 0) memmove(buffer_, buffer_ + total, remaining);
            length_ = remaining;

            return true;
        }
    }

    size_t EncodePacket(uint8_t* out, size_t out_capacity, uint8_t command, uint8_t sequence,
                        const uint8_t* payload, uint16_t payload_length) {
        if (payload_length > kMaxPayloadSize) return 0;

        const size_t total = kOverheadSize + payload_length;
        if (out_capacity < total) return 0;

        out[0] = kSof0;
        out[1] = kSof1;
        out[2] = kProtocolVersion;
        out[3] = command;
        out[4] = static_cast<uint8_t>(payload_length & 0xFF);
        out[5] = static_cast<uint8_t>((payload_length >> 8) & 0xFF);
        out[6] = sequence;
        out[7] = Crc8(out, 7);

        if (payload_length > 0 && payload != nullptr) {
            memcpy(out + kHeaderSize, payload, payload_length);
        }

        const uint16_t crc = Crc16(out + kHeaderSize, payload_length);
        out[kHeaderSize + payload_length]     = static_cast<uint8_t>(crc & 0xFF);
        out[kHeaderSize + payload_length + 1] = static_cast<uint8_t>((crc >> 8) & 0xFF);

        return total;
    }
}