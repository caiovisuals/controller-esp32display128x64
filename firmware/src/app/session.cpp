#ifndef OLEDMIRROR_NATIVE_TEST

#include "app/session.h"

#include <Arduino.h>
#include <Preferences.h>
#include <string.h>

#include "config/config.h"
#include "framebuffer/frame_codec.h"

namespace oledmirror {
    namespace {

    Preferences g_prefs;
    constexpr const char* kPrefsNamespace = "oledmirror";

    void PutU16(uint8_t* p, uint16_t v) {
        p[0] = static_cast<uint8_t>(v & 0xFF);
        p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
    }

    void PutU32(uint8_t* p, uint32_t v) {
        p[0] = static_cast<uint8_t>(v & 0xFF);
        p[1] = static_cast<uint8_t>((v >> 8) & 0xFF);
        p[2] = static_cast<uint8_t>((v >> 16) & 0xFF);
        p[3] = static_cast<uint8_t>((v >> 24) & 0xFF);
    }
}

Session::Session(DisplayDriver* display, LinkTransport* link)
        : display_(display), link_(link) {}

    void Session::Begin() {
        boot_ms_ = millis();
        last_frame_ms_ = boot_ms_;
        ApplyStoredSettings();
        ShowIdleScreen();
    }

    void Session::ApplyStoredSettings() {
        if (g_prefs.begin(kPrefsNamespace, /*readOnly=*/true)) {
            contrast_ = g_prefs.getUChar("contrast", contrast_);
            g_prefs.end();
        }
        display_->SetContrast(contrast_);
    }

    void Session::PersistController(ControllerId controller) {
        if (!g_prefs.begin(kPrefsNamespace, /*readOnly=*/false)) return;
        g_prefs.putUChar("controller", static_cast<uint8_t>(controller));
        g_prefs.end();
    }

    void Session::ShowIdleScreen() {
        char line[24];
        snprintf(line, sizeof(line), "%s", kDeviceName);
        display_->ShowMessage("OledMirror", "aguardando o PC", line);
        idle_screen_shown_ = true;
    }

    void Session::Poll() {
        // 1. Consumir a serial, com teto por iteracao para nunca deixar o painel
        //    sem atualizacao por causa de uma rajada.
        int budget = OLEDMIRROR_MAX_BYTES_PER_LOOP;
        ParsedPacket packet;

        while (budget-- > 0 && link_->Available() > 0) {
            const int value = link_->ReadByte();
            if (value < 0) break;

            if (parser_.Push(static_cast<uint8_t>(value), &packet)) {
                HandlePacket(packet);
                // Um frame por iteracao: assim o loop volta a rodar (watchdog,
                // temporizadores) entre uma escrita no painel e a proxima.
                if (IsFrameCommand(packet.command)) break;
            }
        }

        // 2. Volta para a tela de espera se o host sumiu.
        const uint32_t now = millis();
        if (idle_timeout_ms_ > 0 && !idle_screen_shown_ &&
            static_cast<uint32_t>(now - last_frame_ms_) > idle_timeout_ms_) {
            streaming_ = false;
            ShowIdleScreen();
        }
    }

    void Session::HandlePacket(const ParsedPacket& packet) {
        switch (packet.command) {
            case kCmdHello:
                SendHelloAck(kCmdHelloAck);
                break;

            case kCmdGetInfo:
                SendHelloAck(kCmdInfo);
                break;

            case kCmdPing:
                SendPacket(kCmdPong, packet.payload, packet.length);
                break;

            case kCmdSync:
                SendAck(kCmdSync);
                break;

            case kCmdStreamBegin:
                streaming_ = true;
                last_frame_ms_ = millis();
                SendAck(kCmdStreamBegin);
                break;

            case kCmdStreamEnd:
                streaming_ = false;
                SendAck(kCmdStreamEnd);
                break;

            case kCmdClear:
                display_->Clear();
                idle_screen_shown_ = false;
                last_frame_ms_ = millis();
                SendAck(kCmdClear);
                break;

            case kCmdText:
                HandleText(packet);
                break;

            case kCmdSetConfig:
                HandleConfig(packet);
                break;

            case kCmdGetStats:
                SendStats();
                break;

            case kCmdFrameRaw:
            case kCmdFrameRle:
            case kCmdFrameDelta:
            case kCmdFrameDeltaRle:
                HandleFrame(packet);
                break;

            default:
                SendNack(packet.command, kNackUnknownCommand, packet.sequence);
                break;
        }
    }

