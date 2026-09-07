#pragma once

#include "protocol/protocol.h"

namespace oledmirror {

    enum FrameDecodeResult : uint8_t {
        kDecodeOk = 0,
        kDecodeBadLength,
        kDecodeBadRect,
        kDecodeBadRle,
        kDecodeNotAFrame,
    };

    // Decodifica RLE. Devolve o numero de bytes escritos, ou -1 se o fluxo estiver truncado ou tentar escrever alem de out_capacity
    // Toda a validacao acontece aqui: este codigo roda sobre bytes vindos da serial, entao nenhuma escrita pode depender de o remetente estar bem comportado
    int RleDecode(const uint8_t* src, size_t src_length, uint8_t* out, size_t out_capacity);

    // Aplica um payload de frame sobre um framebuffer de kFrameBytes bytes
    // scratch precisa ter pelo menos kFrameBytes bytes
    FrameDecodeResult ApplyFramePayload(uint8_t command, const uint8_t* payload, uint16_t length,
                                        uint8_t* framebuffer, uint8_t* scratch);

    // Mapeia o motivo da recusa para o codigo de NACK correspondente
    uint8_t NackReasonForDecode(FrameDecodeResult result);
}