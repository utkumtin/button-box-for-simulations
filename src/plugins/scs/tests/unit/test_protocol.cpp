// Protocol tests: every line of the shared vectors (protocol/vectors.txt), frames whole and byte by byte, and the
// grammar of the compiled input and channel names.

#include "test_util.h"

#include "plugin_info.h"
#include "protocol.h"
#include "tables.h"

#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

namespace
{

struct PayloadCase
{
    bool to_host = false;
    bool ok = false;
    std::string payload;
    int line = 0;
};

struct FrameCase
{
    std::string verdict; // ok, bad, partial
    std::vector<std::uint8_t> bytes;
    int line = 0;
};

struct Vectors
{
    std::string pipe_name;
    std::vector<PayloadCase> payloads;
    std::vector<FrameCase> frames;
    int unknown_lines = 0;
    bool opened = false;
};

int hex_digit(char c)
{
    if (c >= '0' && c <= '9')
    {
        return c - '0';
    }
    if (c >= 'A' && c <= 'F')
    {
        return c - 'A' + 10;
    }
    return -1;
}

// '\\' is one backslash, '\xNN' is the byte with hex value NN.
bool unescape(const std::string &text, std::string &out)
{
    out.clear();
    for (size_t i = 0; i < text.size(); ++i)
    {
        if (text[i] != '\\')
        {
            out.push_back(text[i]);
            continue;
        }
        if (i + 1 < text.size() && text[i + 1] == '\\')
        {
            out.push_back('\\');
            ++i;
        }
        else if (i + 3 < text.size() + 0 && text[i + 1] == 'x' && hex_digit(text[i + 2]) >= 0 &&
                 hex_digit(text[i + 3]) >= 0)
        {
            out.push_back(static_cast<char>(hex_digit(text[i + 2]) * 16 + hex_digit(text[i + 3])));
            i += 3;
        }
        else
        {
            return false;
        }
    }
    return true;
}

Vectors load_vectors()
{
    Vectors v;
    std::ifstream in(t::vectors_path(), std::ios::binary);
    v.opened = in.good();
    std::string line;
    int number = 0;
    while (std::getline(in, line))
    {
        ++number;
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back(); // a checkout with CRLF; the file itself is LF
        }
        if (line.empty() || line[0] == '#')
        {
            continue;
        }
        if (line.rfind("pipe-name ", 0) == 0)
        {
            v.pipe_name = line.substr(10);
        }
        else if (line.rfind("payload ", 0) == 0)
        {
            // payload <to-host|to-plugin> <ok|bad> <payload>
            const size_t dir_end = line.find(' ', 8);
            const size_t verdict_end = dir_end == std::string::npos ? dir_end : line.find(' ', dir_end + 1);
            PayloadCase c;
            c.line = number;
            if (verdict_end == std::string::npos)
            {
                ++v.unknown_lines;
                continue;
            }
            const std::string dir = line.substr(8, dir_end - 8);
            const std::string verdict = line.substr(dir_end + 1, verdict_end - dir_end - 1);
            c.to_host = dir == "to-host";
            c.ok = verdict == "ok";
            if ((dir != "to-host" && dir != "to-plugin") || (verdict != "ok" && verdict != "bad") ||
                !unescape(line.substr(verdict_end + 1), c.payload))
            {
                ++v.unknown_lines;
                continue;
            }
            v.payloads.push_back(c);
        }
        else if (line.rfind("frame ", 0) == 0)
        {
            const size_t verdict_end = line.find(' ', 6);
            FrameCase c;
            c.line = number;
            if (verdict_end == std::string::npos)
            {
                ++v.unknown_lines;
                continue;
            }
            c.verdict = line.substr(6, verdict_end - 6);
            const std::string hex = line.substr(verdict_end + 1);
            bool good = (c.verdict == "ok" || c.verdict == "bad" || c.verdict == "partial") && hex.size() % 2 == 0;
            for (size_t i = 0; good && i < hex.size(); i += 2)
            {
                const int hi = hex_digit(hex[i]);
                const int lo = hex_digit(hex[i + 1]);
                good = hi >= 0 && lo >= 0;
                c.bytes.push_back(static_cast<std::uint8_t>(hi * 16 + lo));
            }
            if (!good)
            {
                ++v.unknown_lines;
                continue;
            }
            v.frames.push_back(c);
        }
        else
        {
            ++v.unknown_lines;
        }
    }
    return v;
}

} // namespace

