# HARDWARE

Este documento explica **como cada conclusão foi tirada** a partir da descrição do
hardware. Onde a informação disponível não permite concluir, isso está dito
explicitamente em vez de virar um palpite silencioso.

## 1. Qual ESP32 é este?

**Informação disponível:** "ESP32 USB-C, 30 pinos".

O número de pinos é o dado mais informativo, porque cada família da Espressif tem
uma contagem de pinos característica nas placas de desenvolvimento comuns:

| Placa | Pinos | USB |
|---|---|---|
| **ESP32 DevKit V1 / DOIT (ESP32-WROOM-32)** | **30** | ponte USB-UART |
| ESP32 DevKitC (WROOM-32) versão larga | 38 | ponte USB-UART |
| ESP32-S2 Saola | 42 | USB nativo |
| ESP32-S3 DevKitC-1 | 44 | USB nativo |
| ESP32-C3 DevKitM-1 | 32 | USB nativo |
| ESP32-C3 SuperMini | 16 | USB nativo |

**Conclusão (alta confiança):** é um **ESP32 clássico (ESP32-WROOM-32,
dual-core Xtensa LX6 a 240 MHz, 520 KB de SRAM, 4 MB de flash)** no formato
DevKit V1 de 30 pinos, com conector USB-C no lugar do micro-USB original —
uma modernização comum dessa placa, que não muda nada eletricamente.

### Por que isso importa muito

**O ESP32 clássico não tem USB nativo.** O conector USB-C vai para um chip de
ponte USB-UART (CP2102 da Silicon Labs ou CH340/CH9102 da WCH), e o que o PC
enxerga é uma porta COM virtual. Consequência direta e inescapável:

> **A banda do sistema inteiro é limitada pelo baud rate da UART.**

| Baud | Bytes/s (8N1) | FPS máximo com frame cru (1034 B no fio) |
|---|---|---|
| 115200 | 11 520 | **11 FPS** |
| 460800 | 46 080 | 44 FPS |
| **921600 (padrão do projeto)** | **92 160** | **89 FPS** |
| 1500000 | 150 000 | 145 FPS |

Se a placa for, na verdade, um **S3 ou C3**, o USB é nativo (CDC) e esse teto
praticamente desaparece. O firmware já tem ambientes prontos para isso
(`esp32s3`, `esp32c3` no `platformio.ini`) — só trocar o ambiente de compilação.

## 2. Qual é o controlador do display?

**Informação disponível:** "Display OLED 128×64". Nada além disso.

**Não é possível concluir com certeza**, e este é exatamente o ponto em que
assumir SSD1306 automaticamente causaria horas de depuração se estivesse errado.

O que se sabe do mercado de módulos 128×64:

| Controlador | Onde aparece | Como se reconhece |
|---|---|---|
| **SSD1306** | módulos de 0,96", a grande maioria | funciona direto |
| **SH1106** | módulos de 1,3", muito comuns | imagem **deslocada 2 px** e lixo nas bordas se tratado como SSD1306 |
| SSD1309 | módulos de 2,42" | inicialização diferente |
| SH1107 | alguns 128×64 quadrados | mapeamento de memória diferente |

### Como o projeto trata isso

1. Existe uma interface `DisplayDriver` (`firmware/src/display/display_driver.h`).
   **Nenhuma outra parte do firmware chama SSD1306 diretamente.**
2. O padrão é SSD1306 — é o palpite mais provável, não uma certeza.
3. **O controlador pode ser trocado em tempo de execução, sem regravar o
   firmware:** a aplicação envia `SET_CONFIG` com a chave `Controller`, o valor
   fica na NVS e vale a partir do próximo boot.
4. O firmware **sonda o barramento I2C** na inicialização e reporta ao PC o
   endereço que respondeu.

### Sintoma → controlador

* Painel mostra a imagem **deslocada 2 pixels na horizontal**, com uma faixa de
  lixo na lateral → **é SH1106**, não SSD1306.
* Painel fica **totalmente apagado**, mas o endereço I2C responde → fiação certa,
  controlador provavelmente errado ou alimentação insuficiente.
* Painel não responde a **nenhum** endereço → problema de fiação ou alimentação.

## 3. I2C ou SPI?

**Informação disponível:** nenhuma explícita.

Deduzível do resto: você citou **protoboard de 400 furos e fios Dupont**. Módulos
128×64 vendidos para esse tipo de montagem são **majoritariamente I2C de 4 pinos**
(VCC, GND, SCL, SDA). Módulos SPI têm 7 pinos (VCC, GND, D0/SCK, D1/MOSI, RES,
DC, CS) e são visivelmente diferentes.

**Conclusão (confiança média-alta): I2C, 4 pinos.**

