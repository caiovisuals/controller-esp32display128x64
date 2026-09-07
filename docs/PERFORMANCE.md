# DESEMPENHO (ONDE ESTÃO OS GARGALOS)

Todos os números aqui foram **medidos**, não estimados. Os comandos que os
produzem estão indicados em cada seção.

## 1. O caminho completo e o custo de cada etapa

```
captura → resize+cinza → tone map → dither → pack → codifica → USB → ESP32 → painel
```

Medido com `oledmirror simulate --seconds 3 --fps 15` (origem 1920×1080,
Floyd-Steinberg, modo Fit):

| Etapa | Tempo por frame | Observação |
|---|---|---|
| captura | 5,6 ms | fonte sintética; ver a ressalva abaixo |
| processamento | 1,6 ms | resize + tone map + dither + pack |
| codificação | 0,09 ms | escolha automática entre RAW/RLE/DELTA |
| envio | 0,07 ms | escrita na porta |

**Ressalva honesta:** os 5,6 ms de captura são do gerador de desktop sintético,
que **pinta** um quadro 1920×1080 inteiro. Uma captura real por `BitBlt` de um
monitor 1080p costuma ficar em 2–6 ms. Esse número precisa ser reconfirmado no
seu hardware — a aplicação mostra o valor real no campo "Tempos".

**Conclusão:** o processamento no PC **não é o gargalo**. Com ~7,3 ms de trabalho
total por frame, o PC sustentaria mais de 100 FPS.

## 2. Os dois tetos reais

O sistema tem dois limites físicos independentes, e o menor deles manda.

### Teto 1 — a serial (ESP32 clássico)

O ESP32 clássico não tem USB nativo: o PC fala com uma ponte USB-UART. Um frame
cru são 1034 bytes no fio.

| Baud | Bytes/s | FPS máx (frame cru) |
|---|---|---|
| 115200 | 11 520 | **11** |
| 460800 | 46 080 | 44 |
| **921600** (padrão) | **92 160** | **89** |
| 1500000 | 150 000 | 145 |

> Se você usar 115200, o teto é 11 FPS, por mais rápido que seja o resto.
> É por isso que o padrão do projeto é 921600.

### Teto 2 — a escrita no painel

Cada byte em I2C custa 9 clocks (8 de dado + 1 de ACK):

| Barramento | Tempo para 1024 bytes | FPS máx |
|---|---|---|
| I2C a 400 kHz (especificação do SSD1306) | ~23 ms | **~43** |
| I2C a 800 kHz (padrão do projeto, fora de spec) | ~11,5 ms | **~87** |
| SPI a 8 MHz | ~1,0 ms | ~900 |

### O resultado

| Configuração | Teto combinado | **Alvo recomendado** |
|---|---|---|
| ESP32 clássico + I2C 400 kHz + 921600 baud | ~43 FPS | **10–15 FPS** |
| ESP32 clássico + I2C 800 kHz + 921600 baud | ~87 FPS | **15–20 FPS** |
| ESP32 clássico + SPI + 921600 baud | ~89 FPS | **20–30 FPS** |
| ESP32-S3/C3 (USB nativo) + SPI | ~900 FPS | limitado pela captura |

**Por que recomendar bem abaixo do teto:** o teto assume o barramento 100 %
ocupado com dados de frame, sem margem para ACKs, jitter do agendador do Windows
ou variação de carga. Rodar a ~40 % do teto é o que dá um FPS **estável**, e um
FPS estável parece melhor do que um FPS alto que treme.

**Recomendação para o hardware descrito: comece em 10 FPS.** Suba para 15 ou 20
e observe o campo "FPS efetivo" — se ele não acompanhar o solicitado, você achou
o limite da sua montagem.

## 3. Compressão: RAW vs RLE vs DELTA

Esta era uma pergunta em aberto no projeto. Foi **medida**, não decidida por
intuição, com conteúdo realista (desktop simulado com janelas, barra de tarefas,
relógio e cursor) em vez de padrões artificiais.

Reproduza com `oledmirror bench`:

```
cenario       raw B/f  rle B/f  delta B/f  auto B/f   ganho  FPS max
tela parada         9        7          9         7    1.2x     5369
relogio           102       86         11         9   11.0x     4763
cursor           1015      853        287       285    3.6x      312
rolagem          1024      872        780       779    1.3x      117
animacao         1024      914        525       524    2.0x      173
ruido            1024      938        925       916    1.1x      100
detalhe fino        9        8          9         8    1.1x     5212
```

### O que os números dizem

**RLE sozinho não vale a pena.** Ganha 10–15 % em conteúdo típico. A razão é que
o dithering, que é o penúltimo estágio do pipeline, produz justamente o tipo de
padrão alternado que o RLE não comprime. Implementar só RLE teria sido trabalho
por quase nada.

**DELTA é o ganho de verdade**, e o ganho segue exatamente quanto da tela mudou:

* tela parada → nada é transmitido;
* relógio mudando num canto → **11× menos banda**;
* cursor se movendo → **3,6×**;
* janela rolando → 1,3× (quase tudo mudou, delta não tem o que economizar).

**Pior caso está limitado.** Com ruído incompressível, `Auto` fica em 916 B/f
contra 1024 do cru — nunca *pior* que RAW, porque `Auto` calcula os candidatos e
escolhe o menor. Essa é a propriedade que importa: a compressão nunca custa banda.

**Descoberta lateral relevante:** o cenário "detalhe fino" (listras verticais de
1 pixel em 1920×1080) vira uma tela estática após o redimensionamento. Ao reduzir
15×, a média de área transforma o padrão fino em cinza uniforme. Isso não é um
defeito do resize — é o que qualquer filtro correto faz — mas explica por que
detalhes pequenos simplesmente **desaparecem** no espelhamento, em vez de virarem
ruído.

