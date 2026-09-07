# CONTROLADOR DO ESP32 + DISPLAY 128x64 (OLEDMIRROR) - ᴘᴛ

Espelhamento da tela do PC num display OLED 128×64 ligado a um ESP32 por USB.

```
┌───────────────────────────────┐
│             PC                │
│   Monitor / Janela / Região   │
└──────────────┬────────────────┘
               │ USB-C (protocolo próprio com CRC)
               ▼
        ┌─────────────┐
        │    ESP32    │
        └──────┬──────┘
               │ I2C (ou SPI)
               ▼
        ┌─────────────┐
        │ OLED 128×64 │
        └─────────────┘
```

O painel mostra uma versão monocromática, em tempo real, do que está na tela.
Não é um painel de informações de CPU/RAM — é espelhamento visual.

## Antes de começar: o que esperar do resultado

Vale dizer isto antes de você montar o circuito. Uma tela 1920×1080 tem **2 073 600 pixels**.
O painel tem **8 192**, e cada um só pode estar aceso ou apagado. O espelhamento descarta **99,6 %** da informação.
**Dá para ver:** a disposição das janelas, movimento, se um vídeo está tocando, a forma geral do conteúdo, o cursor se mexendo.
**Não dá para ver:** nenhum texto. Uma linha de texto de 1080p ocupa menos de meio pixel no painel. 
**Para um resultado realmente útil:** espelhe uma **região** (ex.: 512×256) ou uma **janela pequena** em vez do monitor inteiro.
A redução cai de 15× para 4× e o conteúdo fica legível. A aplicação suporta os três modos por causa disso.

## Estado do projeto

| Componente | Estado |
|---|---|
| Pipeline de imagem (resize, tone map, 5 algoritmos de dithering, packing) | pronto, testado |
| Protocolo (framing, CRC duplo, ACK/NACK, controle de fluxo, resync, reconexão) | pronto, testado |
| Codificação RAW / RLE / DELTA / Auto | pronto, testado e **medido** |
| Firmware ESP32 modular | pronto, testes unitários passando |
| Captura Windows (monitor / janela / região) | pronto, compila |
| Aplicação WPF | pronta — **ver a ressalva abaixo** |
| CLI (prévia, benchmark, simulação, diagnóstico) | pronto, em uso |
| 124 testes em C# + 28 no firmware | passando |

> **Ressalva honesta:** o projeto foi desenvolvido em Linux. Tudo foi compilado e
> testado lá, **exceto o projeto WPF** (`OledMirror.App`), que o SDK do .NET não
> consegue compilar fora do Windows. Por isso toda a lógica da interface foi posta
> na `MainViewModel`, que **não depende de WPF e compila e é verificada**; o
> projeto WPF ficou reduzido a XAML e ~60 linhas de código de janela. Ainda assim,
> **espere ter que corrigir algum detalhe de XAML na primeira compilação em
> Windows.** Nenhuma outra parte do sistema depende disso — o CLI faz tudo o que
> a interface faz.

## Comece por aqui

### Sem hardware nenhum

```bash
cd desktop
dotnet run --project OledMirror.Cli -- preview --pattern Text
dotnet run --project OledMirror.Cli -- simulate --seconds 3 --show
dotnet run --project OledMirror.Cli -- bench
```

O primeiro mostra no terminal exatamente o que iria para o painel.
O segundo roda o pipeline completo — incluindo o protocolo — contra um ESP32 simulado.
O terceiro mede a compressão.

### Com hardware

1. Leia **[docs/HARDWARE.md](docs/HARDWARE.md)** e monte o circuito.
2. Grave o firmware.
3. Rode `oledmirror device --do info`.

## Hardware

Resumo; a análise completa, com o porquê de cada escolha, está em
**[docs/HARDWARE.md](docs/HARDWARE.md)**.

| Item | Valor |
|---|---|
| Placa provável | ESP32 clássico (WROOM-32), DevKit V1 de 30 pinos |
| USB | ponte USB-UART (CP2102 ou CH340) — **não** é USB nativo |
| Barramento do painel | I2C, 4 pinos (provável) |
| Endereço I2C | 0x3C (sondado automaticamente; 0x3D também) |
| Controlador | **não confirmado** — SSD1306 assumido, trocável em tempo de execução |

### Ligação

