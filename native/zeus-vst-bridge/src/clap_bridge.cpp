// SPDX-License-Identifier: GPL-2.0-or-later
//
// Openhpsdr-Zeus CLAP host bridge (include/zclap.h).
//
// Hosts CLAP audio effects in-process with the header-only, MIT-licensed
// CLAP SDK. The shape mirrors the VST3 bridge: planar float32 in/out at the
// host's geometry, mono<->stereo bridging onto the plug-in's main ports,
// a state blob, a never-blocking process path.
//
// Threading follows the CLAP rules: every non-audio call happens on the
// plug-in's "main thread". Each instance owns a HostLoop that provides one —
// a dedicated thread with an event loop (plug-in callbacks, timers, POSIX
// fds on Linux, the editor window's messages on Windows), or on macOS the
// process main thread when the host runs an AppKit loop (desktop mode).

#include "zclap.h"

#include <clap/clap.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#  include <ole2.h>
#elif defined(__APPLE__)
#  include <CoreFoundation/CoreFoundation.h>
#  include <dlfcn.h>
#  include <poll.h>
#  include <unistd.h>
#  include "mac_ui.h"
#else
#  include <dlfcn.h>
#  include <poll.h>
#  include <unistd.h>
#  include <X11/Xlib.h>
#  include <X11/Xutil.h>
#endif

#define ZCLAP_LOG(...) do { std::fprintf(stderr, "[zclap] " __VA_ARGS__); std::fprintf(stderr, "\n"); std::fflush(stderr); } while (0)

namespace {

std::atomic<int> g_init_count{0};

using Clock = std::chrono::steady_clock;

// ---------------------------------------------------------------------
// Module loading. A .clap is loaded once per path and shared by every
// instance; clap_entry.init/deinit bracket its lifetime (ref-counted).
// ---------------------------------------------------------------------

struct Module {
    std::string path;
    const clap_plugin_entry_t* entry{nullptr};
    int refs{0};
#if defined(_WIN32)
    HMODULE lib{nullptr};
#elif defined(__APPLE__)
    CFBundleRef bundle{nullptr};
#else
    void* lib{nullptr};
#endif
};

std::mutex g_modules_mtx;
std::map<std::string, std::unique_ptr<Module>> g_modules;

static void unload_library(Module& m) {
#if defined(_WIN32)
    if (m.lib) FreeLibrary(m.lib);
    m.lib = nullptr;
#elif defined(__APPLE__)
    if (m.bundle) CFRelease(m.bundle);
    m.bundle = nullptr;
#else
    if (m.lib) dlclose(m.lib);
    m.lib = nullptr;
#endif
}

// Returns ZVST_OK and a referenced module, or a failure status.
static int acquire_module(const std::string& path, Module** out) {
    std::lock_guard<std::mutex> lk(g_modules_mtx);
    auto it = g_modules.find(path);
    if (it != g_modules.end()) {
        it->second->refs++;
        *out = it->second.get();
        return ZVST_OK;
    }
    auto m = std::make_unique<Module>();
    m->path = path;
#if defined(_WIN32)
    int wlen = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
    std::wstring wpath(static_cast<size_t>(wlen > 0 ? wlen : 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wpath.data(), wlen);
    if (GetFileAttributesW(wpath.c_str()) == INVALID_FILE_ATTRIBUTES) return ZVST_FILE_NOT_FOUND;
    // Resolve the plug-in's own dependent DLLs from its folder.
    m->lib = LoadLibraryExW(wpath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!m->lib) return ZVST_NOT_A_VST3;
    m->entry = reinterpret_cast<const clap_plugin_entry_t*>(GetProcAddress(m->lib, "clap_entry"));
#elif defined(__APPLE__)
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(
        kCFAllocatorDefault, reinterpret_cast<const UInt8*>(path.c_str()),
        static_cast<CFIndex>(path.size()), true);
    if (!url) return ZVST_FILE_NOT_FOUND;
    m->bundle = CFBundleCreate(kCFAllocatorDefault, url);
    CFRelease(url);
    if (!m->bundle) return ZVST_FILE_NOT_FOUND;
    if (!CFBundleLoadExecutable(m->bundle)) { unload_library(*m); return ZVST_NOT_A_VST3; }
    m->entry = reinterpret_cast<const clap_plugin_entry_t*>(
        CFBundleGetDataPointerForName(m->bundle, CFSTR("clap_entry")));
#else
    if (FILE* f = std::fopen(path.c_str(), "rb")) std::fclose(f);
    else return ZVST_FILE_NOT_FOUND;
    m->lib = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!m->lib) { ZCLAP_LOG("dlopen failed: %s", dlerror()); return ZVST_NOT_A_VST3; }
    m->entry = reinterpret_cast<const clap_plugin_entry_t*>(dlsym(m->lib, "clap_entry"));
#endif
    if (!m->entry || !clap_version_is_compatible(m->entry->clap_version) ||
        !m->entry->init || !m->entry->init(path.c_str())) {
        unload_library(*m);
        return ZVST_NOT_A_VST3;
    }
    m->refs = 1;
    *out = m.get();
    g_modules.emplace(path, std::move(m));
    return ZVST_OK;
}

static void release_module(Module* m) {
    if (!m) return;
    std::lock_guard<std::mutex> lk(g_modules_mtx);
    if (--m->refs > 0) return;
    if (m->entry && m->entry->deinit) m->entry->deinit();
    unload_library(*m);
    g_modules.erase(m->path);
}

static const clap_plugin_factory_t* plugin_factory(const Module* m) {
    return static_cast<const clap_plugin_factory_t*>(m->entry->get_factory(CLAP_PLUGIN_FACTORY_ID));
}

static bool has_feature(const clap_plugin_descriptor_t* d, const char* feature) {
    if (!d || !d->features) return false;
    for (const char* const* f = d->features; *f; ++f)
        if (std::strcmp(*f, feature) == 0) return true;
    return false;
}

static void json_escape(const char* s, std::string& out) {
    if (!s) return;
    for (const char* c = s; *c; ++c) {
        switch (*c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if (static_cast<unsigned char>(*c) < 0x20) {
                    char b[8];
                    std::snprintf(b, sizeof b, "\\u%04x", static_cast<unsigned>(static_cast<unsigned char>(*c)));
                    out += b;
                } else {
                    out += *c;
                }
        }
    }
}

// ---------------------------------------------------------------------
// HostLoop: the plug-in's main thread.
// ---------------------------------------------------------------------

class HostLoop {
public:
    virtual ~HostLoop() = default;
    // Run fn on the loop and wait (inline when already on it). False on
    // timeout; fn may still run later, so it must only capture shared state.
    virtual bool run(const std::function<void()>& fn, int timeout_ms) = 0;
    virtual void post(std::function<void()> fn) = 0;
    virtual bool on_loop_thread() const = 0;
    // Loop thread only.
    virtual bool add_timer(uint32_t period_ms, clap_id* id) = 0;
    virtual bool remove_timer(clap_id id) = 0;
    virtual bool add_fd(int fd, clap_posix_fd_flags_t flags) { (void)fd; (void)flags; return false; }
    virtual bool modify_fd(int fd, clap_posix_fd_flags_t flags) { (void)fd; (void)flags; return false; }
    virtual bool remove_fd(int fd) { (void)fd; return false; }

