// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus test VST3 — a tiny, deterministic effect the bridge tests load in CI
// so hosting is exercised on every platform without third-party plug-ins.
// Never shipped.
//
// Two audio-effect classes in one module (so load-by-class-UID has
// something to choose between):
//   "Zeus Test Gain"    out = in * 2 * gain      (gain 0.5 = unity)
//   "Zeus Test Invert"  out = -in * 2 * gain
//
// Deliberately strict in the ways real plug-ins are:
//   * a stereo sidechain input bus, and setBusArrangements refuses a call
//     that does not name every bus (FabFilter Pro-Q/Pro-C behave this way);
//   * main bus accepts mono or stereo, in == out;
//   * a kIsBypass parameter and component state (gain + bypass), so state
//     round-trips and plug-in-level bypass can be verified.
//
// Fault injection for isolation tests, selected by the environment variable
// ZEUS_TEST_VST_FAULT at the moment the relevant call happens:
//   crash-scan    abort() in GetPluginFactory (hits describe / scan)
//   hang-scan     never return from GetPluginFactory
//   crash-process abort() in process()
//   hang-process  never return from process()
//   nan-process   write NaN into every output sample
//   latency-N     report N samples of latency (e.g. latency-256)
//   only-64       refuse 32-bit processing (64-bit samples only)

#include "public.sdk/source/vst/vstsinglecomponenteffect.h"
#include "public.sdk/source/main/pluginfactory.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"
#include "pluginterfaces/vst/vsttypes.h"
#include "base/source/fstreamer.h"

#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <string>
#include <thread>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

std::string fault() {
    const char* f = std::getenv("ZEUS_TEST_VST_FAULT");
    return f ? std::string(f) : std::string();
}

[[noreturn]] void hang_forever() {
    for (;;) std::this_thread::sleep_for(std::chrono::seconds(1));
}

enum ParamIds : ParamID { kGainId = 0, kBypassId = 1 };

constexpr uint32 kStateMagic = 0x5A545350; // 'ZTSP'

class ZeusTestEffect : public SingleComponentEffect {
public:
    explicit ZeusTestEffect(float sign) : sign_(sign) {}

    tresult PLUGIN_API initialize(FUnknown* context) SMTG_OVERRIDE {
        tresult r = SingleComponentEffect::initialize(context);
        if (r != kResultOk) return r;
        addAudioInput(STR16("In"), SpeakerArr::kStereo);
        addAudioInput(STR16("Sidechain"), SpeakerArr::kStereo, kAux, 0);
        addAudioOutput(STR16("Out"), SpeakerArr::kStereo);
        parameters.addParameter(STR16("Gain"), nullptr, 0, 0.5,
                                ParameterInfo::kCanAutomate, kGainId);
        parameters.addParameter(STR16("Bypass"), nullptr, 1, 0.0,
                                ParameterInfo::kCanAutomate | ParameterInfo::kIsBypass, kBypassId);
        return kResultOk;
    }

    tresult PLUGIN_API setBusArrangements(SpeakerArrangement* inputs, int32 numIns,
                                          SpeakerArrangement* outputs, int32 numOuts) SMTG_OVERRIDE {
        if (numIns != 2 || numOuts != 1 || !inputs || !outputs) return kResultFalse;
        const int32 ch = SpeakerArr::getChannelCount(inputs[0]);
        if (ch < 1 || ch > 2 || inputs[0] != outputs[0]) return kResultFalse;
        if (inputs[1] != SpeakerArr::kStereo) return kResultFalse;
        return SingleComponentEffect::setBusArrangements(inputs, numIns, outputs, numOuts);
    }

    tresult PLUGIN_API canProcessSampleSize(int32 size) SMTG_OVERRIDE {
        if (size == kSample32) return fault() == "only-64" ? kResultFalse : kResultTrue;
        return size == kSample64 ? kResultTrue : kResultFalse;
    }

    uint32 PLUGIN_API getLatencySamples() SMTG_OVERRIDE {
        const std::string f = fault();
        if (f.rfind("latency-", 0) == 0) return static_cast<uint32>(std::atoi(f.c_str() + 8));
        return 0;
    }

    tresult PLUGIN_API setProcessing(TBool) SMTG_OVERRIDE { return kResultOk; }