| Módulo OLED | ESP32 | Atenção |
|---|---|---|
| VCC | **3V3** | **nunca 5 V** — os pull-ups do módulo colocariam 5 V nos GPIOs |
| GND | GND | |
| SDA | **GPIO21** | |
| SCL | **GPIO22** | |

Pinos a evitar: GPIO 6–11 (flash), **GPIO 12** (impede o boot se estiver em HIGH),
GPIO 34–39 (só entrada), GPIO 0/2/15 (strapping).

## Instalação

### Pré-requisitos

* **.NET 8 SDK** — https://dotnet.microsoft.com/download
* **PlatformIO** (`pip install platformio`) ou a extensão do VS Code
* **Driver USB-UART** — Silicon Labs (CP2102) ou WCH (CH340), conforme a placa

### Firmware

```bash
cd firmware
pio run -e esp32dev -t upload # ESP32 clássico, I2C a 800 kHz
```

Outros ambientes:

| Ambiente | Quando usar |
|---|---|
| `esp32dev` | padrão: ESP32 clássico, I2C a 800 kHz |
| `esp32dev-i2c400` | se houver artefatos a 800 kHz |
| `esp32dev-spi` | painel SPI de 7 pinos |
| `esp32s3` / `esp32c3` | se a placa for S3 ou C3 (USB nativo, bem mais rápido) |
| `native` | testes unitários do protocolo, no PC |

### Aplicação

```bash
cd desktop
dotnet build OledMirror.sln -c Release # no Windows
dotnet run --project OledMirror.App # interface gráfica
```

Fora do Windows, use o filtro de solução que exclui os projetos de Windows:

```bash
dotnet build OledMirror.CrossPlatform.slnf
dotnet test OledMirror.Tests/OledMirror.Tests.csproj
```

## Uso

### Interface gráfica

Mostra o status da conexão, o dispositivo detectado, resolução de origem e saída,
FPS solicitado e efetivo, latência, bytes e frames enviados, erros, e uma
**prévia ampliada de como o frame fica no painel**.

Controles: monitor / janela / região, modo de encaixe, dithering, FPS, iniciar,
parar, reconectar, e um modo **Somente prévia** que roda tudo sem enviar nada.

As configurações são salvas em
`%APPDATA%\OledMirror\settings.json`.

### Linha de comando

```bash
oledmirror preview  --pattern Text --dither FloydSteinberg 
oledmirror bench
oledmirror simulate --seconds 5 --fps 15
oledmirror ports
oledmirror device  --do info
oledmirror device  --do text --text "OLA MUNDO"
oledmirror device  --do pattern --pattern Checkerboard
oledmirror device  --do stats
```

## Roteiro de integração

Cada fase é verificável de forma independente. **Não pule fases** — é assim que
você descobre em qual camada está o problema.

| Fase | O que provar | Como |
|---|---|---|
| 1 | ESP32 + painel funcionam | grave o firmware; deve aparecer "aguardando o PC" |
| 2 | PC fala com o ESP32 | `oledmirror device --do info` |
| 3 | Texto chega ao painel | `oledmirror device --do text --text "OLA MUNDO"` |
| 4 | Frames chegam ao painel | `oledmirror device --do pattern` |
| 5 | Captura de tela funciona | `oledmirror preview` (só PC) |
| 6 | Pipeline completo sem hardware | `oledmirror simulate --show` |
| 7 | Espelhamento real | abra a interface, escolha o monitor, Iniciar |
| 8 | FPS estável | suba o FPS até o efetivo parar de acompanhar |
| 9 | Reconexão | desconecte o USB com o espelhamento rodando; religue |
| 10 | Ajuste fino | região em vez de monitor, Bayer para movimento |

## Desempenho: o que é realista

Números completos e medidos em **[docs/PERFORMANCE.md](docs/PERFORMANCE.md)**.

O sistema tem dois tetos independentes, e o menor manda:

| Configuração | Teto | **Alvo recomendado** |
|---|---|---|
| ESP32 clássico + I2C 400 kHz + 921600 baud | ~43 FPS | **10–15 FPS** |
| ESP32 clássico + I2C 800 kHz + 921600 baud | ~87 FPS | **15–20 FPS** |
| ESP32 clássico + SPI | ~89 FPS | **20–30 FPS** |

> **A 115200 baud o teto é 11 FPS**, por mais rápido que seja o resto. O padrão do
> projeto é 921600 por causa disso.

**Compressão medida** (`oledmirror bench`, desktop realista):

