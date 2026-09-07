# ARQUITETURA

## INTRODUÇÃO

```
┌──────────────────────────── PC (Windows) ────────────────────────────┐
│                                                                      │
│  ┌────────────────┐  OledMirror.App (WPF)                            │
│  │  MainWindow    │  ← só XAML + WriteableBitmap da prévia           │
│  └───────┬────────┘                                                  │
│          │ binding                                                   │
│  ┌───────▼────────┐  OledMirror.Windows                              │
│  │ MainViewModel  │  ← sem dependência de WPF                        │
│  │ MirrorController│                                                 │
│  └───────┬────────┘                                                  │
│          │                ┌──────────────────────────┐               │
│          │                │ Capture (GDI)            │               │
│          │                │  MonitorCapture          │               │
│          ├────────────────┤  WindowCapture           │               │
│          │                │  RegionCapture           │               │
│          │                │  WmiSerialPortScanner    │               │
│          │                └──────────────────────────┘               │
│  ┌───────▼──────────────────────────────────────────┐  OledMirror.Core│
│  │  MirrorPipeline                                   │  (multiplataforma)│
│  │    captura → processa → codifica → envia          │                │
│  │  ┌─────────────┐ ┌──────────────┐ ┌────────────┐  │                │
│  │  │FrameProcessor│ │ FrameEncoder │ │ DeviceLink │  │                │
│  │  │ Rescaler     │ │  RAW/RLE/    │ │ handshake  │  │                │
│  │  │ ToneMapper   │ │  DELTA/Auto  │ │ fluxo      │  │                │
│  │  │ Ditherer     │ │              │ │ reconexão  │  │                │
│  │  └─────────────┘ └──────────────┘ └─────┬──────┘  │                │
│  └────────────────────────────────────────┬─┴─────────┘               │
└────────────────────────────────────────────┼─────────────────────────┘
                                             │ ITransport
                        ┌────────────────────┴────────────────────┐
                        │                                         │
                 SerialPortTransport                    LoopbackTransport
                        │                                         │
                        │ USB-C                          SimulatedDevice
                        ▼                                  (sem hardware)
┌───────────────────────────── ESP32 ──────────────────────────────────┐
│  LinkTransport  →  PacketParser  →  Session  →  DisplayDriver        │
│  (UART / CDC)      valida CRC,       máquina     U8g2Display          │
│                    tamanho, resync   de estados  (SSD1306/SH1106/...)│
│                                          │                            │
│                                    frame_codec                        │
│                                    RAW/RLE/DELTA                      │
└──────────────────────────────────────┬───────────────────────────────┘
                                       │ I2C (ou SPI)
                                       ▼
                              ┌─────────────────┐
                              │  OLED 128 × 64  │
                              └─────────────────┘
```

## Projetos

| Projeto | Alvo | Papel |
|---|---|---|
| `OledMirror.Core` | `net8.0` | Todo o processamento, protocolo, transporte, pipeline. **Sem nenhuma dependência de Windows.** |
| `OledMirror.Windows` | `net8.0-windows` | Captura GDI, enumeração de monitores/janelas/portas, ViewModel. **Sem WPF.** |
| `OledMirror.App` | `net8.0-windows` + WPF | Só XAML e código de janela. |
| `OledMirror.Cli` | `net8.0` | Prévia, benchmark, simulação, diagnóstico de hardware. |
| `OledMirror.Tests` | `net8.0` | 124 testes. |
| `firmware/` | ESP32 / Arduino | Firmware modular. |

### Por que esta divisão

O objetivo foi **maximizar o que é testável e compilável sem Windows**. Consequência
prática: o `Core` e os testes rodam em qualquer plataforma, o benchmark e a
simulação ponta a ponta rodam sem hardware, e o WPF fica reduzido a XAML — a
camada onde erros são mais baratos de achar.

A `MainViewModel` usa apenas `INotifyPropertyChanged`, `ICommand` e um
`SynchronizationContext`. Nenhum `Dispatcher`, nenhum tipo de WPF. Isso não é
purismo: é o que permite que a lógica da interface seja compilada e verificada
fora do WPF.

## Interfaces principais

| Interface | Onde | Implementações |
|---|---|---|
| `ICaptureSource` | Core | `MonitorCapture`, `WindowCapture`, `RegionCapture`, `TestPatternSource`, `MockDesktopSource` |
| `ICaptureSourceProvider` | Core | `WindowsCaptureSourceProvider` |
| `IImageProcessor` | Core | `FrameProcessor` |
| `ITransport` | Core | `SerialPortTransport`, `LoopbackTransport` |
| `ISerialPortScanner` | Core | `BasicSerialPortScanner`, `WmiSerialPortScanner` |
| `DisplayDriver` | firmware | `U8g2Display` (SSD1306 / SH1106 / SSD1309 / SH1107) |

Foram criadas onde há **mais de uma implementação real** ou onde a troca é
esperada. Não há interface para coisas com uma implementação só.

## Modelo de threads

| Thread | Responsabilidade |
|---|---|
| UI (WPF) | binding, prévia, comandos |
| `MirrorPipeline` | captura → processa → codifica → envia, no ritmo do FPS |
| `DeviceLink` | conectar, handshake, ler e despachar pacotes, reconectar |
| `SimulatedDevice` | lado dispositivo, quando em modo simulado |
| `System.Threading.Timer` | amostra as métricas 2× por segundo |

**Regras de sincronização:**

* O pipeline roda em `AboveNormal` — acima da UI para o ritmo não tremer, mas
  nunca em tempo real, o que travaria a máquina do usuário.
