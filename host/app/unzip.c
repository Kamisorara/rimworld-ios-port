// See unzip.h. The zip reader: EOCD at the tail gives the central directory; every entry
// there points at its local file header, whose own name/extra lengths are where the data
// really starts (the central directory's extra length can differ — a classic trap).
#include "unzip.h"
#include "host.h"

#include <CoreFoundation/CoreFoundation.h>
#include <dirent.h>
#include <iconv.h>
#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include <zlib.h>

#define ZIP_CHUNK (256 * 1024)

static uint16_t rd16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint32_t rd32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }

// ---- entry names ----
// Bit 11 of an entry's flags says its name is UTF-8. Without it, a zipper on a Chinese Windows writes the
// name in the system's code page (GBK), and Explorer or WinRAR may add Info-ZIP's Unicode Path field
// (0x7075: version, CRC-32 of the raw name, the name in UTF-8), whose CRC makes it certain. APFS takes
// UTF-8 only: raw GBK bytes fail on their way to the disk, and with them the whole archive. So a name
// that is valid UTF-8 stays as it is, and the rest is read as GB18030 (GBK and more).
static int utf8_valid(const uint8_t *p, size_t n)
{
    size_t i = 0;
    while (i < n)
    {
        uint32_t c = p[i++];
        int extra;
        uint32_t lowest;
        if (c < 0x80)
            continue;
        if (c >= 0xC2 && c <= 0xDF) { extra = 1; lowest = 0x80; c &= 0x1F; }
        else if (c >= 0xE0 && c <= 0xEF) { extra = 2; lowest = 0x800; c &= 0x0F; }
        else if (c >= 0xF0 && c <= 0xF4) { extra = 3; lowest = 0x10000; c &= 0x07; }
        else
            return 0;
        if (i + (size_t)extra > n)
            return 0;
        for (int k = 0; k < extra; k++)
        {
            if ((p[i] & 0xC0) != 0x80)
                return 0;
            c = (c << 6) | (p[i] & 0x3F);
            i++;
        }
        if (c < lowest || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF))
            return 0;
    }
    return 1;
}

// A name as it is, when it fits: -1 when it does not (a cut path is another file's) or holds a NUL.
static int name_copy(const uint8_t *from, size_t len, char *out, size_t outSize)
{
    while (len > 0 && from[len - 1] == '\0')
        len--; // the Unicode Path field may end with a NUL
    if (len == 0 || len >= outSize || memchr(from, '\0', len) != NULL)
        return -1;
    memcpy(out, from, len);
    out[len] = '\0';
    return 0;
}

static int name_from_unicode_field(const uint8_t *extra, size_t extraLen, const uint8_t *raw, size_t rawLen,
                                   char *out, size_t outSize)
{
    uint32_t crc = (uint32_t)crc32(0L, raw, (uInt)rawLen);
    size_t at = 0;
    while (at + 4 <= extraLen)
    {
        uint16_t id = rd16(extra + at), size = rd16(extra + at + 2);
        if (at + 4 + size > extraLen)
            break;
        const uint8_t *field = extra + at + 4;
        if (id == 0x7075 && size >= 5 && field[0] == 1 && rd32(field + 1) == crc)
            return name_copy(field + 5, (size_t)size - 5, out, outSize);
        at += 4 + (size_t)size;
    }
    return -1;
}

static int name_from_gb(const uint8_t *from, size_t len, char *out, size_t outSize)
{
    static const char *const pages[] = { "GB18030", "GBK", "CP936", "EUC-CN" };
    for (size_t i = 0; i < sizeof pages / sizeof pages[0]; i++)
    {
        iconv_t cd = iconv_open("UTF-8", pages[i]);
        if (cd == (iconv_t)-1)
            continue;
        char *in = (char *)from;
        size_t left = len, room = outSize - 1;
        char *to = out;
        size_t rc = iconv(cd, &in, &left, &to, &room);
        int done = rc != (size_t)-1 && left == 0;
        iconv_close(cd);
        if (!done)
            return -1;
        *to = '\0';
        return utf8_valid((const uint8_t *)out, (size_t)(to - out)) ? 0 : -1;
    }
    return -1;
}