Confirme contando os pinos do seu módulo. Se forem 7, use o ambiente
`esp32dev-spi` — o SPI é **muito** mais rápido (ver `PERFORMANCE.md`).

### Endereço I2C

Praticamente todos os módulos vêm em **0x3C**. Alguns têm um jumper ou resistor
para **0x3D**. O firmware sonda os dois automaticamente, nessa ordem, e informa
qual respondeu no handshake — não é preciso adivinhar.

## 4. Tensão — a parte que pode danificar a placa

**O ESP32 é 3,3 V e os GPIOs NÃO são tolerantes a 5 V.**

O detalhe que causa dano e quase ninguém menciona:

> Os módulos OLED trazem **resistores de pull-up de I2C soldados na placa, ligados
> ao pino VCC do módulo**. Se você alimentar o módulo com 5 V, as linhas SDA e SCL
> passam a repousar em **5 V**, e vão direto para os GPIOs do ESP32.

O módulo pode até "funcionar" assim por um tempo. O que acontece é desgaste dos
diodos de proteção do ESP32, com falhas intermitentes e morte prematura do pino.

**Regra:** alimente o módulo pelo pino **3V3** do ESP32. Nunca pelo VIN/5V.

O consumo de um 128×64 é de 10–20 mA típicos (até ~25 mA com tudo aceso), bem
dentro do que o regulador da placa DevKit fornece.

### Pull-ups

A maioria dos módulos traz 4,7 kΩ ou 10 kΩ na própria placa — normalmente
suficiente. Os pull-ups internos do ESP32 (~45 kΩ) **não** são suficientes para
400 kHz, muito menos 800 kHz.

Se aparecerem erros de I2C ou artefatos a 800 kHz:
1. primeiro tente o ambiente `esp32dev-i2c400` (400 kHz, dentro da especificação);
2. se persistir, adicione **4,7 kΩ de SDA para 3V3 e 4,7 kΩ de SCL para 3V3**;
3. encurte os fios Dupont — em protoboard, acima de ~20 cm o I2C degrada rápido.

---

## 5. Quais GPIOs usar (e quais evitar)

Pinagem do DevKit V1 de 30 pinos:

```
        ┌───────── USB-C ─────────┐
   EN  ─┤ 1                    30 ├─ D23
  VP36 ─┤ 2                    29 ├─ D22   ← SCL
  VN39 ─┤ 3                    28 ├─ TX0    (evitar: console/gravação)
   D34 ─┤ 4                    27 ├─ RX0    (evitar: console/gravação)
   D35 ─┤ 5                    26 ├─ D21   ← SDA
   D32 ─┤ 6                    25 ├─ D19
   D33 ─┤ 7                    24 ├─ D18
   D25 ─┤ 8                    23 ├─ D5     (strap: HIGH no boot)
   D26 ─┤ 9                    22 ├─ TX2/17
   D27 ─┤ 10                   21 ├─ RX2/16
   D14 ─┤ 11                   20 ├─ D4
   D12 ─┤ 12 (CUIDADO)         19 ├─ D2     (strap + LED da placa)
   D13 ─┤ 13                   18 ├─ D15    (strap)
   GND ─┤ 14                   17 ├─ GND
   VIN ─┤ 15                   16 ├─ 3V3
        └─────────────────────────┘
```

**Pinos a evitar e por quê:**

| Pino | Motivo |
|---|---|
| GPIO 6–11 | ligados à flash SPI interna. **Não existem** no conector de 30 pinos. |
| **GPIO 12** | strap MTDI. **Se estiver em HIGH no boot, a placa configura a flash para 1,8 V e não inicializa.** É o erro mais destrutivo dessa lista. |
| GPIO 0 | strap de boot. HIGH = execução normal, LOW = modo de gravação. |
| GPIO 2 | strap + LED da placa. |
| GPIO 15 | strap; controla o log de boot. |
| GPIO 34–39 | **somente entrada**, sem pull-up interno. Não servem para I2C. |
| GPIO 1 / 3 (TX0/RX0) | é a UART usada para gravar e para falar com o PC. **Este projeto usa essa UART**, então não podem ser reaproveitados. |

**GPIO 21 (SDA) e GPIO 22 (SCL)** são os padrões de I2C do ESP32, não são pinos
de strap, e estão livres no conector de 30 pinos. É a escolha certa.

## 6. Esquema de ligação

### I2C (o caso provável)

