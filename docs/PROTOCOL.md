# PROTOCOLO v1

Especificação do enlace serial entre a aplicação Windows e o ESP32.

**Implementações que precisam permanecer idênticas:**

| Lado | Arquivos |
|---|---|
| PC (C#) | `desktop/OledMirror.Core/Protocol/` |
| ESP32 (C++) | `firmware/src/protocol/` |

Há testes de compatibilidade byte a byte nos dois lados (ver a seção final).

## 1. Formato do pacote

```
 offset  tam  campo
 ──────  ───  ──────────────────────────────────────────────
   0      1   SOF0        = 0xAA
   1      1   SOF1        = 0x55
   2      1   VERSION     = 0x01
   3      1   COMMAND
   4      2   LENGTH      (uint16, little-endian, 0..2048)
   6      1   SEQUENCE    (0..255, circular)
   7      1   HEADER_CRC8 (CRC-8/ATM sobre os bytes 0..6)
   8      N   PAYLOAD
  8+N     2   PAYLOAD_CRC16 (CRC-16/CCITT-FALSE, little-endian)
```

* Cabeçalho: **8 bytes**. Trailer: **2 bytes**. Overhead total: **10 bytes**.
* `LENGTH` máximo: **2048**. Um pacote de frame cru tem **1034 bytes** no fio.

### Por que dois CRCs

O CRC8 do cabeçalho não é redundância exagerada — ele resolve um problema
concreto. Sem ele, um `LENGTH` corrompido de `0x0010` para `0xEA60` faria o
receptor esperar por 60 000 bytes que nunca chegariam, e **o dispositivo pararia
de responder para sempre**. Validando o cabeçalho antes de confiar no `LENGTH`,
um cabeçalho corrompido é descartado em 1 byte.

Existe um teste dedicado a esse cenário nos dois lados
(`test_corrupt_length_does_not_stall_the_parser`).

### Definição dos CRCs

| | CRC8 | CRC16 |
|---|---|---|
| Polinômio | 0x07 | 0x1021 |
| Init | 0x00 | 0xFFFF |
| Reflexão | não | não |
| XorOut | 0x00 | 0x0000 |
| **Check de `"123456789"`** | **0xF4** | **0x29B1** |

Os valores de *check* são testados nas duas implementações. Se algum divergir,
nenhum pacote passa e o teste diz de que lado está o erro.

## 2. Comandos

Convenção: **0x00–0x7F** = host → dispositivo, **0x80–0xFF** = dispositivo → host.

### Host → dispositivo

| ID | Nome | Payload | Resposta |
|---|---|---|---|
| 0x01 | `HELLO` | `[ver][caps_lo][caps_hi][rsv]` | `HELLO_ACK` |
| 0x02 | `PING` | `[token u32]` | `PONG` (ecoa o token) |
| 0x03 | `STREAM_BEGIN` | `[flags]` | `ACK` |
| 0x04 | `STREAM_END` | — | `ACK` |
| 0x05 | `CLEAR` | — | `ACK` |
| 0x06 | `SET_CONFIG` | TLV (ver §4) | `ACK` / `NACK` |
| 0x07 | `GET_INFO` | — | `INFO` |
| 0x08 | `GET_STATS` | — | `STATS` |
| 0x09 | `SYNC` | — | `ACK` |
| 0x0A | `TEXT` | UTF-8 | `ACK` |
| 0x10 | `FRAME_RAW` | 1024 bytes | `FRAME_ACK` |
| 0x11 | `FRAME_RLE` | fluxo RLE | `FRAME_ACK` |
| 0x12 | `FRAME_DELTA` | retângulo + dados | `FRAME_ACK` |
| 0x13 | `FRAME_DELTA_RLE` | retângulo + RLE | `FRAME_ACK` |

### Dispositivo → host

| ID | Nome | Payload |
|---|---|---|
| 0x81 | `HELLO_ACK` | ver §3 |
| 0x82 | `PONG` | `[token u32]` |
| 0x83 | `ACK` | `[comando_confirmado]` |
| 0x84 | `NACK` | `[comando][motivo]` |
| 0x85 | `FRAME_ACK` | `[seq][fila_livre][render_us u16][flags]` |
| 0x86 | `INFO` | mesmo formato do `HELLO_ACK` |
| 0x87 | `STATS` | ver §5 |
| 0x8F | `LOG` | `[nível][UTF-8]` |

### Motivos de NACK

| Código | Significado |
|---|---|
| 0x01 | CRC inválido |
| 0x02 | tamanho de payload incompatível com o comando |
| 0x03 | comando desconhecido |
| 0x04 | versão de protocolo não suportada |
| 0x05 | ocupado / fila cheia |
| 0x06 | payload malformado (retângulo fora dos limites, RLE truncado) |
| 0x07 | não está em modo de streaming |
| 0x08 | erro do display |

## 3. HELLO_ACK

```
 offset  tam  campo
   0      1   versão do protocolo
   1      1   firmware major
   2      1   firmware minor
   3      1   firmware patch
   4      2   capacidades (uint16 LE)
   6      1   largura do painel   (128)
   7      1   altura do painel    (64)
   8      1   controlador  (0=desconhecido 1=SSD1306 2=SH1106 3=SSD1309 4=SH1107)
   9      1   barramento   (0=I2C, 1=SPI)
  10      1   endereço I2C (0 se SPI)
  11      1   profundidade da fila de recepção
  12+     -   nome do dispositivo, UTF-8 (opcional)
```

**Bits de capacidade:**

| Bit | Significado |
|---|---|
| 0 | aceita `FRAME_RLE` |
| 1 | aceita `FRAME_DELTA` |
| 2 | aceita `TEXT` |
| 3 | contraste ajustável |
| 4 | reporta estatísticas |
| 5 | inversão / rotação |

O host **só usa codificações que o dispositivo anunciou**. Um firmware antigo,
sem delta, recebe apenas frames crus e continua funcionando.

O campo `controlador` é como a aplicação sabe qual painel foi realmente
detectado — não é um palpite do lado do PC.

## 4. SET_CONFIG (TLV)

Sequência de `[chave][tamanho][valor...]`:

| Chave | Tam | Valor |
|---|---|---|
| 0x01 `Contrast` | 1 | 0..255 |
| 0x02 `Invert` | 1 | 0 / 1 |
| 0x03 `FlipVert` | 1 | 0 / 1 |
| 0x04 `FlipHoriz` | 1 | 0 / 1 |
| 0x05 `DisplayOn` | 1 | 0 / 1 |
| 0x06 `IdleTimeoutMs` | 2 | uint16 LE, 0 desliga |
| 0x07 `Controller` | 1 | grava o controlador na NVS; vale no próximo boot |

**Chaves desconhecidas são ignoradas, não rejeitadas.** É o que permite uma
aplicação nova conversar com um firmware antigo sem quebrar.

A chave `Controller` é a saída para o caso de o palpite SSD1306 estar errado:
a aplicação corrige o painel sem você precisar regravar o firmware.

## 5. STATS

```
 offset  tam  campo
   0      4   frames aplicados       (uint32 LE)
   4      4   frames rejeitados      (uint32 LE)
   8      4   erros de CRC           (uint32 LE)
  12      4   ressincronizações      (uint32 LE)
  16      2   último render em µs    (uint16 LE)
  18      2   heap livre em KB       (uint16 LE)
  20      4   uptime em segundos     (uint32 LE)
```

## 6. Codificação dos frames

O painel é tratado no **layout nativo da GDDRAM** do controlador — o mesmo do
buffer interno do U8g2. Isso faz com que aplicar um frame no ESP32 seja um
`memcpy`, sem transposição de bits:

```
índice do byte = página * 128 + x        (página = y / 8)
bit dentro do byte = y % 8               (bit 0 = linha de cima)
bit = 1  →  pixel ACESO
```

128 × 64 = 8192 pixels ÷ 8 = **1024 bytes por frame**.

### FRAME_RAW (0x10)
Exatamente 1024 bytes. Qualquer outro tamanho → `NACK` de tamanho.

### FRAME_RLE (0x11)

RLE orientado a byte:

```
C em [0x00..0x7F] → literal: os próximos (C + 1) bytes      (1..128)
C em [0x80..0xFF] → repetição: o próximo byte, (C-0x80)+2×   (2..129)
```

Pior caso: 1024 literais = 1024 + 8 bytes de controle = **1032 bytes**, que cabe
com folga no `LENGTH` máximo de 2048. O receptor exige que a expansão dê
**exatamente** 1024 bytes.

### FRAME_DELTA (0x12) e FRAME_DELTA_RLE (0x13)

```
 offset  tam  campo
   0      1   x0  (coluna inicial, 0..127)
   1      1   x1  (coluna final, inclusiva)
   2      1   p0  (página inicial, 0..7)
   3      1   p1  (página final, inclusiva)
   4+     -   dados da região, ordem página-major
              0x12: crus, exatamente (x1-x0+1) * (p1-p0+1) bytes
              0x13: fluxo RLE que expande para esse mesmo tamanho
```

O retângulo trabalha em **páginas de 8 linhas**, não em linhas de pixel. Assim
cada byte da região vai inteiro para a mesma posição no painel — sem mexer em
bits soltos.

**Validação obrigatória no receptor, antes de qualquer escrita:**
`x1 >= x0`, `p1 >= p0`, `x1 < 128`, `p1 < 8`, e o tamanho do payload igual ao
esperado. Há testes para cada uma dessas condições nos dois lados.

## 7. Controle de fluxo

O dispositivo anuncia a profundidade da sua fila no `HELLO_ACK` (padrão: **2**).
O host mantém no máximo esse número de frames sem confirmação.

* O `FRAME_ACK` é enviado **depois** de o frame chegar ao painel, com o tempo de
  render em microssegundos. Isso faz o host medir a latência real até a imagem
  aparecer, não até o byte sair da porta.
* **Janela cheia → o host descarta o frame novo.** É deliberado: num
  espelhamento, o frame mais recente vale mais do que uma fila do anterior. O
  descarte é contabilizado e mostrado na interface.
* Um `FRAME_ACK` perdido por ruído travaria a janela para sempre. Passado
  `FrameAckTimeout` (1 s), o slot mais antigo é liberado, o evento é
  contabilizado, e o codificador é invalidado — o próximo frame vai completo,
  porque o conteúdo do painel deixou de ser conhecido.

## 8. Recuperação de erros

O decodificador é uma máquina de estados incremental, idêntica nos dois lados:

1. **Varredura:** procura a assinatura `0xAA 0x55`; bytes antes disso são
   descartados e contabilizados.
2. **Cabeçalho:** o CRC8 é verificado **antes** de qualquer campo ser usado.
   Falhou → descarta **1 byte** e volta a varrer.
3. **Versão:** diferente de 0x01 → descarta 2 bytes.
4. **Tamanho:** `LENGTH > 2048` → descarta 2 bytes, sem reservar nada.
5. **Payload:** CRC16 errado → descarta **apenas 2 bytes** (a assinatura), não o
   pacote inteiro. O "pacote" pode ter sido um falso positivo dentro de lixo, e a
   varredura precisa poder achar o início real logo adiante.

### Ressincronização explícita

Ao conectar, o host envia **64 bytes 0x00** seguidos de um `SYNC`. Os zeros
desalinham qualquer parser preso no meio de um pacote de uma sessão anterior
(ou no log de boot do ESP32), e o `SYNC` confirma que o canal está limpo.

### Reconexão

O host detecta a queda por erro de I/O da porta ou por **inatividade** (nenhum
pacote por 4 s, com ping de keepalive a cada 2 s). Ao reconectar:

1. a porta é reaberta, com backoff exponencial de 500 ms até 5 s;
2. o parser é zerado;
3. novo `SYNC` + `HELLO`;
4. **o codificador de frames é invalidado** — o próximo frame vai completo.

## 9. Verificação cruzada entre as implementações

O risco real deste projeto não é um bug de lógica, é **as duas implementações
divergirem silenciosamente** e só se descobrir isso com o hardware na mão.

`desktop/OledMirror.Tests/Protocol/CrossImplementationTests.cs` compara os bytes
produzidos pelo C# com vetores **gerados pelo C++ do firmware**:

| Pacote | Bytes no fio |
|---|---|
| `PING` vazio, seq 0 | `AA55010200000053FFFF` |
| `HELLO` seq 7, `01 07 00 00` | `AA550101040007D701070000E477` |
| `FRAME_DELTA` seq 200, 16 bytes | `AA5501121000C8E00120…7B72` |
| `FRAME_RAW` seq 42, 1024 bytes | cabeçalho `AA55011000042A9A`, CRC16 `8DCC` |

**Para regerar os vetores** depois de mudar o protocolo:

```bash
# compila só os módulos puros do firmware, sem Arduino
g++ -std=gnu++17 -D OLEDMIRROR_NATIVE_TEST=1 -I firmware/src \
    ferramenta_de_dump.cpp -o /tmp/xcheck && /tmp/xcheck
```

Chamando `EncodePacket` e imprimindo em hex. Depois atualize os vetores do teste
em C#. Se os dois lados forem alterados de forma coerente, os bytes batem.

Os testes unitários do firmware rodam no PC, sem placa:

```bash
cd firmware && pio test -e native
```

by caiothevisual