// The name the disk takes: 0, or -1 (too long, or bytes no rule here reads).
static int decode_name(const uint8_t *raw, size_t rawLen, uint16_t flags, const uint8_t *extra, size_t extraLen,
                       char *out, size_t outSize)
{
    int plain = 1;
    for (size_t i = 0; i < rawLen; i++)
        if (raw[i] >= 0x80 || raw[i] == 0)
        {
            plain = 0;
            break;
        }
    if (plain || (flags & 0x0800) != 0)
        return name_copy(raw, rawLen, out, outSize);
    if (name_from_unicode_field(extra, extraLen, raw, rawLen, out, outSize) == 0)
        return 0;
    if (utf8_valid(raw, rawLen))
        return name_copy(raw, rawLen, out, outSize);
    return name_from_gb(raw, rawLen, out, outSize);
}

// mkdir -p. The path given is a directory's path (callers cut file names off first).
static int mkdir_p(const char *path)
{
    char buf[2048];
    snprintf(buf, sizeof buf, "%s", path);
    size_t n = strlen(buf);
    if (n == 0 || n >= sizeof buf)
        return -1;
    if (buf[n - 1] == '/')
        buf[n - 1] = '\0';
    for (char *p = buf + 1; *p; p++)
    {
        if (*p != '/')
            continue;
        *p = '\0';
        if (mkdir(buf, 0755) != 0 && errno != EEXIST)
            return -1;
        *p = '/';
    }
    return mkdir(buf, 0755) != 0 && errno != EEXIST ? -1 : 0;
}

// A name the game could use: no leading slash, no ".." component, no drive-letter business.
static int name_is_safe(const char *name)
{
    if (name[0] == '\0' || name[0] == '/' || strchr(name, ':') != NULL)
        return 0;
    const char *p = name;
    while (*p)
    {
        const char *slash = strchr(p, '/');
        size_t len = slash != NULL ? (size_t)(slash - p) : strlen(p);
        if (len == 2 && p[0] == '.' && p[1] == '.')
            return 0;
        if (slash == NULL)
            break;
        p = slash + 1;
    }
    return 1;
}

// The parent directory of a file path, made (directories also come as their own entries,
// but a zip made in a hurry can rely on the file's name alone).
static int make_parent(const char *path)
{
    const char *slash = strrchr(path, '/');
    if (slash == NULL)
        return 0;
    char parent[2048];
    size_t n = (size_t)(slash - path);
    if (n >= sizeof parent)
        return -1;
    memcpy(parent, path, n);
    parent[n] = '\0';
    return mkdir_p(parent);
}

