#pragma once

#ifndef OLEDMIRROR_NATIVE_TEST

#include <U8g2lib.h>
#include <stdint.h>

#include "display/display_driver.h"

namespace oledmirror {

// Implementacao do DisplayDriver sobre o U8g2
// O controlador e' escolhido no construtor; o barramento (I2C ou SPI) e' fixado em tempo de compilacao por OLEDMIRROR_BUS_SPI
class U8g2Display : public DisplayDriver {
    public:
        U8g2Display(ControllerId controller, uint8_t i2c_address);
        ~U8g2Display() override;

        // Procura o painel nos enderecos I2C conhecidos. Devolve o endereco que respondeu, ou 0 se nenhum respondeu
        static uint8_t ProbeI2c();

        bool Begin() override;
        uint8_t* Framebuffer() override;
        void Flush() override;
        void Clear() override;
        void ShowMessage(const char* line1, const char* line2 = nullptr,
                        const char* line3 = nullptr) override;

        void SetContrast(uint8_t value) override;
        void SetInverted(bool inverted) override;
        void SetFlipped(bool flipped) override;
        void SetPowerOn(bool on) override;

        ControllerId controller() const override { return controller_; }
        BusId bus() const override { return bus_; }
        uint8_t i2c_address() const override { return i2c_address_; }

    private:
        void Destroy();

        ControllerId controller_;
        BusId        bus_;
        uint8_t      i2c_address_;
        U8G2*        u8g2_ = nullptr;
        bool         inverted_ = false;
    };
}

#endif