| Cenário | Banda vs. frame cru |
|---|---|
| tela parada | tráfego ~zero (frames idênticos não são enviados) |
| região pequena mudando (relógio) | **11× menos** |
| cursor se movendo | **3,6× menos** |
| janela rolando | 1,3× menos |
| pior caso (ruído) | 1,1× — nunca *pior* que cru |

RLE sozinho rende só 10–15 % (o dithering produz justamente o padrão que ele não comprime). 
**DELTA é o ganho real.** O modo `Auto` calcula os candidatos e envia o menor, custando 0,09 ms por frame.

## Limitações

Ditas de forma direta:

1. **Nenhum texto é legível** espelhando um monitor inteiro. Física, não software. Use região ou janela.
2. **O baud rate é um teto rígido** no ESP32 clássico, que não tem USB nativo.
3. **1 bit por pixel.** Gradientes viram textura de dithering. É o que o painel é.
4. **O controlador do painel não está confirmado.** SSD1306 é o palpite; se a imagem sair deslocada 2 px, é SH1106 — trocável sem regravar (`SET_CONFIG`).
5. **Captura de janela pode sair preta** em janelas que não respondem a `PrintWindow`. Use o modo região.
6. **Painéis de duas cores** (faixa amarela no topo) cortam uma faixa da imagem.
7. **O projeto WPF não foi compilado** neste ambiente — ver a ressalva acima.
8. **I2C a 800 kHz está fora da especificação** do SSD1306. Funciona na maioria dos módulos; se não funcionar no seu, use `esp32dev-i2c400`.


## Documentação

| Documento | Conteúdo |
|---|---|
| **[docs/HARDWARE.md](docs/HARDWARE.md)** | Análise do hardware, ligação, tensões, GPIOs, **o que precisa ser confirmado** |
| **[docs/PROTOCOL.md](docs/PROTOCOL.md)** | Especificação completa do protocolo |
| **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** | Estrutura, threads, e o porquê de cada decisão |
| **[docs/PERFORMANCE.md](docs/PERFORMANCE.md)** | Gargalos medidos, compressão, qualidade de imagem |
| **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** | Problemas por sintoma |

## Testes

```bash
cd desktop && dotnet test OledMirror.Tests/OledMirror.Tests.csproj   # 124 testes
cd firmware && pio test -e native                                    # 28 testes
```

Cobrem: protocolo (pacote válido, CRC ruim, tamanho inválido, fragmentado, bytes
extras, perda de sincronização, recuperação), imagem (resize em 7 resoluções,
threshold, Floyd-Steinberg, Bayer, packing, preto/branco/xadrez exatos),
codificação (round-trip de todas as estratégias, validação defensiva de payload
hostil), integração ponta a ponta (handshake, controle de fluxo, link com ruído,
reconexão, convergência do painel) e **ausência de alocações no caminho quente**.

Há ainda testes de **compatibilidade byte a byte entre o C# e o C++**: vetores
gerados pelo firmware são verificados contra o encoder do PC, para que as duas
implementações não divirjam em silêncio.

# ESP32 CONTROLLER + 128x64 DISPLAY (OLEDMIRROR) - ᴇɴ

Mirroring the PC screen to a 128×64 OLED display connected to an ESP32 via USB.

```
┌───────────────────────────────┐
│           Desktop             │
│   Monitor / Window / Region   │
└──────────────┬────────────────┘
               │ USB-C (specific protocol with CRC)
               ▼
        ┌─────────────┐
        │    ESP32    │
        └──────┬──────┘
               │ I2C (or SPI)
               ▼
        ┌─────────────┐
        │ OLED 128×64 │
        └─────────────┘
```

The panel displays a real-time, monochrome version of what is on the screen.
It is not a CPU/RAM information panel—it is visual mirroring.

## Before you start: what to expect from the result

It is worth noting this before you build the circuit. A 1920×1080 screen has **2,073,600 pixels**.
The panel has **8,192**, and each one can only be on or off. Mirroring discards **99.6%** of the information.
**What you can see:** window layout, movement, whether a video is playing, the general shape of the content, the moving cursor.
**What you cannot see:** any text. A line of 1080p text occupies less than half a pixel on the panel.
**For a truly useful result:** mirror a **region** (e.g., 512×256) or a **small window** instead of the entire monitor.
The downscaling factor drops from 15× to 4×, making the content legible. The application supports all three modes for this reason.

by caiothevisual