// One entry: local header says where the bytes are; stored copies, deflate inflates (raw).
static int unpack_entry(FILE *zip, uint32_t lho, const char *name, uint16_t method,
                        uint32_t csize, uint32_t usize, const char *destDir, int64_t entry, int64_t total)
{
    uint8_t lfh[30];
    if (fseek(zip, (long)lho, SEEK_SET) != 0 || fread(lfh, 1, sizeof lfh, zip) != sizeof lfh ||
        rd32(lfh) != 0x04034b50)
        return -1;
    uint16_t fnLen = rd16(lfh + 26), exLen = rd16(lfh + 28);
    long dataStart = (long)lho + 30 + fnLen + exLen;

    char full[2048];
    int joined = snprintf(full, sizeof full, "%s/%s", destDir, name);
    if (joined < 0 || (size_t)joined >= sizeof full)
        return -1;
    size_t nlen = strlen(name);
    if (nlen > 0 && name[nlen - 1] == '/')
        return mkdir_p(full); // a directory entry

    if (make_parent(full) != 0)
        return -1;

    FILE *out = fopen(full, "wb");
    if (out == NULL)
        return -1;
    int ok = 0;
    if (method == 0 && csize == usize) // stored: a plain copy
    {
        if (dataStart >= 0 && fseek(zip, dataStart, SEEK_SET) == 0)
        {
            static uint8_t chunk[ZIP_CHUNK];
            uint32_t left = csize;
            ok = 1;
            while (left > 0)
            {
                size_t want = left > sizeof chunk ? sizeof chunk : left;
                if (fread(chunk, 1, want, zip) != want || fwrite(chunk, 1, want, out) != want)
                {
                    ok = 0;
                    break;
                }
                left -= (uint32_t)want;
            }
        }
    }
    else if (method == 8) // deflate: libz's raw inflate (no zlib header in zip members)
    {
        if (dataStart >= 0 && fseek(zip, dataStart, SEEK_SET) == 0)
        {
            static uint8_t in[ZIP_CHUNK], outBuf[ZIP_CHUNK];
            z_stream zs;
            memset(&zs, 0, sizeof zs);
            if (inflateInit2(&zs, -15) == Z_OK)
            {
                uint32_t left = csize;
                ok = 1;
                while (1)
                {
                    if (zs.avail_in == 0 && left > 0)
                    {
                        size_t want = left > sizeof in ? sizeof in : left;
                        if (fread(in, 1, want, zip) != want)
                        {
                            ok = 0;
                            break;
                        }
                        zs.next_in = in;
                        zs.avail_in = (uInt)want;
                        left -= (uint32_t)want;
                    }
                    zs.next_out = outBuf;
                    zs.avail_out = (uInt)sizeof outBuf;
                    int rc = inflate(&zs, Z_NO_FLUSH);
                    size_t got = sizeof outBuf - zs.avail_out;
                    if (got > 0 && fwrite(outBuf, 1, got, out) != got)
                    {
                        ok = 0;
                        break;
                    }
                    if (rc == Z_STREAM_END)
                        break;
                    if (rc != Z_OK || (zs.avail_in == 0 && left == 0))
                    {
                        ok = rc == Z_STREAM_END || (rc == Z_OK && zs.total_out == usize);
                        break;
                    }
                }
                inflateEnd(&zs);
            }
        }
    }
    if (fclose(out) != 0)
        ok = 0;
    if (!ok)
        unlink(full);
    else if (entry > 0 && entry % 500 == 0)
    {
        char line[160];
        snprintf(line, sizeof line, "安装：%lld/%lld 项（%s…）", (long long)entry, (long long)total, name);
        host_log(line);
    }
    return ok ? 0 : -1;
}

int port17_unzip(const char *zipPath, const char *destDir)
{
    FILE *zip = fopen(zipPath, "rb");
    if (zip == NULL)
        return -1;
    // EOCD: at least its 22 bytes at the tail, plus whatever comment the writer left.
    static uint8_t tail[22 + 65536];
    long fileSize;
    if (fseek(zip, 0, SEEK_END) != 0 || (fileSize = ftell(zip)) < 22)
    {
        fclose(zip);
        return -2;
    }
    long tailLen = fileSize < (long)sizeof tail ? fileSize : (long)sizeof tail;
    if (fseek(zip, fileSize - tailLen, SEEK_SET) != 0 || fread(tail, 1, (size_t)tailLen, zip) != (size_t)tailLen)
    {
        fclose(zip);
        return -2;
    }
    long eocd = -1;
    for (long i = tailLen - 22; i >= 0; i--)
    {
        if (rd32(tail + i) == 0x06054b50)
        {
            eocd = i;
            break;
        }
    }
    if (eocd < 0)
    {
        fclose(zip);
        return -3;
    }
    uint16_t entries = rd16(tail + eocd + 10);
    uint32_t cdSize = rd32(tail + eocd + 12);
    uint32_t cdOff = rd32(tail + eocd + 16);
    if (cdOff == 0xFFFFFFFF || cdOff + cdSize > (uint32_t)fileSize) // a zip64 marker: not ours
    {
        fclose(zip);
        return -4;
    }
    uint8_t *cd = cdSize != 0 ? (uint8_t *)malloc(cdSize) : NULL;
    if (cdSize != 0 && (cd == NULL || fseek(zip, (long)cdOff, SEEK_SET) != 0 || fread(cd, 1, cdSize, zip) != cdSize))
    {
        free(cd);
        fclose(zip);
        return -5;
    }
    if (mkdir_p(destDir) != 0)
    {
        free(cd);
        fclose(zip);
        return -6;
    }
    int rc = 0;
    uint32_t at = 0;
    for (uint16_t i = 0; i < entries && rc == 0; i++)
    {
        if (at + 46 > cdSize || rd32(cd + at) != 0x02014b50)
        {
            rc = -7;
            break;
        }
        uint16_t method = rd16(cd + at + 10);
        uint32_t csize = rd32(cd + at + 20);
        uint32_t usize = rd32(cd + at + 24);
        uint16_t fnLen = rd16(cd + at + 28);
        uint16_t exLen = rd16(cd + at + 30);
        uint16_t cmLen = rd16(cd + at + 32);
        uint32_t lho = rd32(cd + at + 42);
        if (at + 46 + (uint32_t)fnLen + exLen + cmLen > cdSize || lho >= (uint32_t)fileSize)
        {
            rc = -7;
            break;
        }
        const uint8_t *raw = cd + at + 46;
        char name[3072];
        if (decode_name(raw, fnLen, rd16(cd + at + 8), raw + fnLen, exLen, name, sizeof name) != 0)
        {
            rc = -8;
            break;
        }
        if (!name_is_safe(name))
        {
            rc = -8;
            break;
        }
        rc = unpack_entry(zip, lho, name, method, csize, usize, destDir, i + 1, entries);
        at += 46 + (uint32_t)fnLen + exLen + cmLen;
    }
    free(cd);
    fclose(zip);
    return rc;
}

