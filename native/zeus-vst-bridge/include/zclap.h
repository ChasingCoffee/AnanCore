/* SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Openhpsdr-Zeus — In-process CLAP host bridge.
 * C ABI consumed by Zeus.Plugins.Host.Audio.ClapBridgeNative (P/Invoke).
 *
 * Lives in the same shared library as the VST3 bridge (zvst.h) and mirrors
 * its shape function for function, so the .NET host drives either format
 * through one interface. Status codes are zvst_status_t. Hosting uses the
 * MIT-licensed CLAP SDK headers (third_party/clap); no CLAP code is linked.
 *
 * Threading: every plug-in instance gets a host "main thread" — a dedicated
 * thread with its own event loop (timers, plug-in callbacks, on Linux file
 * descriptors, on Windows the editor's message pump), or on macOS the
 * process main thread when the host runs an AppKit loop. All non-audio
 * plug-in calls run there; zclap_process runs on the caller's audio thread.
 */

#ifndef OPENHPSDR_ZEUS_ZCLAP_H
#define OPENHPSDR_ZEUS_ZCLAP_H

#include "zvst.h"

#ifdef __cplusplus
extern "C" {
#endif

/* v1: initial CLAP hosting. */
#define ZCLAP_ABI 1

typedef void* zclap_handle_t;

/* abi MUST equal ZCLAP_ABI. Idempotent. */
ZVST_EXPORT int32_t zclap_init(int32_t abi);
ZVST_EXPORT int32_t zclap_shutdown(void);

/*
 * List the plug-ins in a .clap file/bundle as a JSON array of
 *   {"uid":"<clap id>","name":"...","category":"<features, | separated>","vendor":"..."}
 * (the same shape as zvst_describe, so one .NET scanner reads both). Every
 * plug-in is listed, instruments included; `category` lets the caller
 * filter. Truncation and *out_len behave as in zvst_describe.
 */
ZVST_EXPORT int32_t zclap_describe(const char* path, char* out_json, int32_t out_cap, int32_t* out_len);

/*
 * Instantiate plug-in `plugin_id` (the `uid` from zclap_describe; NULL or ""
 * = the first audio effect) from the .clap at `path`, activate it at the
 * given geometry, and return a handle. `channels` (1..2) is the HOST buffer
 * geometry; the bridge up/down-mixes to the plug-in's main ports.
 * ZVST_ACTIVATE_FAILED for plug-ins without a 1- or 2-channel main audio
 * input and output (instruments, analysers without outputs).
 */
ZVST_EXPORT int32_t zclap_load(
    const char* path,
    const char* plugin_id,
    int32_t channels,
    int32_t sample_rate,
    int32_t block_size,
    zclap_handle_t* out_handle);

/* Same contract as zvst_process (planar float32, never blocks). */
ZVST_EXPORT int32_t zclap_process(zclap_handle_t handle, const float* input, float* output, int32_t frames);

/* Normalised [0,1] value, mapped onto the parameter's plain range. */
ZVST_EXPORT int32_t zclap_set_param(zclap_handle_t handle, uint32_t param_id, double normalized);

ZVST_EXPORT int32_t zclap_unload(zclap_handle_t handle);
ZVST_EXPORT int32_t zclap_get_latency_samples(zclap_handle_t handle);

/* Same contracts as zvst_get_state / zvst_set_state. */
ZVST_EXPORT int32_t zclap_get_state(zclap_handle_t handle, uint8_t* out_buf, int32_t cap, int32_t* out_len);
ZVST_EXPORT int32_t zclap_set_state(zclap_handle_t handle, const uint8_t* data, int32_t len);

/* The plug-in's own GUI (clap.gui): embedded in a bridge window where the
 * plug-in supports it, otherwise its floating window. macOS needs the
 * host's AppKit loop (desktop mode). */
ZVST_EXPORT int32_t zclap_editor_open(zclap_handle_t handle, const char* title);
ZVST_EXPORT int32_t zclap_editor_close(zclap_handle_t handle);
ZVST_EXPORT int32_t zclap_editor_is_open(zclap_handle_t handle); /* boolean */

#ifdef __cplusplus
}
#endif

#endif /* OPENHPSDR_ZEUS_ZCLAP_H */
