// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus test CLAP — the CLAP counterpart of zeus_test_plugin.cpp, loaded by
// the bridge tests on every platform. Never shipped.
//
// Plug-ins in this module:
//   org.openhpsdr.zeus.test.gain    out = in * gain         (gain param, plain 0..2, default 1)
//   org.openhpsdr.zeus.test.invert  out = -in * gain
//   org.openhpsdr.zeus.test.synth   an instrument (no audio input) the scanner must skip
//
// Like real plug-ins it has a stereo main in/out plus a stereo sidechain
// input, a bypass parameter, and state. The state also records whether the
// host honoured the threading contract: init() ran on what the host reports
// as its main thread, and a request_callback() came back as on_main_thread()
// on that thread.
//
// Faults, from ZEUS_TEST_VST_FAULT (shared with the VST3 test plug-in):
//   crash-scan / hang-scan      in clap_entry.init
//   crash-process / hang-process / nan-process   in process()
//   latency-N                   report N samples of latency

#include <clap/clap.h>

#include <math.h>
#include <stdlib.h>
#include <string.h>
#if defined(_WIN32)
#  include <windows.h>
static void sleep_forever(void) { for (;;) Sleep(1000); }
#else
#  include <unistd.h>
static void sleep_forever(void) { for (;;) sleep(1); }
#endif

static const char* fault(void) {
    const char* f = getenv("ZEUS_TEST_VST_FAULT");
    return f ? f : "";
}

enum { PARAM_GAIN = 0, PARAM_BYPASS = 1 };

typedef struct {
    clap_plugin_t plugin;
    const clap_host_t* host;
    const clap_host_thread_check_t* thread_check;
    float sign;
    double gain;
    int bypass;
    int main_thread_ok;
    int callback_seen;
} test_plugin_t;

static const char* const k_fx_features[] = {CLAP_PLUGIN_FEATURE_AUDIO_EFFECT, CLAP_PLUGIN_FEATURE_UTILITY, NULL};
static const char* const k_synth_features[] = {CLAP_PLUGIN_FEATURE_INSTRUMENT, CLAP_PLUGIN_FEATURE_SYNTHESIZER, NULL};

static const clap_plugin_descriptor_t k_gain_desc = {
    CLAP_VERSION_INIT, "org.openhpsdr.zeus.test.gain", "Zeus Test Gain", "Zeus Test",
    "", "", "", "1.0.0", "Deterministic gain for host tests", k_fx_features};
static const clap_plugin_descriptor_t k_invert_desc = {
    CLAP_VERSION_INIT, "org.openhpsdr.zeus.test.invert", "Zeus Test Invert", "Zeus Test",
    "", "", "", "1.0.0", "Deterministic inverting gain for host tests", k_fx_features};
static const clap_plugin_descriptor_t k_synth_desc = {
    CLAP_VERSION_INIT, "org.openhpsdr.zeus.test.synth", "Zeus Test Synth", "Zeus Test",
    "", "", "", "1.0.0", "An instrument hosts must not load as an insert", k_synth_features};

static test_plugin_t* self(const clap_plugin_t* p) { return (test_plugin_t*)p->plugin_data; }

// ---- audio ports ----
static uint32_t ports_count(const clap_plugin_t* p, bool is_input) {
    if (self(p)->sign == 0.0f) return is_input ? 0 : 1; // the synth: output only
    return is_input ? 2 : 1;
}
static bool ports_get(const clap_plugin_t* p, uint32_t index, bool is_input, clap_audio_port_info_t* info) {
    (void)p;
    memset(info, 0, sizeof *info);
    info->channel_count = 2;
    info->port_type = CLAP_PORT_STEREO;
    info->in_place_pair = CLAP_INVALID_ID;
    if (index == 0) {
        info->id = is_input ? 0 : 100;
        info->flags = CLAP_AUDIO_PORT_IS_MAIN;
        strcpy(info->name, is_input ? "In" : "Out");
        return true;
    }
    if (is_input && index == 1) {
        info->id = 1;
        strcpy(info->name, "Sidechain");
        return true;
    }
    return false;
}
static const clap_plugin_audio_ports_t k_ports = {ports_count, ports_get};

