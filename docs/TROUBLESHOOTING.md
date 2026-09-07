# SOLUÇÃO DE PROBLEMAS

Organizado por sintoma. Cada item diz **como distinguir** as causas possíveis,
não só o que tentar.

## O painel não acende de jeito nenhum

**Primeiro, separe os dois problemas possíveis.** Rode:

```bash
oledmirror device --do info
```

| Resultado | Significado |
|---|---|
| `ESP32 conectado ... controlador Unknown`, nome "sem painel" | **O ESP32 está vivo, o painel é que não respondeu.** Problema de fiação/alimentação. |
| Não conecta / handshake sem resposta | Problema de porta, driver ou firmware. Veja a seção seguinte. |

Se o ESP32 responde mas o painel não:

1. **Confira VCC.** Deve estar no **3V3**, não no VIN. (Se estiver no 5 V, veja
   `HARDWARE.md` §4 — pode ter danificado os GPIOs.)
2. **Confira SDA/SCL.** SDA→GPIO21, SCL→GPIO22. É muito fácil invertê-los.
3. **Confira os contatos da protoboard.** Protoboards de 400 furos baratas têm
   trilhas com mau contato; teste em outra fileira.
4. **Reduza o I2C para 400 kHz:** grave com `pio run -e esp32dev-i2c400 -t upload`.
5. **Encurte os fios.** Acima de ~20 cm o I2C degrada rápido em protoboard.

## A imagem aparece deslocada 2 pixels, com lixo na lateral

**Este é o sintoma clássico do SH1106 sendo tratado como SSD1306.** Módulos de
1,3 polegada quase sempre são SH1106.

Não precisa regravar o firmware. Envie a troca de controlador e reinicie a placa:

```bash
# 2 = SH1106
oledmirror device --do contrast --value 127   # confirma que o link está bom
```

Na aplicação, a troca fica em Configuração → controlador; via protocolo é
`SET_CONFIG` com a chave `Controller` = 2. O valor fica gravado na NVS e vale a
partir do próximo boot.

Alternativa: compilar com `-D OLEDMIRROR_DEFAULT_CONTROLLER=oledmirror::kControllerSh1106`.

## "ESP32 desconectado" / handshake sem resposta

Em ordem, o que verificar:

1. **A porta existe?**
   ```bash
   oledmirror ports
   ```
   Nenhuma porta listada → o driver da ponte USB-UART não está instalado.
   Descubra o chip perto do conector USB da placa:
   * **CP2102** → driver da Silicon Labs
   * **CH340 / CH9102** → driver da WCH

2. **Outro programa está com a porta aberta?** O monitor serial do Arduino IDE,
   do PlatformIO ou do VS Code segura a porta com exclusividade. **Feche-o.**
   Esta é a causa mais comum de todas.

3. **O baud confere?** A aplicação usa 921600 por padrão; o firmware também. Se
   você mudou um, mude o outro.

4. **O firmware está gravado?** Uma placa nova vem sem ele. Ver o README.

5. **Cabo USB de carga.** Muitos cabos USB-C baratos só têm as linhas de
   alimentação. Se o dispositivo não aparece em nenhuma porta do sistema, teste
   outro cabo antes de qualquer outra coisa.

6. **Reinicie a placa** (botão EN) e tente reconectar.

## Conecta, mas o FPS efetivo fica bem abaixo do solicitado

Isso é **informação, não defeito**: você pediu mais do que o enlace aguenta.

Olhe o campo "Tempos" e o contador de erros na interface:

| Sintoma | Causa | O que fazer |
|---|---|---|
| Muitos descartes por controle de fluxo | O painel/serial não acompanha | Reduza o FPS |
| Tempo de captura alto (>15 ms) | Monitor muito grande | Espelhe uma região ou janela |
| Latência alta (>50 ms) | I2C a 400 kHz | Tente 800 kHz (`esp32dev`) |
| Tudo baixo, FPS baixo mesmo assim | Baud em 115200 | Mude para 921600 |

Lembre: **115200 baud limita a 11 FPS**, por mais rápido que seja o resto do
sistema. Ver `PERFORMANCE.md` §2.

## A imagem "ferve" / cintila com a tela em movimento

É o Floyd-Steinberg funcionando como esperado. A difusão de erro faz o padrão
inteiro mudar quando um pixel da origem muda.

**Troque o dithering para `BayerOrdered4x4`.** Ele é periódico e estável no
tempo — e, de quebra, mantém o delta eficiente, porque pixels que não mudaram
continuam iguais.

## A imagem fica toda preta ou toda branca

Quase sempre é o auto-contraste desligado com conteúdo de contraste baixo.

1. **Ligue o auto-contraste** (é o padrão). Sem ele, um desktop cujo conteúdo
   fique todo abaixo do limiar apaga o painel por completo.
2. Ajuste o **limiar** se o conteúdo for muito claro ou muito escuro.
3. Se o painel estiver com o fundo aceso, marque **Inverter imagem**.

## O painel mostra a imagem espelhada ou de cabeça para baixo

Alguns módulos têm o vidro montado com orientação diferente. Use `SET_CONFIG`
com `FlipVert` / `FlipHoriz`.

## Erros de CRC ou ressincronizações aumentando

Olhe as estatísticas do parser (no CLI, ao final de qualquer comando `device`).

| Causa | Como confirmar | Solução |
|---|---|---|
| Fios longos / mau contato | Piora ao mexer nos fios | Encurte, reassente |
| Baud alto demais para o cabo | Some ao reduzir para 460800 | Reduza o baud |
| Ruído de fonte | Piora com outros dispositivos USB | Outra porta USB |

O sistema **se recupera sozinho** desses erros — é para isso que existem o CRC e
a ressincronização. Erros ocasionais são normais; erros que crescem sem parar
indicam problema físico.

## A aplicação não lista nenhuma janela para capturar

Janelas menores que 64×64 e janelas sem título são filtradas de propósito
(são quase sempre janelas auxiliares invisíveis de outros processos).

Clique em **Atualizar listas** depois de abrir a janela que você quer.

## A captura de uma janela sai preta

Algumas janelas não respondem a `PrintWindow`. A implementação cai
automaticamente para `BitBlt`, que **não captura a parte coberta por outras
janelas**.

Contorne mantendo a janela visível, ou use o modo **Região** sobre a área dela.

## Nada funciona e eu quero isolar o problema

O projeto foi feito para permitir isolar cada camada:

```bash
# 1. O pipeline de imagem funciona? (não precisa de nada além do PC)
oledmirror preview --pattern Text

# 2. O protocolo funciona ponta a ponta? (ESP32 simulado)
oledmirror simulate --seconds 3 --show

# 3. O PC enxerga a placa?
oledmirror ports

# 4. O firmware responde?
oledmirror device --do info

# 5. O painel escreve?
oledmirror device --do text --text "OLA MUNDO"

# 6. Frames chegam?
oledmirror device --do pattern --pattern Checkerboard

# 7. O que o firmware está vendo?
oledmirror device --do stats
```

O primeiro passo que falhar aponta a camada com problema.

by caiothevisual