    std::function<void(clap_id)> on_timer;                       // plug-in timer callback
    std::function<void(int, clap_posix_fd_flags_t)> on_fd;       // plug-in fd callback
};

// A dedicated thread with an event loop: Windows pumps window messages too;
// POSIX polls a wake pipe, the plug-in's fds and (Linux) the editor's X
// connection. Used everywhere except the macOS desktop main thread.
class ThreadLoop final : public HostLoop {
public:
    ThreadLoop() {
#if defined(_WIN32)
        wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
#else
        if (pipe(wake_pipe_) != 0) { wake_pipe_[0] = wake_pipe_[1] = -1; }
#endif
        thread_ = std::thread([this] { loop(); });
        std::unique_lock<std::mutex> lk(mtx_);
        cv_.wait(lk, [this] { return started_; });
    }

    ~ThreadLoop() override {
        {
            std::lock_guard<std::mutex> lk(mtx_);
            quit_ = true;
        }
        wake();
        if (thread_.joinable()) {
            // A plug-in wedged on this thread must not hang the host: give
            // it a bounded wait, then leave the thread behind.
            for (int i = 0; i < 500 && !exited_.load(); ++i)
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            if (exited_.load()) thread_.join();
            else thread_.detach();
        }
#if defined(_WIN32)
        if (exited_.load() && wake_) CloseHandle(wake_);
#else
        if (exited_.load()) {
            if (wake_pipe_[0] >= 0) close(wake_pipe_[0]);
            if (wake_pipe_[1] >= 0) close(wake_pipe_[1]);
        }
#endif
    }

    bool run(const std::function<void()>& fn, int timeout_ms) override {
        if (on_loop_thread()) { fn(); return true; }
        struct Waiter { std::mutex m; std::condition_variable cv; bool done{false}; };
        auto w = std::make_shared<Waiter>();
        auto task = std::make_shared<std::function<void()>>(fn);
        post([w, task] {
            (*task)();
            std::lock_guard<std::mutex> lk(w->m);
            w->done = true;
            w->cv.notify_all();
        });
        std::unique_lock<std::mutex> lk(w->m);
        return w->cv.wait_for(lk, std::chrono::milliseconds(timeout_ms), [&] { return w->done; });
    }

    void post(std::function<void()> fn) override {
        {
            std::lock_guard<std::mutex> lk(mtx_);
            queue_.push_back(std::move(fn));
        }
        wake();
    }

    bool on_loop_thread() const override { return std::this_thread::get_id() == thread_id_; }

    bool add_timer(uint32_t period_ms, clap_id* id) override {
        if (!id) return false;
        const clap_id tid = next_timer_++;
        timers_.push_back({tid, std::max<uint32_t>(period_ms, 1),
                           Clock::now() + std::chrono::milliseconds(std::max<uint32_t>(period_ms, 1))});
        *id = tid;
        return true;
    }
    bool remove_timer(clap_id id) override {
        auto it = std::remove_if(timers_.begin(), timers_.end(), [&](const Timer& t) { return t.id == id; });
        const bool found = it != timers_.end();
        timers_.erase(it, timers_.end());
        return found;
    }

#if !defined(_WIN32)
    bool add_fd(int fd, clap_posix_fd_flags_t flags) override {
        for (auto& f : fds_) if (f.fd == fd) return false;
        fds_.push_back({fd, flags});
        return true;
    }
    bool modify_fd(int fd, clap_posix_fd_flags_t flags) override {
        for (auto& f : fds_) if (f.fd == fd) { f.flags = flags; return true; }
        return false;
    }
    bool remove_fd(int fd) override {
        auto it = std::remove_if(fds_.begin(), fds_.end(), [&](const Fd& f) { return f.fd == fd; });
        const bool found = it != fds_.end();
        fds_.erase(it, fds_.end());
        return found;
    }
#endif

#if defined(__linux__)
    // The editor's X connection, pumped by the loop (loop thread only).
    std::function<void()> on_x_events;
    int x_fd{-1};
#endif

private:
    struct Timer { clap_id id; uint32_t period_ms; Clock::time_point next; };
#if !defined(_WIN32)
    struct Fd { int fd; clap_posix_fd_flags_t flags; };
#endif

    void wake() {
#if defined(_WIN32)
        if (wake_) SetEvent(wake_);
#else
        if (wake_pipe_[1] >= 0) { char c = 1; ssize_t n = write(wake_pipe_[1], &c, 1); (void)n; }
#endif
    }

    int next_timeout_ms() const {
        int timeout = 1000;
        const auto now = Clock::now();
        for (const auto& t : timers_) {
            const auto d = std::chrono::duration_cast<std::chrono::milliseconds>(t.next - now).count();
            timeout = std::min<int>(timeout, static_cast<int>(std::max<long long>(d, 0)));
        }
        return timeout;
    }

    void fire_timers() {
        const auto now = Clock::now();
        // Copy ids first: a callback may add or remove timers.
        std::vector<clap_id> due;
        for (auto& t : timers_) {
            if (t.next <= now) {
                due.push_back(t.id);
                t.next = now + std::chrono::milliseconds(t.period_ms);
            }
        }
        for (clap_id id : due)
            if (on_timer) on_timer(id);
    }

    void drain_queue() {
        std::deque<std::function<void()>> q;
        {
            std::lock_guard<std::mutex> lk(mtx_);
            q.swap(queue_);
        }
        for (auto& fn : q) fn();
    }

    void loop() {
        thread_id_ = std::this_thread::get_id();
#if defined(_WIN32)
        OleInitialize(nullptr); // plug-in GUIs (and drag/drop) expect an STA with OLE
#endif
        {
            std::lock_guard<std::mutex> lk(mtx_);
            started_ = true;
        }
        cv_.notify_all();

        for (;;) {
            drain_queue();
            {
                std::lock_guard<std::mutex> lk(mtx_);
                if (quit_) break;
            }
            fire_timers();
            const int timeout = next_timeout_ms();
#if defined(_WIN32)
            MsgWaitForMultipleObjects(1, &wake_, FALSE, static_cast<DWORD>(timeout), QS_ALLINPUT);
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
#else
            std::vector<struct pollfd> pfds;
            pfds.push_back({wake_pipe_[0], POLLIN, 0});
#  if defined(__linux__)
            const bool with_x = x_fd >= 0;
            if (with_x) pfds.push_back({x_fd, POLLIN, 0});
#  endif
            const size_t first_plugin_fd = pfds.size();
            for (const auto& f : fds_) {
                short ev = 0;
                if (f.flags & CLAP_POSIX_FD_READ)  ev |= POLLIN;
                if (f.flags & CLAP_POSIX_FD_WRITE) ev |= POLLOUT;
                pfds.push_back({f.fd, ev, 0});
            }
            poll(pfds.data(), static_cast<nfds_t>(pfds.size()), timeout);
            if (pfds[0].revents & POLLIN) {
                char b[64];
                while (read(wake_pipe_[0], b, sizeof b) == static_cast<ssize_t>(sizeof b)) { }
            }
#  if defined(__linux__)
            if (on_x_events) on_x_events(); // cheap when nothing is pending
#  endif
            for (size_t i = first_plugin_fd; i < pfds.size(); ++i) {
                if (!pfds[i].revents) continue;
                clap_posix_fd_flags_t fl = 0;
                if (pfds[i].revents & POLLIN)  fl |= CLAP_POSIX_FD_READ;
                if (pfds[i].revents & POLLOUT) fl |= CLAP_POSIX_FD_WRITE;
                if (pfds[i].revents & (POLLERR | POLLHUP)) fl |= CLAP_POSIX_FD_ERROR;
                if (on_fd) on_fd(pfds[i].fd, fl);
            }
#endif
        }
#if defined(_WIN32)
        OleUninitialize();
#endif
        exited_.store(true);
    }