TEST(vectors_file_is_fully_understood)
{
    const Vectors v = load_vectors();
    CHECK(v.opened);
    CHECK(v.unknown_lines == 0);
    // A runner that read nothing would pass every loop below; make sure the file really was read.
    CHECK(v.payloads.size() > 50);
    CHECK(v.frames.size() >= 8);
}

TEST(pipe_name_constant_equals_the_vectors)
{
    const Vectors v = load_vectors();
    std::string constant;
    for (const wchar_t *c = bbs::kPipeName; *c != L'\0'; ++c)
    {
        CHECK(*c < 0x80);
        constant.push_back(static_cast<char>(*c));
    }
    CHECK(!v.pipe_name.empty());
    CHECK(constant == v.pipe_name);
}

TEST(payload_vectors_parse_and_round_trip)
{
    const Vectors v = load_vectors();
    int checked = 0;
    for (const PayloadCase &c : v.payloads)
    {
        char buffer[512];
        if (c.to_host)
        {
            bbs::ToHost m;
            const bool parsed = bbs::parse_to_host(c.payload, m);
            if (c.ok)
            {
                // ok: parses, and encoding the parsed message gives the same bytes
                CHECK(parsed);
                const size_t n = bbs::encode_to_host(m, buffer, sizeof buffer);
                CHECK(n == c.payload.size() && c.payload.compare(0, c.payload.size(), buffer, n) == 0);
            }
            else
            {
                CHECK(!parsed);
            }
        }
        else
        {
            bbs::ToPlugin m;
            const bool parsed = bbs::parse_to_plugin(c.payload, m);
            if (c.ok)
            {
                CHECK(parsed);
                const size_t n = bbs::encode_to_plugin(m, buffer, sizeof buffer);
                CHECK(n == c.payload.size() && c.payload.compare(0, c.payload.size(), buffer, n) == 0);
            }
            else
            {
                CHECK(!parsed);
            }
        }
        ++checked;
    }
    CHECK(checked == static_cast<int>(v.payloads.size()));
}

TEST(frame_vectors_whole_and_byte_by_byte)
{
    const Vectors v = load_vectors();
    for (const FrameCase &c : v.frames)
    {
        // The whole byte stream at once.
        bbs::FrameReader whole;
        const size_t used = whole.feed(c.bytes.data(), c.bytes.size());
        if (c.verdict == "ok")
        {
            CHECK(whole.status() == bbs::FrameReader::Status::Frame);
            CHECK(used == c.bytes.size()); // no byte left
            CHECK(whole.payload().size() + 2 == c.bytes.size());
            // and the frame encoder produces the same bytes
            std::uint8_t encoded[2 + bbs::kMaxPayload];
            const size_t n = bbs::encode_frame(whole.payload(), encoded, sizeof encoded);
            CHECK(n == c.bytes.size());
            bool same = n == c.bytes.size();
            for (size_t i = 0; same && i < n; ++i)
            {
                same = encoded[i] == c.bytes[i];
            }
            CHECK(same);
        }
        else if (c.verdict == "bad")
        {
            CHECK(whole.status() == bbs::FrameReader::Status::Error);
        }
        else
        {
            CHECK(whole.status() == bbs::FrameReader::Status::NeedMore);
            CHECK(used == c.bytes.size());
        }

        // The same bytes one at a time: same verdict, and a frame completes only with its last byte.
        bbs::FrameReader slow;
        size_t fed = 0;
        while (fed < c.bytes.size() && slow.status() == bbs::FrameReader::Status::NeedMore)
        {
            CHECK(slow.feed(&c.bytes[fed], 1) == 1);
            ++fed;
        }
        if (c.verdict == "ok")
        {
            CHECK(slow.status() == bbs::FrameReader::Status::Frame);
            CHECK(fed == c.bytes.size());
        }
        else if (c.verdict == "bad")
        {
            CHECK(slow.status() == bbs::FrameReader::Status::Error);
        }
        else
        {
            CHECK(slow.status() == bbs::FrameReader::Status::NeedMore);
        }
    }
}