// ---- params ----
static uint32_t params_count(const clap_plugin_t* p) { (void)p; return 2; }
static bool params_info(const clap_plugin_t* p, uint32_t index, clap_param_info_t* info) {
    (void)p;
    memset(info, 0, sizeof *info);
    if (index == 0) {
        info->id = PARAM_GAIN;
        info->flags = CLAP_PARAM_IS_AUTOMATABLE;
        strcpy(info->name, "Gain");
        info->min_value = 0.0; info->max_value = 2.0; info->default_value = 1.0;
        return true;
    }
    if (index == 1) {
        info->id = PARAM_BYPASS;
        info->flags = CLAP_PARAM_IS_STEPPED | CLAP_PARAM_IS_BYPASS | CLAP_PARAM_IS_AUTOMATABLE;
        strcpy(info->name, "Bypass");
        info->min_value = 0.0; info->max_value = 1.0; info->default_value = 0.0;
        return true;
    }
    return false;
}
static bool params_value(const clap_plugin_t* p, clap_id id, double* out) {
    test_plugin_t* t = self(p);
    if (id == PARAM_GAIN) { *out = t->gain; return true; }
    if (id == PARAM_BYPASS) { *out = t->bypass; return true; }
    return false;
}
static bool params_to_text(const clap_plugin_t* p, clap_id id, double v, char* out, uint32_t cap) {
    (void)p; (void)id; (void)v;
    if (cap) out[0] = 0;
    return false;
}
static bool params_from_text(const clap_plugin_t* p, clap_id id, const char* text, double* out) {
    (void)p; (void)id; (void)text; (void)out;
    return false;
}
static void apply_events(test_plugin_t* t, const clap_input_events_t* in) {
    if (!in) return;
    const uint32_t n = in->size(in);
    for (uint32_t i = 0; i < n; ++i) {
        const clap_event_header_t* h = in->get(in, i);
        if (!h || h->space_id != CLAP_CORE_EVENT_SPACE_ID || h->type != CLAP_EVENT_PARAM_VALUE) continue;
        const clap_event_param_value_t* ev = (const clap_event_param_value_t*)h;
        if (ev->param_id == PARAM_GAIN) t->gain = ev->value;
        if (ev->param_id == PARAM_BYPASS) t->bypass = ev->value >= 0.5;
    }
}
static void params_flush(const clap_plugin_t* p, const clap_input_events_t* in, const clap_output_events_t* out) {
    (void)out;
    apply_events(self(p), in);
}
static const clap_plugin_params_t k_params = {params_count, params_info, params_value, params_to_text,
                                              params_from_text, params_flush};

// ---- state ----
static const char k_magic[4] = {'Z', 'T', 'C', 'P'};
static bool write_all(const clap_ostream_t* s, const void* b, uint64_t n) {
    return s->write(s, b, n) == (int64_t)n;
}
static bool read_all(const clap_istream_t* s, void* b, uint64_t n) {
    return s->read(s, b, n) == (int64_t)n;
}
static bool state_save(const clap_plugin_t* p, const clap_ostream_t* s) {
    test_plugin_t* t = self(p);
    int32_t b = t->bypass, m = t->main_thread_ok, c = t->callback_seen;
    return write_all(s, k_magic, 4) && write_all(s, &t->gain, sizeof t->gain) &&
           write_all(s, &b, sizeof b) && write_all(s, &m, sizeof m) && write_all(s, &c, sizeof c);
}
static bool state_load(const clap_plugin_t* p, const clap_istream_t* s) {
    test_plugin_t* t = self(p);
    char magic[4];
    double g;
    int32_t b, m, c;
    if (!read_all(s, magic, 4) || memcmp(magic, k_magic, 4) != 0) return false;
    if (!read_all(s, &g, sizeof g) || !read_all(s, &b, sizeof b) ||
        !read_all(s, &m, sizeof m) || !read_all(s, &c, sizeof c)) return false;
    t->gain = g;
    t->bypass = b != 0;
    return true;
}
static const clap_plugin_state_t k_state = {state_save, state_load};

// ---- latency ----
static uint32_t latency_get(const clap_plugin_t* p) {
    (void)p;
    const char* f = fault();
    return strncmp(f, "latency-", 8) == 0 ? (uint32_t)atoi(f + 8) : 0;
}
static const clap_plugin_latency_t k_latency = {latency_get};

// ---- plugin ----
static bool plug_init(const clap_plugin_t* p) {
    test_plugin_t* t = self(p);
    t->thread_check = (const clap_host_thread_check_t*)t->host->get_extension(t->host, CLAP_EXT_THREAD_CHECK);
    t->main_thread_ok = t->thread_check && t->thread_check->is_main_thread(t->host);
    t->host->request_callback(t->host);
    return true;
}
static void plug_destroy(const clap_plugin_t* p) { free(self(p)); }
static bool plug_activate(const clap_plugin_t* p, double sr, uint32_t minf, uint32_t maxf) {
    (void)p; (void)sr; (void)minf; (void)maxf;
    return true;
}
static void plug_deactivate(const clap_plugin_t* p) { (void)p; }
static bool plug_start(const clap_plugin_t* p) { (void)p; return true; }
static void plug_stop(const clap_plugin_t* p) { (void)p; }
static void plug_reset(const clap_plugin_t* p) { (void)p; }