    std::thread thread_;
    std::thread::id thread_id_{};
    std::mutex mtx_;
    std::condition_variable cv_;
    bool started_{false};
    bool quit_{false};
    std::atomic<bool> exited_{false};
    std::deque<std::function<void()>> queue_;
    std::vector<Timer> timers_;
    clap_id next_timer_{1};
#if defined(_WIN32)
    HANDLE wake_{nullptr};
#else
    int wake_pipe_[2]{-1, -1};
    std::vector<Fd> fds_;
#endif
};

#if defined(__APPLE__)
// The process main thread, when the host runs an AppKit loop.
class MacMainLoop final : public HostLoop {
public:
    ~MacMainLoop() override {
        for (auto& kv : timers_) zvst_mac::timer_stop(kv.second);
    }
    bool run(const std::function<void()>& fn, int timeout_ms) override {
        return zvst_mac::run_on_main(fn, timeout_ms);
    }
    void post(std::function<void()> fn) override {
        auto task = std::make_shared<std::function<void()>>(std::move(fn));
        zvst_mac::run_on_main_async([task] { (*task)(); });
    }
    bool on_loop_thread() const override { return zvst_mac::is_main_thread(); }
    bool add_timer(uint32_t period_ms, clap_id* id) override {
        if (!id) return false;
        const clap_id tid = next_timer_++;
        void* t = zvst_mac::timer_start(static_cast<int>(std::max<uint32_t>(period_ms, 1)),
                                        [this, tid] { if (on_timer) on_timer(tid); });
        if (!t) return false;
        timers_[tid] = t;
        *id = tid;
        return true;
    }
    bool remove_timer(clap_id id) override {
        auto it = timers_.find(id);
        if (it == timers_.end()) return false;
        zvst_mac::timer_stop(it->second);
        timers_.erase(it);
        return true;
    }

private:
    std::map<clap_id, void*> timers_;
    clap_id next_timer_{1};
};
#endif

// ---------------------------------------------------------------------
// Per-instance state.
// ---------------------------------------------------------------------

struct ParamRange { double min; double max; bool stepped; };

struct EventList {
    // clap_event_param_value per queued edit, handed to process() as input.
    static constexpr int kCap = 256;
    clap_event_param_value_t events[kCap];
    uint32_t count{0};
};

struct ClapPlugin {
    Module* module{nullptr};
    const clap_plugin_t* plugin{nullptr};
    std::unique_ptr<HostLoop> loop;
    clap_host_t host{};

    // Extensions the plug-in offers (resolved once, on the main thread).
    const clap_plugin_audio_ports_t*     audio_ports{nullptr};
    const clap_plugin_params_t*          params{nullptr};
    const clap_plugin_state_t*           state{nullptr};
    const clap_plugin_latency_t*         latency{nullptr};
    const clap_plugin_gui_t*             gui{nullptr};
    const clap_plugin_timer_support_t*   timer_support{nullptr};
    const clap_plugin_posix_fd_support_t* fd_support{nullptr};

    int32_t channels{1};        // host geometry
    int32_t sample_rate{48000};
    int32_t block_size{256};
    uint32_t in_main_ch{2}, out_main_ch{2};
    std::atomic<int32_t> latency_samples{0};
    std::unordered_map<clap_id, ParamRange> param_ranges;

    // Audio buffers, all sized at load: main in/out (plug-in's channel
    // counts) plus silence / discard for every other port.
    std::vector<float>  main_in, main_out;          // in_main_ch / out_main_ch * block
    std::vector<float*> main_in_ptrs, main_out_ptrs;
    std::vector<float>  aux_silence, aux_discard;   // block
    std::vector<float*> aux_in_ptrs, aux_out_ptrs;
    std::vector<clap_audio_buffer_t> in_bufs, out_bufs;
    int64_t steady_time{0};

    // Parameter edits: producers (set_param) under param_mtx, drained by
    // process() with try_lock into `events`.
    std::mutex param_mtx;
    struct Edit { clap_id id; double value; };
    std::vector<Edit> pending;           // reserved kCap
    EventList events;
    clap_input_events_t in_events{};
    clap_output_events_t out_events{};

    std::atomic<bool> processing{false}; // start_processing succeeded on the audio thread
    std::mutex state_mtx;                // held across state.load; process only try_locks
    std::atomic<bool> callback_pending{false};

    // Editor. Main thread only, except editor_open.
    std::atomic<bool> editor_open{false};
    bool gui_created{false};
    bool gui_floating{false};
    std::string editor_title;
#if defined(_WIN32)
    HWND editor_hwnd{nullptr};
#elif defined(__APPLE__)
    bool ui_on_main{false};
    void* mac_window{nullptr};
#else
    Display* x_display{nullptr};
    Window   x_window{0};
    Atom     wm_delete{0};
#endif
};

thread_local bool t_in_process = false;

static ClapPlugin* owner_of(const clap_host_t* h) {
    return h ? static_cast<ClapPlugin*>(h->host_data) : nullptr;
}

// ---- Host extensions -------------------------------------------------

static void host_log(const clap_host_t*, clap_log_severity sev, const char* msg) {
    if (sev >= CLAP_LOG_WARNING) ZCLAP_LOG("plug-in: %s", msg ? msg : "");
}
static const clap_host_log_t k_host_log{host_log};

static bool host_is_main_thread(const clap_host_t* h) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->on_loop_thread();
}
static bool host_is_audio_thread(const clap_host_t*) { return t_in_process; }
static const clap_host_thread_check_t k_host_thread_check{host_is_main_thread, host_is_audio_thread};

static bool host_register_timer(const clap_host_t* h, uint32_t period_ms, clap_id* id) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->add_timer(period_ms, id);
}
static bool host_unregister_timer(const clap_host_t* h, clap_id id) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->remove_timer(id);
}
static const clap_host_timer_support_t k_host_timer{host_register_timer, host_unregister_timer};

static bool host_register_fd(const clap_host_t* h, int fd, clap_posix_fd_flags_t fl) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->add_fd(fd, fl);
}
static bool host_modify_fd(const clap_host_t* h, int fd, clap_posix_fd_flags_t fl) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->modify_fd(fd, fl);
}
static bool host_unregister_fd(const clap_host_t* h, int fd) {
    auto* p = owner_of(h);
    return p && p->loop && p->loop->remove_fd(fd);
}
static const clap_host_posix_fd_support_t k_host_fd{host_register_fd, host_modify_fd, host_unregister_fd};

static void host_params_rescan(const clap_host_t*, clap_param_rescan_flags) {}
static void host_params_clear(const clap_host_t*, clap_id, clap_param_clear_flags) {}
static uint32_t empty_events_size(const clap_input_events_t*) { return 0; }
static const clap_event_header_t* empty_events_get(const clap_input_events_t*, uint32_t) { return nullptr; }
static bool discard_try_push(const clap_output_events_t*, const clap_event_header_t*) { return true; }
static void host_params_request_flush(const clap_host_t* h) {
    // While audio runs, process() flushes. Otherwise flush on the main thread.
    auto* p = owner_of(h);
    if (!p || !p->loop || p->processing.load() || !p->params || !p->params->flush) return;
    p->loop->post([p] {
        static const clap_input_events_t in{nullptr, empty_events_size, empty_events_get};
        static const clap_output_events_t out{nullptr, discard_try_push};
        if (p->plugin && p->params && !p->processing.load()) p->params->flush(p->plugin, &in, &out);
    });
}
static const clap_host_params_t k_host_params{host_params_rescan, host_params_clear, host_params_request_flush};

static void host_state_mark_dirty(const clap_host_t*) {} // saves compare content instead
static const clap_host_state_t k_host_state{host_state_mark_dirty};

