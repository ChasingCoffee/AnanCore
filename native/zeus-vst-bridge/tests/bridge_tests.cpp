// SPDX-License-Identifier: GPL-2.0-or-later
//
// Native tests for the zvst ABI, run by ctest against the in-tree test
// plug-in. They cover what the .NET tests cannot reach in-process: the
// plug-in's ZEUS_TEST_VST_FAULT modes are read with getenv(), which .NET's
// Environment.SetEnvironmentVariable does not update on Unix.
//
//   bridge_tests <path to ZeusTestPlugin.vst3>

#include "zvst.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace {

const char* g_plugin = nullptr;
int g_failures = 0;
int g_checks = 0;

#define CHECK(cond) do { \
    ++g_checks; \
    if (!(cond)) { ++g_failures; std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); } \
} while (0)

const char* kGainUid   = "5A455553544553544741494E00000001";
const char* kInvertUid = "5A45555354455354494E565400000002";

void set_fault(const char* value) {
#ifdef _WIN32
    _putenv_s("ZEUS_TEST_VST_FAULT", value ? value : "");
#else
    if (value) setenv("ZEUS_TEST_VST_FAULT", value, 1);
    else unsetenv("ZEUS_TEST_VST_FAULT");
#endif
}

std::vector<float> ramp(int channels, int frames) {
    std::vector<float> x(static_cast<size_t>(channels * frames));
    for (size_t i = 0; i < x.size(); i++)
        x[i] = static_cast<float>(i % static_cast<size_t>(frames)) / frames - 0.5f;
    return x;
}

// Process one block; true if every output sample == k * input.
bool processes_with_gain(zvst_handle_t h, int channels, int frames, float k) {
    auto in = ramp(channels, frames);
    std::vector<float> out(in.size(), 99.0f);
    if (zvst_process(h, in.data(), out.data(), frames) != ZVST_OK) return false;
    for (size_t i = 0; i < in.size(); i++)
        if (std::fabs(out[i] - k * in[i]) > 1e-5f) return false;
    return true;
}

zvst_handle_t load(const char* uid, int channels, int frames, int* status_out = nullptr) {
    zvst_handle_t h = nullptr;
    int st = zvst_load_vst3_class(g_plugin, uid, channels, 48000, frames, &h);
    if (status_out) *status_out = st;
    return st == ZVST_OK ? h : nullptr;
}

void test_describe() {
    char json[4096];
    int32_t len = 0;
    CHECK(zvst_describe(g_plugin, json, sizeof json, &len) == ZVST_OK);
    CHECK(std::strstr(json, kGainUid) != nullptr);
    CHECK(std::strstr(json, kInvertUid) != nullptr);
    CHECK(std::strstr(json, "Zeus Test Invert") != nullptr);
}

void test_default_class_is_first_and_unity() {
    for (int ch = 1; ch <= 2; ch++) {
        zvst_handle_t h = nullptr;
        CHECK(zvst_load_vst3(g_plugin, ch, 48000, 256, &h) == ZVST_OK);
        if (!h) continue;
        CHECK(processes_with_gain(h, ch, 256, 1.0f));
        CHECK(zvst_unload(h) == ZVST_OK);
    }
}

void test_load_by_class_uid() {
    zvst_handle_t h = load(kInvertUid, 1, 128);
    CHECK(h != nullptr);
    if (h) { CHECK(processes_with_gain(h, 1, 128, -1.0f)); zvst_unload(h); }

    h = load(kGainUid, 2, 128);
    CHECK(h != nullptr);
    if (h) { CHECK(processes_with_gain(h, 2, 128, 1.0f)); zvst_unload(h); }

    int st = 0;
    CHECK(load("00000000000000000000000000000000", 1, 128, &st) == nullptr);
    CHECK(st == ZVST_NO_AUDIO_EFFECT_CLASS);
}

void test_set_param() {
    zvst_handle_t h = load(nullptr, 1, 128);
    CHECK(h != nullptr);
    if (!h) return;
    CHECK(zvst_set_param(h, 0, 1.0) == ZVST_OK); // gain x2
    CHECK(processes_with_gain(h, 1, 128, 2.0f));
    zvst_unload(h);
}

