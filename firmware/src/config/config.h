#pragma once

#include <stdint.h>

#include "protocol/protocol.h"

// Valores padrao. Todos podem ser sobrescritos pelo ambiente do PlatformIO
// (ver firmware/platformio.ini) sem editar este arquivo
#ifndef OLEDMIRROR_LINK_BAUD
#define OLEDMIRROR_LINK_BAUD 921600
#endif

#ifndef OLEDMIRROR_I2C_SDA
#define OLEDMIRROR_I2C_SDA 21
#endif

#ifndef OLEDMIRROR_I2C_SCL
#define OLEDMIRROR_I2C_SCL 22
#endif

// 800 kHz esta acima do "fast mode" de 400 kHz que o SSD1306 especifica, mas os
// modulos comuns funcionam nessa velocidade e isso quase dobra o teto de FPS.
// Se aparecerem artefatos, use o ambiente esp32dev-i2c400
#ifndef OLEDMIRROR_I2C_CLOCK
#define OLEDMIRROR_I2C_CLOCK 800000
#endif

// Barramento do painel: 0 = I2C (padrao), 1 = SPI
#ifndef OLEDMIRROR_BUS_SPI
#define OLEDMIRROR_BUS_SPI 0
#endif

// 1 quando a placa tem USB nativo (S2/S3/C3) em vez de ponte USB-UART
#ifndef OLEDMIRROR_NATIVE_USB
#define OLEDMIRROR_NATIVE_USB 0
#endif

#ifndef OLEDMIRROR_SPI_CS
#define OLEDMIRROR_SPI_CS 5
#endif
#ifndef OLEDMIRROR_SPI_DC
#define OLEDMIRROR_SPI_DC 16
#endif
#ifndef OLEDMIRROR_SPI_RST
#define OLEDMIRROR_SPI_RST 17
#endif

// Enderecos I2C sondados na inicializacao, nesta ordem
#ifndef OLEDMIRROR_I2C_ADDR_PRIMARY
#define OLEDMIRROR_I2C_ADDR_PRIMARY 0x3C
#endif
#ifndef OLEDMIRROR_I2C_ADDR_SECONDARY
#define OLEDMIRROR_I2C_ADDR_SECONDARY 0x3D
#endif

// Controlador assumido quando nao ha nada gravado na NVS
// Pode ser trocado em tempo de execucao pelo host (SET_CONFIG / kCfgController), sem regravar
#ifndef OLEDMIRROR_DEFAULT_CONTROLLER
#define OLEDMIRROR_DEFAULT_CONTROLLER oledmirror::kControllerSsd1306
#endif

// Quantos frames o dispositivo aceita ter em voo
// Anunciado no HELLO_ACK e usado pelo host como janela de controle de fluxo
#ifndef OLEDMIRROR_RX_QUEUE_DEPTH
#define OLEDMIRROR_RX_QUEUE_DEPTH 2
#endif

// Sem receber frame nenhum por este tempo, o painel volta para a tela de espera
// 0 desliga o comportamento
#ifndef OLEDMIRROR_IDLE_TIMEOUT_MS
#define OLEDMIRROR_IDLE_TIMEOUT_MS 5000
#endif

// Teto de bytes processados por iteracao do loop
// Impede que uma rajada da serial deixe o loop preso sem nunca chegar a atualizar o painel
#ifndef OLEDMIRROR_MAX_BYTES_PER_LOOP
#define OLEDMIRROR_MAX_BYTES_PER_LOOP 4096
#endif

namespace oledmirror {
    static constexpr uint8_t kFirmwareVersionMajor = 1;
    static constexpr uint8_t kFirmwareVersionMinor = 0;
    static constexpr uint8_t kFirmwareVersionPatch = 0;

    static constexpr const char* kDeviceName = "OledMirror ESP32";

    static constexpr uint16_t kDeviceCapabilities =
        kCapRle | kCapDelta | kCapText | kCapContrastCtrl | kCapStats | kCapInvertRotate;
}