#!/usr/bin/env python3
"""
Cliente de teste em Python para o firmware OledMirror.

Cobre as fases 2 a 4 do roteiro de integracao (ver README) sem precisar do
programa em .NET - util quando o Windows bloqueia executaveis compilados
localmente (Smart App Control).

Requisito: pyserial  ->  pip install pyserial

Uso:
    python tools/oledmirror_test.py ports
    python tools/oledmirror_test.py info
    python tools/oledmirror_test.py text "OLA MUNDO"
    python tools/oledmirror_test.py pattern checkerboard
    python tools/oledmirror_test.py clear
    python tools/oledmirror_test.py stats
    python tools/oledmirror_test.py contrast 200
    python tools/oledmirror_test.py controller sh1106
    python tools/oledmirror_test.py selftest        (nao precisa de placa)

Opcoes: --port COM5  --baud 921600  --verbose

O protocolo implementado aqui e' o de docs/PROTOCOL.md e precisa continuar
identico ao de firmware/src/protocol/ e desktop/OledMirror.Core/Protocol/.
"""

import argparse
import struct
import sys
import time

# Protocolo (docs/PROTOCOL.md)

SOF0, SOF1 = 0xAA, 0x55
VERSION = 0x01
HEADER_SIZE = 8
MAX_PAYLOAD = 2048

WIDTH, HEIGHT = 128, 64
FRAME_BYTES = WIDTH * HEIGHT // 8

CMD_HELLO = 0x01
CMD_PING = 0x02
CMD_STREAM_BEGIN = 0x03
CMD_STREAM_END = 0x04
CMD_CLEAR = 0x05
CMD_SET_CONFIG = 0x06
CMD_GET_INFO = 0x07
CMD_GET_STATS = 0x08
CMD_SYNC = 0x09
CMD_TEXT = 0x0A
CMD_FRAME_RAW = 0x10

CMD_HELLO_ACK = 0x81
CMD_PONG = 0x82
CMD_ACK = 0x83
CMD_NACK = 0x84
CMD_FRAME_ACK = 0x85
CMD_INFO = 0x86
CMD_STATS = 0x87
CMD_LOG = 0x8F

CFG_CONTRAST = 0x01
CFG_CONTROLLER = 0x07

NACK_REASONS = {
    0x01: "CRC invalido",
    0x02: "tamanho de payload incompativel",
    0x03: "comando desconhecido",
    0x04: "versao nao suportada",
    0x05: "ocupado / fila cheia",
    0x06: "payload malformado",
    0x07: "nao esta em modo de streaming",
    0x08: "erro do display",
}

CONTROLLERS = {0: "desconhecido", 1: "SSD1306", 2: "SH1106", 3: "SSD1309", 4: "SH1107"}
CAPABILITIES = ["RLE", "DELTA", "TEXT", "contraste", "estatisticas", "inversao/rotacao"]
LOG_LEVELS = {0: "DEBUG", 1: "INFO", 2: "AVISO", 3: "ERRO"}

ESP32_VIDS = {0x10C4: "CP210x", 0x1A86: "CH340/CH9102", 0x303A: "Espressif USB nativo"}


def crc8(data, crc=0x00):
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if crc & 0x80 else (crc << 1) & 0xFF
    return crc


def crc16(data, crc=0xFFFF):
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def encode_packet(command, seq, payload=b""):
    payload = bytes(payload)
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("payload maior que 2048 bytes")
    header = bytes([SOF0, SOF1, VERSION, command]) + struct.pack("<H", len(payload)) + bytes([seq & 0xFF])
    header += bytes([crc8(header)])
    return header + payload + struct.pack("<H", crc16(payload))


class PacketParser:
    """Mesma maquina de estados do firmware (docs/PROTOCOL.md, secao 8)."""

    def __init__(self):
        self.buf = bytearray()

    def feed(self, data):
        self.buf += data
        packets = []
        while True:
            start = self.buf.find(bytes([SOF0, SOF1]))
            if start < 0:
                # Guarda um 0xAA solto no fim: pode ser o inicio de uma assinatura.
                self.buf = self.buf[-1:] if self.buf[-1:] == bytes([SOF0]) else bytearray()
                return packets
            del self.buf[:start]
            if len(self.buf) < HEADER_SIZE:
                return packets
            header = self.buf[:HEADER_SIZE]
            if crc8(header[:7]) != header[7]:
                del self.buf[:1]
                continue
            length = header[4] | (header[5] << 8)
            if header[2] != VERSION or length > MAX_PAYLOAD:
                del self.buf[:2]
                continue
            total = HEADER_SIZE + length + 2
            if len(self.buf) < total:
                return packets
            payload = bytes(self.buf[HEADER_SIZE:HEADER_SIZE + length])
            received = self.buf[total - 2] | (self.buf[total - 1] << 8)
            if crc16(payload) != received:
                del self.buf[:2]
                continue
            packets.append((header[3], header[6], payload))
            del self.buf[:total]


