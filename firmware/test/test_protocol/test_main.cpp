#include <unity.h>

#include <string.h>
#include <vector>

#include "framebuffer/frame_codec.cpp"
#include "protocol/crc.cpp"
#include "protocol/parser.cpp"

using namespace oledmirror;

// CRC

void test_crc8_check_value() {
    const uint8_t data[] = "123456789";
    TEST_ASSERT_EQUAL_HEX8(0xF4, Crc8(data, 9));
}

void test_crc16_check_value() {
    const uint8_t data[] = "123456789";
    TEST_ASSERT_EQUAL_HEX16(0x29B1, Crc16(data, 9));
}

void test_crc_empty_is_seed() {
    TEST_ASSERT_EQUAL_HEX8(0x00, Crc8(nullptr, 0));
    TEST_ASSERT_EQUAL_HEX16(0xFFFF, Crc16(nullptr, 0));
}

void test_crc16_detects_bit_flip() {
    uint8_t data[64];
    for (int i = 0; i < 64; ++i) data[i] = static_cast<uint8_t>(i * 7 + 1);
    const uint16_t original = Crc16(data, sizeof(data));

    for (int i = 0; i < 64; ++i) {
        data[i] ^= 0x01;
        TEST_ASSERT_NOT_EQUAL(original, Crc16(data, sizeof(data)));
        data[i] ^= 0x01;
    }
}

// Helpers

static std::vector<uint8_t> MakePacket(uint8_t command, uint8_t sequence,
                                       const uint8_t* payload, uint16_t length) {
    std::vector<uint8_t> out(kOverheadSize + length);
    const size_t n = EncodePacket(out.data(), out.size(), command, sequence, payload, length);
    out.resize(n);
    return out;
}

static std::vector<uint8_t> MakePayload(int length, uint8_t seed = 1) {
    std::vector<uint8_t> p(length);
    for (int i = 0; i < length; ++i) p[i] = static_cast<uint8_t>(i * 31 + seed);
    return p;
}

// Alimenta o parser byte a byte e conta os pacotes validos
static int FeedAll(PacketParser& parser, const std::vector<uint8_t>& data,
                   ParsedPacket* last = nullptr) {
    int count = 0;
    ParsedPacket packet;
    for (uint8_t b : data) {
        if (parser.Push(b, &packet)) {
            ++count;
            if (last != nullptr) *last = packet;
        }
    }
    return count;
}

// Parser

void test_valid_packet_is_decoded() {
    const std::vector<uint8_t> payload = MakePayload(64);
    const std::vector<uint8_t> packet = MakePacket(kCmdFrameRaw, 42, payload.data(), 64);

    PacketParser parser;
    ParsedPacket decoded;
    TEST_ASSERT_EQUAL(1, FeedAll(parser, packet, &decoded));
    TEST_ASSERT_EQUAL_UINT8(kCmdFrameRaw, decoded.command);
    TEST_ASSERT_EQUAL_UINT8(42, decoded.sequence);
    TEST_ASSERT_EQUAL_UINT16(64, decoded.length);
    TEST_ASSERT_EQUAL_UINT8_ARRAY(payload.data(), decoded.payload, 64);
}

void test_empty_payload_is_valid() {
    const std::vector<uint8_t> packet = MakePacket(kCmdPing, 0, nullptr, 0);
    PacketParser parser;
    ParsedPacket decoded;
    TEST_ASSERT_EQUAL(1, FeedAll(parser, packet, &decoded));
    TEST_ASSERT_EQUAL_UINT16(0, decoded.length);
}

void test_maximum_payload_round_trips() {
    const std::vector<uint8_t> payload = MakePayload(kMaxPayloadSize);
    const std::vector<uint8_t> packet = MakePacket(kCmdFrameRle, 3, payload.data(), kMaxPayloadSize);
    PacketParser parser;
    ParsedPacket decoded;
    TEST_ASSERT_EQUAL(1, FeedAll(parser, packet, &decoded));
    TEST_ASSERT_EQUAL_UINT16(kMaxPayloadSize, decoded.length);
}

