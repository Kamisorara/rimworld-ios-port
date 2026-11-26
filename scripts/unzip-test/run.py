"""Host check of host/app/unzip.c (the zip reader behind the in-app game install and the mod / save import).

Windows has no Apple SDK, so the reader is compiled as it is with three stand-ins: zlib.h (the few
functions it calls, zlib's stable ABI, linked against a real zlib such as Git for Windows' zlib1.dll),
iconv (over the Windows code pages 936 and 54936, for the GBK names) and CoreFoundation (two names of
the install step, which is not run here). The archives are written here byte by byte, to have the cases
zipfile will not make: GBK names without the UTF-8 flag, Info-ZIP's Unicode Path field with a right and
a wrong CRC, UTF-8 without the flag, ".." names, names too long.

    python scripts/unzip-test/run.py [--gcc <gcc.exe>] [--zlib <zlib1.dll>]

Exit code 0 when every case passes (the work folder is kept when one does not). The reader itself is
not changed by the stand-ins: unzip.c is included into the test, and the name decoder is asked directly
as well as through port17_unzip.
"""
import argparse
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

ABOUT = b'<?xml version="1.0"?><ModMetaData><name>test</name></ModMetaData>'
UTF8_ABOUT = "中文Mod/About/About.xml".encode("utf-8")   # the mod folder's name in UTF-8
GBK_ABOUT = "中文Mod/About/About.xml".encode("gbk")       # the same in GBK, as a Chinese Windows writes it


def entry(name, data, flags=0, extra=b"", method=8):
    """One entry: local header + data, and the central record in two halves (build puts the local
    header's offset between them). Returns (local, head, tail)."""
    if method == 8:
        packed = zlib.compressobj(9, zlib.DEFLATED, -15)
        blob = packed.compress(data) + packed.flush()
    else:
        blob = data
    crc = zlib.crc32(data) & 0xFFFFFFFF
    local = struct.pack("<IHHHHHIIIHH", 0x04034B50, 20, flags, method, 0, 0x5000, crc, len(blob), len(data),
                        len(name), len(extra)) + name + extra + blob
    central = struct.pack("<IHHHHHHIIIHHHHHI", 0x02014B50, 20, 20, flags, method, 0, 0x5000, crc, len(blob),
                          len(data), len(name), len(extra), 0, 0, 0, 0)
    return local, central, name + extra


def unicode_field(raw_name, utf8_name, crc=None):
    """Info-ZIP Unicode Path extra field (0x7075): version 1, CRC-32 of the raw name, the UTF-8 name."""
    if crc is None:
        crc = zlib.crc32(raw_name) & 0xFFFFFFFF
    body = bytes([1]) + struct.pack("<I", crc) + utf8_name
    return struct.pack("<HH", 0x7075, len(body)) + body


def build(path, entries):
    body = b""
    cd = b""
    offsets = []
    for local, head, tail in entries:
        offsets.append(len(body))
        cd += head + struct.pack("<I", len(body)) + tail
        body += local
    eocd = struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, len(entries), len(entries), len(cd), len(body), 0)
    with open(path, "wb") as out:
        out.write(body + cd + eocd)
    # Python's own reader as the referee of what was written (it reads names by its own rules).
    assert [i.header_offset for i in zipfile.ZipFile(path).infolist()] == offsets