static void host_latency_changed(const clap_host_t* h) {
    auto* p = owner_of(h);
    if (p && p->latency && p->plugin) p->latency_samples.store(static_cast<int32_t>(p->latency->get(p->plugin)));
}
static const clap_host_latency_t k_host_latency{host_latency_changed};

static void host_audio_ports_rescan(const clap_host_t*, uint32_t) {}
static bool host_audio_ports_flag_supported(const clap_host_t*, uint32_t) { return false; }
static const clap_host_audio_ports_t k_host_audio_ports{host_audio_ports_flag_supported, host_audio_ports_rescan};

static void editor_close_on_main(ClapPlugin& p); // below

static void host_gui_resize_hints_changed(const clap_host_t*) {}
static bool host_gui_request_resize(const clap_host_t* h, uint32_t w, uint32_t hgt); // below
static bool host_gui_request_show(const clap_host_t* h) {
    auto* p = owner_of(h);
    return p && p->gui && p->gui_created && p->gui->show(p->plugin);
}
static bool host_gui_request_hide(const clap_host_t* h) {
    auto* p = owner_of(h);
    return p && p->gui && p->gui_created && p->gui->hide(p->plugin);
}
static void host_gui_closed(const clap_host_t* h, bool was_destroyed) {
    // A floating window the operator closed.
    auto* p = owner_of(h);
    if (!p || !p->loop) return;
    p->loop->post([p, was_destroyed] {
        if (was_destroyed) p->gui_created = false;
        editor_close_on_main(*p);
    });
}
static const clap_host_gui_t k_host_gui{host_gui_resize_hints_changed, host_gui_request_resize,
                                        host_gui_request_show, host_gui_request_hide, host_gui_closed};

static const void* host_get_extension(const clap_host_t*, const char* id) {
    if (!id) return nullptr;
    if (!std::strcmp(id, CLAP_EXT_LOG))           return &k_host_log;
    if (!std::strcmp(id, CLAP_EXT_THREAD_CHECK))  return &k_host_thread_check;
    if (!std::strcmp(id, CLAP_EXT_TIMER_SUPPORT)) return &k_host_timer;
#if !defined(_WIN32) && !defined(__APPLE__)
    if (!std::strcmp(id, CLAP_EXT_POSIX_FD_SUPPORT)) return &k_host_fd;
#endif
    if (!std::strcmp(id, CLAP_EXT_PARAMS))        return &k_host_params;
    if (!std::strcmp(id, CLAP_EXT_STATE))         return &k_host_state;
    if (!std::strcmp(id, CLAP_EXT_LATENCY))       return &k_host_latency;
    if (!std::strcmp(id, CLAP_EXT_AUDIO_PORTS))   return &k_host_audio_ports;
    if (!std::strcmp(id, CLAP_EXT_GUI))           return &k_host_gui;
    return nullptr;
}

static void host_request_restart(const clap_host_t*) {
    // Not supported: the chain would have to stop feeding this slot while it
    // re-activates. Plug-ins that need it keep running at their old setup.
}
static void host_request_process(const clap_host_t*) {}
static void host_request_callback(const clap_host_t* h) {
    auto* p = owner_of(h);
    if (!p || !p->loop) return;
    if (p->callback_pending.exchange(true)) return; // one already queued
    p->loop->post([p] {
        p->callback_pending.store(false);
        if (p->plugin) p->plugin->on_main_thread(p->plugin);
    });
}

// ---- Input events for process() ---------------------------------------

static uint32_t events_size(const clap_input_events_t* list) {
    return static_cast<const EventList*>(list->ctx)->count;
}
static const clap_event_header_t* events_get(const clap_input_events_t* list, uint32_t i) {
    auto* e = static_cast<const EventList*>(list->ctx);
    return i < e->count ? &e->events[i].header : nullptr;
}

// ---- Load / activate / teardown (main thread) --------------------------

static int setup_ports(ClapPlugin& p) {
    if (!p.audio_ports) return ZVST_ACTIVATE_FAILED;
    const uint32_t n_in  = p.audio_ports->count(p.plugin, true);
    const uint32_t n_out = p.audio_ports->count(p.plugin, false);
    if (n_in < 1 || n_out < 1) return ZVST_ACTIVATE_FAILED; // not an effect

    std::vector<clap_audio_port_info_t> ins(n_in), outs(n_out);
    for (uint32_t i = 0; i < n_in; ++i)
        if (!p.audio_ports->get(p.plugin, i, true, &ins[i])) return ZVST_ACTIVATE_FAILED;
    for (uint32_t i = 0; i < n_out; ++i)
        if (!p.audio_ports->get(p.plugin, i, false, &outs[i])) return ZVST_ACTIVATE_FAILED;

    // The main ports are flagged; by convention they are also index 0.
    auto main_index = [](const std::vector<clap_audio_port_info_t>& v) -> uint32_t {
        for (uint32_t i = 0; i < v.size(); ++i) if (v[i].flags & CLAP_AUDIO_PORT_IS_MAIN) return i;
        return 0;
    };
    const uint32_t mi = main_index(ins), mo = main_index(outs);
    p.in_main_ch  = ins[mi].channel_count;
    p.out_main_ch = outs[mo].channel_count;
    if (p.in_main_ch < 1 || p.in_main_ch > 2 || p.out_main_ch < 1 || p.out_main_ch > 2)
        return ZVST_ACTIVATE_FAILED;

    const size_t bs = static_cast<size_t>(p.block_size);
    p.main_in.assign(p.in_main_ch * bs, 0.0f);
    p.main_out.assign(p.out_main_ch * bs, 0.0f);
    p.main_in_ptrs.resize(p.in_main_ch);
    p.main_out_ptrs.resize(p.out_main_ch);
    for (uint32_t c = 0; c < p.in_main_ch; ++c)  p.main_in_ptrs[c]  = p.main_in.data()  + c * bs;
    for (uint32_t c = 0; c < p.out_main_ch; ++c) p.main_out_ptrs[c] = p.main_out.data() + c * bs;

    size_t aux_in_total = 0, aux_out_total = 0;
    for (uint32_t i = 0; i < n_in; ++i)  if (i != mi) aux_in_total  += ins[i].channel_count;
    for (uint32_t i = 0; i < n_out; ++i) if (i != mo) aux_out_total += outs[i].channel_count;
    if (aux_in_total)  p.aux_silence.assign(bs, 0.0f);
    if (aux_out_total) p.aux_discard.assign(bs, 0.0f);
    p.aux_in_ptrs.assign(aux_in_total, p.aux_silence.empty() ? nullptr : p.aux_silence.data());
    p.aux_out_ptrs.assign(aux_out_total, p.aux_discard.empty() ? nullptr : p.aux_discard.data());

    p.in_bufs.assign(n_in, clap_audio_buffer_t{});
    p.out_bufs.assign(n_out, clap_audio_buffer_t{});
    size_t off = 0;
    for (uint32_t i = 0; i < n_in; ++i) {
        auto& b = p.in_bufs[i];
        b.channel_count = ins[i].channel_count;
        if (i == mi) b.data32 = p.main_in_ptrs.data();
        else { b.data32 = p.aux_in_ptrs.data() + off; off += ins[i].channel_count; b.constant_mask = ~0ull; }
    }
    off = 0;
    for (uint32_t i = 0; i < n_out; ++i) {
        auto& b = p.out_bufs[i];
        b.channel_count = outs[i].channel_count;
        if (i == mo) b.data32 = p.main_out_ptrs.data();
        else { b.data32 = p.aux_out_ptrs.data() + off; off += outs[i].channel_count; }
    }
    // Keep the main ports first so process() can address them as [0].
    if (mi != 0) std::swap(p.in_bufs[0], p.in_bufs[mi]);
    if (mo != 0) std::swap(p.out_bufs[0], p.out_bufs[mo]);
    return ZVST_OK;
}

