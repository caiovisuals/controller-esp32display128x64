#include "protocol/crc.h"

namespace oledmirror {
    namespace {

    // Tabelas geradas uma vez na inicializacao
    // 256 + 512 bytes de RAM em troca de ~8x menos ciclos por byte - relevante quando sao 1034 bytes por frame a dezenas de frames por segundo
    uint8_t  g_table8[256];
    uint16_t g_table16[256];
    bool     g_initialised = false;

    void EnsureTables() {
        if (g_initialised) return;

        for (int i = 0; i < 256; ++i) {
            uint8_t c = static_cast<uint8_t>(i);
            for (int b = 0; b < 8; ++b) {
                c = (c & 0x80) ? static_cast<uint8_t>((c << 1) ^ 0x07) : static_cast<uint8_t>(c << 1);
            }
            g_table8[i] = c;
        }

        for (int i = 0; i < 256; ++i) {
            uint16_t c = static_cast<uint16_t>(i << 8);
            for (int b = 0; b < 8; ++b) {
                c = (c & 0x8000) ? static_cast<uint16_t>((c << 1) ^ 0x1021) : static_cast<uint16_t>(c << 1);
            }
            g_table16[i] = c;
        }

        g_initialised = true;
    }

    }

    uint8_t Crc8(const uint8_t* data, size_t length, uint8_t seed) {
        EnsureTables();
        uint8_t crc = seed;
        for (size_t i = 0; i < length; ++i) crc = g_table8[crc ^ data[i]];
        return crc;
    }

    uint16_t Crc16(const uint8_t* data, size_t length, uint16_t seed) {
        EnsureTables();
        uint16_t crc = seed;
        for (size_t i = 0; i < length; ++i) {
            crc = static_cast<uint16_t>((crc << 8) ^ g_table16[((crc >> 8) ^ data[i]) & 0xFF]);
        }
        return crc;
    }
}