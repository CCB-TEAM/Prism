use repak::{Compression, PakBuilder as RepakBuilder, PakWriter as RepakWriter, Version};
use std::ffi::{c_char, c_void, CStr, CString};
use std::io::{Read, Seek, SeekFrom, Write};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;

type ReadCallback = extern "C" fn(*mut c_void, *mut u8, u64) -> i64;
type WriteCallback = extern "C" fn(*mut c_void, *const u8, i32) -> i32;
type SeekCallback = extern "C" fn(*mut c_void, i64, i32) -> u64;
type FlushCallback = extern "C" fn(*mut c_void) -> i32;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct StreamCallbacks {
    context: *mut c_void,
    read_cb: Option<ReadCallback>,
    write_cb: Option<WriteCallback>,
    seek_cb: Option<SeekCallback>,
    flush_cb: Option<FlushCallback>,
}

struct CallbackStream {
    callbacks: StreamCallbacks,
}

impl Read for CallbackStream {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let Some(read_cb) = self.callbacks.read_cb else {
            return Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "missing read callback"));
        };

        let read = read_cb(self.callbacks.context, buf.as_mut_ptr(), buf.len() as u64);
        if read < 0 {
            Err(std::io::Error::new(std::io::ErrorKind::Other, "read callback failed"))
        } else {
            Ok(read as usize)
        }
    }
}

impl Write for CallbackStream {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        let Some(write_cb) = self.callbacks.write_cb else {
            return Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "missing write callback"));
        };

        let written = write_cb(self.callbacks.context, buf.as_ptr(), buf.len() as i32);
        if written < 0 {
            Err(std::io::Error::new(std::io::ErrorKind::Other, "write callback failed"))
        } else {
            Ok(written as usize)
        }
    }

    fn flush(&mut self) -> std::io::Result<()> {
        let Some(flush_cb) = self.callbacks.flush_cb else {
            return Ok(());
        };

        if flush_cb(self.callbacks.context) == 0 {
            Ok(())
        } else {
            Err(std::io::Error::new(std::io::ErrorKind::Other, "flush callback failed"))
        }
    }
}

impl Seek for CallbackStream {
    fn seek(&mut self, pos: SeekFrom) -> std::io::Result<u64> {
        let Some(seek_cb) = self.callbacks.seek_cb else {
            return Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "missing seek callback"));
        };

        let (offset, origin) = match pos {
            SeekFrom::Start(offset) => (offset as i64, 0),
            SeekFrom::Current(offset) => (offset, 1),
            SeekFrom::End(offset) => (offset, 2),
        };

        let result = seek_cb(self.callbacks.context, offset, origin);
        if result == u64::MAX {
            Err(std::io::Error::new(std::io::ErrorKind::Other, "seek callback failed"))
        } else {
            Ok(result)
        }
    }
}

struct Builder {
    inner: Option<RepakBuilder>,
}

struct Writer {
    inner: Option<RepakWriter<CallbackStream>>,
}

fn version_from_u8(version: u8) -> Version {
    match version {
        0 => Version::V0,
        1 => Version::V1,
        2 => Version::V2,
        3 => Version::V3,
        4 => Version::V4,
        5 => Version::V5,
        6 => Version::V6,
        7 => Version::V7,
        8 => Version::V8A,
        9 => Version::V8B,
        10 => Version::V9,
        11 => Version::V10,
        _ => Version::V11,
    }
}

fn compression_from_u8(compression: u8) -> Compression {
    match compression {
        0 => Compression::Zlib,
        1 => Compression::Gzip,
        2 => Compression::Oodle,
        3 => Compression::Zstd,
        _ => Compression::Oodle,
    }
}

unsafe fn cstr_to_string(value: *const c_char, fallback: &str) -> String {
    if value.is_null() {
        return fallback.to_string();
    }

    CStr::from_ptr(value).to_string_lossy().into_owned()
}

#[no_mangle]
pub extern "C" fn pak_setup_allocator() -> *mut c_void {
    ptr::null_mut()
}

#[no_mangle]
pub extern "C" fn pak_teardown_allocator() -> *mut c_void {
    ptr::null_mut()
}

#[no_mangle]
pub extern "C" fn pak_builder_new() -> *mut Builder {
    Box::into_raw(Box::new(Builder {
        inner: Some(RepakBuilder::new()),
    }))
}