static void collect_param_ranges(ClapPlugin& p) {
    if (!p.params) return;
    const uint32_t n = p.params->count(p.plugin);
    for (uint32_t i = 0; i < n; ++i) {
        clap_param_info_t info{};
        if (!p.params->get_info(p.plugin, i, &info)) continue;
        p.param_ranges[info.id] = {info.min_value, info.max_value, (info.flags & CLAP_PARAM_IS_STEPPED) != 0};
    }
}

template <typename T>
static const T* ext(const ClapPlugin& p, const char* id) {
    return static_cast<const T*>(p.plugin->get_extension(p.plugin, id));
}

static void teardown(ClapPlugin& p) {
    if (p.plugin) {
        if (p.processing.exchange(false)) p.plugin->stop_processing(p.plugin);
        p.plugin->deactivate(p.plugin);
        p.plugin->destroy(p.plugin);
        p.plugin = nullptr;
    }
    release_module(p.module);
    p.module = nullptr;
}

static int do_load(ClapPlugin& p, const std::string& path, const std::string& wanted_id) {
    int st = acquire_module(path, &p.module);
    if (st != ZVST_OK) return st;
    const clap_plugin_factory_t* factory = plugin_factory(p.module);
    if (!factory) { release_module(p.module); p.module = nullptr; return ZVST_NOT_A_VST3; }

    std::string id = wanted_id;
    if (id.empty()) {
        const uint32_t n = factory->get_plugin_count(factory);
        for (uint32_t i = 0; i < n && id.empty(); ++i) {
            const clap_plugin_descriptor_t* d = factory->get_plugin_descriptor(factory, i);
            if (d && d->id && has_feature(d, CLAP_PLUGIN_FEATURE_AUDIO_EFFECT)) id = d->id;
        }
        if (id.empty()) { release_module(p.module); p.module = nullptr; return ZVST_NO_AUDIO_EFFECT_CLASS; }
    }

    p.host.clap_version = CLAP_VERSION;
    p.host.host_data = &p;
    p.host.name = "Openhpsdr-Zeus";
    p.host.vendor = "Openhpsdr-Zeus";
    p.host.url = "https://github.com/ChasingCoffee/AnanCore";
    p.host.version = "1.0";
    p.host.get_extension = host_get_extension;
    p.host.request_restart = host_request_restart;
    p.host.request_process = host_request_process;
    p.host.request_callback = host_request_callback;

    p.plugin = factory->create_plugin(factory, &p.host, id.c_str());
    if (!p.plugin) { release_module(p.module); p.module = nullptr; return ZVST_NO_AUDIO_EFFECT_CLASS; }
    if (!p.plugin->init(p.plugin)) {
        p.plugin->destroy(p.plugin);
        p.plugin = nullptr;
        release_module(p.module);
        p.module = nullptr;
        return ZVST_ACTIVATE_FAILED;
    }

    p.audio_ports   = ext<clap_plugin_audio_ports_t>(p, CLAP_EXT_AUDIO_PORTS);
    p.params        = ext<clap_plugin_params_t>(p, CLAP_EXT_PARAMS);
    p.state         = ext<clap_plugin_state_t>(p, CLAP_EXT_STATE);
    p.latency       = ext<clap_plugin_latency_t>(p, CLAP_EXT_LATENCY);
    p.gui           = ext<clap_plugin_gui_t>(p, CLAP_EXT_GUI);
    p.timer_support = ext<clap_plugin_timer_support_t>(p, CLAP_EXT_TIMER_SUPPORT);
    p.fd_support    = ext<clap_plugin_posix_fd_support_t>(p, CLAP_EXT_POSIX_FD_SUPPORT);

    ClapPlugin* pp = &p;
    p.loop->on_timer = [pp](clap_id tid) {
        if (pp->plugin && pp->timer_support) pp->timer_support->on_timer(pp->plugin, tid);
    };
    p.loop->on_fd = [pp](int fd, clap_posix_fd_flags_t fl) {
        if (pp->plugin && pp->fd_support) pp->fd_support->on_fd(pp->plugin, fd, fl);
    };

    st = setup_ports(p);
    if (st == ZVST_OK && !p.plugin->activate(p.plugin, static_cast<double>(p.sample_rate), 1,
                                             static_cast<uint32_t>(p.block_size)))
        st = ZVST_ACTIVATE_FAILED;
    if (st != ZVST_OK) {
        p.plugin->destroy(p.plugin);
        p.plugin = nullptr;
        release_module(p.module);
        p.module = nullptr;
        return st;
    }
    if (p.latency) p.latency_samples.store(static_cast<int32_t>(p.latency->get(p.plugin)));
    collect_param_ranges(p);

    p.pending.reserve(EventList::kCap);
    p.in_events.ctx = &p.events;
    p.in_events.size = events_size;
    p.in_events.get = events_get;
    p.out_events.ctx = nullptr;
    p.out_events.try_push = discard_try_push;
    return ZVST_OK;
}

// ---- Editor (main thread) ----------------------------------------------

static const char* platform_gui_api() {
#if defined(_WIN32)
    return CLAP_WINDOW_API_WIN32;
#elif defined(__APPLE__)
    return CLAP_WINDOW_API_COCOA;
#else
    return CLAP_WINDOW_API_X11;
#endif
}

#if defined(_WIN32)
static const wchar_t* kClapEditorClass = L"ZeusClapEditorWindow";

static LRESULT CALLBACK clap_editor_wndproc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    auto* p = reinterpret_cast<ClapPlugin*>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (msg == WM_CLOSE) {
        if (p) editor_close_on_main(*p);
        else DestroyWindow(h);
        return 0;
    }
    if (msg == WM_SIZE && p && p->gui && p->gui_created && !p->gui_floating) {
        uint32_t cw = LOWORD(l), ch = HIWORD(l);
        if (p->gui->can_resize(p->plugin) && p->gui->adjust_size(p->plugin, &cw, &ch))
            p->gui->set_size(p->plugin, cw, ch);
        return 0;
    }
    if (msg == WM_ERASEBKGND) return 1;
    return DefWindowProcW(h, msg, w, l);
}

static bool resize_host_window(ClapPlugin& p, uint32_t w, uint32_t h) {
    if (!p.editor_hwnd) return false;
    RECT r{0, 0, static_cast<LONG>(w), static_cast<LONG>(h)};
    AdjustWindowRectEx(&r, static_cast<DWORD>(GetWindowLongW(p.editor_hwnd, GWL_STYLE)), FALSE,
                       static_cast<DWORD>(GetWindowLongW(p.editor_hwnd, GWL_EXSTYLE)));
    return SetWindowPos(p.editor_hwnd, nullptr, 0, 0, r.right - r.left, r.bottom - r.top,
                        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE) != 0;
}

static bool create_host_window(ClapPlugin& p, uint32_t w, uint32_t h, bool resizable, clap_window_t* out) {
    static std::once_flag once;
    std::call_once(once, [] {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = clap_editor_wndproc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)); // IDC_ARROW (wide, without UNICODE)
        wc.lpszClassName = kClapEditorClass;
        RegisterClassExW(&wc);
    });
    DWORD style = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
    if (resizable) style |= WS_THICKFRAME | WS_MAXIMIZEBOX;
    RECT r{0, 0, static_cast<LONG>(w), static_cast<LONG>(h)};
    AdjustWindowRectEx(&r, style, FALSE, WS_EX_APPWINDOW);
    const std::string& t = p.editor_title.empty() ? std::string("CLAP Plug-in") : p.editor_title;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, t.c_str(), -1, nullptr, 0);
    std::wstring title(static_cast<size_t>(wlen > 0 ? wlen : 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, t.c_str(), -1, title.data(), wlen);
    p.editor_hwnd = CreateWindowExW(WS_EX_APPWINDOW, kClapEditorClass, title.c_str(), style,
                                    CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left, r.bottom - r.top,
                                    nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!p.editor_hwnd) return false;
    SetWindowLongPtrW(p.editor_hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(&p));
    out->api = CLAP_WINDOW_API_WIN32;
    out->win32 = p.editor_hwnd;
    return true;
}

