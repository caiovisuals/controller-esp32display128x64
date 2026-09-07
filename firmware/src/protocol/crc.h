#pragma once

#include <stddef.h>
#include <stdint.h>

namespace oledmirror {

    // CRC-8/ATM: poly 0x07, init 0x00. Protege o cabecalho
    uint8_t Crc8(const uint8_t* data, size_t length, uint8_t seed = 0x00);

    // CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF. Protege o payload
    uint16_t Crc16(const uint8_t* data, size_t length, uint16_t seed = 0xFFFF);
}