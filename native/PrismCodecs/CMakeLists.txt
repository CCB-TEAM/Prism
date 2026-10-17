cmake_minimum_required(VERSION 3.22)
project(prism_codecs LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(ASTC_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../astc-encoder-main")
set(BC7_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../bc7enc_rdo-master")
set(BINKA_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../binka_decoder-main")

set(ASTCENC_CLI OFF CACHE BOOL "" FORCE)
set(ASTCENC_ISA_NATIVE OFF CACHE BOOL "" FORCE)
set(ASTCENC_ISA_NEON ON CACHE BOOL "" FORCE)
set(ASTCENC_WERROR OFF CACHE BOOL "" FORCE)
set(ASTCENC_INVARIANCE ON CACHE BOOL "" FORCE)
add_subdirectory("${ASTC_ROOT}" astcenc-build)

add_library(prism_codecs SHARED
    prism_codecs.cpp
    "${BINKA_ROOT}/bcf1_decoder.cpp"
    "${BINKA_ROOT}/binka_dec.cpp"
    "${BINKA_ROOT}/binka_transform.cpp"
    "${BINKA_ROOT}/ueba_decoder.cpp"
    "${BC7_ROOT}/bc7enc.cpp"
    "${BC7_ROOT}/bc7decomp.cpp"
    "${BC7_ROOT}/bc7decomp_ref.cpp"
    "${BC7_ROOT}/ert.cpp"
    "${BC7_ROOT}/lodepng.cpp"
    "${BC7_ROOT}/rgbcx.cpp"
    "${BC7_ROOT}/rdo_bc_encoder.cpp"
    "${BC7_ROOT}/utils.cpp")

target_include_directories(prism_codecs PRIVATE
    "${ASTC_ROOT}/Source"
    "${BC7_ROOT}"
    "${BINKA_ROOT}")

target_compile_definitions(prism_codecs PRIVATE SUPPORT_BC7E=0)

if(ANDROID)
    target_compile_options(prism_codecs PRIVATE -fno-strict-aliasing -Wno-unused-function -Wno-unused-variable)
else()
    target_compile_options(prism_codecs PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/W3>)
endif()

target_link_libraries(prism_codecs PRIVATE astcenc-neon-static)