static void show_host_window(ClapPlugin& p) {
    ShowWindow(p.editor_hwnd, SW_SHOW);
    UpdateWindow(p.editor_hwnd);
}

static void destroy_host_window(ClapPlugin& p) {
    if (!p.editor_hwnd) return;
    HWND h = p.editor_hwnd;
    p.editor_hwnd = nullptr;
    SetWindowLongPtrW(h, GWLP_USERDATA, 0);
    DestroyWindow(h);
}
#elif defined(__APPLE__)
static bool resize_host_window(ClapPlugin& p, uint32_t w, uint32_t h) {
    if (!p.mac_window) return false;
    zvst_mac::editor_window_set_content_size(p.mac_window, static_cast<int>(w), static_cast<int>(h));
    return true;
}

static bool create_host_window(ClapPlugin& p, uint32_t w, uint32_t h, bool resizable, clap_window_t* out) {
    ClapPlugin* pp = &p;
    p.mac_window = zvst_mac::editor_window_create(
        p.editor_title.empty() ? "CLAP Plug-in" : p.editor_title.c_str(),
        static_cast<int>(w), static_cast<int>(h), resizable,
        [pp] { editor_close_on_main(*pp); }); // operator closed the window
    if (!p.mac_window) return false;
    out->api = CLAP_WINDOW_API_COCOA;
    out->cocoa = zvst_mac::editor_window_content_view(p.mac_window);
    return true;
}

static void show_host_window(ClapPlugin& p) { zvst_mac::editor_window_show(p.mac_window); }

static void destroy_host_window(ClapPlugin& p) {
    if (!p.mac_window) return;
    void* w = p.mac_window;
    p.mac_window = nullptr;
    zvst_mac::editor_window_destroy(w);
}
#else
static void x_pump(ClapPlugin& p) {
    if (!p.x_display) return;
    while (XPending(p.x_display)) {
        XEvent ev;
        XNextEvent(p.x_display, &ev);
        if (ev.type == ClientMessage && static_cast<Atom>(ev.xclient.data.l[0]) == p.wm_delete) {
            editor_close_on_main(p);
            return;
        }
        if (ev.type == ConfigureNotify && p.gui && p.gui_created && p.gui->can_resize(p.plugin)) {
            uint32_t cw = static_cast<uint32_t>(ev.xconfigure.width);
            uint32_t ch = static_cast<uint32_t>(ev.xconfigure.height);
            if (p.gui->adjust_size(p.plugin, &cw, &ch)) p.gui->set_size(p.plugin, cw, ch);
        }
    }
}

static bool resize_host_window(ClapPlugin& p, uint32_t w, uint32_t h) {
    if (!p.x_display || !p.x_window) return false;
    XResizeWindow(p.x_display, p.x_window, std::max<uint32_t>(w, 1), std::max<uint32_t>(h, 1));
    XFlush(p.x_display);
    return true;
}

static bool create_host_window(ClapPlugin& p, uint32_t w, uint32_t h, bool resizable, clap_window_t* out) {
    (void)resizable;
    if (!p.x_display) {
        p.x_display = XOpenDisplay(nullptr);
        if (!p.x_display) { ZCLAP_LOG("editor: no X display"); return false; }
        auto* tl = static_cast<ThreadLoop*>(p.loop.get());
        tl->x_fd = ConnectionNumber(p.x_display);
        ClapPlugin* pp = &p;
        tl->on_x_events = [pp] { x_pump(*pp); };
    }
    const int screen = DefaultScreen(p.x_display);
    p.x_window = XCreateSimpleWindow(p.x_display, RootWindow(p.x_display, screen), 0, 0,
                                     std::max<uint32_t>(w, 1), std::max<uint32_t>(h, 1), 0,
                                     BlackPixel(p.x_display, screen), BlackPixel(p.x_display, screen));
    if (!p.x_window) return false;
    XStoreName(p.x_display, p.x_window, p.editor_title.empty() ? "CLAP Plug-in" : p.editor_title.c_str());
    p.wm_delete = XInternAtom(p.x_display, "WM_DELETE_WINDOW", False);
    XSetWMProtocols(p.x_display, p.x_window, &p.wm_delete, 1);
    XSelectInput(p.x_display, p.x_window, StructureNotifyMask);
    out->api = CLAP_WINDOW_API_X11;
    out->x11 = static_cast<clap_xwnd>(p.x_window);
    return true;
}

static void show_host_window(ClapPlugin& p) {
    XMapWindow(p.x_display, p.x_window);
    XFlush(p.x_display);
}

static void destroy_host_window(ClapPlugin& p) {
    if (!p.x_display || !p.x_window) return;
    XDestroyWindow(p.x_display, p.x_window);
    XFlush(p.x_display);
    p.x_window = 0;
}
#endif

static bool host_gui_request_resize(const clap_host_t* h, uint32_t w, uint32_t hgt) {
    auto* p = owner_of(h);
    if (!p || p->gui_floating) return false;
    return resize_host_window(*p, w, hgt);
}

static void editor_close_on_main(ClapPlugin& p) {
    if (p.gui && p.gui_created) {
        p.gui->hide(p.plugin);
        p.gui->destroy(p.plugin);
    }
    p.gui_created = false;
    destroy_host_window(p);
    p.editor_open.store(false);
}

static int editor_open_on_main(ClapPlugin& p) {
    if (p.editor_open.load()) return 1;
    if (!p.gui) { ZCLAP_LOG("editor: plug-in has no GUI"); return 0; }
    const char* api = platform_gui_api();
    bool floating;
    if (p.gui->is_api_supported(p.plugin, api, false)) floating = false;
    else if (p.gui->is_api_supported(p.plugin, api, true)) floating = true;
    else { ZCLAP_LOG("editor: no %s GUI", api); return 0; }
    if (!p.gui->create(p.plugin, api, floating)) { ZCLAP_LOG("editor: create failed"); return 0; }
    p.gui_created = true;
    p.gui_floating = floating;

    if (floating) {
        p.gui->suggest_title(p.plugin, p.editor_title.empty() ? "CLAP Plug-in" : p.editor_title.c_str());
        if (!p.gui->show(p.plugin)) { editor_close_on_main(p); return 0; }
        p.editor_open.store(true);
        return 1;
    }

    uint32_t w = 800, h = 600;
    p.gui->get_size(p.plugin, &w, &h);
    clap_window_t win{};
    if (!create_host_window(p, w, h, p.gui->can_resize(p.plugin), &win) ||
        !p.gui->set_parent(p.plugin, &win)) {
        editor_close_on_main(p);
        return 0;
    }
    show_host_window(p);
    p.gui->show(p.plugin);
    p.editor_open.store(true);
    return 1;
}

// ---- State blob: "ZCS1" | u32 length | plug-in state stream ------------

static constexpr uint8_t kClapStateMagic[4] = {'Z', 'C', 'S', '1'};

struct WriteCtx { std::vector<uint8_t>* out; };
struct ReadCtx { const uint8_t* data; size_t size; size_t pos; };

