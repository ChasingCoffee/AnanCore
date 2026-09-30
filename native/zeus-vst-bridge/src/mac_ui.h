// SPDX-License-Identifier: GPL-2.0-or-later
//
// macOS main-thread helpers for the VST3 bridge (implemented in mac_ui.mm).
// Plain C++ interface so bridge.cpp stays a .cpp file; everything AppKit
// lives behind these calls.
//
// AppKit only works on the process main thread, and only while something runs
// that thread's run loop — in Zeus that is the desktop host (Photino). A
// headless server never runs it, so ui_loop_available() is false there and the
// bridge keeps all plug-in calls on the caller's thread and reports editors as
// unavailable.

#pragma once

#include <functional>

namespace zvst_mac {

// True when an NSApplication exists and its run loop is running.
bool ui_loop_available();

bool is_main_thread();

// Run fn on the main thread and wait for it, up to timeout_ms. Runs inline
// when already on the main thread. Returns false on timeout; fn may then
// still run later, so callers must not free what it references.
bool run_on_main(const std::function<void()>& fn, int timeout_ms);

// ---- Editor window. MAIN THREAD ONLY. ----

// A titled window whose content view the plug-in's IPlugView attaches to.
// on_user_close runs (on the main thread) when the operator closes the
// window, before it goes away; it is not called by editor_window_destroy.
void* editor_window_create(const char* title, int width, int height, bool resizable,
                           std::function<void()> on_user_close);
void* editor_window_content_view(void* window);       // NSView* to attach to
void  editor_window_set_content_size(void* window, int width, int height);
void  editor_window_show(void* window);
void  editor_window_destroy(void* window);            // idempotent per handle

// A repeating main-queue timer; returns an opaque handle for timer_stop.
void* timer_start(int interval_ms, std::function<void()> tick);
void  timer_stop(void* timer);

} // namespace zvst_mac
