# SPDX-License-Identifier: GPL-2.0-or-later
#
# Openhpsdr-Zeus VST3 bridge.
#
#   cmake -B build -DCMAKE_BUILD_TYPE=Release
#   cmake --build build
#
# Output: build/libzeus-vst-bridge.{so,dylib,dll}

cmake_minimum_required(VERSION 3.20)
# Must precede project(). The libraries ship to operators' Macs, so they
# cannot inherit the build machine's OS version as their minimum.
if(APPLE AND NOT CMAKE_OSX_DEPLOYMENT_TARGET)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Minimum macOS version")
endif()

project(zeus-vst-bridge VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# Default to a stub build if vst3sdk is missing (works in CI / dev
# without the submodule). With the submodule in place, build the
# real hosting bridge.
set(VST3SDK_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/vst3sdk"
    CACHE PATH "Path to vendored vst3sdk")

# Release/staging builds set this so a missing submodule fails loudly instead
# of quietly producing the stub library.
option(ZEUS_VST_REQUIRE_SDK "Fail configuration when vst3sdk is missing" OFF)

if(EXISTS "${VST3SDK_DIR}/pluginterfaces/vst/ivstcomponent.h")
    message(STATUS "Building zeus-vst-bridge with vst3sdk at ${VST3SDK_DIR}")
    set(ZEUS_USE_VST3SDK ON)
else()
    if(ZEUS_VST_REQUIRE_SDK)
        message(FATAL_ERROR "vst3sdk not found at ${VST3SDK_DIR} — run: "
            "git submodule update --init native/zeus-vst-bridge/third_party/vst3sdk && "
            "git -C native/zeus-vst-bridge/third_party/vst3sdk submodule update --init base pluginterfaces public.sdk cmake")
    endif()
    message(STATUS "vst3sdk not vendored — building stub bridge (no VST3 loading)")
    set(ZEUS_USE_VST3SDK OFF)
endif()

# CLAP hosting (src/clap_bridge.cpp, include/zclap.h) uses the header-only,
# MIT-licensed CLAP SDK, independent of vst3sdk.
set(CLAP_SDK_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/clap"
    CACHE PATH "Path to vendored CLAP SDK")
if(EXISTS "${CLAP_SDK_DIR}/include/clap/clap.h")
    message(STATUS "Building CLAP hosting with the CLAP SDK at ${CLAP_SDK_DIR}")
    set(ZEUS_USE_CLAP ON)
else()
    if(ZEUS_VST_REQUIRE_SDK)
        message(FATAL_ERROR "CLAP SDK not found at ${CLAP_SDK_DIR} — run: "
            "git submodule update --init native/zeus-vst-bridge/third_party/clap")
    endif()
    message(STATUS "CLAP SDK not vendored — CLAP hosting stubbed")
    set(ZEUS_USE_CLAP OFF)
endif()

set(ZEUS_BRIDGE_SOURCES)
if(ZEUS_USE_VST3SDK)
    # Minimal vst3sdk source subset: hosting helpers + base + pluginterfaces.
    # We deliberately skip vstgui4, samples, validator — they're not needed
    # to host VSTs and they pull a lot of transitive deps.
    list(APPEND ZEUS_BRIDGE_SOURCES
        src/bridge.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/hostclasses.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/processdata.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/parameterchanges.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/hosting/eventlist.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/commonstringconvert.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/commoniids.cpp
        ${VST3SDK_DIR}/public.sdk/source/common/memorystream.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/vstinitiids.cpp
        ${VST3SDK_DIR}/public.sdk/source/vst/utility/stringconvert.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/coreiids.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/conststringtable.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/funknown.cpp
        ${VST3SDK_DIR}/pluginterfaces/base/ustring.cpp
        ${VST3SDK_DIR}/base/source/baseiids.cpp
        ${VST3SDK_DIR}/base/source/fobject.cpp
        ${VST3SDK_DIR}/base/source/fbuffer.cpp
        ${VST3SDK_DIR}/base/source/fstring.cpp
        ${VST3SDK_DIR}/base/source/updatehandler.cpp
        ${VST3SDK_DIR}/base/thread/source/flock.cpp
        ${VST3SDK_DIR}/base/source/fdebug.cpp
    )
    if(APPLE)
        list(APPEND ZEUS_BRIDGE_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_mac.mm)
        # vst3sdk's module_mac.mm requires ARC.
        set_source_files_properties(
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_mac.mm
            PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    elseif(WIN32)
        list(APPEND ZEUS_BRIDGE_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_win32.cpp)
    else()
        list(APPEND ZEUS_BRIDGE_SOURCES
            ${VST3SDK_DIR}/public.sdk/source/vst/hosting/module_linux.cpp)
    endif()
else()
    # Stub: exports the full zvst.h ABI but loads nothing, so a tree without
    # the SDK still builds.
    list(APPEND ZEUS_BRIDGE_SOURCES src/bridge_stub.cpp)
endif()

if(ZEUS_USE_CLAP)
    list(APPEND ZEUS_BRIDGE_SOURCES src/clap_bridge.cpp)
else()
    list(APPEND ZEUS_BRIDGE_SOURCES src/clap_stub.cpp)
endif()

if(APPLE)
    # AppKit editor hosting (NSWindow + main-thread dispatch), ARC. Used by
    # both the VST3 and the CLAP host.
    enable_language(OBJCXX)
    list(APPEND ZEUS_BRIDGE_SOURCES src/mac_ui.mm)
    set_source_files_properties(src/mac_ui.mm PROPERTIES COMPILE_FLAGS "-fobjc-arc")
endif()

add_library(zeus-vst-bridge SHARED ${ZEUS_BRIDGE_SOURCES})

target_include_directories(zeus-vst-bridge
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
if(ZEUS_USE_VST3SDK)
    target_include_directories(zeus-vst-bridge PRIVATE ${VST3SDK_DIR})
    target_compile_definitions(zeus-vst-bridge PRIVATE
        RELEASE=$<IF:$<CONFIG:Release>,1,0>
        DEVELOPMENT=$<IF:$<CONFIG:Debug>,1,0>
    )
endif()
if(ZEUS_USE_CLAP)
    target_include_directories(zeus-vst-bridge PRIVATE ${CLAP_SDK_DIR}/include)
endif()

if(APPLE)
    find_library(COREFOUNDATION_LIB CoreFoundation)
    find_library(FOUNDATION_LIB    Foundation)
    find_library(APPKIT_LIB        AppKit)
    target_link_libraries(zeus-vst-bridge PRIVATE
        ${COREFOUNDATION_LIB} ${FOUNDATION_LIB} ${APPKIT_LIB})
elseif(UNIX)
    # X11 hosts the plug-in editor windows + run loops on Linux.
    # find_package surfaces include dirs/libs portably.
    find_package(X11 REQUIRED)
    target_include_directories(zeus-vst-bridge PRIVATE ${X11_INCLUDE_DIR})
    target_link_libraries(zeus-vst-bridge PRIVATE dl pthread ${X11_LIBRARIES})
endif()

if(WIN32)
    # Editor hosting creates native windows + a COM/OLE apartment for the
    # plug-in GUI. These are in the MSVC default lib set, but link them
    # explicitly so the dependency is visible.
    target_link_libraries(zeus-vst-bridge PRIVATE user32 ole32)
endif()

# Test plug-ins (test-plugin/): tiny deterministic VST3 and CLAP effects the
# bridge tests load, so real hosting is exercised in CI on every platform.
# Never shipped. Native ABI tests: `ctest --test-dir build -C Release`.
option(ZEUS_VST_BUILD_TEST_PLUGIN "Build the test plug-ins and native tests" ON)
if(ZEUS_VST_BUILD_TEST_PLUGIN AND (ZEUS_USE_VST3SDK OR ZEUS_USE_CLAP))
    add_subdirectory(test-plugin)
    enable_testing()
    if(ZEUS_USE_VST3SDK)
        add_executable(zeus-vst-bridge-tests tests/bridge_tests.cpp)
        target_link_libraries(zeus-vst-bridge-tests PRIVATE zeus-vst-bridge)
        add_test(NAME zvst-abi
                 COMMAND zeus-vst-bridge-tests
                         "${CMAKE_CURRENT_BINARY_DIR}/test-plugin/ZeusTestPlugin.vst3")
    endif()
    if(ZEUS_USE_CLAP)
        add_executable(zeus-clap-bridge-tests tests/clap_tests.cpp)
        target_link_libraries(zeus-clap-bridge-tests PRIVATE zeus-vst-bridge)
        add_test(NAME zclap-abi
                 COMMAND zeus-clap-bridge-tests
                         "${CMAKE_CURRENT_BINARY_DIR}/test-plugin/ZeusTestPlugin.clap")
    endif()
    if(WIN32)
        # Run next to the freshly built DLL.
        foreach(t zvst-abi zclap-abi)
            if(TEST ${t})
                set_tests_properties(${t} PROPERTIES
                    WORKING_DIRECTORY "$<TARGET_FILE_DIR:zeus-vst-bridge>")
            endif()
        endforeach()
    endif()
endif()

# Strip non-ABI symbols on platforms that support it.
if(NOT WIN32)
    set_target_properties(zeus-vst-bridge PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON)
endif()

install(TARGETS zeus-vst-bridge
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
    ARCHIVE DESTINATION lib)
install(FILES include/zvst.h include/zclap.h DESTINATION include)
