/*
    This code was written by @trumank as part of the repak crate: https://github.com/trumank/repak

    MIT License

    Copyright 2024 Truman Kilen, spuds

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:
    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.
    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    SOFTWARE.
*/

namespace UAssetAPI;

using System;
using System.Runtime.InteropServices;

internal static class RePakInterop
{
    public const string NativeLib = "repak_bind";

    // ---------------------------------------------------------------------
    // 本地修改（Prism）：所有传给 native 的 C 字符串显式声明为 UTF-8。
    //
    // 原因：repak_bind 是 Rust 库，它用 std::str::from_utf8().unwrap() 解析
    // 这些 char*。而 P/Invoke 的 string 默认按【系统 ANSI 代码页】封送
    // （中文 Windows 是 GBK/936）。一旦 Pak 内路径含非 ASCII 字符：
    //   - 能映射成单字节的（如 Ä U+00C4 → 0xC4）恰好是合法 UTF-8 续字节，
    //     于是【静默损坏】：写进去的名字读回变成 '?'。
    //   - 不能映射的（如 É U+00C9 → 多字节）产生非法 UTF-8，
    //     Rust 侧 unwrap() panic，而该库 catch_unwind 拦不住，
    //     直接 abort 整个 .NET 进程（不可捕获）。
    //
    // 实测：路径 kards/.../48e_RÉGIMENT_D_INFANTERIE.uasset 会让打包进程崩溃。
    // 指定 LPUTF8Str 后两端都是 UTF-8，路径原样保留。
    // ---------------------------------------------------------------------

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_setup_allocator();
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_teardown_allocator();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_builder_new();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pak_builder_drop(IntPtr builder);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pak_reader_drop(IntPtr reader);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pak_writer_drop(IntPtr writer);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pak_buffer_drop(IntPtr buffer, ulong length);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pak_cstring_drop(IntPtr cstrign);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_builder_key(IntPtr builder, byte[] key);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_builder_compression(IntPtr builder, byte[] compressions, int length);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_builder_reader(IntPtr builder, StreamCallbacks ctx);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_builder_writer(
        IntPtr builder,
        StreamCallbacks ctx,
        PakVersion version,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mount_point,
        ulong path_hash_seed = 0);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern PakVersion pak_reader_version(IntPtr reader);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_reader_mount_point(IntPtr reader);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pak_reader_get(
        IntPtr reader,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        StreamCallbacks ctx,
        out IntPtr buffer,
        out ulong length);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_reader_files(IntPtr reader, out ulong length);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pak_drop_files(IntPtr buffer, ulong length);

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pak_writer_write_file(
        IntPtr writer,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] data,
        int data_len);
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pak_writer_write_index(IntPtr writer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long ReadCallback(IntPtr context, IntPtr buffer, ulong bufferLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteCallback(IntPtr context, IntPtr buffer, int bufferLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong SeekCallback(IntPtr context, long offset, int origin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FlushCallback(IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamCallbacks
    {
        public IntPtr Context;
        public ReadCallback ReadCb;
        public WriteCallback WriteCb;
        public SeekCallback SeekCb;
        public FlushCallback FlushCb;
    }
}
