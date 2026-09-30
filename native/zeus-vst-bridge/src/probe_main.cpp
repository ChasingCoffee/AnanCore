// SPDX-License-Identifier: GPL-2.0-or-later
//
// zeus-plugin-probe — reads a plug-in module's class list in a throw-away,
// native process:
//
//   zeus-plugin-probe describe <path.vst3 | path.clap>
//
// The host runs this during a scan instead of touching third-party code
// itself. It is deliberately native, not a .NET process: some plug-ins'
// copy protection stalls inside a .NET runtime (it installs its own
// exception handling) yet reads in well under a second here, and a probe
// that crashes is filed under this executable rather than under the app.
//
// Output: one line, "@@zeus-plugin-probe@@" + JSON, the same shape the .NET
// probe prints (Zeus.Plugins.Host.Audio.PluginProbeReply):
//   {"ok":true,"status":0,"classes":[{"uid","name","category","vendor"},...]}
//   {"ok":false,"status":<zvst_status_t>,"error":"..."}
// Anything else on stdout (plug-ins print freely) is ignored by the host.

#include "zvst.h"
#include "zclap.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

const char* kMarker = "@@zeus-plugin-probe@@";

bool ends_with_ci(const std::string& s, const char* suffix) {
    const size_t n = std::strlen(suffix);
    if (s.size() < n) return false;
    for (size_t i = 0; i < n; ++i) {
        char a = s[s.size() - n + i], b = suffix[i];
        if (a >= 'A' && a <= 'Z') a = static_cast<char>(a - 'A' + 'a');
        if (a != b) return false;
    }
    return true;
}

int reply_fail(int status, const char* error) {
    std::printf("%s{\"ok\":false,\"status\":%d,\"error\":\"%s\"}\n", kMarker, status, error);
    std::fflush(stdout);
    return 1;
}

const char* describe_error(int status) {
    switch (status) {
        case ZVST_FILE_NOT_FOUND: return "plugin not found";
        case ZVST_NOT_A_VST3:     return "not a loadable plugin on this platform";
        case ZVST_ABI_MISMATCH:   return "plugin host version mismatch";
        default:                  return "the plugin could not be read";
    }
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 3 || std::strcmp(argv[1], "describe") != 0)
        return reply_fail(ZVST_INVALID_ARGUMENTS, "usage: zeus-plugin-probe describe <path>");

    std::string path = argv[2];
    while (!path.empty() && (path.back() == '/' || path.back() == '\\')) path.pop_back();
    const bool clap = ends_with_ci(path, ".clap");

    const int init = clap ? zclap_init(ZCLAP_ABI) : zvst_init(ZVST_ABI);
    if (init != ZVST_OK) return reply_fail(init, "plugin host could not start");

    std::vector<char> json(256 * 1024);
    int32_t len = 0;
    int status = clap ? zclap_describe(path.c_str(), json.data(), static_cast<int32_t>(json.size()), &len)
                      : zvst_describe(path.c_str(), json.data(), static_cast<int32_t>(json.size()), &len);
    if (status == ZVST_OK && len >= static_cast<int32_t>(json.size())) {
        json.resize(static_cast<size_t>(len) + 1);
        status = clap ? zclap_describe(path.c_str(), json.data(), static_cast<int32_t>(json.size()), &len)
                      : zvst_describe(path.c_str(), json.data(), static_cast<int32_t>(json.size()), &len);
    }
    if (status != ZVST_OK) return reply_fail(status, describe_error(status));

    std::printf("%s{\"ok\":true,\"status\":0,\"classes\":%s}\n", kMarker, json.data());
    std::fflush(stdout);
    // Leave without unwinding: some plug-ins' static destructors misbehave,
    // and the host has what it needs.
    std::_Exit(0);
}
