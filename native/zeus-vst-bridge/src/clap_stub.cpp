// SPDX-License-Identifier: GPL-2.0-or-later
//
// CLAP host bridge — stub build, compiled instead of clap_bridge.cpp when the
// CLAP SDK submodule is not initialised (see CMakeLists.txt). Exports the full
// zclap.h ABI; nothing can be loaded, so the .NET scanner finds no CLAP
// plug-ins.

#include "zclap.h"

#include <cstring>

extern "C" {

int32_t zclap_init(int32_t abi) { return abi == ZCLAP_ABI ? ZVST_OK : ZVST_ABI_MISMATCH; }
int32_t zclap_shutdown(void) { return ZVST_OK; }

int32_t zclap_describe(const char* path, char* out_json, int32_t out_cap, int32_t* out_len) {
    if (out_len) *out_len = 0;
    if (!path || !out_json || out_cap < 1) return ZVST_INVALID_ARGUMENTS;
    out_json[0] = '\0';
    return ZVST_NOT_IMPLEMENTED;
}

int32_t zclap_load(const char* path, const char* plugin_id, int32_t channels, int32_t sample_rate,
                   int32_t block_size, zclap_handle_t* out_handle) {
    (void)plugin_id; (void)channels; (void)sample_rate; (void)block_size;
    if (out_handle) *out_handle = nullptr;
    if (!path || !out_handle) return ZVST_INVALID_ARGUMENTS;
    return ZVST_NOT_IMPLEMENTED;
}

int32_t zclap_process(zclap_handle_t handle, const float* input, float* output, int32_t frames) {
    if (!handle) return ZVST_INVALID_HANDLE;
    if (!input || !output || frames < 1) return ZVST_INVALID_ARGUMENTS;
    std::memmove(output, input, static_cast<size_t>(frames) * sizeof(float));
    return ZVST_OK;
}

int32_t zclap_set_param(zclap_handle_t handle, uint32_t, double) {
    return handle ? ZVST_OK : ZVST_INVALID_HANDLE;
}

int32_t zclap_unload(zclap_handle_t) { return ZVST_OK; }
int32_t zclap_get_latency_samples(zclap_handle_t) { return 0; }

int32_t zclap_get_state(zclap_handle_t handle, uint8_t*, int32_t, int32_t* out_len) {
    if (out_len) *out_len = 0;
    return handle ? ZVST_NOT_IMPLEMENTED : ZVST_INVALID_HANDLE;
}

int32_t zclap_set_state(zclap_handle_t handle, const uint8_t*, int32_t) {
    return handle ? ZVST_NOT_IMPLEMENTED : ZVST_INVALID_HANDLE;
}

int32_t zclap_editor_open(zclap_handle_t, const char*) { return ZVST_NOT_IMPLEMENTED; }
int32_t zclap_editor_close(zclap_handle_t) { return ZVST_OK; }
int32_t zclap_editor_is_open(zclap_handle_t) { return 0; }

} // extern "C"
