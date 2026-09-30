// SPDX-License-Identifier: GPL-2.0-or-later
//
// Native tests for the zclap ABI against the in-tree test CLAP, run by
// ctest. The plug-in's ZEUS_TEST_VST_FAULT modes are read with getenv(),
// which only native code can set for the process.
//
//   clap_tests <path to ZeusTestPlugin.clap>

#include "zclap.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

namespace {

const char* g_plugin = nullptr;
int g_failures = 0;
int g_checks = 0;

#define CHECK(cond) do { \
    ++g_checks; \
    if (!(cond)) { ++g_failures; std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); } \
} while (0)

const char* kGain   = "org.openhpsdr.zeus.test.gain";
const char* kInvert = "org.openhpsdr.zeus.test.invert";
const char* kSynth  = "org.openhpsdr.zeus.test.synth";

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

bool processes_with_gain(zclap_handle_t h, int channels, int frames, float k) {
    auto in = ramp(channels, frames);
    std::vector<float> out(in.size(), 99.0f);
    if (zclap_process(h, in.data(), out.data(), frames) != ZVST_OK) return false;
    for (size_t i = 0; i < in.size(); i++)
        if (std::fabs(out[i] - k * in[i]) > 1e-5f) return false;
    return true;
}

zclap_handle_t load(const char* id, int channels, int frames, int* status_out = nullptr) {
    zclap_handle_t h = nullptr;
    const int st = zclap_load(g_plugin, id, channels, 48000, frames, &h);
    if (status_out) *status_out = st;
    return st == ZVST_OK ? h : nullptr;
}

std::vector<uint8_t> state_of(zclap_handle_t h) {
    int32_t len = 0;
    if (zclap_get_state(h, nullptr, 0, &len) != ZVST_BUFFER_TOO_SMALL || len <= 0) return {};
    std::vector<uint8_t> b(static_cast<size_t>(len));
    if (zclap_get_state(h, b.data(), len, &len) != ZVST_OK) return {};
    return b;
}

void test_describe() {
    char json[8192];
    int32_t len = 0;
    CHECK(zclap_describe(g_plugin, json, sizeof json, &len) == ZVST_OK);
    CHECK(std::strstr(json, kGain) != nullptr);
    CHECK(std::strstr(json, kInvert) != nullptr);
    CHECK(std::strstr(json, "\"uid\":\"org.openhpsdr.zeus.test.synth\"") != nullptr);
    CHECK(std::strstr(json, "instrument") != nullptr); // features ride in "category"
    CHECK(std::strstr(json, "audio-effect") != nullptr);
}

void test_default_is_first_effect_and_unity() {
    for (int ch = 1; ch <= 2; ch++) {
        zclap_handle_t h = load(nullptr, ch, 256);
        CHECK(h != nullptr);
        if (!h) continue;
        CHECK(processes_with_gain(h, ch, 256, 1.0f));
        CHECK(processes_with_gain(h, ch, 64, 1.0f)); // short block
        CHECK(zclap_unload(h) == ZVST_OK);
    }
}

void test_select_by_id() {
    zclap_handle_t h = load(kInvert, 1, 128);
    CHECK(h != nullptr);
    if (h) { CHECK(processes_with_gain(h, 1, 128, -1.0f)); zclap_unload(h); }

    int st = 0;
    CHECK(load("org.example.missing", 1, 128, &st) == nullptr);
    CHECK(st == ZVST_NO_AUDIO_EFFECT_CLASS);
    CHECK(load(kSynth, 1, 128, &st) == nullptr); // no audio input: not an insert
    CHECK(st == ZVST_ACTIVATE_FAILED);
}

void test_set_param_maps_to_plain_range() {
    zclap_handle_t h = load(kGain, 2, 128);
    CHECK(h != nullptr);
    if (!h) return;
    CHECK(zclap_set_param(h, 0, 0.75) == ZVST_OK); // gain 0..2 -> 1.5
    CHECK(processes_with_gain(h, 2, 128, 1.5f));
    CHECK(zclap_set_param(h, 1, 1.0) == ZVST_OK);  // bypass
    CHECK(processes_with_gain(h, 2, 128, 1.0f));
    zclap_unload(h);
}

void test_state_round_trip_and_threading() {
    zclap_handle_t a = load(kGain, 1, 128);
    CHECK(a != nullptr);
    if (!a) return;
    CHECK(zclap_set_param(a, 0, 0.25) == ZVST_OK); // gain 0.5
    CHECK(processes_with_gain(a, 1, 128, 0.5f));

    // request_callback() from init() must come back as on_main_thread() on
    // the host's main thread; init() itself must have run there.
    std::vector<uint8_t> blob;
    for (int i = 0; i < 100; i++) {
        blob = state_of(a);
        if (blob.size() >= 32 && blob[28] == 1) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    CHECK(blob.size() == 32);                       // "ZCS1" + len + plug-in's 24 bytes
    CHECK(blob.size() >= 32 && std::memcmp(blob.data(), "ZCS1", 4) == 0);
    CHECK(blob.size() >= 32 && blob[24] == 1);      // init() on the main thread
    CHECK(blob.size() >= 32 && blob[28] == 1);      // on_main_thread() delivered
    zclap_unload(a);

    zclap_handle_t b = load(kGain, 1, 128);
    CHECK(b != nullptr);
    if (!b) return;
    CHECK(processes_with_gain(b, 1, 128, 1.0f));
    CHECK(zclap_set_state(b, blob.data(), static_cast<int32_t>(blob.size())) == ZVST_OK);
    CHECK(processes_with_gain(b, 1, 128, 0.5f));
    std::vector<uint8_t> bad(blob);
    bad[0] = 'X';
    CHECK(zclap_set_state(b, bad.data(), static_cast<int32_t>(bad.size())) == ZVST_INVALID_ARGUMENTS);
    zclap_unload(b);
}

void test_latency() {
    set_fault("latency-128");
    zclap_handle_t h = load(kGain, 1, 128);
    CHECK(h != nullptr);
    if (h) { CHECK(zclap_get_latency_samples(h) == 128); zclap_unload(h); }
    set_fault(nullptr);
}

void test_editor_without_gui_fails_cleanly() {
    zclap_handle_t h = load(kGain, 1, 128);
    CHECK(h != nullptr);
    if (!h) return;
    CHECK(zclap_editor_open(h, "test") != ZVST_OK);
    CHECK(zclap_editor_is_open(h) == 0);
    CHECK(zclap_editor_close(h) == ZVST_OK);
    zclap_unload(h);
}

void test_many_instances_share_the_module() {
    std::vector<zclap_handle_t> hs;
    for (int i = 0; i < 4; i++) hs.push_back(load(i % 2 ? kInvert : kGain, 1, 128));
    for (size_t i = 0; i < hs.size(); i++) {
        CHECK(hs[i] != nullptr);
        if (hs[i]) CHECK(processes_with_gain(hs[i], 1, 128, i % 2 ? -1.0f : 1.0f));
    }
    for (auto h : hs) if (h) zclap_unload(h);
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: %s <ZeusTestPlugin.clap>\n", argv[0]);
        return 2;
    }
    g_plugin = argv[1];
    if (zclap_init(ZCLAP_ABI) != ZVST_OK) { std::fprintf(stderr, "zclap_init failed\n"); return 1; }

    test_describe();
    test_default_is_first_effect_and_unity();
    test_select_by_id();
    test_set_param_maps_to_plain_range();
    test_state_round_trip_and_threading();
    test_latency();
    test_editor_without_gui_fails_cleanly();
    test_many_instances_share_the_module();

    zclap_shutdown();
    std::printf("%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