def make_cases(folder):
    # 1. What the port's own writer and every UTF-8 zipper make: flag 11 set, deflate and stored.
    build(os.path.join(folder, "utf8.zip"), [
        entry(UTF8_ABOUT, ABOUT, flags=0x0800),
        entry("中文Mod/Textures/a.png".encode("utf-8"), b"\x89PNG" + bytes(5000), flags=0x0800, method=0),
        entry("中文Mod/Defs/".encode("utf-8"), b"", flags=0x0800),
        entry(b"readme.txt", b"hello", flags=0x0800),
    ])
    # 2. Windows Explorer on a Chinese Windows: GBK names, no flag, no extra field.
    build(os.path.join(folder, "gbk.zip"), [
        entry(GBK_ABOUT, ABOUT),
        entry("中文Mod/说明.txt".encode("gbk"), "说明".encode("utf-8")),
        entry(b"plain.txt", b"plain"),
    ])
    # 3. WinRAR on a Chinese Windows: GBK names plus the Unicode Path field with the right CRC.
    build(os.path.join(folder, "gbk-field.zip"), [
        entry(GBK_ABOUT, ABOUT, extra=unicode_field(GBK_ABOUT, UTF8_ABOUT)),
    ])
    # 4. The same with a CRC that does not match (the field is for another name): read as GBK then.
    build(os.path.join(folder, "gbk-badcrc.zip"), [
        entry(GBK_ABOUT, ABOUT, extra=unicode_field(GBK_ABOUT, UTF8_ABOUT, crc=0x12345678)),
    ])
    # 5. UTF-8 names without the flag (some Linux tools): kept as they are.
    build(os.path.join(folder, "utf8-noflag.zip"), [entry(UTF8_ABOUT, ABOUT)])
    # 6. Names no disk should take: refused whole (-8), nothing written outside.
    for i, bad in enumerate([b"../escape.txt", b"/absolute.txt", b"a/../../escape.txt", b"C:/drive.txt",
                             b"a/b/../../../escape.txt"]):
        build(os.path.join(folder, "bad%d.zip" % i), [entry(b"ok/first.txt", b"first"), entry(bad, b"x")])
    # 7. A name too long for the buffers: refused (-8) instead of being cut into another file's path.
    build(os.path.join(folder, "longname.zip"), [entry(b"a" * 1200 + b".txt", b"x"), entry(b"ok.txt", b"ok")])
    # 8. Not a zip at all, and an empty one.
    with open(os.path.join(folder, "notzip.zip"), "wb") as out:
        out.write(b"PK\x03\x04" + bytes(100))
    with open(os.path.join(folder, "empty.zip"), "wb") as out:
        out.write(b"")


ZLIB_H = r"""// Stand-in for zlib.h on a host without the Apple SDK (scripts/unzip-test/run.py): the functions
// unzip.c calls, with zlib's stable ABI (linked against a real zlib).
#pragma once
typedef unsigned int uInt;
typedef unsigned long uLong;
typedef struct z_stream_s {
    const unsigned char *next_in;
    uInt avail_in;
    uLong total_in;
    unsigned char *next_out;
    uInt avail_out;
    uLong total_out;
    char *msg;
    void *state;
    void *(*zalloc)(void *, uInt, uInt);
    void (*zfree)(void *, void *);
    void *opaque;
    int data_type;
    uLong adler;
    uLong reserved;
} z_stream;
typedef z_stream *z_streamp;
#define Z_NO_FLUSH 0
#define Z_OK 0
#define Z_STREAM_END 1
#define Z_NULL 0
#define ZLIB_VERSION "1.3.1"
#define inflateInit2(strm, windowBits) inflateInit2_((strm), (windowBits), ZLIB_VERSION, (int)sizeof(z_stream))
int inflateInit2_(z_streamp strm, int windowBits, const char *version, int stream_size);
int inflate(z_streamp strm, int flush);
int inflateEnd(z_streamp strm);
uLong crc32(uLong crc, const unsigned char *buf, uInt len);
"""