// ---- the install step ----

static int rmtree(const char *path)
{
    DIR *d = opendir(path);
    if (d != NULL)
    {
        struct dirent *e;
        while ((e = readdir(d)) != NULL)
        {
            if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0)
                continue;
            char child[1100];
            snprintf(child, sizeof child, "%s/%s", path, e->d_name);
            struct stat st;
            if (stat(child, &st) == 0 && S_ISDIR(st.st_mode))
                rmtree(child);
            else
                unlink(child);
        }
        closedir(d);
        return rmdir(path);
    }
    return -1;
}

static int file_there(const char *path)
{
    struct stat st;
    return stat(path, &st) == 0;
}

static int install_now(const char *home)
{
    char zipPath[1024], staging[1024], game[1024];
    snprintf(zipPath, sizeof zipPath, "%s/Documents/port17-game.zip", home);
    snprintf(staging, sizeof staging, "%s/Documents/.game-unzip", home);
    snprintf(game, sizeof game, "%s/Documents/game", home);

    struct stat st;
    if (stat(zipPath, &st) != 0)
        return 1; // no zip: nothing to install (not an error)

    char line[256];
    snprintf(line, sizeof line, "安装：port17-game.zip 找到（%.1f GB），解压中…", st.st_size / 1073741824.0);
    host_log(line);
    rmtree(staging);
    if (port17_unzip(zipPath, staging) != 0)
    {
        host_log("安装：解压失败（半成品已清理）——zip 坏了？重新拷一份");
        rmtree(staging);
        return -1;
    }
    // The zip is the game folder itself. One made of the folder around it (iOS's own
    // "compress" wraps a game/ directory) has its root one level down — take that.
    char root[1024], probe[1100];
    snprintf(root, sizeof root, "%s", staging);
    snprintf(probe, sizeof probe, "%s/RimWorld.dll", root);
    if (!file_there(probe))
    {
        snprintf(probe, sizeof probe, "%s/game/RimWorld.dll", staging);
        if (file_there(probe))
            snprintf(root, sizeof root, "%s/game", staging);
    }
    snprintf(probe, sizeof probe, "%s/RimWorld.dll", root);
    if (!file_there(probe))
    {
        host_log("安装：解压完成了但里面没有 RimWorld.dll——zip 不是 game 文件夹打的（PC 上用 scripts/pack-game-zip.py）");
        rmtree(staging);
        return -2;
    }
    // The old game steps aside (kept, not deleted: it stays the player's data until the new
    // one proves itself; the log says where it went).
    if (file_there(game))
    {
        char previous[1100];
        snprintf(previous, sizeof previous, "%s.previous", game);
        rmtree(previous);
        if (rename(game, previous) != 0)
        {
            host_log("安装：旧 game 目录挪不走（Documents 改不动？）——安装中止");
            rmtree(staging);
            return -3;
        }
        host_log("安装：旧 game 挪为 game.previous（新版能跑后可在文件 app 里删掉）");
    }
    if (rename(root, game) != 0)
    {
        host_log("安装：就位失败（.game-unzip → game 改名）");
        rmtree(staging);
        return -4;
    }
    if (strcmp(root, game) != 0)
        rmtree(staging); // the wrapper directory the zip came in
    char installed[1100];
    snprintf(installed, sizeof installed, "%s.installed", zipPath);
    rename(zipPath, installed); // one copy installs once; the file stays as the player's record
    host_log("安装：game 就位（port17-game.zip 改名 .installed，可在文件 app 删除腾空间）");
    return 0;
}

