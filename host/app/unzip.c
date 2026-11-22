// See unzip.h. The zip reader: EOCD at the tail gives the central directory; every entry
// there points at its local file header, whose own name/extra lengths are where the data
// really starts (the central directory's extra length can differ — a classic trap).
#include "unzip.h"
#include "host.h"

#include <CoreFoundation/CoreFoundation.h>
#include <dirent.h>
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

// mkdir -p. The path given is a directory's path (callers cut file names off first).
static int mkdir_p(const char *path)
{
    char buf[1100];
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
    char parent[1100];
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

    char full[1100];
    snprintf(full, sizeof full, "%s/%s", destDir, name);
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
        if (at + 46 + (uint32_t)fnLen > cdSize || lho >= (uint32_t)fileSize)
        {
            rc = -7;
            break;
        }
        char name[1024];
        size_t nn = fnLen < sizeof name - 1 ? fnLen : sizeof name - 1;
        memcpy(name, cd + at + 46, nn);
        name[nn] = '\0';
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