ICONV_H = r"""// Stand-in for iconv.h on a host without the Apple SDK (scripts/unzip-test/run.py): iconv over the
// Windows code pages, which is what Darwin's libiconv is asked for (GB18030 = 54936, GBK / CP936 = 936).
#pragma once
#include <errno.h>
#include <stddef.h>
#include <windows.h>
typedef void *iconv_t;
static iconv_t iconv_open(const char *to, const char *from)
{
    if (!to || !from || strcmp(to, "UTF-8") != 0) return (iconv_t)-1;
    if (!strcmp(from, "GB18030")) return (iconv_t)(intptr_t)54936;
    if (!strcmp(from, "GBK") || !strcmp(from, "CP936") || !strcmp(from, "EUC-CN")) return (iconv_t)(intptr_t)936;
    return (iconv_t)-1;
}
static int iconv_close(iconv_t cd) { (void)cd; return 0; }
static size_t iconv(iconv_t cd, char **inbuf, size_t *inbytesleft, char **outbuf, size_t *outbytesleft)
{
    if (!inbuf || !*inbuf) return 0;
    UINT page = (UINT)(intptr_t)cd;
    int wide = MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, *inbuf, (int)*inbytesleft, NULL, 0);
    if (wide <= 0) { errno = EILSEQ; return (size_t)-1; }
    wchar_t *tmp = (wchar_t *)malloc((size_t)wide * sizeof(wchar_t));
    if (!tmp) { errno = ENOMEM; return (size_t)-1; }
    MultiByteToWideChar(page, MB_ERR_INVALID_CHARS, *inbuf, (int)*inbytesleft, tmp, wide);
    int need = WideCharToMultiByte(CP_UTF8, 0, tmp, wide, NULL, 0, NULL, NULL);
    if (need <= 0 || (size_t)need > *outbytesleft) { free(tmp); errno = E2BIG; return (size_t)-1; }
    WideCharToMultiByte(CP_UTF8, 0, tmp, wide, *outbuf, need, NULL, NULL);
    free(tmp);
    *inbuf += *inbytesleft;
    *outbuf += need;
    *outbytesleft -= (size_t)need;
    *inbytesleft = 0;
    return 0;
}
"""

COREFOUNDATION_H = r"""// Stand-in for CoreFoundation/CoreFoundation.h on a host without the Apple SDK (scripts/unzip-test/
// run.py): the two names of the install step, which the test does not run. Darwin's headers bring
// stdbool along; MinGW's do not.
#pragma once
#include <stdbool.h>
typedef unsigned char Boolean;
typedef double CFTimeInterval;
typedef int CFRunLoopRunResult;
typedef const void *CFStringRef;
extern const void *kCFRunLoopDefaultMode;
CFRunLoopRunResult CFRunLoopRunInMode(CFStringRef mode, CFTimeInterval seconds, Boolean returnAfterSourceHandled);
static int pthread_main_np(void) { return 1; }
"""

SYS_STAT_H = r"""// Stand-in for sys/stat.h on a host without the Apple SDK (scripts/unzip-test/run.py): the real one,
// with POSIX's two-argument mkdir over MinGW's one-argument one.
#pragma once
#include <direct.h>
#include_next <sys/stat.h>
#define mkdir(path, mode) _mkdir(path)
"""