* A escrita no transporte é serializada por um único lock.
* O último frame é publicado sob lock; a UI copia em vez de compartilhar buffer.
* Eventos do link e do pipeline vão para a thread de UI via `SynchronizationContext.Post`.

No firmware **não há threads**: um único loop não bloqueante, com teto de bytes
processados por iteração e **no máximo um frame aplicado por volta** — assim o
loop sempre volta a rodar entre uma escrita no painel e a próxima.

## Decisões de projeto e por quê

### Layout do frame = layout nativo do controlador

O formato do fio é exatamente o da GDDRAM do SSD1306/SH1106 (byte = página×128+x,
bit = y%8). Aplicar um frame no ESP32 é um `memcpy` de 1024 bytes, sem
transposição de bits. Um layout linear "mais natural" custaria uma transposição
de 8192 bits por frame no processador mais lento do sistema.

### CRC8 no cabeçalho, além do CRC16 no payload

Não é redundância. Sem ele, um `LENGTH` corrompido faz o receptor esperar por
bytes que nunca chegam — e o dispositivo trava permanentemente. Ver `PROTOCOL.md` §1.

### Descartar frames quando a janela está cheia

Num espelhamento, o frame mais recente vale mais do que a fila do anterior.
Enfileirar aumentaria a latência sem melhorar nada. O descarte é contabilizado e
exibido, para o usuário perceber que pediu mais FPS do que o enlace aguenta.

### Invalidar o codificador quando um frame se perde

Os deltas se aplicam sobre o frame anterior. Se um frame foi escrito no fio mas
não chegou ao painel (ACK expirado, NACK, reconexão), o codificador passaria a
gerar deltas sobre uma base errada e **o painel divergiria de forma permanente**.
O `DeviceLink` expõe o evento `FrameLost` e o pipeline força o próximo frame a
ser completo. Este caso foi encontrado durante os testes de integração, não em
revisão de código.

### GDI em vez de Windows.Graphics.Capture

Vale explicar, porque a preferência declarada era WGC.

**A favor do WGC:** é a API moderna, tem menos overhead em telas grandes, e
captura corretamente janelas com composição de GPU.

**A favor do GDI, que foi o escolhido:**

* O custo de captura **não é o gargalo** deste sistema. O gargalo é a serial e o
  I2C, que são 3 a 10× mais lentos que qualquer diferença entre GDI e WGC (ver
  `PERFORMANCE.md` §1 e §2). Otimizar a etapa que não é o gargalo é a definição
  de otimização prematura.
* WGC exige interop de WinRT mais Direct3D 11 — várias centenas de linhas de
  interop cujo custo de manutenção é real e permanente.
* WGC precisa do Windows 10 1803+; o GDI funciona em qualquer versão.
* Para **captura de região**, o GDI é direto; no WGC é preciso capturar tudo e
  recortar.
* Para janelas com GPU, o `PrintWindow` com `PW_RENDERFULLCONTENT` resolve o caso
  que motivaria o WGC, e é o que a implementação usa.

**O ponto importante:** existe a interface `ICaptureSource`. Trocar para WGC é
escrever uma classe nova e registrá-la no provedor — nenhuma outra parte do
projeto muda. A decisão foi tomar o caminho simples primeiro e deixar a troca
barata, em vez de pagar a complexidade adiantado por um ganho que as medições
mostram ser irrelevante.

**Quando reconsiderar:** se o campo "Tempos" da interface mostrar a captura
dominando o orçamento por frame no seu hardware, ou se você migrar para
ESP32-S3 com SPI, quando os outros gargalos somem.

### Arduino em vez de ESP-IDF

**A favor do ESP-IDF:** controle fino, menos abstração, melhor para tempo real.

**A favor do Arduino, que foi o escolhido:**

* O firmware é simples: uma UART, um barramento I2C, um loop. Nada aqui pede o
  controle fino do IDF.
* O U8g2 resolve as diferenças entre SSD1306, SH1106, SSD1309 e SH1107 — que é
  exatamente a incerteza principal deste projeto (ver `HARDWARE.md` §2). Ter isso
  pronto vale mais do que qualquer ganho de desempenho.
* O buffer interno do U8g2 já está no layout que usamos, tornando a aplicação de
  um frame um `memcpy`.
* Gravar e depurar é mais simples, o que importa nas fases iniciais.

O código está isolado atrás de `DisplayDriver`, `LinkTransport` e `PacketParser`.
Os módulos de protocolo e codec **não incluem nada de Arduino** — tanto que os
testes nativos deles compilam com `g++` puro.

## Fluxo de um frame

```
1. MirrorPipeline acorda no tick do FPS
2. ICaptureSource.TryCapture         → BGRA, buffer reutilizado, sem cópia
3. Rescaler.ResizeToGray             → média de área + luminância, numa passada
4. ToneMapper.Apply                  → auto-contraste, contraste, gamma
5. Ditherer.DitherAndPack            → binariza e empacota direto em 1024 bytes
6. publica o frame para a prévia
7. FrameEncoder.TryEncode            → RAW/RLE/DELTA/DELTA_RLE, o menor
      └─ igual ao anterior? não envia nada
8. DeviceLink.TrySendFrame           → checa a janela, monta o pacote, escreve
9. ────────────────────── USB ──────────────────────
10. PacketParser (ESP32)             → valida CRC, tamanho, resync
11. ApplyFramePayload                → valida limites, escreve no framebuffer
12. DisplayDriver.Flush              → I2C/SPI para o painel
13. FRAME_ACK com o tempo de render  → libera a janela, mede a latência
```

Etapas 2 a 8 não alocam nada (garantido por teste).

by caiothevisual