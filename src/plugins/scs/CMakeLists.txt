cmake_minimum_required(VERSION 3.20)
project(button_box_scs LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
# Static CRT: the DLL must not need a VC++ redistributable inside the game.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(button_box_scs SHARED src/plugin.cpp)
target_include_directories(button_box_scs SYSTEM PRIVATE third_party/scs_sdk_1_16/include)
target_compile_options(button_box_scs PRIVATE /W4 /WX /permissive- /utf-8 /external:W0)

# Formatting: our own sources only, never third_party/.
find_program(CLANG_FORMAT_EXECUTABLE clang-format
  HINTS
    "$ENV{VCINSTALLDIR}/Tools/Llvm/x64/bin"
    "$ENV{VSINSTALLDIR}/VC/Tools/Llvm/x64/bin"
    "C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/VC/Tools/Llvm/x64/bin"
    "C:/Program Files/Microsoft Visual Studio/18/Enterprise/VC/Tools/Llvm/x64/bin")

file(GLOB_RECURSE BBS_SCS_FORMAT_SOURCES CONFIGURE_DEPENDS
  "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp" "${CMAKE_CURRENT_SOURCE_DIR}/src/*.h")

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