// On the main thread the work runs on a thread of its own while the main run loop keeps
// turning (the launch watchdog counts the seconds till the app services its loop); anywhere
// else the caller is a worker thread already and the work is done inline.
struct install_arg
{
    const char *home;
    int result;
};

static void *install_thread(void *argRaw)
{
    struct install_arg *arg = (struct install_arg *)argRaw;
    arg->result = install_now(arg->home);
    return NULL;
}

int port17_install_game_zip(const char *home)
{
    char zipPath[1024];
    snprintf(zipPath, sizeof zipPath, "%s/Documents/port17-game.zip", home);
    if (!file_there(zipPath))
        return 1; // nothing waiting
    if (!pthread_main_np())
        return install_now(home);
    struct install_arg arg = { home, -100 };
    pthread_t t;
    if (pthread_create(&t, NULL, install_thread, &arg) != 0)
        return install_now(home);
    // Services the loop in slices until the worker is through (0.05 s each: the watchdog
    // sees a live run loop, the log's dispatch blocks get their turn).
    while (arg.result == -100)
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, false);
    pthread_join(t, NULL);
    return arg.result;
}

// ---- the zip writer (the export side) ----
// The reader above, run backwards: walk a tree, emit local headers + raw bytes + a central
// directory + EOCD, all STORE (method 0). The local header's crc/sizes are patched in place
// after the bytes are counted (no data descriptor — the reader prefers real fields anyway).

struct zentry
{
    char name[960]; // zip-relative, '/' separators
    uint16_t fnlen;
    uint32_t crc, csize, usize, lho;
};

static void wr16(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
}
static void wr32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}

static int zadd_file(FILE *zf, const char *disk_path, const char *zip_name,
                     struct zentry *entries, size_t *count)
{
    if (*count >= 32767) // EOCD counts in a u16; more than this is not our tree
        return -1;
    struct zentry e;
    snprintf(e.name, sizeof e.name, "%s", zip_name);
    e.fnlen = (uint16_t)strlen(e.name);
    FILE *in = fopen(disk_path, "rb");
    if (in == NULL)
        return -2;
    long lho = ftell(zf);
    if (lho < 0)
    {
        fclose(in);
        return -3;
    }
    e.lho = (uint32_t)lho;

    uint8_t head[30 + sizeof e.name];
    wr32(head, 0x04034b50);
    wr16(head + 4, 20);     // version needed: 2.0
    wr16(head + 6, 0x0800); // bit 11: the name is UTF-8 (save names are any language)
    wr16(head + 8, 0);      // method: STORE
    wr16(head + 10, 0);     // mod time
    wr16(head + 12, 0x21);  // mod date: 1980-01-01 — the bytes are what's being kept
    wr32(head + 14, 0);     // crc: patched below
    wr32(head + 18, 0);     // csize: patched below
    wr32(head + 22, 0);     // usize: patched below
    wr16(head + 26, e.fnlen);
    wr16(head + 28, 0); // no extra field
    memcpy(head + 30, e.name, e.fnlen);
    if (fwrite(head, 1, 30u + e.fnlen, zf) != 30u + e.fnlen)
    {
        fclose(in);
        return -4;
    }

    uint8_t buf[ZIP_CHUNK];
    uLong crc = crc32(0, Z_NULL, 0);
    uint64_t total = 0;
    size_t got;
    while ((got = fread(buf, 1, sizeof buf, in)) > 0)
    {
        crc = crc32(crc, buf, (uInt)got);
        if (fwrite(buf, 1, got, zf) != got)
        {
            fclose(in);
            return -5;
        }
        total += got;
    }
    int ferr = ferror(in);
    fclose(in);
    if (ferr)
        return -6;
    if (total > 0xFFFFFFFFull) // would need zip64 fields: not ours
        return -7;
    e.crc = (uint32_t)crc;
    e.csize = (uint32_t)total;
    e.usize = (uint32_t)total;
    if (fseek(zf, lho + 14, SEEK_SET) != 0) // crc(14) csize(18) usize(22)
        return -8;
    uint8_t fix[12];
    wr32(fix, e.crc);
    wr32(fix + 4, e.csize);
    wr32(fix + 8, e.usize);
    if (fwrite(fix, 1, 12, zf) != 12)
        return -9;
    if (fseek(zf, 0, SEEK_END) != 0)
        return -10;
    entries[(*count)++] = e;
    return 0;
}