    void Session::HandleFrame(const ParsedPacket& packet) {
        uint8_t* framebuffer = display_->Framebuffer();
        if (framebuffer == nullptr) {
            ++frames_dropped_;
            SendNack(packet.command, kNackDisplayError, packet.sequence);
            return;
        }

        // Sai da tela de espera na primeira imagem que chegar.
        if (idle_screen_shown_) {
            memset(framebuffer, 0, kFrameBytes);
            idle_screen_shown_ = false;
        }

        const FrameDecodeResult result =
            ApplyFramePayload(packet.command, packet.payload, packet.length, framebuffer, scratch_);

        if (result != kDecodeOk) {
            ++frames_dropped_;
            SendNack(packet.command, NackReasonForDecode(result), packet.sequence);
            return;
        }

        const uint32_t started = micros();
        display_->Flush();
        const uint32_t elapsed = micros() - started;
        last_render_us_ = elapsed > 0xFFFF ? 0xFFFF : static_cast<uint16_t>(elapsed);

        ++frames_applied_;
        last_frame_ms_ = millis();

        // FRAME_ACK depois do render: o host mede a latencia real ate a imagem
        // aparecer, e a janela de controle de fluxo reflete o ritmo do painel.
        uint8_t payload[5];
        payload[0] = packet.sequence;
        payload[1] = OLEDMIRROR_RX_QUEUE_DEPTH;
        PutU16(payload + 2, last_render_us_);
        payload[4] = 0;
        SendPacket(kCmdFrameAck, payload, sizeof(payload));
    }

    void Session::HandleText(const ParsedPacket& packet) {
        // Copia limitada e sempre terminada: o payload vem da serial.
        char text[64];
        const uint16_t n = packet.length < sizeof(text) - 1 ? packet.length : sizeof(text) - 1;
        memcpy(text, packet.payload, n);
        text[n] = '\0';

        // Quebra em ate tres linhas de 20 caracteres.
        char l1[21] = {0}, l2[21] = {0}, l3[21] = {0};
        strncpy(l1, text, 20);
        if (n > 20) strncpy(l2, text + 20, 20);
        if (n > 40) strncpy(l3, text + 40, 20);

        display_->ShowMessage(l1, l2, l3);
        idle_screen_shown_ = false;
        last_frame_ms_ = millis();
        SendAck(kCmdText);
    }