void test_payload_above_maximum_is_refused_by_encoder() {
    uint8_t out[64];
    TEST_ASSERT_EQUAL(0, EncodePacket(out, sizeof(out), kCmdFrameRaw, 0, nullptr, kMaxPayloadSize + 1));
}

void test_bad_payload_crc_is_rejected() {
    const std::vector<uint8_t> payload = MakePayload(32);
    std::vector<uint8_t> packet = MakePacket(kCmdFrameRaw, 1, payload.data(), 32);
    packet.back() ^= 0xFF;

    PacketParser parser;
    TEST_ASSERT_EQUAL(0, FeedAll(parser, packet));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().payload_crc_bad);
}

void test_bad_header_crc_is_rejected() {
    const std::vector<uint8_t> payload = MakePayload(4);
    std::vector<uint8_t> packet = MakePacket(kCmdPing, 1, payload.data(), 4);
    packet[7] ^= 0xFF;

    PacketParser parser;
    TEST_ASSERT_EQUAL(0, FeedAll(parser, packet));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().header_crc_bad);
}

void test_corrupt_length_does_not_stall_the_parser() {
    const std::vector<uint8_t> payload = MakePayload(16);
    std::vector<uint8_t> bad = MakePacket(kCmdFrameRaw, 1, payload.data(), 16);
    bad[4] = 0x60;
    bad[5] = 0xEA;

    const std::vector<uint8_t> good_payload = MakePayload(4, 9);
    const std::vector<uint8_t> good = MakePacket(kCmdPing, 99, good_payload.data(), 4);

    PacketParser parser;
    FeedAll(parser, bad);
    ParsedPacket decoded;
    TEST_ASSERT_EQUAL(1, FeedAll(parser, good, &decoded));
    TEST_ASSERT_EQUAL_UINT8(99, decoded.sequence);
}

void test_length_above_maximum_is_rejected() {
    std::vector<uint8_t> header(kHeaderSize);
    header[0] = kSof0;
    header[1] = kSof1;
    header[2] = kProtocolVersion;
    header[3] = kCmdFrameRaw;
    header[4] = 0xFF;
    header[5] = 0xFF;
    header[6] = 0;
    header[7] = Crc8(header.data(), 7);

    PacketParser parser;
    TEST_ASSERT_EQUAL(0, FeedAll(parser, header));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().length_bad);
}

void test_unsupported_version_is_rejected() {
    const std::vector<uint8_t> payload = MakePayload(4);
    std::vector<uint8_t> packet = MakePacket(kCmdPing, 1, payload.data(), 4);
    packet[2] = 0x7E;
    packet[7] = Crc8(packet.data(), 7);

    PacketParser parser;
    TEST_ASSERT_EQUAL(0, FeedAll(parser, packet));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().version_bad);
}

void test_garbage_before_packet_is_discarded() {
    std::vector<uint8_t> stream = {0x00, 0xFF, 0xAA, 0x12, 0x55, 0xAA, 0x99};
    const std::vector<uint8_t> payload = MakePayload(8);
    const std::vector<uint8_t> packet = MakePacket(kCmdPong, 5, payload.data(), 8);
    stream.insert(stream.end(), packet.begin(), packet.end());

    PacketParser parser;
    ParsedPacket decoded;
    TEST_ASSERT_EQUAL(1, FeedAll(parser, stream, &decoded));
    TEST_ASSERT_EQUAL_UINT8(5, decoded.sequence);
    TEST_ASSERT_GREATER_THAN_UINT32(0, parser.stats().discarded_bytes);
}

void test_back_to_back_packets_all_decode() {
    std::vector<uint8_t> stream;
    for (int i = 0; i < 10; ++i) {
        const std::vector<uint8_t> payload = MakePayload(16, static_cast<uint8_t>(i));
        const std::vector<uint8_t> packet = MakePacket(kCmdPing, static_cast<uint8_t>(i), payload.data(), 16);
        stream.insert(stream.end(), packet.begin(), packet.end());
    }

    PacketParser parser;
    TEST_ASSERT_EQUAL(10, FeedAll(parser, stream));
}

