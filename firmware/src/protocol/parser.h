#pragma once

#include "protocol/protocol.h"

namespace oledmirror {

    struct ParsedPacket {
        uint8_t        command;
        uint8_t        sequence;
        const uint8_t* payload;
        uint16_t       length;
    };

    struct ParserStats {
        uint32_t packets_ok      = 0;
        uint32_t header_crc_bad  = 0;
        uint32_t payload_crc_bad = 0;
        uint32_t length_bad      = 0;
        uint32_t version_bad     = 0;
        uint32_t discarded_bytes = 0;
        uint32_t resyncs         = 0;
    };

    // Decodificador incremental. Recebe bytes em qualquer fragmentacao e entrega pacotes validados, descartando lixo sem nunca travar nem estourar o buffer
    class PacketParser {
    public:
        void Reset();

        bool Push(uint8_t byte, ParsedPacket* out_packet);

        const ParserStats& stats() const { return stats_; }

    private:
        bool TryParse(ParsedPacket* out_packet);
        void Discard(size_t count);

        uint8_t     buffer_[kMaxPacketSize];
        size_t      length_ = 0;
        ParserStats stats_;
        bool        was_aligned_ = true;
    };

    size_t EncodePacket(uint8_t* out, size_t out_capacity, uint8_t command, uint8_t sequence,
                        const uint8_t* payload, uint16_t payload_length);

}