    void Session::HandleConfig(const ParsedPacket& packet) {
        uint16_t offset = 0;
        bool ok = true;

        while (offset < packet.length) {
            if (offset + 2 > packet.length) { ok = false; break; }

            const uint8_t key = packet.payload[offset];
            const uint8_t length = packet.payload[offset + 1];
            if (offset + 2 + length > packet.length) { ok = false; break; }

            const uint8_t* value = packet.payload + offset + 2;

            switch (key) {
                case kCfgContrast:
                    if (length >= 1) {
                        contrast_ = value[0];
                        display_->SetContrast(contrast_);
                        if (g_prefs.begin(kPrefsNamespace, false)) {
                            g_prefs.putUChar("contrast", contrast_);
                            g_prefs.end();
                        }
                    } else ok = false;
                    break;

                case kCfgInvert:
                    if (length >= 1) display_->SetInverted(value[0] != 0); else ok = false;
                    break;

                case kCfgFlipVert:
                case kCfgFlipHoriz:
                    if (length >= 1) display_->SetFlipped(value[0] != 0); else ok = false;
                    break;

                case kCfgDisplayOn:
                    if (length >= 1) display_->SetPowerOn(value[0] != 0); else ok = false;
                    break;

                case kCfgIdleTimeoutMs:
                    if (length >= 2) {
                        idle_timeout_ms_ = static_cast<uint32_t>(value[0]) |
                                        (static_cast<uint32_t>(value[1]) << 8);
                    } else ok = false;
                    break;

                case kCfgController:
                    if (length >= 1 && value[0] <= kControllerSh1107) {
                        // Fica gravado e vale a partir do proximo boot: trocar o
                        // objeto do U8g2 com um frame em voo daria tela corrompida.
                        PersistController(static_cast<ControllerId>(value[0]));
                        SendLog(kLogInfo, "controlador salvo; reinicie a placa");
                    } else ok = false;
                    break;

                default:
                    // Chave desconhecida e' ignorada de proposito: assim um host mais
                    // novo nao quebra contra um firmware mais antigo.
                    break;
            }

            offset = static_cast<uint16_t>(offset + 2 + length);
        }

        if (ok) SendAck(kCmdSetConfig);
        else SendNack(kCmdSetConfig, kNackBadPayload, packet.sequence);
    }

    void Session::SendHelloAck(uint8_t command) {
        const char* name = kDeviceName;
        const size_t name_length = strlen(name);

        uint8_t payload[12 + 32];
        payload[0] = kProtocolVersion;
        payload[1] = kFirmwareVersionMajor;
        payload[2] = kFirmwareVersionMinor;
        payload[3] = kFirmwareVersionPatch;
        PutU16(payload + 4, kDeviceCapabilities);
        payload[6] = kDisplayWidth;
        payload[7] = kDisplayHeight;
        payload[8] = static_cast<uint8_t>(display_->controller());
        payload[9] = static_cast<uint8_t>(display_->bus());
        payload[10] = display_->i2c_address();
        payload[11] = OLEDMIRROR_RX_QUEUE_DEPTH;

        const size_t n = name_length < 32 ? name_length : 32;
        memcpy(payload + 12, name, n);

        SendPacket(command, payload, static_cast<uint16_t>(12 + n));
    }

    void Session::SendStats() {
        const ParserStats& p = parser_.stats();

        uint8_t payload[24];
        PutU32(payload + 0, frames_applied_);
        PutU32(payload + 4, frames_dropped_);
        PutU32(payload + 8, p.header_crc_bad + p.payload_crc_bad);
        PutU32(payload + 12, p.resyncs);
        PutU16(payload + 16, last_render_us_);
        PutU16(payload + 18, static_cast<uint16_t>(ESP.getFreeHeap() / 1024));
        PutU32(payload + 20, (millis() - boot_ms_) / 1000);

        SendPacket(kCmdStats, payload, sizeof(payload));
    }

    void Session::SendAck(uint8_t acked_command) {
        const uint8_t payload[1] = {acked_command};
        SendPacket(kCmdAck, payload, 1);
    }

    void Session::SendNack(uint8_t acked_command, uint8_t reason, uint8_t sequence) {
        const uint8_t payload[2] = {acked_command, reason};
        const size_t n = EncodePacket(tx_buffer_, sizeof(tx_buffer_), kCmdNack, sequence, payload, 2);
        if (n > 0) link_->Write(tx_buffer_, n);
    }

    void Session::SendLog(LogLevel level, const char* message) {
        uint8_t payload[97];
        payload[0] = static_cast<uint8_t>(level);
        const size_t n = strnlen(message, sizeof(payload) - 1);
        memcpy(payload + 1, message, n);
        SendPacket(kCmdLog, payload, static_cast<uint16_t>(1 + n));
    }

    void Session::SendPacket(uint8_t command, const uint8_t* payload, uint16_t length) {
        const size_t n = EncodePacket(tx_buffer_, sizeof(tx_buffer_), command, tx_sequence_++, payload, length);
        if (n > 0) link_->Write(tx_buffer_, n);
    }
}

#endif