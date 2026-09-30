# SPDX-License-Identifier: GPL-2.0-or-later
#
# Test fixtures for the bridge, never shipped:
#   ZeusTestPlugin.vst3 (zeus_test_plugin.cpp) and ZeusTestPlugin.clap
#   (zeus_test_clap.c). The VST3 bundle uses the per-platform layout the
#   SDK's hosting Module expects:
#   macOS    ZeusTestPlugin.vst3/Contents/MacOS/ZeusTestPlugin
#   Linux    ZeusTestPlugin.vst3/Contents/<uname -m>-linux/ZeusTestPlugin.so
#   Windows  ZeusTestPlugin.vst3/Contents/<arch>-win/ZeusTestPlugin.vst3

if(ZEUS_USE_VST3SDK)
set(ZTP_SOURCES
    zeus_test_plugin.cpp
    # vstsinglecomponenteffect.cpp #includes vsteditcontroller.cpp itself.
    ${VST3SDK_DIR}/public.sdk/source/vst/vstsinglecomponenteffect.cpp
    ${VST3SDK_DIR}/public.sdk/source/vst/vstcomponentbase.cpp
    ${VST3SDK_DIR}/public.sdk/source/vst/vstparameters.cpp
    ${VST3SDK_DIR}/public.sdk/source/vst/vstbus.cpp
    ${VST3SDK_DIR}/public.sdk/source/vst/vstinitiids.cpp
    ${VST3SDK_DIR}/public.sdk/source/main/pluginfactory.cpp
    ${VST3SDK_DIR}/public.sdk/source/main/moduleinit.cpp
    ${VST3SDK_DIR}/public.sdk/source/common/commoniids.cpp
    ${VST3SDK_DIR}/public.sdk/source/common/pluginview.cpp
    ${VST3SDK_DIR}/pluginterfaces/base/coreiids.cpp
    ${VST3SDK_DIR}/pluginterfaces/base/conststringtable.cpp
    ${VST3SDK_DIR}/pluginterfaces/base/funknown.cpp
    ${VST3SDK_DIR}/pluginterfaces/base/ustring.cpp
    ${VST3SDK_DIR}/base/source/baseiids.cpp
    ${VST3SDK_DIR}/base/source/fobject.cpp
    ${VST3SDK_DIR}/base/source/fbuffer.cpp
    ${VST3SDK_DIR}/base/source/fstreamer.cpp
    ${VST3SDK_DIR}/base/source/fstring.cpp
    ${VST3SDK_DIR}/base/source/updatehandler.cpp
    ${VST3SDK_DIR}/base/source/fdebug.cpp
    ${VST3SDK_DIR}/base/thread/source/flock.cpp
)

if(APPLE)
    list(APPEND ZTP_SOURCES ${VST3SDK_DIR}/public.sdk/source/main/macmain.cpp)
elseif(WIN32)
    list(APPEND ZTP_SOURCES ${VST3SDK_DIR}/public.sdk/source/main/dllmain.cpp)
else()
    list(APPEND ZTP_SOURCES ${VST3SDK_DIR}/public.sdk/source/main/linuxmain.cpp)
endif()

add_library(ZeusTestPlugin MODULE ${ZTP_SOURCES})
target_include_directories(ZeusTestPlugin PRIVATE ${VST3SDK_DIR})
target_compile_definitions(ZeusTestPlugin PRIVATE
    RELEASE=$<IF:$<CONFIG:Debug>,0,1>
    DEVELOPMENT=$<IF:$<CONFIG:Debug>,1,0>
)
set_target_properties(ZeusTestPlugin PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    PREFIX "")

set(ZTP_BUNDLE "${CMAKE_CURRENT_BINARY_DIR}/ZeusTestPlugin.vst3")

if(APPLE)
    find_library(ZTP_COREFOUNDATION CoreFoundation)
    target_link_libraries(ZeusTestPlugin PRIVATE ${ZTP_COREFOUNDATION})
    # $<1:...> keeps multi-config generators from appending a config subdir.
    set_target_properties(ZeusTestPlugin PROPERTIES
        BUNDLE TRUE
        BUNDLE_EXTENSION vst3
        LIBRARY_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>"
        MACOSX_BUNDLE_GUI_IDENTIFIER org.openhpsdr.zeus.testplugin
        MACOSX_BUNDLE_BUNDLE_NAME ZeusTestPlugin)
elseif(WIN32)
    if(CMAKE_GENERATOR_PLATFORM MATCHES "^(ARM64|arm64)$" OR CMAKE_SYSTEM_PROCESSOR MATCHES "^(ARM64|arm64|aarch64)$")
        set(ZTP_ARCH_DIR arm64-win)
    else()
        set(ZTP_ARCH_DIR x86_64-win)
    endif()
    set_target_properties(ZeusTestPlugin PROPERTIES
        SUFFIX ".vst3"
        LIBRARY_OUTPUT_DIRECTORY "$<1:${ZTP_BUNDLE}/Contents/${ZTP_ARCH_DIR}>"
        RUNTIME_OUTPUT_DIRECTORY "$<1:${ZTP_BUNDLE}/Contents/${ZTP_ARCH_DIR}>")
else()
    # The SDK's Linux module loader looks in Contents/<uname -m>-linux.
    execute_process(COMMAND uname -m OUTPUT_VARIABLE ZTP_MACHINE OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(CMAKE_CROSSCOMPILING AND CMAKE_SYSTEM_PROCESSOR)
        set(ZTP_MACHINE ${CMAKE_SYSTEM_PROCESSOR})
    endif()
    target_link_libraries(ZeusTestPlugin PRIVATE dl pthread)
    set_target_properties(ZeusTestPlugin PROPERTIES
        SUFFIX ".so"
        LIBRARY_OUTPUT_DIRECTORY "$<1:${ZTP_BUNDLE}/Contents/${ZTP_MACHINE}-linux>")
endif()
endif()

# ---- ZeusTestPlugin.clap -------------------------------------------------
# macOS: a bundle (ZeusTestPlugin.clap/Contents/MacOS/ZeusTestPlugin).
# Windows / Linux: a single shared library named ZeusTestPlugin.clap.
if(ZEUS_USE_CLAP)
    enable_language(C)
    add_library(ZeusTestClap MODULE zeus_test_clap.c)
    target_include_directories(ZeusTestClap PRIVATE ${CLAP_SDK_DIR}/include)
    set_target_properties(ZeusTestClap PROPERTIES
        C_VISIBILITY_PRESET hidden
        PREFIX ""
        OUTPUT_NAME ZeusTestPlugin)
    if(APPLE)
        set_target_properties(ZeusTestClap PROPERTIES
            BUNDLE TRUE
            BUNDLE_EXTENSION clap
            LIBRARY_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>"
            MACOSX_BUNDLE_GUI_IDENTIFIER org.openhpsdr.zeus.testclap
            MACOSX_BUNDLE_BUNDLE_NAME ZeusTestPlugin)
    else()
        set_target_properties(ZeusTestClap PROPERTIES
            SUFFIX ".clap"
            LIBRARY_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>"
            RUNTIME_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>")
        if(UNIX)
            target_link_libraries(ZeusTestClap PRIVATE m)
        endif()
    endif()
endif()
