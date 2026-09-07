#pragma once

#include "protocol/protocol.h"

namespace oledmirror {

// Interface do painel
// Nenhuma outra parte do firmware fala com o U8g2 ou com o SSD1306 diretamente: 
// trocar de controlador ou de biblioteca e' escrever uma nova implementacao desta classe
class DisplayDriver {
    public:
        virtual ~DisplayDriver() = default;

        // Inicializa o painel. Devolve false se o hardware nao respondeu
        virtual bool Begin() = 0;

        // Ponteiro para os kFrameBytes bytes do buffer interno, no mesmo layout do
        // protocolo (page-major). 
        // Escrever aqui e chamar Flush() e' o caminho rapido: sem transposicao de bits, sem copia extra
        virtual uint8_t* Framebuffer() = 0;

        // Envia o buffer para o painel
        virtual void Flush() = 0;

        virtual void Clear() = 0;

        // Texto centralizado, usado nas telas de espera e diagnostico
        virtual void ShowMessage(const char* line1, const char* line2 = nullptr,
                                const char* line3 = nullptr) = 0;

        virtual void SetContrast(uint8_t value) = 0;
        virtual void SetInverted(bool inverted) = 0;
        virtual void SetFlipped(bool flipped) = 0;
        virtual void SetPowerOn(bool on) = 0;

        virtual ControllerId controller() const = 0;
        virtual BusId bus() const = 0;
        virtual uint8_t i2c_address() const = 0;
    };
}