void test_parser_recovers_after_heavy_corruption() {
    std::vector<uint8_t> stream;
    for (int i = 0; i < 50; ++i) {
        const std::vector<uint8_t> payload = MakePayload(64, static_cast<uint8_t>(i));
        const std::vector<uint8_t> packet = MakePacket(kCmdFrameRaw, static_cast<uint8_t>(i), payload.data(), 64);
        stream.insert(stream.end(), packet.begin(), packet.end());
    }

    unsigned seed = 12345;
    for (int i = 0; i < 40; ++i) {
        seed = seed * 1103515245u + 12345u;
        stream[(seed >> 8) % stream.size()] ^= static_cast<uint8_t>(1 << ((seed >> 4) & 7));
    }

    PacketParser parser;
    ParsedPacket packet;
    for (uint8_t b : stream) {
        if (!parser.Push(b, &packet)) continue;
        const std::vector<uint8_t> expected = MakePayload(packet.length, packet.sequence);
        TEST_ASSERT_EQUAL_UINT8_ARRAY(expected.data(), packet.payload, packet.length);
    }

    const std::vector<uint8_t> clean_payload = MakePayload(8, 200);
    const std::vector<uint8_t> clean = MakePacket(kCmdPong, 200, clean_payload.data(), 8);
    TEST_ASSERT_EQUAL(1, FeedAll(parser, clean));
}

// RLE

void test_rle_round_trip_zeros() {
    uint8_t input[kFrameBytes];
    memset(input, 0, sizeof(input));

    uint8_t encoded[64];
    size_t o = 0;
    size_t remaining = kFrameBytes;
    while (remaining > 0) {
        const size_t n = remaining > 129 ? 129 : remaining;
        encoded[o++] = static_cast<uint8_t>(0x80 | (n - 2));
        encoded[o++] = 0x00;
        remaining -= n;
    }

    uint8_t out[kFrameBytes];
    TEST_ASSERT_EQUAL_INT(kFrameBytes, RleDecode(encoded, o, out, sizeof(out)));
    TEST_ASSERT_EQUAL_UINT8_ARRAY(input, out, kFrameBytes);
}

void test_rle_literal_run() {
    const uint8_t encoded[] = {0x02, 0xAA, 0xBB, 0xCC};
    uint8_t out[8];
    TEST_ASSERT_EQUAL_INT(3, RleDecode(encoded, sizeof(encoded), out, sizeof(out)));
    TEST_ASSERT_EQUAL_HEX8(0xAA, out[0]);
    TEST_ASSERT_EQUAL_HEX8(0xCC, out[2]);
}

void test_rle_truncated_stream_is_refused() {
    const uint8_t literal_without_data[] = {0x05};
    uint8_t out[64];
    TEST_ASSERT_EQUAL_INT(-1, RleDecode(literal_without_data, 1, out, sizeof(out)));

    const uint8_t repeat_without_value[] = {0x80};
    TEST_ASSERT_EQUAL_INT(-1, RleDecode(repeat_without_value, 1, out, sizeof(out)));
}

void test_rle_overflow_is_refused() {
    const uint8_t hostile[] = {0xFF, 0x41};
    uint8_t out[8];
    TEST_ASSERT_EQUAL_INT(-1, RleDecode(hostile, sizeof(hostile), out, sizeof(out)));

    // E o destino nao pode ter sido tocado
    uint8_t untouched[8];
    memset(untouched, 0, sizeof(untouched));
    memset(out, 0, sizeof(out));
    RleDecode(hostile, sizeof(hostile), out, sizeof(out));
    TEST_ASSERT_EQUAL_UINT8_ARRAY(untouched, out, sizeof(out));
}

// Frames

