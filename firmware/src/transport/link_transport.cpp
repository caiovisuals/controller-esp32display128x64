#ifndef OLEDMIRROR_NATIVE_TEST

#include "transport/link_transport.h"

#include "config/config.h"

namespace oledmirror {

    void LinkTransport::Begin(uint32_t baud) {
    #if OLEDMIRROR_NATIVE_USB
        // USB CDC: o baud e' ignorado pelo hardware, a taxa e' a do USB
        Serial.begin(baud);
    #else
        Serial.setRxBufferSize(4096);
        Serial.begin(baud);
    #endif
        Serial.setTimeout(0);
    }

    int LinkTransport::Available() { return Serial.available(); }

    int LinkTransport::ReadByte() { return Serial.read(); }

    void LinkTransport::Write(const uint8_t* data, size_t length) {
        size_t written = 0;
        const uint32_t deadline = millis() + 50;

        while (written < length) {
            const size_t n = Serial.write(data + written, length - written);
            written += n;
            if (written >= length) break;
            if (static_cast<int32_t>(millis() - deadline) >= 0) break;
            delay(0);
        }
    }

    void LinkTransport::DiscardInput() {
        while (Serial.available() > 0) Serial.read();
    }

    bool LinkTransport::IsConnected() {
    #if OLEDMIRROR_NATIVE_USB
        return static_cast<bool>(Serial);
    #else
        // Numa ponte USB-UART nao ha como saber: a UART nao tem sinal de presenca
        return true;
    #endif
    }

}

#endif