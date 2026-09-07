#ifndef OLEDMIRROR_NATIVE_TEST

#include "display/u8g2_display.h"

#include <Wire.h>

#include "config/config.h"

namespace oledmirror {

    U8g2Display::U8g2Display(ControllerId controller, uint8_t i2c_address)
        : controller_(controller),
    #if OLEDMIRROR_BUS_SPI
        bus_(kBusSpi),
    #else
        bus_(kBusI2c),
    #endif
        i2c_address_(i2c_address) {}

    U8g2Display::~U8g2Display() { Destroy(); }

    void U8g2Display::Destroy() {
        delete u8g2_;
        u8g2_ = nullptr;
    }

    uint8_t U8g2Display::ProbeI2c() {
        Wire.begin(OLEDMIRROR_I2C_SDA, OLEDMIRROR_I2C_SCL, OLEDMIRROR_I2C_CLOCK);

        const uint8_t candidates[] = {OLEDMIRROR_I2C_ADDR_PRIMARY, OLEDMIRROR_I2C_ADDR_SECONDARY};
        for (uint8_t address : candidates) {
            Wire.beginTransmission(address);
            if (Wire.endTransmission() == 0) return address;
        }
        return 0;
    }

    bool U8g2Display::Begin() {
        Destroy();

    #if OLEDMIRROR_BUS_SPI
        // SPI por hardware: bem mais rapido que I2C
        switch (controller_) {
            case kControllerSh1106:
                u8g2_ = new U8G2_SH1106_128X64_NONAME_F_4W_HW_SPI(
                    U8G2_R0, OLEDMIRROR_SPI_CS, OLEDMIRROR_SPI_DC, OLEDMIRROR_SPI_RST);
                break;
            case kControllerSsd1309:
                u8g2_ = new U8G2_SSD1309_128X64_NONAME0_F_4W_HW_SPI(
                    U8G2_R0, OLEDMIRROR_SPI_CS, OLEDMIRROR_SPI_DC, OLEDMIRROR_SPI_RST);
                break;
            default:
                u8g2_ = new U8G2_SSD1306_128X64_NONAME_F_4W_HW_SPI(
                    U8G2_R0, OLEDMIRROR_SPI_CS, OLEDMIRROR_SPI_DC, OLEDMIRROR_SPI_RST);
                break;
        }
    #else
        switch (controller_) {
            case kControllerSh1106:
                u8g2_ = new U8G2_SH1106_128X64_NONAME_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE,
                                                            OLEDMIRROR_I2C_SCL, OLEDMIRROR_I2C_SDA);
                break;
            case kControllerSh1107:
                u8g2_ = new U8G2_SH1107_128X64_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE,
                                                        OLEDMIRROR_I2C_SCL, OLEDMIRROR_I2C_SDA);
                break;
            case kControllerSsd1309:
                u8g2_ = new U8G2_SSD1309_128X64_NONAME0_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE,
                                                                OLEDMIRROR_I2C_SCL, OLEDMIRROR_I2C_SDA);
                break;
            default:
                u8g2_ = new U8G2_SSD1306_128X64_NONAME_F_HW_I2C(U8G2_R0, U8X8_PIN_NONE,
                                                                OLEDMIRROR_I2C_SCL, OLEDMIRROR_I2C_SDA);
                break;
        }

        if (u8g2_ != nullptr) u8g2_->setI2CAddress(i2c_address_ << 1);
    #endif

        if (u8g2_ == nullptr) return false;

        if (!u8g2_->begin()) return false;
        u8g2_->setBusClock(OLEDMIRROR_I2C_CLOCK);
        u8g2_->setPowerSave(0);
        u8g2_->clearBuffer();
        u8g2_->sendBuffer();
        return true;
    }

    uint8_t* U8g2Display::Framebuffer() {
        return u8g2_ == nullptr ? nullptr : u8g2_->getBufferPtr();
    }

    void U8g2Display::Flush() {
        if (u8g2_ != nullptr) u8g2_->sendBuffer();
    }

    void U8g2Display::Clear() {
        if (u8g2_ == nullptr) return;
        u8g2_->clearBuffer();
        u8g2_->sendBuffer();
    }

    void U8g2Display::ShowMessage(const char* line1, const char* line2, const char* line3) {
        if (u8g2_ == nullptr) return;

        u8g2_->clearBuffer();
        u8g2_->setFont(u8g2_font_6x12_tr);

        const char* lines[3] = {line1, line2, line3};
        int count = 0;
        for (int i = 0; i < 3; ++i) {
            if (lines[i] != nullptr && lines[i][0] != '\0') ++count;
        }

        int y = 32 - (count * 13) / 2 + 10;
        for (int i = 0; i < 3; ++i) {
            if (lines[i] == nullptr || lines[i][0] == '\0') continue;
            const int width = u8g2_->getStrWidth(lines[i]);
            u8g2_->drawStr((kDisplayWidth - width) / 2, y, lines[i]);
            y += 13;
        }

        u8g2_->sendBuffer();
    }

    void U8g2Display::SetContrast(uint8_t value) {
        if (u8g2_ != nullptr) u8g2_->setContrast(value);
    }

    void U8g2Display::SetInverted(bool inverted) {
        inverted_ = inverted;
        if (u8g2_ != nullptr) u8g2_->sendF("c", inverted ? 0xA7 : 0xA6);
    }

    void U8g2Display::SetFlipped(bool flipped) {
        if (u8g2_ != nullptr) u8g2_->setFlipMode(flipped ? 1 : 0);
    }

    void U8g2Display::SetPowerOn(bool on) {
        if (u8g2_ != nullptr) u8g2_->setPowerSave(on ? 0 : 1);
    }
}

#endif