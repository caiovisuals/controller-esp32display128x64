#pragma once

#ifndef OLEDMIRROR_NATIVE_TEST

#include <stdint.h>

#include "display/display_driver.h"
#include "protocol/parser.h"
#include "protocol/protocol.h"
#include "transport/link_transport.h"

namespace oledmirror {

// Maquina de estados da sessao com o PC
//
// Regra que orienta o desenho inteiro: nada aqui pode bloquear
// O painel precisa continuar sendo atualizado mesmo que o host pare de falar no meio de um
// pacote, mande lixo, ou desapareca
class Session {
    public:
        Session(DisplayDriver* display, LinkTransport* link);

        void Begin();

        // Chamada a cada iteracao do loop()
        // Consome o que chegou, aplica no maximo um frame por vez e cuida dos temporizadores
        void Poll();

        // Envia uma linha de log para o host (aparece no painel de log da aplicacao)
        void SendLog(LogLevel level, const char* message);

    private:
        void HandlePacket(const ParsedPacket& packet);
        void HandleFrame(const ParsedPacket& packet);
        void HandleConfig(const ParsedPacket& packet);
        void HandleText(const ParsedPacket& packet);

        void SendPacket(uint8_t command, const uint8_t* payload, uint16_t length);
        void SendAck(uint8_t acked_command);
        void SendNack(uint8_t acked_command, uint8_t reason, uint8_t sequence = 0);
        void SendHelloAck(uint8_t command);
        void SendStats();

        void ShowIdleScreen();
        void ApplyStoredSettings();
        void PersistController(ControllerId controller);

        DisplayDriver* display_;
        LinkTransport* link_;
        PacketParser   parser_;

        uint8_t scratch_[kFrameBytes]; // Area de trabalho do RLE / delta
        uint8_t tx_buffer_[kMaxPacketSize];

        uint8_t  tx_sequence_ = 0;
        bool     streaming_ = false;
        bool     idle_screen_shown_ = false;
        uint32_t last_frame_ms_ = 0;
        uint32_t idle_timeout_ms_ = OLEDMIRROR_IDLE_TIMEOUT_MS;
        uint32_t boot_ms_ = 0;

        uint32_t frames_applied_ = 0;
        uint32_t frames_dropped_ = 0;
        uint16_t last_render_us_ = 0;
        uint8_t  contrast_ = 0x7F;
    };
}

#endif