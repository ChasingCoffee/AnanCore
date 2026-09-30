// SPDX-License-Identifier: GPL-2.0-or-later
//
// macOS main-thread helpers for the VST3 bridge — see mac_ui.h. Compiled with
// ARC (CMakeLists.txt).

#include "mac_ui.h"

#import <AppKit/AppKit.h>
#include <dispatch/dispatch.h>

#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <string>

@interface ZvstEditorHolder : NSObject <NSWindowDelegate>
@property(strong) NSWindow* window;
@property(assign) BOOL closing; // inside windowWillClose: don't -close again
@end

@implementation ZvstEditorHolder {
    std::function<void()> _onUserClose;
}
- (void)setOnUserClose:(std::function<void()>)f {
    _onUserClose = std::move(f);
}
- (void)clearOnUserClose {
    _onUserClose = nullptr;
}
- (void)windowWillClose:(NSNotification*)note {
    (void)note;
    self.closing = YES;
    if (_onUserClose) {
        auto f = std::move(_onUserClose);
        _onUserClose = nullptr;
        f();
    }
}
@end

namespace zvst_mac {

bool ui_loop_available() {
    NSApplication* app = NSApp;
    return app != nil && [app isRunning];
}

bool is_main_thread() {
    return [NSThread isMainThread];
}

bool run_on_main(const std::function<void()>& fn, int timeout_ms) {
    if ([NSThread isMainThread]) {
        fn();
        return true;
    }
    struct Waiter {
        std::mutex m;
        std::condition_variable cv;
        bool done{false};
        std::function<void()> fn;
    };
    auto w = std::make_shared<Waiter>();
    w->fn = fn;
    dispatch_async(dispatch_get_main_queue(), ^{
        w->fn();
        std::lock_guard<std::mutex> lk(w->m);
        w->done = true;
        w->cv.notify_all();
    });
    std::unique_lock<std::mutex> lk(w->m);
    return w->cv.wait_for(lk, std::chrono::milliseconds(timeout_ms), [&] { return w->done; });
}

void run_on_main_async(std::function<void()> fn) {
    if ([NSThread isMainThread]) {
        fn();
        return;
    }
    auto task = std::make_shared<std::function<void()>>(std::move(fn));
    dispatch_async(dispatch_get_main_queue(), ^{
        (*task)();
    });
}

void* editor_window_create(const char* title, int width, int height, bool resizable,
                           std::function<void()> on_user_close) {
    @autoreleasepool {
        NSUInteger style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
                         | NSWindowStyleMaskMiniaturizable;
        if (resizable) style |= NSWindowStyleMaskResizable;
        NSRect frame = NSMakeRect(0, 0, width > 0 ? width : 800, height > 0 ? height : 600);
        NSWindow* window = [[NSWindow alloc] initWithContentRect:frame
                                                       styleMask:style
                                                         backing:NSBackingStoreBuffered
                                                           defer:NO];
        window.releasedWhenClosed = NO; // the holder owns the lifetime
        if (title && *title) {
            NSString* t = [NSString stringWithUTF8String:title];
            if (t) window.title = t;
        }
        NSView* content = [[NSView alloc] initWithFrame:frame];
        window.contentView = content;
        [window center];

        ZvstEditorHolder* holder = [[ZvstEditorHolder alloc] init];
        holder.window = window;
        [holder setOnUserClose:std::move(on_user_close)];
        window.delegate = holder;
        return (__bridge_retained void*)holder;
    }
}

void* editor_window_content_view(void* handle) {
    ZvstEditorHolder* holder = (__bridge ZvstEditorHolder*)handle;
    return (__bridge void*)holder.window.contentView;
}

void editor_window_set_content_size(void* handle, int width, int height) {
    ZvstEditorHolder* holder = (__bridge ZvstEditorHolder*)handle;
    if (width < 1 || height < 1) return;
    [holder.window setContentSize:NSMakeSize(width, height)];
}

void editor_window_show(void* handle) {
    ZvstEditorHolder* holder = (__bridge ZvstEditorHolder*)handle;
    [holder.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

void editor_window_destroy(void* handle) {
    if (!handle) return;
    ZvstEditorHolder* holder = (__bridge_transfer ZvstEditorHolder*)handle;
    [holder clearOnUserClose];
    NSWindow* window = holder.window;
    window.delegate = nil;
    if (!holder.closing) {
        [window orderOut:nil];
        [window close];
    }
    // Release after the current call stack unwinds: this may run from inside
    // the window's own close notification.
    dispatch_async(dispatch_get_main_queue(), ^{
        (void)holder;
    });
}

void* timer_start(int interval_ms, std::function<void()> tick) {
    dispatch_source_t t = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0,
                                                 dispatch_get_main_queue());
    if (!t) return nullptr;
    auto fn = std::make_shared<std::function<void()>>(std::move(tick));
    const uint64_t ns = static_cast<uint64_t>(interval_ms) * NSEC_PER_MSEC;
    dispatch_source_set_timer(t, dispatch_time(DISPATCH_TIME_NOW, static_cast<int64_t>(ns)),
                              ns, 5 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(t, ^{
        (*fn)();
    });
    dispatch_resume(t);
    return (__bridge_retained void*)t;
}

void timer_stop(void* handle) {
    if (!handle) return;
    dispatch_source_t t = (__bridge_transfer dispatch_source_t)handle;
    dispatch_source_cancel(t);
}

} // namespace zvst_mac
