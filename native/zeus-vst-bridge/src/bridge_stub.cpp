// SPDX-License-Identifier: GPL-2.0-or-later
//
// Openhpsdr-Zeus VST3 host bridge — stub build.
//
// Compiled instead of bridge.cpp when the vst3sdk submodule is not
// initialised (see CMakeLists.txt), so a checkout without the Steinberg
// sources still builds a library that exports the full zvst.h ABI. Nothing
// here can load a plug-in: every load / describe reports
// ZVST_NOT_IMPLEMENTED, so the .NET scanner registers nothing and the
// audio chain stays empty. process() is defined as pass-through only so a
// caller that ignores a failed load still cannot corrupt audio.

#include "zvst.h"

#include <atomic>
#include <cstring>

namespace {
std::atomic<int> g_init_count{0};
}

extern "C" {

int32_t zvst_init(int32_t abi) {
    if (abi != ZVST_ABI) return ZVST_ABI_MISMATCH;
    g_init_count.fetch_add(1);
    return ZVST_OK;
}

int32_t zvst_load_vst3(const char* path, int32_t channels, int32_t sample_rate,
                       int32_t block_size, zvst_handle_t* out_handle) {
    (void)channels; (void)sample_rate; (void)block_size;
    if (out_handle) *out_handle = nullptr;
    if (!path || !out_handle) return ZVST_INVALID_ARGUMENTS;
    return ZVST_NOT_IMPLEMENTED;
}

int32_t zvst_process(zvst_handle_t handle, const float* input, float* output, int32_t frames) {
    if (!handle) return ZVST_INVALID_HANDLE;
    if (!input || !output || frames < 1) return ZVST_INVALID_ARGUMENTS;
    std::memcpy(output, input, static_cast<size_t>(frames) * sizeof(float));
    return ZVST_OK;
}

int32_t zvst_set_param(zvst_handle_t handle, uint32_t param_id, double normalized) {
    (void)param_id; (void)normalized;
    return handle ? ZVST_OK : ZVST_INVALID_HANDLE;
}

int32_t zvst_unload(zvst_handle_t handle) {
    (void)handle;
    return ZVST_OK;
}

int32_t zvst_shutdown(void) {
    if (g_init_count.load() > 0) g_init_count.fetch_sub(1);
    return ZVST_OK;
}

int32_t zvst_describe(const char* path, char* out_json, int32_t out_cap, int32_t* out_len) {
    if (out_len) *out_len = 0;
    if (!path || !out_json || out_cap < 1) return ZVST_INVALID_ARGUMENTS;
    out_json[0] = '\0';
    return ZVST_NOT_IMPLEMENTED;
}

int32_t zvst_editor_open(zvst_handle_t handle, const char* title) {
    (void)handle; (void)title;
    return ZVST_NOT_IMPLEMENTED;
}

int32_t zvst_editor_close(zvst_handle_t handle) {
    (void)handle;
    return ZVST_OK;
}

int32_t zvst_editor_is_open(zvst_handle_t handle) {
    (void)handle;
    return 0;
}

int32_t zvst_get_latency_samples(zvst_handle_t handle) {
    (void)handle;
    return 0;
}

} // extern "C"