void test_state_round_trip() {
    zvst_handle_t a = load(nullptr, 1, 128);
    CHECK(a != nullptr);
    if (!a) return;
    CHECK(zvst_set_param(a, 0, 0.75) == ZVST_OK); // gain x1.5, applied on the next block
    CHECK(processes_with_gain(a, 1, 128, 1.5f));

    int32_t len = 0;
    CHECK(zvst_get_state(a, nullptr, 0, &len) == ZVST_BUFFER_TOO_SMALL);
    CHECK(len > 12);
    std::vector<uint8_t> blob(static_cast<size_t>(len));
    int32_t len2 = 0;
    CHECK(zvst_get_state(a, blob.data(), len, &len2) == ZVST_OK);
    CHECK(len2 == len);
    CHECK(std::memcmp(blob.data(), "ZVS1", 4) == 0);
    zvst_unload(a);

    zvst_handle_t b = load(nullptr, 1, 128);
    CHECK(b != nullptr);
    if (!b) return;
    CHECK(processes_with_gain(b, 1, 128, 1.0f)); // fresh instance: unity
    CHECK(zvst_set_state(b, blob.data(), len) == ZVST_OK);
    CHECK(processes_with_gain(b, 1, 128, 1.5f));

    // A garbled blob is refused, not applied.
    std::vector<uint8_t> bad(blob);
    bad[0] = 'X';
    CHECK(zvst_set_state(b, bad.data(), static_cast<int32_t>(bad.size())) == ZVST_INVALID_ARGUMENTS);
    bad = blob;
    bad.pop_back();
    CHECK(zvst_set_state(b, bad.data(), static_cast<int32_t>(bad.size())) == ZVST_INVALID_ARGUMENTS);
    CHECK(processes_with_gain(b, 1, 128, 1.5f));
    zvst_unload(b);
}

void test_64_bit_only_plugin() {
    set_fault("only-64");
    for (int ch = 1; ch <= 2; ch++) {
        zvst_handle_t h = load(kInvertUid, ch, 256);
        CHECK(h != nullptr);
        if (!h) continue;
        CHECK(processes_with_gain(h, ch, 256, -1.0f));
        CHECK(processes_with_gain(h, ch, 100, -1.0f)); // short block
        zvst_unload(h);
    }
    set_fault(nullptr);
}

void test_latency_report() {
    set_fault("latency-256");
    zvst_handle_t h = load(nullptr, 1, 128);
    CHECK(h != nullptr);
    if (h) { CHECK(zvst_get_latency_samples(h) == 256); zvst_unload(h); }
    set_fault(nullptr);
}

void test_editor_without_view_fails_cleanly() {
    // The test plug-in has no editor view; opening must fail, not crash.
    zvst_handle_t h = load(nullptr, 1, 128);
    CHECK(h != nullptr);
    if (!h) return;
    CHECK(zvst_editor_open(h, "test") != ZVST_OK);
    CHECK(zvst_editor_is_open(h) == 0);
    CHECK(zvst_editor_close(h) == ZVST_OK);
    zvst_unload(h);
}

void test_invalid_arguments() {
    int32_t len = 0;
    CHECK(zvst_get_state(nullptr, nullptr, 0, &len) == ZVST_INVALID_HANDLE);
    uint8_t b = 0;
    CHECK(zvst_set_state(nullptr, &b, 1) == ZVST_INVALID_HANDLE);
    zvst_handle_t h = nullptr;
    CHECK(zvst_load_vst3_class(nullptr, nullptr, 1, 48000, 128, &h) == ZVST_INVALID_ARGUMENTS);
    CHECK(zvst_load_vst3_class(g_plugin, nullptr, 3, 48000, 128, &h) == ZVST_INVALID_ARGUMENTS);
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: %s <ZeusTestPlugin.vst3>\n", argv[0]);
        return 2;
    }
    g_plugin = argv[1];
    if (zvst_init(ZVST_ABI) != ZVST_OK) { std::fprintf(stderr, "zvst_init failed\n"); return 1; }

    test_describe();
    test_default_class_is_first_and_unity();
    test_load_by_class_uid();
    test_set_param();
    test_state_round_trip();
    test_64_bit_only_plugin();
    test_latency_report();
    test_editor_without_view_fails_cleanly();
    test_invalid_arguments();

    zvst_shutdown();
    std::printf("%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