    tresult PLUGIN_API process(ProcessData& data) SMTG_OVERRIDE {
        if (data.inputParameterChanges) {
            const int32 n = data.inputParameterChanges->getParameterCount();
            for (int32 i = 0; i < n; i++) {
                IParamValueQueue* q = data.inputParameterChanges->getParameterData(i);
                if (!q || q->getPointCount() < 1) continue;
                int32 offset = 0;
                ParamValue v = 0;
                if (q->getPoint(q->getPointCount() - 1, offset, v) != kResultTrue) continue;
                if (q->getParameterId() == kGainId) gain_ = v;
                if (q->getParameterId() == kBypassId) bypass_ = v >= 0.5;
                setParamNormalized(q->getParameterId(), v);
            }
        }
        if (data.numSamples < 1 || data.numInputs < 1 || data.numOutputs < 1) return kResultOk;

        const std::string f = fault();
        if (f == "crash-process") std::abort();
        if (f == "hang-process") hang_forever();
        const bool nan = f == "nan-process";

        const AudioBusBuffers& in = data.inputs[0];
        AudioBusBuffers& out = data.outputs[0];
        const double k = bypass_ ? 1.0 : static_cast<double>(sign_) * 2.0 * gain_;
        const int32 ch = std::min(in.numChannels, out.numChannels);
        for (int32 c = 0; c < ch; c++) {
            for (int32 s = 0; s < data.numSamples; s++) {
                double y = data.symbolicSampleSize == kSample64
                    ? in.channelBuffers64[c][s] * k
                    : static_cast<double>(in.channelBuffers32[c][s]) * k;
                if (nan) y = std::numeric_limits<double>::quiet_NaN();
                if (data.symbolicSampleSize == kSample64) out.channelBuffers64[c][s] = y;
                else out.channelBuffers32[c][s] = static_cast<float>(y);
            }
        }
        return kResultOk;
    }

    tresult PLUGIN_API setState(IBStream* state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        uint32 magic = 0;
        double g = 0.5;
        int32 b = 0;
        if (!s.readInt32u(magic) || magic != kStateMagic) return kResultFalse;
        if (!s.readDouble(g) || !s.readInt32(b)) return kResultFalse;
        gain_ = g;
        bypass_ = b != 0;
        setParamNormalized(kGainId, gain_);
        setParamNormalized(kBypassId, bypass_ ? 1.0 : 0.0);
        return kResultOk;
    }

    tresult PLUGIN_API getState(IBStream* state) SMTG_OVERRIDE {
        IBStreamer s(state, kLittleEndian);
        if (!s.writeInt32u(kStateMagic)) return kResultFalse;
        if (!s.writeDouble(gain_) || !s.writeInt32(bypass_ ? 1 : 0)) return kResultFalse;
        return kResultOk;
    }

    static FUnknown* createGain(void*) { return static_cast<IAudioProcessor*>(new ZeusTestEffect(1.0f)); }
    static FUnknown* createInvert(void*) { return static_cast<IAudioProcessor*>(new ZeusTestEffect(-1.0f)); }

private:
    float  sign_;
    double gain_{0.5};
    bool   bypass_{false};
};

// Class IDs are fixed so tests can name them.
//   Gain:   5A455553-54455354-4741494E-00000001
//   Invert: 5A455553-54455354-494E5654-00000002
const FUID kGainUID(0x5A455553, 0x54455354, 0x4741494E, 0x00000001);
const FUID kInvertUID(0x5A455553, 0x54455354, 0x494E5654, 0x00000002);

} // namespace

// Written out (rather than BEGIN_FACTORY_DEF) so the scan-time faults can
// fire before any factory exists.
SMTG_EXPORT_SYMBOL IPluginFactory* PLUGIN_API GetPluginFactory() {
    const std::string f = fault();
    if (f == "crash-scan") std::abort();
    if (f == "hang-scan") hang_forever();

    if (!gPluginFactory) {
        static PFactoryInfo info("Zeus Test", "https://github.com/ChasingCoffee/AnanCore",
                                 "", PFactoryInfo::kUnicode);
        gPluginFactory = new CPluginFactory(info);
        {
            TUID cid;
            kGainUID.toTUID(cid);
            static PClassInfo2 ci(cid, PClassInfo::kManyInstances, kVstAudioEffectClass,
                                  "Zeus Test Gain", 0, "Fx", nullptr, "1.0.0", kVstVersionString);
            gPluginFactory->registerClass(&ci, ZeusTestEffect::createGain);
        }
        {
            TUID cid;
            kInvertUID.toTUID(cid);
            static PClassInfo2 ci(cid, PClassInfo::kManyInstances, kVstAudioEffectClass,
                                  "Zeus Test Invert", 0, "Fx", nullptr, "1.0.0", kVstVersionString);
            gPluginFactory->registerClass(&ci, ZeusTestEffect::createInvert);
        }
    } else {
        gPluginFactory->addRef();
    }
    return gPluginFactory;
}
