cmake_minimum_required(VERSION 3.22)
project(prism_codecs LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# 源码解析顺序：native/third_party → 仓库根 → reference/（与 build-android-arm64.ps1 一致）
set(PRISM_REPO "${CMAKE_CURRENT_LIST_DIR}/../..")
function(prism_resolve_source out_var name)
    foreach(base "${PRISM_REPO}/native/third_party" "${PRISM_REPO}" "${PRISM_REPO}/reference")
        if(EXISTS "${base}/${name}")
            set(${out_var} "${base}/${name}" PARENT_SCOPE)
            return()
        endif()
    endforeach()
    message(FATAL_ERROR "缺少源码目录：${name}（放到 native/third_party 或 reference 下都行）")
endfunction()

prism_resolve_source(ASTC_ROOT "astc-encoder-main")
prism_resolve_source(BC7_ROOT "bc7enc_rdo-master")
prism_resolve_source(BINKA_ROOT "binka_decoder-main")
prism_resolve_source(BINKAENC_ROOT "binkaenc-main")
set(BINKA_COMPAT "${PRISM_REPO}/native/BinkaEnc/compat")
set(BINKA_PRISM "${PRISM_REPO}/native/BinkaEnc")

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
    "${BINKAENC_ROOT}/Src/binkace.c"
    "${BINKAENC_ROOT}/Src/popmal.c"
    "${BINKAENC_ROOT}/Src/binka_ue_encode.cpp"
    "${BINKA_PRISM}/radfft_prism.cpp"
    "${BINKA_PRISM}/prism_binkaenc.cpp"
    "${BC7_ROOT}/bc7enc.cpp"
    "${BC7_ROOT}/bc7decomp.cpp"
    "${BC7_ROOT}/bc7decomp_ref.cpp"
    "${BC7_ROOT}/ert.cpp"
    "${BC7_ROOT}/lodepng.cpp"
    "${BC7_ROOT}/rgbcx.cpp"
    "${BC7_ROOT}/rdo_bc_encoder.cpp"
    "${BC7_ROOT}/utils.cpp")

# binkace.c 里 #include "popmal.h"，而引号包含会先搜它所在目录；自备的上游树里那个文件
# 是 0 字节空壳，所以强制先包含 compat 里的真声明（native/third_party 那份没有这个坑）。
if(NOT MSVC)
    set_source_files_properties("${BINKAENC_ROOT}/Src/binkace.c"
        PROPERTIES COMPILE_OPTIONS "-include;${BINKA_COMPAT}/popmal.h")
endif()

target_include_directories(prism_codecs PRIVATE
    "${ASTC_ROOT}/Source"
    "${BC7_ROOT}"
    "${BINKA_ROOT}"
    "${BINKA_COMPAT}"
    "${BINKAENC_ROOT}/Src"
    "${BINKAENC_ROOT}/Include")

target_compile_definitions(prism_codecs PRIVATE SUPPORT_BC7E=0)

if(ANDROID)
    target_compile_options(prism_codecs PRIVATE -fno-strict-aliasing -Wno-unused-function -Wno-unused-variable)
else()
    target_compile_options(prism_codecs PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/W3>)
endif()

target_link_libraries(prism_codecs PRIVATE astcenc-neon-static)