TEST(oversized_length_is_an_error_as_soon_as_it_is_read)
{
    // 0x0101 = 257: the reader must not wait for the payload.
    bbs::FrameReader reader;
    const std::uint8_t length[2] = {0x01, 0x01};
    CHECK(reader.feed(length, 1) == 1);
    CHECK(reader.status() == bbs::FrameReader::Status::NeedMore);
    CHECK(reader.feed(length + 1, 1) == 1);
    CHECK(reader.status() == bbs::FrameReader::Status::Error);
    // an error is sticky: nothing more is consumed until reset()
    CHECK(reader.feed(length, 1) == 0);
    reader.reset();
    const std::uint8_t good[3] = {0x01, 0x00, 'x'};
    CHECK(reader.feed(good, 3) == 3);
    CHECK(reader.status() == bbs::FrameReader::Status::Frame);
}

TEST(frame_reader_splits_back_to_back_frames_at_any_chunking)
{
    // "ping 1" and "press lighton" glued together, delivered in chunks of every size.
    std::vector<std::uint8_t> stream;
    for (const char *text : {"ping 1", "press lighton"})
    {
        std::uint8_t frame[2 + bbs::kMaxPayload];
        const size_t n = bbs::encode_frame(text, frame, sizeof frame);
        CHECK(n > 0);
        stream.insert(stream.end(), frame, frame + n);
    }
    for (size_t chunk = 1; chunk <= stream.size(); ++chunk)
    {
        bbs::FrameReader reader;
        std::vector<std::string> payloads;
        size_t pos = 0;
        while (pos < stream.size())
        {
            const size_t end = pos + chunk < stream.size() ? pos + chunk : stream.size();
            size_t at = pos;
            while (at < end)
            {
                at += reader.feed(&stream[at], end - at);
                if (reader.status() == bbs::FrameReader::Status::Frame)
                {
                    payloads.emplace_back(reader.payload());
                    reader.next();
                }
            }
            pos = end;
        }
        CHECK(payloads.size() == 2);
        CHECK(payloads.size() == 2 && payloads[0] == "ping 1" && payloads[1] == "press lighton");
    }
}

TEST(encode_frame_refuses_what_the_protocol_forbids)
{
    std::uint8_t out[2 + bbs::kMaxPayload + 4];
    CHECK(bbs::encode_frame("", out, sizeof out) == 0);
    CHECK(bbs::encode_frame(std::string(bbs::kMaxPayload + 1, 'a'), out, sizeof out) == 0);
    CHECK(bbs::encode_frame(std::string(bbs::kMaxPayload, 'a'), out, sizeof out) == 2 + bbs::kMaxPayload);
    CHECK(out[0] == 0x00 && out[1] == 0x01);      // 256, little endian
    CHECK(bbs::encode_frame("abc", out, 4) == 0); // does not fit
}

TEST(compiled_input_and_channel_names_are_protocol_valid)
{
    // A name the grammar rejects could never be pressed or reported; a duplicate would be ambiguous.
    for (size_t i = 0; i < bbs::kInputCount; ++i)
    {
        bbs::ToPlugin m;
        const std::string payload = std::string("press ") + bbs::kInputNames[i];
        CHECK(bbs::parse_to_plugin(payload, m));
        CHECK(bbs::find_input(bbs::kInputNames[i]) == i);
        for (const char *c = bbs::kInputNames[i]; *c != '\0'; ++c)
        {
            CHECK(*c != '.'); // the SDK allows letters, digits and underscore in input names
        }
    }
    for (size_t i = 0; i < bbs::kChannelCount; ++i)
    {
        bbs::ToHost m;
        const std::string payload = std::string("tel ") + bbs::kChannels[i].name + " 0";
        CHECK(bbs::parse_to_host(payload, m));
        CHECK(bbs::find_channel(bbs::kChannels[i].name) == i);
    }
    CHECK(bbs::find_input("nosuchinput") == bbs::kNotFound);
    CHECK(bbs::find_input("") == bbs::kNotFound);
}