void test_raw_frame_applies() {
    uint8_t payload[kFrameBytes];
    memset(payload, 0, sizeof(payload));
    for (size_t i = 0; i < kFrameBytes; ++i) payload[i] = static_cast<uint8_t>(i);

    uint8_t framebuffer[kFrameBytes];
    uint8_t scratch[kFrameBytes] = {};
    memset(framebuffer, 0, sizeof(framebuffer));

    TEST_ASSERT_EQUAL(kDecodeOk,
        ApplyFramePayload(kCmdFrameRaw, payload, kFrameBytes, framebuffer, scratch));
    TEST_ASSERT_EQUAL_UINT8_ARRAY(payload, framebuffer, kFrameBytes);
}

void test_raw_frame_with_wrong_length_is_refused() {
    uint8_t payload[kFrameBytes];
    uint8_t framebuffer[kFrameBytes];
    uint8_t scratch[kFrameBytes];
    memset(payload, 0x5A, sizeof(payload));

    TEST_ASSERT_EQUAL(kDecodeBadLength,
        ApplyFramePayload(kCmdFrameRaw, payload, kFrameBytes - 1, framebuffer, scratch));
    TEST_ASSERT_EQUAL(kDecodeBadLength,
        ApplyFramePayload(kCmdFrameRaw, payload, kFrameBytes + 1, framebuffer, scratch));
}

void test_delta_frame_applies_to_the_right_region() {
    uint8_t framebuffer[kFrameBytes];
    uint8_t scratch[kFrameBytes] = {};
    memset(framebuffer, 0, sizeof(framebuffer));

    // Retangulo x 10..13, pages 2..3 => 4 colunas x 2 pages = 8 bytes
    uint8_t payload[4 + 8];
    payload[0] = 10; payload[1] = 13; payload[2] = 2; payload[3] = 3;
    for (int i = 0; i < 8; ++i) payload[4 + i] = static_cast<uint8_t>(0xF0 + i);

    TEST_ASSERT_EQUAL(kDecodeOk,
        ApplyFramePayload(kCmdFrameDelta, payload, sizeof(payload), framebuffer, scratch));

    TEST_ASSERT_EQUAL_HEX8(0xF0, framebuffer[2 * kDisplayWidth + 10]);
    TEST_ASSERT_EQUAL_HEX8(0xF3, framebuffer[2 * kDisplayWidth + 13]);
    TEST_ASSERT_EQUAL_HEX8(0xF4, framebuffer[3 * kDisplayWidth + 10]);
    TEST_ASSERT_EQUAL_HEX8(0xF7, framebuffer[3 * kDisplayWidth + 13]);

    TEST_ASSERT_EQUAL_HEX8(0x00, framebuffer[2 * kDisplayWidth + 9]);
    TEST_ASSERT_EQUAL_HEX8(0x00, framebuffer[2 * kDisplayWidth + 14]);
    TEST_ASSERT_EQUAL_HEX8(0x00, framebuffer[1 * kDisplayWidth + 10]);
}

void test_delta_out_of_bounds_is_refused() {
    uint8_t framebuffer[kFrameBytes];
    uint8_t scratch[kFrameBytes];
    memset(framebuffer, 0xAB, sizeof(framebuffer));
    memset(scratch, 0, sizeof(scratch));

    struct { uint8_t x0, x1, p0, p1; } bad[] = {
        {10, 5, 0, 0},      // X1 < x0
        {0, 128, 0, 0},     // Coluna fora do painel
        {0, 10, 0, 8},      // Page fora do painel
        {0, 10, 5, 2},      // P1 < p0
        {120, 200, 0, 1},   // X1 estoura
    };

    for (const auto& rect : bad) {
        uint8_t payload[4 + 256];
        memset(payload, 0, sizeof(payload));
        payload[0] = rect.x0; payload[1] = rect.x1; payload[2] = rect.p0; payload[3] = rect.p1;
        const FrameDecodeResult result =
            ApplyFramePayload(kCmdFrameDelta, payload, sizeof(payload), framebuffer, scratch);
        TEST_ASSERT_TRUE(result == kDecodeBadRect || result == kDecodeBadLength);
    }

    // O framebuffer continua intacto
    for (size_t i = 0; i < kFrameBytes; ++i) TEST_ASSERT_EQUAL_HEX8(0xAB, framebuffer[i]);
}