C_TEST = r"""
// The reader as it is (unzip.c is included), asked through port17_unzip and the name decoder directly.
#include "unzip.c"
#include <direct.h>

#define ABOUT "<?xml version=\"1.0\"?><ModMetaData><name>test</name></ModMetaData>"
#define UTF8_ABOUT "\xe4\xb8\xad\xe6\x96\x87Mod/About/About.xml"
#define UTF8_PNG "\xe4\xb8\xad\xe6\x96\x87Mod/Textures/a.png"

static char g_folder[1024];
static int failed = 0;

void host_log(const char *msg) { (void)msg; }
// The install step is linked in but never called.
const void *kCFRunLoopDefaultMode = "default";
CFRunLoopRunResult CFRunLoopRunInMode(CFStringRef mode, CFTimeInterval seconds, Boolean handled) { return 0; }

static void check(int ok, const char *what)
{
    printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failed = 1;
}

static int file_is(const char *path, const char *content)
{
    FILE *f = fopen(path, "rb");
    if (!f) return 0;
    char buf[8192];
    size_t n = fread(buf, 1, sizeof buf - 1, f);
    fclose(f);
    buf[n] = '\0';
    return n == strlen(content) && memcmp(buf, content, n) == 0;
}

static int file_starts(const char *path, const char *prefix)
{
    FILE *f = fopen(path, "rb");
    if (!f)
        return 0;
    size_t n = strlen(prefix), need = n + 1;
    char buf[64];
    if (need > sizeof buf)
        return 0;
    size_t got = fread(buf, 1, need, f);
    fclose(f);
    return got == need && memcmp(buf, prefix, n) == 0;
}

static int exists(const char *path)
{
    FILE *f = fopen(path, "rb");
    if (f) fclose(f);
    return f != NULL;
}

static int exists_in(const char *folder, const char *name)
{
    char path[2048];
    snprintf(path, sizeof path, "%s/%s", folder, name);
    return exists(path);
}

static void wipe(const char *path)
{
    _rmdir(path); // empty only: a folder that is still there holds what a refused archive should not have left
}

static int unzip_into(const char *zip, const char *name, int code)
{
    char dest[1024], line[2048];
    snprintf(dest, sizeof dest, "%s/%s", g_folder, name);
    wipe(dest);
    int rc = port17_unzip(zip, dest);
    snprintf(line, sizeof line, "%s: code %d (wanted %d)", name, rc, code);
    check(rc == code, line);
    return rc;
}

// decode_name through the central record of a one-entry archive: the name the disk would be asked for.
static int decode_one(const char *zip, char *out, size_t outSize)
{
    FILE *f = fopen(zip, "rb");
    if (!f) return -99;
    static uint8_t all[1 << 20];
    size_t n = fread(all, 1, sizeof all, f);
    fclose(f);
    long eocd = -1;
    for (long i = (long)n - 22; i >= 0; i--)
        if (rd32(all + i) == 0x06054b50) { eocd = i; break; }
    if (eocd < 0) return -98;
    uint32_t cdOff = rd32(all + eocd + 16);
    uint16_t fnLen = rd16(all + cdOff + 28), exLen = rd16(all + cdOff + 30), flags = rd16(all + cdOff + 8);
    const uint8_t *raw = all + cdOff + 46;
    return decode_name(raw, fnLen, flags, raw + fnLen, exLen, out, outSize);
}

int main(int argc, char **argv)
{
    char zip[1024], path[2048], name[3072];
    (void)argc; (void)argv;
    // Relative paths, from the work folder: MinGW's mkdir cannot make "C:" on the way down a full path.
    _chdir(argc > 1 ? argv[1] : ".");
    strcpy(g_folder, ".");

    // utf8.zip: flag 11 set, deflate and stored, Chinese names, a directory entry.
    snprintf(zip, sizeof zip, "%s/utf8.zip", g_folder);
    if (unzip_into(zip, "utf8", 0) == 0)
    {
        snprintf(path, sizeof path, "%s/utf8/%s", g_folder, UTF8_ABOUT);
        check(file_is(path, ABOUT), "utf8.zip: About.xml inflated");
        snprintf(path, sizeof path, "%s/utf8/%s", g_folder, UTF8_PNG);
        check(file_starts(path, "\x89PNG"), "utf8.zip: stored png");
        snprintf(path, sizeof path, "%s/utf8/readme.txt", g_folder);
        check(file_is(path, "hello"), "utf8.zip: readme.txt");
    }

    // gbk.zip: the names come out as UTF-8 of the same characters.
    snprintf(zip, sizeof zip, "%s/gbk.zip", g_folder);
    check(decode_one(zip, name, sizeof name) == 0 && !strcmp(name, UTF8_ABOUT), "gbk.zip: GBK name read as UTF-8");
    if (unzip_into(zip, "gbk", 0) == 0)
    {
        snprintf(path, sizeof path, "%s/gbk/%s", g_folder, UTF8_ABOUT);
        check(file_is(path, ABOUT), "gbk.zip: About.xml under the decoded name");
        snprintf(path, sizeof path, "%s/gbk/plain.txt", g_folder);
        check(file_is(path, "plain"), "gbk.zip: plain.txt");
    }

    // gbk-field.zip: the Unicode Path field wins (its CRC says so).
    snprintf(zip, sizeof zip, "%s/gbk-field.zip", g_folder);
    check(decode_one(zip, name, sizeof name) == 0 && !strcmp(name, UTF8_ABOUT), "gbk-field.zip: name from the field");
    unzip_into(zip, "gbkfield", 0);

    // gbk-badcrc.zip: the field is for another name: read as GBK.
    snprintf(zip, sizeof zip, "%s/gbk-badcrc.zip", g_folder);
    check(decode_one(zip, name, sizeof name) == 0 && !strcmp(name, UTF8_ABOUT), "gbk-badcrc.zip: field ignored, GBK");

    // utf8-noflag.zip: kept.
    snprintf(zip, sizeof zip, "%s/utf8-noflag.zip", g_folder);
    check(decode_one(zip, name, sizeof name) == 0 && !strcmp(name, UTF8_ABOUT), "utf8-noflag.zip: kept as it is");
    unzip_into(zip, "noflag", 0);

    // bad*.zip: refused whole, nothing outside.
    snprintf(path, sizeof path, "%s/escape.txt", g_folder);
    for (int i = 0; i < 5; i++)
    {
        snprintf(zip, sizeof zip, "%s/bad%d.zip", g_folder, i);
        snprintf(path, sizeof path, "%s/bad%d", g_folder, i);
        wipe(path);
        check(port17_unzip(zip, path) == -8, "traversal name refused (-8)");
    }
    snprintf(path, sizeof path, "%s/escape.txt", g_folder);
    check(!exists(path), "nothing written outside");
    snprintf(path, sizeof path, "%s/absolute.txt", g_folder);
    check(!exists(path), "no absolute path written");

    // longname.zip: the 1200-byte name is over what any disk takes for one name: the entry fails, and
    // fails as a whole (nothing is cut into a shorter file's path); ok.txt, after it, is not reached.
    snprintf(zip, sizeof zip, "%s/longname.zip", g_folder);
    snprintf(path, sizeof path, "%s/longname", g_folder);
    check(port17_unzip(zip, path) < 0, "a name the disk cannot take fails the archive");
    snprintf(path, sizeof path, "%s/longname", g_folder);
    check(!exists_in(path, "ok.txt"), "the entry after it is not written");
    wipe(path);

    // notzip.zip / empty.zip.
    snprintf(zip, sizeof zip, "%s/notzip.zip", g_folder);
    check(port17_unzip(zip, path) == -3, "no end record: -3");
    snprintf(zip, sizeof zip, "%s/empty.zip", g_folder);
    check(port17_unzip(zip, path) == -2, "empty file: -2");

    printf(failed ? "FAILED\n" : "all passed\n");
    return failed;
}
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--gcc", default=os.environ.get("GCC", "gcc"))
    parser.add_argument("--zlib", default=r"C:\Program Files\Git\mingw64\bin\zlib1.dll")
    args = parser.parse_args()
    work = tempfile.mkdtemp(prefix="port17-unzip-")
    shim = os.path.join(work, "shim")
    os.makedirs(os.path.join(shim, "CoreFoundation"))
    os.makedirs(os.path.join(shim, "sys"))
    for rel, text in (("zlib.h", ZLIB_H), ("iconv.h", ICONV_H), (os.path.join("sys", "stat.h"), SYS_STAT_H),
                      (os.path.join("CoreFoundation", "CoreFoundation.h"), COREFOUNDATION_H)):
        with open(os.path.join(shim, rel), "w", encoding="utf-8") as out:
            out.write(text)
    with open(os.path.join(work, "test_unzip.c"), "w", encoding="utf-8") as out:
        out.write(C_TEST)
    make_cases(work)
    exe = os.path.join(work, "test_unzip.exe")
    command = [args.gcc, "-std=c11", "-Wall", "-Wextra", "-Wno-unused-parameter", "-Wno-unused-function", "-O1",
               "-I", shim, "-I", os.path.join(ROOT, "host", "app"), os.path.join(work, "test_unzip.c"), "-o", exe, args.zlib]
    built = subprocess.run(command, capture_output=True, text=True)
    sys.stdout.write(built.stdout)
    sys.stderr.write(built.stderr[:6000])
    if built.returncode != 0:
        print("compile failed")
        return 1
    run = subprocess.run([exe, work], capture_output=True, text=True)
    sys.stdout.write(run.stdout)
    sys.stderr.write(run.stderr[-3000:])
    if run.returncode == 0 and "all passed" in run.stdout:
        shutil.rmtree(work, ignore_errors=True)
        return 0
    print("exit", run.returncode, "work", work)
    return 1


if __name__ == "__main__":
    sys.exit(main())