static int64_t ostream_write(const clap_ostream_t* s, const void* buf, uint64_t size) {
    auto* c = static_cast<WriteCtx*>(s->ctx);
    auto* b = static_cast<const uint8_t*>(buf);
    c->out->insert(c->out->end(), b, b + size);
    return static_cast<int64_t>(size);
}
static int64_t istream_read(const clap_istream_t* s, void* buf, uint64_t size) {
    auto* c = static_cast<ReadCtx*>(s->ctx);
    const size_t n = static_cast<size_t>(std::min<uint64_t>(size, c->size - c->pos));
    std::memcpy(buf, c->data + c->pos, n);
    c->pos += n;
    return static_cast<int64_t>(n);
}

static int build_state(ClapPlugin& p, std::vector<uint8_t>& out) {
    std::vector<uint8_t> body;
    if (p.state) {
        WriteCtx wc{&body};
        clap_ostream_t os{&wc, ostream_write};
        if (!p.state->save(p.plugin, &os)) return ZVST_OTHER;
    }
    out.resize(8 + body.size());
    std::memcpy(out.data(), kClapStateMagic, 4);
    const uint32_t n = static_cast<uint32_t>(body.size());
    out[4] = static_cast<uint8_t>(n); out[5] = static_cast<uint8_t>(n >> 8);
    out[6] = static_cast<uint8_t>(n >> 16); out[7] = static_cast<uint8_t>(n >> 24);
    if (!body.empty()) std::memcpy(out.data() + 8, body.data(), body.size());
    return ZVST_OK;
}

static int apply_state(ClapPlugin& p, const std::vector<uint8_t>& blob) {
    if (blob.size() < 8 || std::memcmp(blob.data(), kClapStateMagic, 4) != 0) return ZVST_INVALID_ARGUMENTS;
    const size_t n = static_cast<size_t>(blob[4]) | (static_cast<size_t>(blob[5]) << 8) |
                     (static_cast<size_t>(blob[6]) << 16) | (static_cast<size_t>(blob[7]) << 24);
    if (8 + n != blob.size()) return ZVST_INVALID_ARGUMENTS;
    if (!p.state || n == 0) return ZVST_OK;
    ReadCtx rc{blob.data() + 8, n, 0};
    clap_istream_t is{&rc, istream_read};
    bool ok;
    {
        std::lock_guard<std::mutex> lk(p.state_mtx);
        ok = p.state->load(p.plugin, &is);
    }
    return ok ? ZVST_OK : ZVST_OTHER;
}

} // namespace