# Frames de teste, no layout nativo do painel: byte = pagina*128 + x, bit = y%8

def pack_frame(pixel):
    frame = bytearray(FRAME_BYTES)
    for y in range(HEIGHT):
        for x in range(WIDTH):
            if pixel(x, y):
                frame[(y // 8) * WIDTH + x] |= 1 << (y % 8)
    return bytes(frame)


BAYER4 = [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]]

PATTERNS = {
    "checkerboard": lambda x, y: ((x // 8) + (y // 8)) % 2 == 0,
    "border": lambda x, y: x in (0, WIDTH - 1) or y in (0, HEIGHT - 1)
                           or x - 2 * y in (0, 1) or (WIDTH - 1 - x) - 2 * y in (0, 1),
    "stripes": lambda x, y: (x // 4) % 2 == 0,
    "lines": lambda x, y: y % 2 == 0,
    "gradient": lambda x, y: (x * 16 // WIDTH) > BAYER4[y % 4][x % 4],
    "white": lambda x, y: True,
    "black": lambda x, y: False,
}


def frame_preview(frame):
    """Previa no terminal, dois pixels por caractere."""
    rows = []
    for y in range(0, HEIGHT, 2):
        line = []
        for x in range(WIDTH):
            top = frame[(y // 8) * WIDTH + x] >> (y % 8) & 1
            bot = frame[((y + 1) // 8) * WIDTH + x] >> ((y + 1) % 8) & 1
            line.append(" ▀▄█"[top | (bot << 1)])
        rows.append("".join(line))
    return "\n".join(rows)


# Serial

def list_ports():
    from serial.tools import list_ports as lp
    return sorted(lp.comports(), key=lambda p: p.device)


def guess_port():
    ports = list_ports()
    for p in ports:
        if p.vid in ESP32_VIDS:
            return p.device
    return ports[0].device if ports else None


class Device:
    def __init__(self, port, baud, verbose=False):
        import serial
        self.verbose = verbose
        self.seq = 0
        self.parser = PacketParser()
        self.pending = []
        # DTR/RTS desligados ANTES de abrir: nas DevKits eles ligam no EN/GPIO0 e
        # resetariam a placa (mesma configuracao da aplicacao em C#).
        self.ser = serial.Serial()
        self.ser.port = port
        self.ser.baudrate = baud
        self.ser.timeout = 0.05
        self.ser.dtr = False
        self.ser.rts = False
        self.ser.open()

    def close(self):
        self.ser.close()

    def send(self, command, payload=b""):
        packet = encode_packet(command, self.seq, payload)
        self.seq = (self.seq + 1) & 0xFF
        if self.verbose:
            print(f"  -> cmd 0x{command:02X} ({len(payload)} bytes)")
        self.ser.write(packet)

    def wait_for(self, commands, timeout=1.0):
        """Le pacotes ate chegar um dos comandos pedidos (ou NACK). Mostra os LOGs."""
        deadline = time.monotonic() + timeout
        while True:
            while self.pending:
                cmd, seq, payload = self.pending.pop(0)
                if self.verbose:
                    print(f"  <- cmd 0x{cmd:02X} ({len(payload)} bytes)")
                if cmd == CMD_LOG and payload:
                    level = LOG_LEVELS.get(payload[0], str(payload[0]))
                    print(f"  [ESP32 {level}] {payload[1:].decode('utf-8', 'replace')}")
                    continue
                if cmd in commands or cmd == CMD_NACK:
                    return cmd, payload
            if time.monotonic() > deadline:
                return None, None
            data = self.ser.read(self.ser.in_waiting or 1)
            if data:
                self.pending += self.parser.feed(data)

    def request(self, command, payload, expect, timeout=1.0):
        self.send(command, payload)
        cmd, reply = self.wait_for(expect, timeout)
        if cmd == CMD_NACK:
            reason = NACK_REASONS.get(reply[1], f"0x{reply[1]:02X}") if len(reply) > 1 else "?"
            raise RuntimeError(f"o ESP32 recusou o comando 0x{command:02X}: {reason}")
        if cmd is None:
            raise TimeoutError(f"sem resposta ao comando 0x{command:02X}")
        return reply

    def connect(self, attempts=5):
        """Ressincroniza (64 zeros + SYNC) e faz o HELLO. Tenta de novo se a placa
        estiver reiniciando - abrir a porta as vezes reseta o ESP32."""
        for attempt in range(attempts):
            self.ser.reset_input_buffer()
            self.parser = PacketParser()
            self.pending = []
            self.ser.write(bytes(64))
            try:
                self.request(CMD_SYNC, b"", {CMD_ACK}, timeout=0.5)
                return self.request(CMD_HELLO, bytes([VERSION, 0, 0, 0]), {CMD_HELLO_ACK})
            except (TimeoutError, RuntimeError):
                if self.verbose:
                    print(f"  tentativa {attempt + 1} sem resposta")
                time.sleep(0.5)
        raise TimeoutError("o ESP32 nao respondeu ao SYNC/HELLO")


def describe_info(p):
    caps = p[4] | (p[5] << 8)
    lines = [
        f"  protocolo     : v{p[0]}",
        f"  firmware      : {p[1]}.{p[2]}.{p[3]}",
        f"  painel        : {p[6]}x{p[7]}",
        f"  controlador   : {CONTROLLERS.get(p[8], p[8])}",
        f"  barramento    : {'SPI' if p[9] else 'I2C'}" + ("" if p[9] else f" (endereco 0x{p[10]:02X})"),
        f"  fila          : {p[11]} frames",
        f"  capacidades   : {', '.join(n for i, n in enumerate(CAPABILITIES) if caps >> i & 1) or '-'}",
    ]
    if len(p) > 12:
        lines.insert(0, f"  nome          : {p[12:].decode('utf-8', 'replace')}")
    if p[8] == 0:
        lines.append("\n  ATENCAO: o painel nao foi detectado. Confira VCC(3V3), GND, SDA(21), SCL(22).")
    return "\n".join(lines)


# Self-test: vetores de docs/PROTOCOL.md, secao 9 (gerados pelo C++ do firmware)

def selftest():
    ok = True

    def check(name, got, want):
        nonlocal ok
        good = got == want
        ok &= good
        print(f"  [{'ok' if good else 'FALHOU'}] {name}" + ("" if good else f": {got} != {want}"))

    check("CRC8 de '123456789'", hex(crc8(b"123456789")), hex(0xF4))
    check("CRC16 de '123456789'", hex(crc16(b"123456789")), hex(0x29B1))
    check("PING vazio, seq 0", encode_packet(CMD_PING, 0).hex().upper(), "AA55010200000053FFFF")
    check("HELLO seq 7", encode_packet(CMD_HELLO, 7, bytes([1, 7, 0, 0])).hex().upper(),
          "AA550101040007D701070000E477")

    raw = encode_packet(CMD_FRAME_RAW, 42, bytes(range(256)) * 4)
    check("FRAME_RAW seq 42, cabecalho", raw[:8].hex().upper(), "AA55011000042A9A")

    parser = PacketParser()
    noisy = b"\x00\xAA\x13lixo" + encode_packet(CMD_PONG, 3, b"abcd") + b"\xAA"
    got = parser.feed(noisy[:9]) + parser.feed(noisy[9:])
    check("parser com lixo e fragmentacao", got, [(CMD_PONG, 3, b"abcd")])

    print("\nTudo certo." if ok else "\nHa falhas: o script diverge do firmware.")
    return 0 if ok else 1


# Comandos

def main():
    ap = argparse.ArgumentParser(description="Cliente de teste do OledMirror (firmware ESP32).")
    ap.add_argument("action", choices=["ports", "info", "text", "pattern", "clear", "stats",
                                       "contrast", "controller", "ping", "selftest"])
    ap.add_argument("value", nargs="?", help="texto, nome do padrao, contraste ou controlador")
    ap.add_argument("--port", help="porta serial, ex.: COM5 (padrao: detecta sozinho)")
    ap.add_argument("--baud", type=int, default=921600)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if args.action == "selftest":
        return selftest()

    try:
        import serial  # noqa: F401
    except ImportError:
        print("Falta o pyserial. Instale com:  pip install pyserial")
        return 2

    if args.action == "ports":
        ports = list_ports()
        if not ports:
            print("Nenhuma porta serial encontrada. O driver CP210x/CH340 esta instalado?")
        for p in ports:
            chip = ESP32_VIDS.get(p.vid)
            print(f"  {p.device:8} {p.description}" + (f"   <- provavelmente o ESP32 ({chip})" if chip else ""))
        return 0

    port = args.port or guess_port()
    if not port:
        print("Nenhuma porta serial encontrada. Use --port COMx.")
        return 2

    print(f"Abrindo {port} a {args.baud} baud...")
    try:
        dev = Device(port, args.baud, args.verbose)
    except Exception as e:  # serial.SerialException e afins
        print(f"Nao foi possivel abrir {port}: {e}")
        print("Feche o monitor serial (PlatformIO/Arduino/VS Code) - ele segura a porta.")
        return 2

    try:
        info = dev.connect()
        print("Conectado.\n")

        if args.action == "info":
            print(describe_info(info))

        elif args.action == "ping":
            token = struct.pack("<I", int(time.time()) & 0xFFFFFFFF)
            started = time.perf_counter()
            reply = dev.request(CMD_PING, token, {CMD_PONG})
            ms = (time.perf_counter() - started) * 1000
            print(f"PONG em {ms:.1f} ms" + ("" if reply == token else " (token diferente!)"))

        elif args.action == "text":
            text = args.value or "OLA MUNDO"
            data = text.encode("utf-8")[:63]
            dev.request(CMD_TEXT, data, {CMD_ACK})
            print(f"Texto enviado: {text}")

        elif args.action == "clear":
            dev.request(CMD_CLEAR, b"", {CMD_ACK})
            print("Painel limpo.")

        elif args.action == "pattern":
            name = (args.value or "checkerboard").lower()
            if name not in PATTERNS:
                print(f"Padrao desconhecido. Opcoes: {', '.join(PATTERNS)}")
                return 1
            frame = pack_frame(PATTERNS[name])
            # Sem STREAM_BEGIN de proposito: fora do streaming a imagem fica no
            # painel; dentro dele o firmware volta para a tela de espera em 5 s.
            reply = dev.request(CMD_FRAME_RAW, frame, {CMD_FRAME_ACK}, timeout=2.0)
            render_us = reply[2] | (reply[3] << 8)
            print(f"Padrao '{name}' enviado (render no ESP32: {render_us} us).\n")
            print(frame_preview(frame))

        elif args.action == "stats":
            s = dev.request(CMD_GET_STATS, b"", {CMD_STATS})
            applied, dropped, crc, resyncs, render, heap, uptime = struct.unpack("<IIIIHHI", s[:24])
            print(f"  frames aplicados : {applied}")
            print(f"  frames rejeitados: {dropped}")
            print(f"  erros de CRC     : {crc}")
            print(f"  resyncs          : {resyncs}")
            print(f"  render           : {render} us")
            print(f"  heap livre       : {heap} KB")
            print(f"  uptime           : {uptime} s")

        elif args.action == "contrast":
            value = max(0, min(255, int(args.value or 127)))
            dev.request(CMD_SET_CONFIG, bytes([CFG_CONTRAST, 1, value]), {CMD_ACK})
            print(f"Contraste ajustado para {value}.")

        elif args.action == "controller":
            names = {v.lower(): k for k, v in CONTROLLERS.items() if k}
            name = (args.value or "").lower()
            if name not in names:
                print(f"Controlador desconhecido. Opcoes: {', '.join(names)}")
                return 1
            dev.request(CMD_SET_CONFIG, bytes([CFG_CONTROLLER, 1, names[name]]), {CMD_ACK})
            dev.wait_for(set(), timeout=0.3)  # mostra o LOG do firmware
            print(f"Controlador {name.upper()} salvo. Aperte EN/RST na placa para valer.")

        return 0

    except (TimeoutError, RuntimeError) as e:
        print(f"Erro: {e}")
        print("Verifique: firmware gravado, porta certa (--port), baud 921600, monitor serial fechado.")
        return 1
    finally:
        dev.close()


if __name__ == "__main__":
    sys.exit(main())