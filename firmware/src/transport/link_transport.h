#pragma once

#ifndef OLEDMIRROR_NATIVE_TEST

#include <Arduino.h>
#include <stddef.h>
#include <stdint.h>

namespace oledmirror {

    class LinkTransport {
    public:
        void Begin(uint32_t baud);

        int Available();

        int ReadByte();

        void Write(const uint8_t* data, size_t length);

        void DiscardInput();

        bool IsConnected();
    };
}

#endif