#[no_mangle]
pub unsafe extern "C" fn pak_builder_drop(builder: *mut Builder) {
    if !builder.is_null() {
        drop(Box::from_raw(builder));
    }
}

#[no_mangle]
pub unsafe extern "C" fn pak_builder_key(builder: *mut Builder, _key: *const u8) -> *mut Builder {
    builder
}

#[no_mangle]
pub unsafe extern "C" fn pak_builder_compression(
    builder: *mut Builder,
    compressions: *const u8,
    length: i32,
) -> *mut Builder {
    if builder.is_null() || compressions.is_null() || length <= 0 {
        return builder;
    }

    let builder_ref = &mut *builder;
    let Some(inner) = builder_ref.inner.take() else {
        return builder;
    };

    let values = slice::from_raw_parts(compressions, length as usize)
        .iter()
        .copied()
        .map(compression_from_u8)
        .collect::<Vec<_>>();
    builder_ref.inner = Some(inner.compression(values));
    builder
}

#[no_mangle]
pub unsafe extern "C" fn pak_builder_writer(
    builder: *mut Builder,
    callbacks: StreamCallbacks,
    version: u8,
    mount_point: *const c_char,
    path_hash_seed: u64,
) -> *mut Writer {
    if builder.is_null() {
        return ptr::null_mut();
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let builder_ref = &mut *builder;
        let inner = builder_ref.inner.take()?;
        let stream = CallbackStream { callbacks };
        let mount_point = cstr_to_string(mount_point, "../../../");
        let writer = inner.writer(
            stream,
            version_from_u8(version),
            mount_point,
            Some(path_hash_seed),
        );
        Some(Box::into_raw(Box::new(Writer { inner: Some(writer) })))
    }));

    result.ok().flatten().unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn pak_builder_reader(_builder: *mut Builder, _callbacks: StreamCallbacks) -> *mut c_void {
    ptr::null_mut()
}

#[no_mangle]
pub unsafe extern "C" fn pak_writer_drop(writer: *mut Writer) {
    if !writer.is_null() {
        drop(Box::from_raw(writer));
    }
}

#[no_mangle]
pub unsafe extern "C" fn pak_writer_write_file(
    writer: *mut Writer,
    path: *const c_char,
    data: *const u8,
    data_len: i32,
) -> i32 {
    if writer.is_null() || path.is_null() || data.is_null() || data_len < 0 {
        return 1;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let writer_ref = &mut *writer;
        let Some(inner) = writer_ref.inner.as_mut() else {
            return Err(repak::Error::Other("writer already finalized".to_string()));
        };

        let path = cstr_to_string(path, "");
        let data = slice::from_raw_parts(data, data_len as usize);
        inner.write_file(&path, true, data)
    }));

    match result {
        Ok(Ok(())) => 0,
        _ => 1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn pak_writer_write_index(writer: *mut Writer) -> i32 {
    if writer.is_null() {
        return 1;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let writer_ref = &mut *writer;
        let Some(inner) = writer_ref.inner.take() else {
            return Err(repak::Error::Other("writer already finalized".to_string()));
        };

        inner.write_index().map(|_| ())
    }));

    match result {
        Ok(Ok(())) => 0,
        _ => 1,
    }
}

#[no_mangle]
pub extern "C" fn pak_reader_drop(_reader: *mut c_void) {}

#[no_mangle]
pub extern "C" fn pak_buffer_drop(_buffer: *mut u8, _length: u64) {}

#[no_mangle]
pub unsafe extern "C" fn pak_cstring_drop(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

#[no_mangle]
pub extern "C" fn pak_reader_version(_reader: *mut c_void) -> u8 {
    0
}

#[no_mangle]
pub extern "C" fn pak_reader_mount_point(_reader: *mut c_void) -> *mut c_char {
    ptr::null_mut()
}

#[no_mangle]
pub extern "C" fn pak_reader_get(
    _reader: *mut c_void,
    _path: *const c_char,
    _callbacks: StreamCallbacks,
    _buffer: *mut *mut u8,
    _length: *mut u64,
) -> i32 {
    1
}

#[no_mangle]
pub extern "C" fn pak_reader_files(_reader: *mut c_void, length: *mut u64) -> *mut *mut c_char {
    unsafe {
        if !length.is_null() {
            *length = 0;
        }
    }
    ptr::null_mut()
}

#[no_mangle]
pub extern "C" fn pak_drop_files(_buffer: *mut *mut c_char, _length: u64) -> *mut c_void {
    ptr::null_mut()
}