```
     ESP32 DevKit V1 (30 pinos)              Módulo OLED 128x64 I2C
    ┌──────────────────────────┐            ┌─────────────────────┐
    │                          │            │                     │
    │  3V3  ●──────────────────┼────────────┼──● VCC              │
    │                          │            │                     │
    │  GND  ●──────────────────┼────────────┼──● GND              │
    │                          │            │                     │
    │  D21  ●──────────────────┼────────────┼──● SDA              │
    │       (GPIO21)           │            │                     │
    │  D22  ●──────────────────┼────────────┼──● SCL              │
    │       (GPIO22)           │            │                     │
    └──────────────────────────┘            └─────────────────────┘
             │                                        ▲
             └── USB-C para o PC                      │
                                              endereço I2C: 0x3C
                                              (alguns módulos: 0x3D)
```

**Tabela de ligação:**

| Módulo OLED | ESP32 | Observação |
|---|---|---|
| VCC | **3V3** (pino 16) | **nunca** VIN/5V — ver a seção 4 |
| GND | GND (pino 14 ou 17) | qualquer um dos dois |
| SDA | **GPIO21** (D21) | pino 26 |
| SCL | **GPIO22** (D22) | pino 29 |

Pull-ups externos (4,7 kΩ de SDA→3V3 e SCL→3V3) só se houver instabilidade.

### SPI (se o seu módulo tiver 7 pinos)

| Módulo OLED | ESP32 | Observação |
|---|---|---|
| VCC | 3V3 | |
| GND | GND | |
| D0 / SCK / CLK | GPIO18 | VSPI SCK |
| D1 / MOSI / SDA | GPIO23 | VSPI MOSI |
| RES / RST | GPIO17 | qualquer GPIO de saída |
| DC | GPIO16 | qualquer GPIO de saída |
| CS | GPIO5 | strap, mas em HIGH no boot é o estado normal |

Compile com o ambiente `esp32dev-spi`.

### Ordem de montagem

1. **Placa desconectada do USB.**
2. Encaixe ESP32 e módulo OLED na protoboard, em trilhas separadas.
3. GND primeiro, depois 3V3, depois SDA e SCL.
4. **Confira VCC no 3V3 antes de energizar** — este é o passo que evita dano.
5. Conecte o USB. O módulo deve acender (mesmo que só um flash) ao energizar.

## 7. INFORMAÇÕES QUE PRECISO CONFIRMAR

Nada abaixo impede o projeto de rodar — há um padrão razoável para cada item.
Mas confirmar reduz a depuração a quase zero.

### Prioridade alta

1. **Quantos pinos tem o módulo OLED?**
   4 → I2C (assumido). 7 → SPI, use o ambiente `esp32dev-spi`.

2. **Está escrito algum controlador no módulo ou na embalagem?**
   Procure por "SSD1306", "SH1106", "SSD1309". Se o módulo for de **1,3 polegada**,
   é muito provavelmente **SH1106**.
   *Se não souber:* grave assim mesmo e olhe o resultado — imagem deslocada 2 px
   significa SH1106, e dá para corrigir sem regravar.

3. **Qual o chip da ponte USB da placa?**
   Olhe o CI pequeno perto do conector USB: `CP2102` (Silicon Labs) ou `CH340`/
   `CH9102` (WCH). Determina qual driver instalar no Windows.
   *Se não souber:* rode `oledmirror ports` — ele diz o nome do dispositivo.

### Prioridade média

4. **A placa é mesmo ESP32 clássico?** Se estiver escrito **S2, S3 ou C3** na
   blindagem metálica do módulo, troque o ambiente do PlatformIO — o USB nativo
   dessas famílias remove o gargalo da UART.

5. **Tamanho do painel:** 0,96" (quase certamente SSD1306) ou 1,3" (quase
   certamente SH1106).

6. **O módulo tem jumper de endereço?** Se sim, e estiver em 0x3D, o firmware
   detecta sozinho — mas é bom saber.

### Baixa prioridade

7. **Cor do painel** (branco / azul / amarelo-azul). Não muda nada no software.
   Painéis de duas cores têm as ~16 linhas de cima em cor diferente, o que na
   prática corta uma faixa da imagem espelhada.

## 8. Limitação honesta sobre o resultado visual

Vale dizer isto antes de você montar, não depois:

Uma tela de **1920×1080 tem 2 073 600 pixels**. O painel tem **8 192**. O
espelhamento descarta **99,6 %** da informação, e ainda reduz cada pixel a
ligado/desligado.

**O que dá para ver:** a disposição geral das janelas, movimento, se um vídeo
está tocando, a forma do que está na tela, a silhueta do cursor.

**O que não dá para ver:** absolutamente nenhum texto. Uma linha de texto de
1080p ocupa menos de meio pixel no painel.

**Como obter um resultado realmente útil:** em vez de espelhar o monitor inteiro,
espelhe uma **região** (ex.: 512×256) ou uma **janela pequena**. Aí a redução é
de 4× em vez de 15×, e o conteúdo fica legível. A aplicação suporta os dois modos
justamente por isso.

by caiothevisual