void test_delta_with_truncated_payload_is_refused() {
    uint8_t framebuffer[kFrameBytes] = {};
    uint8_t scratch[kFrameBytes] = {};

    const uint8_t payload[] = {0, 9, 0, 1, 1, 2, 3, 4, 5};
    TEST_ASSERT_EQUAL(kDecodeBadLength,
        ApplyFramePayload(kCmdFrameDelta, payload, sizeof(payload), framebuffer, scratch));
}

void test_delta_header_too_short_is_refused() {
    uint8_t framebuffer[kFrameBytes] = {};
    uint8_t scratch[kFrameBytes] = {};
    const uint8_t payload[] = {0, 1, 2};
    TEST_ASSERT_EQUAL(kDecodeBadLength,
        ApplyFramePayload(kCmdFrameDelta, payload, sizeof(payload), framebuffer, scratch));
}

void test_rle_frame_that_does_not_fill_the_panel_is_refused() {
    uint8_t framebuffer[kFrameBytes] = {};
    uint8_t scratch[kFrameBytes] = {};
    const uint8_t payload[] = {0x80, 0x00}; // Expande para 2 bytes, nao 1024
    TEST_ASSERT_EQUAL(kDecodeBadLength,
        ApplyFramePayload(kCmdFrameRle, payload, sizeof(payload), framebuffer, scratch));
}

void test_non_frame_command_is_refused() {
    uint8_t framebuffer[kFrameBytes] = {};
    uint8_t scratch[kFrameBytes] = {};
    const uint8_t payload[] = {1, 2, 3, 4};
    TEST_ASSERT_EQUAL(kDecodeNotAFrame,
        ApplyFramePayload(kCmdPing, payload, sizeof(payload), framebuffer, scratch));
}

// Runner
void setUp() {}
void tearDown() {}

int main(int, char**) {
    UNITY_BEGIN();

    RUN_TEST(test_crc8_check_value);
    RUN_TEST(test_crc16_check_value);
    RUN_TEST(test_crc_empty_is_seed);
    RUN_TEST(test_crc16_detects_bit_flip);

    RUN_TEST(test_valid_packet_is_decoded);
    RUN_TEST(test_empty_payload_is_valid);
    RUN_TEST(test_maximum_payload_round_trips);
    RUN_TEST(test_payload_above_maximum_is_refused_by_encoder);
    RUN_TEST(test_bad_payload_crc_is_rejected);
    RUN_TEST(test_bad_header_crc_is_rejected);
    RUN_TEST(test_corrupt_length_does_not_stall_the_parser);
    RUN_TEST(test_length_above_maximum_is_rejected);
    RUN_TEST(test_unsupported_version_is_rejected);
    RUN_TEST(test_garbage_before_packet_is_discarded);
    RUN_TEST(test_back_to_back_packets_all_decode);
    RUN_TEST(test_parser_recovers_after_heavy_corruption);

    RUN_TEST(test_rle_round_trip_zeros);
    RUN_TEST(test_rle_literal_run);
    RUN_TEST(test_rle_truncated_stream_is_refused);
    RUN_TEST(test_rle_overflow_is_refused);

    RUN_TEST(test_raw_frame_applies);
    RUN_TEST(test_raw_frame_with_wrong_length_is_refused);
    RUN_TEST(test_delta_frame_applies_to_the_right_region);
    RUN_TEST(test_delta_out_of_bounds_is_refused);
    RUN_TEST(test_delta_with_truncated_payload_is_refused);
    RUN_TEST(test_delta_header_too_short_is_refused);
    RUN_TEST(test_rle_frame_that_does_not_fill_the_panel_is_refused);
    RUN_TEST(test_non_frame_command_is_refused);

    return UNITY_END();
}