extern "C" {

int32_t zclap_init(int32_t abi) {
    if (abi != ZCLAP_ABI) return ZVST_ABI_MISMATCH;
    g_init_count.fetch_add(1);
    return ZVST_OK;
}

int32_t zclap_shutdown(void) {
    if (g_init_count.load() > 0) g_init_count.fetch_sub(1);
    return ZVST_OK;
}

int32_t zclap_describe(const char* path, char* out_json, int32_t out_cap, int32_t* out_len) {
    if (out_len) *out_len = 0;
    if (!path || !out_json || out_cap < 1) return ZVST_INVALID_ARGUMENTS;
    Module* m = nullptr;
    const int st = acquire_module(path, &m);
    if (st != ZVST_OK) return st;
    std::string json = "[";
    if (const clap_plugin_factory_t* f = plugin_factory(m)) {
        const uint32_t n = f->get_plugin_count(f);
        bool first = true;
        for (uint32_t i = 0; i < n; ++i) {
            const clap_plugin_descriptor_t* d = f->get_plugin_descriptor(f, i);
            if (!d || !d->id) continue;
            if (!first) json += ",";
            first = false;
            std::string features;
            if (d->features)
                for (const char* const* ft = d->features; *ft; ++ft) {
                    if (!features.empty()) features += "|";
                    features += *ft;
                }
            json += "{\"uid\":\"";      json_escape(d->id, json);
            json += "\",\"name\":\"";   json_escape(d->name, json);
            json += "\",\"category\":\""; json_escape(features.c_str(), json);
            json += "\",\"vendor\":\""; json_escape(d->vendor, json);
            json += "\"}";
        }
    }
    json += "]";
    release_module(m);
    if (out_len) *out_len = static_cast<int32_t>(json.size());
    const size_t n = std::min<size_t>(json.size(), static_cast<size_t>(out_cap) - 1);
    std::memcpy(out_json, json.data(), n);
    out_json[n] = '\0';
    return ZVST_OK;
}

int32_t zclap_load(const char* path, const char* plugin_id, int32_t channels,
                   int32_t sample_rate, int32_t block_size, zclap_handle_t* out_handle) {
    if (!path || !out_handle) return ZVST_INVALID_ARGUMENTS;
    if (channels < 1 || channels > 2) return ZVST_INVALID_ARGUMENTS;
    if (sample_rate < 44100 || sample_rate > 192000) return ZVST_INVALID_ARGUMENTS;
    if (block_size < 32 || block_size > 4096) return ZVST_INVALID_ARGUMENTS;
    *out_handle = nullptr;

    auto p = std::make_unique<ClapPlugin>();
    p->channels = channels;
    p->sample_rate = sample_rate;
    p->block_size = block_size;
#if defined(__APPLE__)
    p->ui_on_main = zvst_mac::ui_loop_available();
    if (p->ui_on_main) p->loop = std::make_unique<MacMainLoop>();
    else p->loop = std::make_unique<ThreadLoop>();
#else
    p->loop = std::make_unique<ThreadLoop>();
#endif
    auto status = std::make_shared<std::atomic<int>>(ZVST_OTHER);
    ClapPlugin* raw = p.get();
    const std::string spath = path, sid = plugin_id ? plugin_id : "";
    if (!p->loop->run([raw, status, spath, sid] { status->store(do_load(*raw, spath, sid)); }, 30000)) {
        (void)p.release(); // main thread wedged mid-load: leak rather than free under it
        return ZVST_OTHER;
    }
    if (status->load() != ZVST_OK) return status->load();
    *out_handle = p.release();
    return ZVST_OK;
}

int32_t zclap_process(zclap_handle_t handle, const float* input, float* output, int32_t frames) {
    if (!handle) return ZVST_INVALID_HANDLE;
    if (!input || !output) return ZVST_INVALID_ARGUMENTS;
    auto* p = static_cast<ClapPlugin*>(handle);
    if (frames < 1 || frames > p->block_size || !p->plugin) return ZVST_INVALID_ARGUMENTS;
    const size_t n = static_cast<size_t>(frames);
    const size_t host_total = static_cast<size_t>(p->channels) * n;

    std::unique_lock<std::mutex> state_lk(p->state_mtx, std::try_to_lock);
    if (!state_lk.owns_lock()) {
        if (output != input) std::memmove(output, input, host_total * sizeof(float));
        return ZVST_OK;
    }

    t_in_process = true;
    if (!p->processing.load()) {
        if (!p->plugin->start_processing(p->plugin)) {
            t_in_process = false;
            if (output != input) std::memmove(output, input, host_total * sizeof(float));
            return ZVST_OTHER;
        }
        p->processing.store(true);
    }

    // Host geometry -> the plug-in's main input (mono duplicated to stereo,
    // stereo averaged to mono).
    const size_t bs = static_cast<size_t>(p->block_size);
    float* in0 = p->main_in.data();
    if (p->in_main_ch == static_cast<uint32_t>(p->channels)) {
        std::memcpy(in0, input, n * sizeof(float));
        if (p->in_main_ch == 2) std::memcpy(in0 + bs, input + n, n * sizeof(float));
    } else if (p->in_main_ch == 2) { // mono host
        std::memcpy(in0, input, n * sizeof(float));
        std::memcpy(in0 + bs, input, n * sizeof(float));
    } else { // stereo host, mono plug-in
        for (size_t i = 0; i < n; i++) in0[i] = 0.5f * (input[i] + input[n + i]);
    }
    if (!p->aux_silence.empty()) std::memset(p->aux_silence.data(), 0, n * sizeof(float));

    // Queued parameter edits become this block's input events.
    p->events.count = 0;
    if (p->param_mtx.try_lock()) {
        for (const auto& e : p->pending) {
            if (p->events.count >= EventList::kCap) break;
            clap_event_param_value_t& ev = p->events.events[p->events.count++];
            ev.header.size = sizeof(clap_event_param_value_t);
            ev.header.time = 0;
            ev.header.space_id = CLAP_CORE_EVENT_SPACE_ID;
            ev.header.type = CLAP_EVENT_PARAM_VALUE;
            ev.header.flags = 0;
            ev.param_id = e.id;
            ev.cookie = nullptr;
            ev.note_id = -1;
            ev.port_index = -1;
            ev.channel = -1;
            ev.key = -1;
            ev.value = e.value;
        }
        p->pending.clear();
        p->param_mtx.unlock();
    }

    clap_process_t proc{};
    proc.steady_time = p->steady_time;
    proc.frames_count = static_cast<uint32_t>(frames);
    proc.transport = nullptr;
    proc.audio_inputs = p->in_bufs.data();
    proc.audio_outputs = p->out_bufs.data();
    proc.audio_inputs_count = static_cast<uint32_t>(p->in_bufs.size());
    proc.audio_outputs_count = static_cast<uint32_t>(p->out_bufs.size());
    proc.in_events = &p->in_events;
    proc.out_events = &p->out_events;
    const clap_process_status st = p->plugin->process(p->plugin, &proc);
    p->steady_time += frames;
    t_in_process = false;

    if (st == CLAP_PROCESS_ERROR) {
        if (output != input) std::memmove(output, input, host_total * sizeof(float));
        return ZVST_OTHER;
    }

    // The plug-in's main output -> host geometry.
    const float* out0 = p->main_out.data();
    if (p->out_main_ch == static_cast<uint32_t>(p->channels)) {
        std::memcpy(output, out0, n * sizeof(float));
        if (p->out_main_ch == 2) std::memcpy(output + n, out0 + bs, n * sizeof(float));
    } else if (p->out_main_ch == 2) { // stereo plug-in, mono host
        for (size_t i = 0; i < n; i++) output[i] = 0.5f * (out0[i] + out0[bs + i]);
    } else { // mono plug-in, stereo host
        std::memcpy(output, out0, n * sizeof(float));
        std::memcpy(output + n, out0, n * sizeof(float));
    }
    return ZVST_OK;
}

int32_t zclap_set_param(zclap_handle_t handle, uint32_t param_id, double normalized) {
    if (!handle) return ZVST_INVALID_HANDLE;
    auto* p = static_cast<ClapPlugin*>(handle);
    normalized = std::clamp(normalized, 0.0, 1.0);
    double value = normalized;
    auto it = p->param_ranges.find(param_id);
    if (it != p->param_ranges.end()) {
        value = it->second.min + normalized * (it->second.max - it->second.min);
        if (it->second.stepped) value = std::round(value);
    }
    std::lock_guard<std::mutex> lk(p->param_mtx);
    if (p->pending.size() < static_cast<size_t>(EventList::kCap)) p->pending.push_back({param_id, value});
    return ZVST_OK;
}

int32_t zclap_get_latency_samples(zclap_handle_t handle) {
    return handle ? static_cast<ClapPlugin*>(handle)->latency_samples.load() : 0;
}

int32_t zclap_unload(zclap_handle_t handle) {
    if (!handle) return ZVST_OK;
    auto* p = static_cast<ClapPlugin*>(handle);
    // stop_processing belongs to the audio thread. The host guarantees no
    // process() call is in flight during unload, so this thread stands in
    // for it (and reports itself as the audio thread meanwhile).
    if (p->processing.exchange(false) && p->plugin) {
        t_in_process = true;
        p->plugin->stop_processing(p->plugin);
        t_in_process = false;
    }
    if (!p->loop->run([p] {
            editor_close_on_main(*p);
#if defined(__linux__)
            if (p->x_display) {
                auto* tl = static_cast<ThreadLoop*>(p->loop.get());
                tl->on_x_events = nullptr;
                tl->x_fd = -1;
                XCloseDisplay(p->x_display);
                p->x_display = nullptr;
            }
#endif
            teardown(*p);
        }, 10000))
        return ZVST_OK; // wedged in the plug-in: leak rather than free under it
    p->loop.reset();
    delete p;
    return ZVST_OK;
}

int32_t zclap_get_state(zclap_handle_t handle, uint8_t* out_buf, int32_t cap, int32_t* out_len) {
    if (out_len) *out_len = 0;
    if (!handle) return ZVST_INVALID_HANDLE;
    if (cap < 0 || (cap > 0 && !out_buf)) return ZVST_INVALID_ARGUMENTS;
    auto* p = static_cast<ClapPlugin*>(handle);
    struct Job { std::vector<uint8_t> blob; std::atomic<int> status{ZVST_OTHER}; };
    auto job = std::make_shared<Job>();
    if (!p->loop->run([p, job] { job->status.store(build_state(*p, job->blob)); }, 10000)) return ZVST_OTHER;
    if (job->status.load() != ZVST_OK) return job->status.load();
    if (job->blob.size() > static_cast<size_t>(INT32_MAX)) return ZVST_OTHER;
    const auto size = static_cast<int32_t>(job->blob.size());
    if (out_len) *out_len = size;
    if (size > cap) return ZVST_BUFFER_TOO_SMALL;
    std::memcpy(out_buf, job->blob.data(), static_cast<size_t>(size));
    return ZVST_OK;
}

int32_t zclap_set_state(zclap_handle_t handle, const uint8_t* data, int32_t len) {
    if (!handle) return ZVST_INVALID_HANDLE;
    if (!data || len < 0) return ZVST_INVALID_ARGUMENTS;
    auto* p = static_cast<ClapPlugin*>(handle);
    auto blob = std::make_shared<std::vector<uint8_t>>(data, data + len);
    auto status = std::make_shared<std::atomic<int>>(ZVST_OTHER);
    if (!p->loop->run([p, blob, status] { status->store(apply_state(*p, *blob)); }, 10000)) return ZVST_OTHER;
    return status->load();
}

int32_t zclap_editor_open(zclap_handle_t handle, const char* title) {
    if (!handle) return ZVST_INVALID_HANDLE;
    auto* p = static_cast<ClapPlugin*>(handle);
#if defined(__APPLE__)
    if (!p->ui_on_main || !zvst_mac::ui_loop_available()) return ZVST_NOT_IMPLEMENTED;
#endif
    auto shown = std::make_shared<std::atomic<int>>(0);
    const std::string t = title ? title : "";
    if (!p->loop->run([p, shown, t] { p->editor_title = t; shown->store(editor_open_on_main(*p)); }, 20000))
        return ZVST_OTHER;
    return shown->load() ? ZVST_OK : ZVST_OTHER;
}

int32_t zclap_editor_close(zclap_handle_t handle) {
    if (!handle) return ZVST_OK;
    auto* p = static_cast<ClapPlugin*>(handle);
    p->loop->run([p] { editor_close_on_main(*p); }, 5000);
    return ZVST_OK;
}

int32_t zclap_editor_is_open(zclap_handle_t handle) {
    return handle && static_cast<ClapPlugin*>(handle)->editor_open.load() ? 1 : 0;
}

} // extern "C"