static int zwalk(FILE *zf, const char *disk_dir, const char *zip_prefix,
                 struct zentry *entries, size_t *count)
{
    DIR *d = opendir(disk_dir);
    if (d == NULL)
        return -20;
    struct dirent *de;
    int rc = 0;
    while (rc == 0 && (de = readdir(d)) != NULL)
    {
        if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0)
            continue;
        char child[1100], zname[960];
        snprintf(child, sizeof child, "%s/%s", disk_dir, de->d_name);
        if (zip_prefix[0])
            snprintf(zname, sizeof zname, "%s/%s", zip_prefix, de->d_name);
        else
            snprintf(zname, sizeof zname, "%s", de->d_name);
        struct stat st;
        if (stat(child, &st) != 0)
            continue; // raced away between readdir and stat: not packable, not fatal
        if (S_ISDIR(st.st_mode))
            rc = zwalk(zf, child, zname, entries, count);
        else if (S_ISREG(st.st_mode))
            rc = zadd_file(zf, child, zname, entries, count);
    }
    closedir(d);
    return rc;
}

int port17_zip_tree(const char *srcDir, const char *destZip)
{
    struct zentry *entries = malloc(32767 * sizeof *entries);
    if (entries == NULL)
        return -30;
    FILE *zf = fopen(destZip, "wb+");
    if (zf == NULL)
    {
        free(entries);
        return -31;
    }
    size_t count = 0;
    int rc = zwalk(zf, srcDir, "", entries, &count);
    long cdoff = rc == 0 ? ftell(zf) : -1;
    if (rc == 0 && cdoff < 0)
        rc = -32;
    for (size_t i = 0; rc == 0 && i < count; i++)
    {
        struct zentry *e = &entries[i];
        uint8_t cd[46 + sizeof e->name];
        wr32(cd, 0x02014b50);
        wr16(cd + 4, 20);     // version made by
        wr16(cd + 6, 20);     // version needed
        wr16(cd + 8, 0x0800); // UTF-8 name
        wr16(cd + 10, 0);     // STORE
        wr16(cd + 12, 0);     // mod time
        wr16(cd + 14, 0x21);  // mod date
        wr32(cd + 16, e->crc);
        wr32(cd + 20, e->csize);
        wr32(cd + 24, e->usize);
        wr16(cd + 28, e->fnlen);
        wr16(cd + 30, 0); // no extra
        wr16(cd + 32, 0); // no comment
        wr16(cd + 34, 0); // disk number start
        wr16(cd + 36, 0); // internal attrs
        wr32(cd + 38, 0); // external attrs
        wr32(cd + 42, e->lho);
        memcpy(cd + 46, e->name, e->fnlen);
        if (fwrite(cd, 1, 46u + e->fnlen, zf) != 46u + e->fnlen)
            rc = -33;
    }
    long cdend = rc == 0 ? ftell(zf) : -1;
    if (rc == 0 && cdend >= 0)
    {
        uint8_t eocd[22];
        wr32(eocd, 0x06054b50);
        wr16(eocd + 4, 0); // this disk
        wr16(eocd + 6, 0); // disk with the central directory
        wr16(eocd + 8, (uint16_t)count);
        wr16(eocd + 10, (uint16_t)count);
        wr32(eocd + 12, (uint32_t)(cdend - cdoff));
        wr32(eocd + 16, (uint32_t)cdoff);
        wr16(eocd + 20, 0); // no comment
        if (fwrite(eocd, 1, 22, zf) != 22)
            rc = -34;
    }
    if (fclose(zf) != 0)
        rc = rc != 0 ? rc : -35;
    if (rc != 0)
        unlink(destZip); // a half-zip is worse than none
    free(entries);
    return rc == 0 ? (int)count : rc;
}