### Decisão

**Padrão: `Auto`.** Calcula RAW, RLE, DELTA e DELTA+RLE e envia o menor. Custa
0,09 ms por frame — irrelevante frente aos 11,5 ms de escrita no painel — e nunca
produz um payload maior que o cru.

Somado ao descarte de frames idênticos, uma tela parada gera **tráfego quase
zero**, o que deixa a banda inteira disponível para quando a tela realmente muda.

## 4. Qualidade de imagem

### Redimensionamento

Reduzir 1920×1080 para 128×64 é uma redução de ~15×. Vizinho-mais-próximo e
bilinear produzem aliasing severo nessa escala: a imagem "cintila" quando algo se
move um pixel na tela de origem.

O projeto usa **média de área (box filter)**, que é o filtro correto para
redução. Para não ler 8,3 milhões de pixels por frame em 4K, o número de amostras
por eixo dentro de cada caixa é limitado a 8 — o que dá ~524 mil leituras por
frame, **independentemente da resolução de origem**. Um monitor 4K custa o mesmo
que um 1080p.

### Modo de encaixe

Para uma origem 16:9 num painel 2:1:

| Modo | Geometria | Perda |
|---|---|---|
| `Stretch` | 128×64 cheio | distorce a proporção |
| **`Fit`** (padrão) | 114×64, barras nas laterais | **nada é cortado** |
| `Crop` | 128×64 cheio | corta 60 px do topo e da base da origem |
| `Letterbox` | largura sempre cheia | corta ou adiciona barras na vertical |

**Padrão `Fit`**, porque num espelhamento de desktop os cantos importam — barra
de tarefas, relógio, bordas de janela. Perder 11 % da altura para preencher o
painel troca informação por área preenchida, o que não é um bom negócio aqui.

### Dithering

| Algoritmo | Melhor para | Custo |
|---|---|---|
| `Threshold` | conteúdo de alto contraste (terminal, texto) | mínimo |
| **`FloydSteinberg`** (padrão) | imagens estáticas, fotos | médio |
| `BayerOrdered4x4` | **tela em movimento** | baixo |
| `BayerOrdered8x8` | gradientes suaves | baixo |
| `Atkinson` | mais contraste, menos ruído | médio |

**Compromisso que vale conhecer:** Floyd-Steinberg dá o melhor detalhe numa
imagem parada, mas a difusão de erro faz o padrão inteiro mudar quando um pixel
da origem muda. Numa tela em movimento isso aparece como um "fervilhar" constante
— e, de quebra, **destrói a eficiência do delta**, porque quase todo byte muda a
cada frame.

**Se você for espelhar vídeo ou tela com muito movimento, use `BayerOrdered4x4`.**
Ele é periódico e estável no tempo: pixels que não mudaram continuam iguais, o
que mantém o delta eficiente.

### Auto-contraste

Ligado por padrão, e faz mais diferença do que qualquer outra opção. Depois de
reduzir um desktop para 8192 pixels, a maior parte do conteúdo cai numa faixa
estreita de cinza médio e, com limiar fixo, o painel apaga por completo ou acende
por completo. O auto-contraste estica o histograma entre os percentis 2 % e 98 %
antes da binarização. Há um teste que demonstra o efeito: uma imagem que rende
**zero** pixels acesos sem ele passa a render ~50 % com ele.

## 5. Alocações no caminho quente

O pipeline roda até 30 vezes por segundo. Alocar por frame significaria coleta de
lixo durante o streaming, que aparece como tremor no FPS.

Todos os buffers são pré-alocados. Há testes que **travam essa propriedade**
(`AllocationTests`), medindo com `GC.GetAllocatedBytesForCurrentThread`:

| Operação | 50 frames | Limite do teste |
|---|---|---|
| processar um frame (resize+dither+pack) | < 4 KB | 4 KB |
| codificar um frame | < 4 KB | 4 KB |
| montar 100 pacotes | < 512 B | 512 B |

## 6. Latência

Medida ponta a ponta: do instante em que o host escreve o pacote até chegar o
`FRAME_ACK`, que o firmware envia **depois** de a imagem estar no painel.

Composição esperada com I2C a 800 kHz e 921600 baud:

```
transmissão do pacote (1034 B a 92 160 B/s)  ≈ 11 ms
decodificação no ESP32                       <  1 ms
escrita no painel                            ≈ 11,5 ms
ACK de volta                                 <  1 ms
                                            ─────────
                                             ≈ 25 ms
```

Somado ao intervalo de captura (100 ms a 10 FPS), o atraso percebido fica em
torno de **60–130 ms**. Perceptível se você comparar lado a lado, irrelevante
para o uso pretendido.

## 7. Se quiser mais desempenho

Em ordem de custo-benefício:

1. **Espelhe uma região ou janela, não o monitor inteiro.** Melhora a qualidade
   muito mais do que qualquer ajuste de software, e reduz o tempo de captura.
2. **Confirme que está em 921600 baud.** 115200 limita a 11 FPS.
3. **Use `BayerOrdered4x4` para conteúdo em movimento.** Mais estável e mantém o
   delta eficiente.
4. **I2C a 800 kHz** (padrão). Se houver artefatos, volte para 400 kHz — é melhor
   ter 10 FPS estáveis do que 20 com falhas.
5. **Migre o painel para SPI.** Reduz a escrita de 11,5 ms para 1 ms; a partir
   daí o gargalo passa a ser só a UART.
6. **Troque para um ESP32-S3 ou C3.** O USB nativo elimina o gargalo da UART.
   Os ambientes já estão prontos no `platformio.ini`.

by caiothevisual