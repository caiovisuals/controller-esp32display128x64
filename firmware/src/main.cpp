#ifndef OLEDMIRROR_NATIVE_TEST

#include <Arduino.h>
#include <Preferences.h>

#include "app/session.h"
#include "config/config.h"
#include "display/u8g2_display.h"
#include "transport/link_transport.h"

namespace {

    // Controlador gravado pelo host via SET_CONFIG (kCfgController); sem nada gravado, vale o padrao
    oledmirror::ControllerId LoadController() {
        Preferences prefs;
        uint8_t value = static_cast<uint8_t>(OLEDMIRROR_DEFAULT_CONTROLLER);
        if (prefs.begin("oledmirror", /*readOnly=*/true)) {
            value = prefs.getUChar("controller", value);
            prefs.end();
        }
        if (value < oledmirror::kControllerSsd1306 || value > oledmirror::kControllerSh1107) {
            value = static_cast<uint8_t>(OLEDMIRROR_DEFAULT_CONTROLLER);
        }
        return static_cast<oledmirror::ControllerId>(value);
    }

    uint8_t g_probed_address = 0;

    uint8_t DetectAddress() {
    #if OLEDMIRROR_BUS_SPI
        return 0;
    #else
        g_probed_address = oledmirror::U8g2Display::ProbeI2c();
        // Nenhum endereco respondeu: segue com o primario para o host ainda conseguir conversar e diagnosticar
        return g_probed_address != 0 ? g_probed_address : OLEDMIRROR_I2C_ADDR_PRIMARY;
    #endif
    }

    oledmirror::LinkTransport g_link;
    oledmirror::U8g2Display*  g_display = nullptr;
    oledmirror::Session*      g_session = nullptr;
}

void setup() {
    g_link.Begin(OLEDMIRROR_LINK_BAUD);

    g_display = new oledmirror::U8g2Display(LoadController(), DetectAddress());
    const bool display_ok = g_display->Begin();

    // Em I2C o begin() do U8g2 nao detecta painel ausente; a sondagem detecta
#if OLEDMIRROR_BUS_SPI
    const bool panel_found = display_ok;
#else
    const bool panel_found = display_ok && g_probed_address != 0;
#endif

    g_session = new oledmirror::Session(g_display, &g_link);
    g_session->SetPanelDetected(panel_found);
    g_session->Begin();

    if (!panel_found) g_session->SendLog(oledmirror::kLogError, "painel nao respondeu; confira a fiacao");
}

void loop() {
    g_session->Poll();
    delay(0);
}

#endif