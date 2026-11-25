cmake_minimum_required(VERSION 3.20)
project(button_box_scs LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# Static CRT: the DLL must not need a VC++ redistributable inside the game.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(BBS_SCS_WARNINGS /W4 /WX /permissive- /utf-8 /external:W0)
set(BBS_SCS_SDK_INCLUDE "${CMAKE_CURRENT_SOURCE_DIR}/third_party/scs_sdk_1_16/include")

# Layout (one folder per target; new files are picked up by the globs):
#   src/core/      pure, testable logic (protocol, scheduling, listening, telemetry); no Windows SDK game types
#   src/plugin/    the DLL: SDK glue, pipe server, exports (.def)
#   src/*.inc      compiled input and channel lists (kept in step with /catalog by a host test)
#   tests/unit/    unit tests of the core, run by CTest with the shared protocol vectors
#   tests/harness/ loads the built DLL with a fake SDK and talks to its pipe

file(GLOB BBS_SCS_CORE_SOURCES CONFIGURE_DEPENDS src/core/*.cpp src/core/*.h)
add_library(bbs_scs_core STATIC ${BBS_SCS_CORE_SOURCES})
target_include_directories(bbs_scs_core PUBLIC src/core src)
target_compile_options(bbs_scs_core PRIVATE ${BBS_SCS_WARNINGS})

file(GLOB BBS_SCS_PLUGIN_SOURCES CONFIGURE_DEPENDS src/plugin/*.cpp src/plugin/*.h src/plugin/*.def)
add_library(button_box_scs SHARED ${BBS_SCS_PLUGIN_SOURCES})
target_link_libraries(button_box_scs PRIVATE bbs_scs_core)
target_include_directories(button_box_scs SYSTEM PRIVATE "${BBS_SCS_SDK_INCLUDE}")
target_compile_options(button_box_scs PRIVATE ${BBS_SCS_WARNINGS})
# Pinned output: the host App build copies build/Release/button_box_scs.dll (decisions §12). Do not move it.
set_target_properties(button_box_scs PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/$<CONFIG>")

enable_testing()

file(GLOB BBS_SCS_UNIT_SOURCES CONFIGURE_DEPENDS tests/unit/*.cpp tests/unit/*.h)
add_executable(bbs_scs_tests ${BBS_SCS_UNIT_SOURCES})
target_link_libraries(bbs_scs_tests PRIVATE bbs_scs_core)
target_compile_options(bbs_scs_tests PRIVATE ${BBS_SCS_WARNINGS})
add_test(NAME unit COMMAND bbs_scs_tests "${CMAKE_CURRENT_SOURCE_DIR}/protocol/vectors.txt")

file(GLOB BBS_SCS_HARNESS_SOURCES CONFIGURE_DEPENDS tests/harness/*.cpp tests/harness/*.h)
add_executable(bbs_scs_harness ${BBS_SCS_HARNESS_SOURCES})
target_link_libraries(bbs_scs_harness PRIVATE bbs_scs_core)
target_include_directories(bbs_scs_harness SYSTEM PRIVATE "${BBS_SCS_SDK_INCLUDE}")
target_compile_options(bbs_scs_harness PRIVATE ${BBS_SCS_WARNINGS})
add_dependencies(bbs_scs_harness button_box_scs)
add_test(NAME harness COMMAND bbs_scs_harness "$<TARGET_FILE:button_box_scs>")

# Formatting: our own sources only, never third_party/.
find_program(CLANG_FORMAT_EXECUTABLE clang-format
  HINTS
    "$ENV{VCINSTALLDIR}/Tools/Llvm/x64/bin"
    "$ENV{VSINSTALLDIR}/VC/Tools/Llvm/x64/bin"
    "C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/VC/Tools/Llvm/x64/bin"
    "C:/Program Files/Microsoft Visual Studio/18/Enterprise/VC/Tools/Llvm/x64/bin")

file(GLOB_RECURSE BBS_SCS_FORMAT_SOURCES CONFIGURE_DEPENDS
  "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp" "${CMAKE_CURRENT_SOURCE_DIR}/src/*.h"
  "${CMAKE_CURRENT_SOURCE_DIR}/tests/*.cpp" "${CMAKE_CURRENT_SOURCE_DIR}/tests/*.h")

if(CLANG_FORMAT_EXECUTABLE)
  execute_process(COMMAND "${CLANG_FORMAT_EXECUTABLE}" --version
    OUTPUT_VARIABLE BBS_CLANG_FORMAT_VERSION OUTPUT_STRIP_TRAILING_WHITESPACE)
  message(STATUS "clang-format: ${CLANG_FORMAT_EXECUTABLE} (${BBS_CLANG_FORMAT_VERSION})")
  add_custom_target(format-check
    COMMAND "${CLANG_FORMAT_EXECUTABLE}" --version
    COMMAND "${CLANG_FORMAT_EXECUTABLE}" --dry-run --Werror ${BBS_SCS_FORMAT_SOURCES}
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}" VERBATIM)
  add_custom_target(format
    COMMAND "${CLANG_FORMAT_EXECUTABLE}" -i ${BBS_SCS_FORMAT_SOURCES}
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}" VERBATIM)
else()
  message(WARNING "clang-format not found; format and format-check targets are unavailable. "
                  "Pass -DCLANG_FORMAT_EXECUTABLE=<path>.")
endif()