static clap_process_status plug_process(const clap_plugin_t* p, const clap_process_t* proc) {
    test_plugin_t* t = self(p);
    const char* f = fault();
    if (strcmp(f, "crash-process") == 0) abort();
    if (strcmp(f, "hang-process") == 0) sleep_forever();
    apply_events(t, proc->in_events);
    if (proc->audio_inputs_count < 1 || proc->audio_outputs_count < 1) return CLAP_PROCESS_CONTINUE;
    const clap_audio_buffer_t* in = &proc->audio_inputs[0];
    const clap_audio_buffer_t* out = &proc->audio_outputs[0];
    const double k = t->bypass ? 1.0 : (double)t->sign * t->gain;
    const int nan_out = strcmp(f, "nan-process") == 0;
    for (uint32_t c = 0; c < out->channel_count && c < in->channel_count; ++c)
        for (uint32_t s = 0; s < proc->frames_count; ++s)
            out->data32[c][s] = nan_out ? NAN : (float)(in->data32[c][s] * k);
    return CLAP_PROCESS_CONTINUE;
}

static const void* plug_ext(const clap_plugin_t* p, const char* id) {
    if (!strcmp(id, CLAP_EXT_AUDIO_PORTS)) return &k_ports;
    if (self(p)->sign == 0.0f) return NULL; // the synth offers nothing else
    if (!strcmp(id, CLAP_EXT_PARAMS)) return &k_params;
    if (!strcmp(id, CLAP_EXT_STATE)) return &k_state;
    if (!strcmp(id, CLAP_EXT_LATENCY)) return &k_latency;
    return NULL;
}
static void plug_on_main_thread(const clap_plugin_t* p) {
    test_plugin_t* t = self(p);
    if (!t->thread_check || t->thread_check->is_main_thread(t->host)) t->callback_seen = 1;
}

static const clap_plugin_t* create(const clap_host_t* host, const clap_plugin_descriptor_t* d, float sign) {
    test_plugin_t* t = (test_plugin_t*)calloc(1, sizeof *t);
    if (!t) return NULL;
    t->host = host;
    t->sign = sign;
    t->gain = 1.0;
    t->plugin.desc = d;
    t->plugin.plugin_data = t;
    t->plugin.init = plug_init;
    t->plugin.destroy = plug_destroy;
    t->plugin.activate = plug_activate;
    t->plugin.deactivate = plug_deactivate;
    t->plugin.start_processing = plug_start;
    t->plugin.stop_processing = plug_stop;
    t->plugin.reset = plug_reset;
    t->plugin.process = plug_process;
    t->plugin.get_extension = plug_ext;
    t->plugin.on_main_thread = plug_on_main_thread;
    return &t->plugin;
}

// ---- factory + entry ----
static uint32_t factory_count(const clap_plugin_factory_t* f) { (void)f; return 3; }
static const clap_plugin_descriptor_t* factory_desc(const clap_plugin_factory_t* f, uint32_t i) {
    (void)f;
    return i == 0 ? &k_gain_desc : i == 1 ? &k_invert_desc : i == 2 ? &k_synth_desc : NULL;
}
static const clap_plugin_t* factory_create(const clap_plugin_factory_t* f, const clap_host_t* host, const char* id) {
    (void)f;
    if (!clap_version_is_compatible(host->clap_version)) return NULL;
    if (!strcmp(id, k_gain_desc.id)) return create(host, &k_gain_desc, 1.0f);
    if (!strcmp(id, k_invert_desc.id)) return create(host, &k_invert_desc, -1.0f);
    if (!strcmp(id, k_synth_desc.id)) return create(host, &k_synth_desc, 0.0f);
    return NULL;
}
static const clap_plugin_factory_t k_factory = {factory_count, factory_desc, factory_create};

static bool entry_init(const char* path) {
    (void)path;
    const char* f = fault();
    if (strcmp(f, "crash-scan") == 0) abort();
    if (strcmp(f, "hang-scan") == 0) sleep_forever();
    return true;
}
static void entry_deinit(void) {}
static const void* entry_factory(const char* id) {
    return !strcmp(id, CLAP_PLUGIN_FACTORY_ID) ? &k_factory : NULL;
}

CLAP_EXPORT const clap_plugin_entry_t clap_entry = {CLAP_VERSION_INIT, entry_init, entry_deinit, entry_factory};
