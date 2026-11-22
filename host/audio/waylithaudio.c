// libwaylithaudio.so: the engine's audio library (Waylith.Audio.MiniAudio.MiniAudioBackend P/Invokes it as
// "waylithaudio"), rebuilt on miniaudio 0.11.22 for Android (AAudio, OpenSL ES fallback). The API is the engine's
// (host/stubs/waylithaudio.c lists the signatures, taken from the P/Invoke declarations):
//   sounds   shared data: PCM (s16, the engine reads and writes it through wa_sound_pcm), a compressed stream in
//            memory or in a file (decoded per voice), or a user stream (the engine's callbacks).
//            A sound the engine loads as PCM from compressed data stays compressed until it is played: the game
//            loads every sound it has when it starts (six thousand of them, five and a half hours, 1.7 GB as
//            samples). Played, a short one is decoded whole and kept in a cache of limited size (the least
//            recently played ones are dropped again); a long one is decoded as it plays, like a stream.
//   voices   one ma_sound each, with optional low-pass, high-pass and echo nodes between it and its group
//   groups   ma_sound_group; handle 0 is the engine's endpoint
//   device   the library's own, handed to the engine, so that wa_update can start a device that stopped by itself or
//            make it again, as the PC's library does since 1.7.4995 (wa_device_recoveries, wa_device_lost)
// Handles are indices with a generation, so that a voice the engine still refers to after its automatic release
// is harmless. Reverb is a Freeverb node of our own (the engine passes Freeverb-style parameters), and so is the mix
// matrix (the engine's panning of mono clips and the panning of 3D voices, as the PC's library does them).
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <pthread.h>
#include <stdio.h>
#include <time.h>
#include <stdarg.h>

#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_ENABLE_ONLY_SPECIFIC_BACKENDS
#define MA_ENABLE_COREAUDIO
#define MA_ENABLE_NULL
#include "miniaudio.h"
#undef STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#define EXPORT __attribute__((visibility("default")))
#define LOGI(...) audio_log(__VA_ARGS__)
#define LOGW(...) audio_log(__VA_ARGS__)

// The host installs its log sink (screen + Documents) before anything else.
static void (*g_audio_log_sink)(const char*) = NULL;
EXPORT void port17_audio_set_log_sink(void (*sink)(const char*)) { g_audio_log_sink = sink; }
static void audio_log(const char* fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    if (g_audio_log_sink) g_audio_log_sink(text);
    fprintf(stderr, "[audio] %s\n", text);
}

typedef int (*wa_read_fn)(void* user, float* output, uint64_t frames);
typedef int (*wa_seek_fn)(void* user, uint64_t frame);

enum { KIND_PCM = 0, KIND_MEMORY_STREAM = 2, KIND_FILE_STREAM = 3, KIND_USER = 4 };

typedef struct Sound
{
    int kind;
    int16_t* pcm;            // KIND_PCM: interleaved s16, owned
    uint64_t frames;
    uint32_t channels, rate;
    const void* data;        // KIND_MEMORY_STREAM
    size_t length;
    void* owned;             // a copy of the stream data when the engine asked for one
    char* path;              // KIND_FILE_STREAM
    wa_read_fn read;         // KIND_USER
    wa_seek_fn seek;
    void* user;
    int refs;                // the handle plus every voice playing it
    // KIND_PCM loaded from compressed data: data/length hold the stream, pcm is there only while decoded.
    int lazy;
    int pinned;              // the engine was given the samples' address: they stay
    int cached;              // in the cache's list
    int pcmUsers;            // voices playing from pcm
    uint64_t pcmFrames;      // frames in pcm (frames is what the engine was told when the sound was loaded)
    size_t pcmBytes;
    struct Sound* newer;     // the cache's list, least recently played first
    struct Sound* older;
} Sound;

// The engine's stream source as a miniaudio data source (f32 interleaved).
typedef struct UserSource
{
    ma_data_source_base base;
    Sound* sound;
    uint64_t cursor;
} UserSource;

// A pass-through node that keeps the last frames it saw, for wa_voice_tap_read.
typedef struct TapNode
{
    ma_node_base base;
    float* ring;             // capacity * channels
    uint32_t capacity, channels;
    volatile uint64_t written; // frames written in total
} TapNode;

typedef struct Voice
{
    int used;
    uint32_t gen;
    Sound* sound;
    ma_audio_buffer_ref bufref;
    ma_decoder decoder;
    int decoderInit;
    UserSource userSource;
    int userInit;
    ma_sound snd;
    int sndInit;
    int usesPcm;             // plays from the sound's samples (they must stay until the voice is released)
    ma_sound_group* group;
    float volume;
    int muted, autoRelease, stopped;
    volatile int ended;
    int lpfOn, hpfOn, echoOn, lpfInit, hpfInit, delayInit;
    ma_lpf_node lpf;
    ma_hpf_node hpf;
    ma_delay_node delay;
    TapNode* tap;
    struct ReverbNode* reverb;
    int reverbOn;
    struct MixNode* mix;
    int mixOn;
    int spatial;             // in 3D (wa_voice_set_3d): the matrix pans it by its direction from the listener
    int placed;              // has had a position in 3D (the first voices' matrices are logged at it)
} Voice;

typedef struct Group
{
    int used;
    uint32_t gen;
    ma_sound_group group;
    float volume;
    int muted;
} Group;

#define MAX_VOICES 1024
#define MAX_GROUPS 64

static ma_engine g_engine;
// The playback device is the library's own and is handed to the engine, as in the PC's library since 1.7.4995, so
// that a device that stops or is lost can be started or made again (wa_update) under the same engine.
static ma_device g_device;
static ma_device_config g_deviceConfig;
static ma_context_config g_contextConfig;
static int g_initialized, g_nullBackend, g_deviceInit;
static int g_masterPaused, g_downUpdates, g_recoveries;
static uint32_t g_periodFrames;
static ma_result g_lastError;
static uint32_t g_rate = 48000, g_channels = 2;
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static Voice g_voices[MAX_VOICES];
static Group g_groups[MAX_GROUPS];
static uint64_t g_voicesCreated, g_updates, g_mixMatrices, g_matrices3d;
static int g_lazy = 1, g_checkProbe;
static uint64_t g_probesChecked;
static void* (*g_alloc)(size_t);
static void* (*g_realloc)(void*, size_t);
static void (*g_free)(void*);

static void* host_malloc(size_t size, void* user) { (void)user; return g_alloc ? g_alloc(size) : malloc(size); }
static void* host_realloc(void* p, size_t size, void* user) { (void)user; return g_realloc ? g_realloc(p, size) : realloc(p, size); }
static void host_free(void* p, void* user) { (void)user; if (!p) return; if (g_free) g_free(p); else free(p); }
static ma_allocation_callbacks g_allocCallbacks;

// ---- handles ----
static uint64_t make_handle(uint32_t index, uint32_t gen) { return ((uint64_t)gen << 32) | (index + 1); }
static Voice* voice_of(uint64_t handle)
{
    uint32_t index = (uint32_t)(handle & 0xffffffffu);
    uint32_t gen = (uint32_t)(handle >> 32);
    if (index == 0 || index > MAX_VOICES) return NULL;
    Voice* v = &g_voices[index - 1];
    return (v->used && v->gen == gen) ? v : NULL;
}
static Group* group_of(uint64_t handle)
{
    uint32_t index = (uint32_t)(handle & 0xffffffffu);
    uint32_t gen = (uint32_t)(handle >> 32);
    if (index == 0 || index > MAX_GROUPS) return NULL;
    Group* g = &g_groups[index - 1];
    return (g->used && g->gen == gen) ? g : NULL;
}

// ---- sounds ----
// The cache of decoded samples (all of it under g_lock).
static Sound* g_cacheOldest;
static Sound* g_cacheNewest;
static size_t g_cacheBytes, g_cacheBudget = 64u << 20, g_decodeNowBytes = 384u << 10;
static uint64_t g_decodes, g_decodedBytes, g_decodeMicros, g_dropped, g_streamedLazy, g_lazyLoaded, g_lazyCopied, g_slowestDecodeMicros;

static uint64_t micros(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (uint64_t)t.tv_sec * 1000000u + (uint64_t)t.tv_nsec / 1000u;
}

static void cache_unlink(Sound* s)
{
    if (!s->cached) return;
    if (s->older) s->older->newer = s->newer; else g_cacheOldest = s->newer;
    if (s->newer) s->newer->older = s->older; else g_cacheNewest = s->older;
    s->newer = s->older = NULL;
    s->cached = 0;
    g_cacheBytes -= s->pcmBytes;
}

static void cache_add_newest(Sound* s)
{
    s->older = g_cacheNewest;
    s->newer = NULL;
    if (g_cacheNewest) g_cacheNewest->newer = s; else g_cacheOldest = s;
    g_cacheNewest = s;
    s->cached = 1;
    g_cacheBytes += s->pcmBytes;
}

// Drops the least recently played samples no voice is playing until the cache fits its budget.
static void cache_trim(void)
{
    Sound* s = g_cacheOldest;
    while (s && g_cacheBytes > g_cacheBudget)
    {
        Sound* next = s->newer;
        if (s->pcmUsers == 0 && !s->pinned)
        {
            cache_unlink(s);
            free(s->pcm);
            s->pcm = NULL;
            s->pcmFrames = 0;
            s->pcmBytes = 0;
            g_dropped++;
        }
        s = next;
    }
}

static void sound_free(Sound* s)
{
    cache_unlink(s);
    if (s->pcm) free(s->pcm);
    if (s->owned) free(s->owned);
    if (s->path) free(s->path);
    free(s);
}
static void sound_unref(Sound* s)
{
    if (s && --s->refs <= 0) sound_free(s);
}

// Decodes a whole stream (memory or file) to interleaved s16 at its own rate. At least minFrames frames are there
// (silence after what was decoded): the engine indexes the samples by the length it was told.
static int16_t* decode_samples(const void* data, size_t length, const char* path, uint64_t minFrames, uint64_t* outFrames, uint32_t* outChannels, uint32_t* outRate)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = data ? ma_decoder_init_memory(data, length, &cfg, &dec) : ma_decoder_init_file(path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return NULL; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    uint32_t channels = dec.outputChannels, rate = dec.outputSampleRate;
    uint64_t capacity = total ? total : 48000;
    if (capacity < minFrames) capacity = minFrames;
    int16_t* pcm = (int16_t*)malloc((size_t)(capacity * channels * sizeof(int16_t)));
    uint64_t have = 0;
    while (pcm)
    {
        if (have == capacity)
        {
            capacity *= 2;
            int16_t* bigger = (int16_t*)realloc(pcm, (size_t)(capacity * channels * sizeof(int16_t)));
            if (!bigger) { free(pcm); pcm = NULL; break; }
            pcm = bigger;
        }
        ma_uint64 got = 0;
        r = ma_decoder_read_pcm_frames(&dec, pcm + have * channels, capacity - have, &got);
        have += got;
        if (r != MA_SUCCESS || got == 0) break;
    }
    ma_decoder_uninit(&dec);
    if (!pcm) { g_lastError = MA_OUT_OF_MEMORY; return NULL; }
    if (have < minFrames) memset(pcm + have * channels, 0, (size_t)((minFrames - have) * channels * sizeof(int16_t)));
    *outFrames = have;
    *outChannels = channels;
    *outRate = rate;
    return pcm;
}

static Sound* decode_whole(const void* data, size_t length, const char* path)
{
    uint64_t frames = 0;
    uint32_t channels = 0, rate = 0;
    int16_t* pcm = decode_samples(data, length, path, 0, &frames, &channels, &rate);
    if (!pcm) return NULL;
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_PCM;
    s->pcm = pcm;
    s->frames = s->pcmFrames = frames;
    s->channels = channels;
    s->rate = rate;
    s->refs = 1;
    return s;
}

// The samples of a sound that was loaded compressed (g_lock held). 0 when they cannot be decoded.
static int sound_decode_locked(Sound* s)
{
    if (s->pcm) return 1;
    uint64_t started = micros(), frames = 0;
    uint32_t channels = 0, rate = 0;
    int16_t* pcm = decode_samples(s->data, s->length, NULL, s->frames, &frames, &channels, &rate);
    if (!pcm || channels != s->channels) { if (pcm) free(pcm); return 0; }
    s->pcm = pcm;
    s->pcmFrames = frames < s->frames ? frames : s->frames;
    s->pcmBytes = (size_t)((frames > s->frames ? frames : s->frames) * channels * sizeof(int16_t));
    uint64_t took = micros() - started;
    g_decodes++;
    g_decodedBytes += s->pcmBytes;
    g_decodeMicros += took;
    if (took > g_slowestDecodeMicros) g_slowestDecodeMicros = took;
    return 1;
}

// Whether the bytes lie in a file mapping that stays (one of the game's asset packs, mapped whole for as long as
// the game runs): then a sound can refer to them instead of keeping a copy. The engine says so itself for streams
// (wa_sound_load_memory_stream's "copy"), for sounds it loads as PCM it does not.
typedef struct { uintptr_t start, end; } Range;
static Range g_packs[64];
static int g_packCount;
static uint64_t g_packsReadAt;
static pthread_mutex_t g_packLock = PTHREAD_MUTEX_INITIALIZER;

static int in_pack_locked(uintptr_t p, size_t length)
{
    for (int i = 0; i < g_packCount; i++) if (p >= g_packs[i].start && p + length <= g_packs[i].end) return 1;
    return 0;
}

static int in_pack_mapping(const void* data, size_t length)
{
    uintptr_t p = (uintptr_t)data;
    pthread_mutex_lock(&g_packLock);
    int found = in_pack_locked(p, length);
    uint64_t now = micros();
    // The list is read again when an address is not in it, but not more often than every two seconds: sounds that
    // mods bring as files are never in a pack, and there are thousands of them.
    if (!found && (g_packsReadAt == 0 || now - g_packsReadAt > 2000000u))
    {
        g_packsReadAt = now ? now : 1;
        g_packCount = 0;
        FILE* maps = fopen("/proc/self/maps", "r");
        if (maps)
        {
            char line[1024];
            while (fgets(line, sizeof line, maps) && g_packCount < 64)
            {
                unsigned long long a = 0, b = 0;
                char perms[8] = "";
                if (sscanf(line, "%llx-%llx %7s", &a, &b, perms) != 3) continue;
                if (perms[0] != 'r' || perms[1] != '-' || perms[3] != 's' || !strstr(line, "Assets.zip")) continue;
                g_packs[g_packCount].start = (uintptr_t)a;
                g_packs[g_packCount].end = (uintptr_t)b;
                g_packCount++;
            }
            fclose(maps);
        }
        found = in_pack_locked(p, length);
    }
    pthread_mutex_unlock(&g_packLock);
    return found;
}

// Length, channels and rate of an Ogg Vorbis stream without setting a decoder up (which builds the stream's code
// books, a millisecond a time): the identification header in the first page, and the position the last page ends at.
// 0 when the data is anything else or does not end in a whole page; the decoder is asked then.
static int ogg_vorbis_probe(const uint8_t* d, size_t n, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    if (n < 64 || memcmp(d, "OggS", 4) != 0) return 0;
    const uint8_t* p = d + 27 + d[26];
    if ((size_t)(p - d) + 30 > n || p[0] != 1 || memcmp(p + 1, "vorbis", 6) != 0) return 0;
    uint32_t ch = p[11];
    uint32_t r = (uint32_t)p[12] | (uint32_t)p[13] << 8 | (uint32_t)p[14] << 16 | (uint32_t)p[15] << 24;
    if (ch == 0 || r == 0) return 0;
    // The last page: an "OggS" within the largest page size of the end whose page runs exactly to the end.
    size_t from = n > 65307 + 27 ? n - 65307 - 27 : 0;
    for (size_t at = n - 27; ; at--)
    {
        if (d[at] == 'O' && memcmp(d + at, "OggS", 4) == 0 && at + 27 + d[at + 26] <= n && memcmp(d + at + 14, d + 14, 4) == 0)
        {
            size_t body = 0;
            for (uint32_t i = 0; i < d[at + 26]; i++) body += d[at + 27 + i];
            if (at + 27 + d[at + 26] + body == n)
            {
                uint64_t granule = 0;
                for (int i = 7; i >= 0; i--) granule = granule << 8 | d[at + 6 + i];
                // The decoder keeps the low 32 bits (stb_vorbis); larger positions are left to it.
                if (granule == 0 || (granule >> 32) != 0) return 0;
                *frames = granule;
                *channels = ch;
                *rate = r;
                return 1;
            }
        }
        if (at <= from) break;
    }
    return 0;
}

// Length, channels and rate of compressed data in memory. 0 when it cannot be decoded.
static int probe_memory(const void* data, size_t length, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    if (ogg_vorbis_probe((const uint8_t*)data, length, frames, channels, rate))
    {
        if (g_checkProbe)
        {
            ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
            ma_decoder dec;
            if (ma_decoder_init_memory(data, length, &cfg, &dec) == MA_SUCCESS)
            {
                ma_uint64 total = 0;
                ma_decoder_get_length_in_pcm_frames(&dec, &total);
                if (total != *frames || dec.outputChannels != *channels || dec.outputSampleRate != *rate)
                    LOGW("audio: probe differs: %llu frames %u ch %u Hz, the decoder says %llu %u %u", (unsigned long long)*frames, *channels, *rate, (unsigned long long)total, dec.outputChannels, dec.outputSampleRate);
                ma_decoder_uninit(&dec);
            }
            else LOGW("audio: probe accepted what the decoder does not open (%zu bytes)", length);
            __sync_fetch_and_add(&g_probesChecked, 1);
        }
        return 1;
    }
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_memory(data, length, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return 0; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    *frames = total;
    *channels = dec.outputChannels;
    *rate = dec.outputSampleRate;
    ma_decoder_uninit(&dec);
    return 1;
}

EXPORT uint64_t wa_sound_load_pcm(void* data, uint64_t frames, uint32_t channels, uint32_t rate, int format)
{
    if (format != 1 || !data || channels == 0) { g_lastError = MA_INVALID_ARGS; return 0; }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    size_t bytes = (size_t)(frames * channels * sizeof(int16_t));
    s->pcm = (int16_t*)malloc(bytes ? bytes : 2);
    memcpy(s->pcm, data, bytes);
    s->kind = KIND_PCM;
    s->frames = s->pcmFrames = frames;
    s->channels = channels;
    s->rate = rate;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_memory(void* data, size_t length)
{
    uint64_t frames = 0;
    uint32_t channels = 0, rate = 0;
    if (!g_lazy || !probe_memory(data, length, &frames, &channels, &rate) || frames == 0 || channels == 0)
    {
        // Not asked for, or the length is not known beforehand: decoded now, as the engine expects.
        return (uint64_t)(uintptr_t)decode_whole(data, length, NULL);
    }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_PCM;
    s->lazy = 1;
    s->frames = frames;
    s->channels = channels;
    s->rate = rate;
    s->length = length;
    if (in_pack_mapping(data, length))
    {
        s->data = data;
    }
    else
    {
        s->owned = malloc(length ? length : 1);
        if (!s->owned) { free(s); g_lastError = MA_OUT_OF_MEMORY; return 0; }
        memcpy(s->owned, data, length);
        s->data = s->owned;
        __sync_fetch_and_add(&g_lazyCopied, 1);
    }
    s->refs = 1;
    __sync_fetch_and_add(&g_lazyLoaded, 1);
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_memory_stream(void* data, size_t length, int copy)
{
    // Probe first, so that undecodable data is reported now (the engine then tries the extracted Vorbis stream).
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_memory(data, length, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return 0; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_MEMORY_STREAM;
    s->channels = dec.outputChannels;
    s->rate = dec.outputSampleRate;
    s->frames = total;
    ma_decoder_uninit(&dec);
    if (copy)
    {
        s->owned = malloc(length ? length : 1);
        memcpy(s->owned, data, length);
        s->data = s->owned;
    }
    else
    {
        s->data = data;
    }
    s->length = length;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_load_file_stream(uint8_t* path)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_file((const char*)path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return 0; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_FILE_STREAM;
    s->channels = dec.outputChannels;
    s->rate = dec.outputSampleRate;
    s->frames = total;
    ma_decoder_uninit(&dec);
    s->path = strdup((const char*)path);
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT uint64_t wa_sound_create_user(uint32_t channels, uint32_t rate, uint64_t lengthFrames, void* read, void* seek, void* user)
{
    if (!read || channels == 0) { g_lastError = MA_INVALID_ARGS; return 0; }
    Sound* s = (Sound*)calloc(1, sizeof(Sound));
    s->kind = KIND_USER;
    s->channels = channels;
    s->rate = rate;
    s->frames = lengthFrames;
    s->read = (wa_read_fn)read;
    s->seek = (wa_seek_fn)seek;
    s->user = user;
    s->refs = 1;
    return (uint64_t)(uintptr_t)s;
}

EXPORT int wa_sound_kind(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->kind : -1; }
EXPORT uint64_t wa_sound_length_frames(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->frames : 0; }
EXPORT uint32_t wa_sound_channels(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->channels : 0; }
EXPORT uint32_t wa_sound_rate(uint64_t sound) { Sound* s = (Sound*)(uintptr_t)sound; return s ? s->rate : 0; }
EXPORT int16_t* wa_sound_pcm(uint64_t sound)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!s) return NULL;
    if (s->lazy && !s->pinned)
    {
        // The engine reads or writes the samples themselves (AudioClip.GetData and SetData): they are decoded and
        // from now on kept, outside the cache.
        pthread_mutex_lock(&g_lock);
        if (!s->pinned)
        {
            cache_unlink(s);
            if (sound_decode_locked(s)) s->pinned = 1;
        }
        pthread_mutex_unlock(&g_lock);
    }
    return s->pcm;
}
// The memory a sound holds now (1.7.4980 counts it for each sound it loads, AudioClip's Bytes): its samples while they
// are decoded, and a copy of the compressed data it keeps. Data that lies in an asset pack's mapping, and files, are
// not the sound's.
EXPORT uint64_t wa_sound_resident_bytes(uint64_t sound)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!s) return 0;
    pthread_mutex_lock(&g_lock);
    uint64_t bytes = 0;
    if (s->pcm) bytes += s->pcmBytes ? s->pcmBytes : s->pcmFrames * s->channels * sizeof(int16_t);
    if (s->owned) bytes += s->length;
    pthread_mutex_unlock(&g_lock);
    return bytes;
}
EXPORT void wa_sound_release(uint64_t sound)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!s) return;
    pthread_mutex_lock(&g_lock);
    sound_unref(s);
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_probe_memory(void* data, size_t length, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    uint64_t f = 0;
    uint32_t c = 0, r = 0;
    if (!probe_memory(data, length, &f, &c, &r)) return (int)(g_lastError != MA_SUCCESS ? g_lastError : MA_ERROR);
    if (frames) *frames = f;
    if (channels) *channels = c;
    if (rate) *rate = r;
    return 0;
}

EXPORT int wa_probe_file(uint8_t* path, uint64_t* frames, uint32_t* channels, uint32_t* rate)
{
    ma_decoder_config cfg = ma_decoder_config_init(ma_format_s16, 0, 0);
    ma_decoder dec;
    ma_result r = ma_decoder_init_file((const char*)path, &cfg, &dec);
    if (r != MA_SUCCESS) { g_lastError = r; return (int)r; }
    ma_uint64 total = 0;
    ma_decoder_get_length_in_pcm_frames(&dec, &total);
    if (frames) *frames = total;
    if (channels) *channels = dec.outputChannels;
    if (rate) *rate = dec.outputSampleRate;
    ma_decoder_uninit(&dec);
    return 0;
}

// ---- the user stream as a data source ----
static ma_result us_read(ma_data_source* ds, void* out, ma_uint64 frameCount, ma_uint64* framesRead)
{
    UserSource* u = (UserSource*)ds;
    int n = u->sound->read(u->sound->user, (float*)out, frameCount);
    if (n < 0) n = 0;
    if ((ma_uint64)n > frameCount) n = (int)(n / u->sound->channels); // a count of samples rather than frames
    if ((ma_uint64)n > frameCount) n = (int)frameCount;
    if (framesRead) *framesRead = (ma_uint64)n;
    u->cursor += (uint64_t)n;
    return n == 0 ? MA_AT_END : MA_SUCCESS;
}
static ma_result us_seek(ma_data_source* ds, ma_uint64 frameIndex)
{
    UserSource* u = (UserSource*)ds;
    if (!u->sound->seek) return MA_NOT_IMPLEMENTED;
    if (u->sound->seek(u->sound->user, frameIndex) != 0) return MA_ERROR;
    u->cursor = frameIndex;
    return MA_SUCCESS;
}
static ma_result us_get_format(ma_data_source* ds, ma_format* format, ma_uint32* channels, ma_uint32* rate, ma_channel* map, size_t mapCap)
{
    UserSource* u = (UserSource*)ds;
    if (format) *format = ma_format_f32;
    if (channels) *channels = u->sound->channels;
    if (rate) *rate = u->sound->rate;
    if (map) ma_channel_map_init_standard(ma_standard_channel_map_default, map, mapCap, u->sound->channels);
    return MA_SUCCESS;
}
static ma_result us_get_cursor(ma_data_source* ds, ma_uint64* cursor) { *cursor = ((UserSource*)ds)->cursor; return MA_SUCCESS; }
static ma_result us_get_length(ma_data_source* ds, ma_uint64* length) { *length = ((UserSource*)ds)->sound->frames; return MA_SUCCESS; }
static ma_data_source_vtable g_userVtable = { us_read, us_seek, us_get_format, us_get_cursor, us_get_length, NULL, 0 };

// ---- the tap node ----
static void tap_process(ma_node* node, const float** in, ma_uint32* inFrames, float** out, ma_uint32* outFrames)
{
    TapNode* t = (TapNode*)node;
    ma_uint32 frames = *inFrames < *outFrames ? *inFrames : *outFrames;
    memcpy(out[0], in[0], (size_t)frames * t->channels * sizeof(float));
    uint64_t written = t->written;
    for (ma_uint32 i = 0; i < frames; i++)
    {
        uint32_t slot = (uint32_t)((written + i) % t->capacity);
        memcpy(t->ring + (size_t)slot * t->channels, in[0] + (size_t)i * t->channels, t->channels * sizeof(float));
    }
    t->written = written + frames;
    *inFrames = frames;
    *outFrames = frames;
}
static ma_node_vtable g_tapVtable = { tap_process, NULL, 1, 1, MA_NODE_FLAG_PASSTHROUGH };

// ---- reverb: a Freeverb (Schroeder) node, per voice ----
#define RV_COMBS 8
#define RV_ALLPASSES 4
static const int rv_combTuning[RV_COMBS] = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
static const int rv_allpassTuning[RV_ALLPASSES] = { 556, 441, 341, 225 };
#define RV_STEREO_SPREAD 23
#define RV_MAX_CHANNELS 2

typedef struct { float* buf; int size, index; float filterStore; } RvComb;
typedef struct { float* buf; int size, index; } RvAllpass;
typedef struct ReverbNode
{
    ma_node_base base;
    uint32_t channels;
    RvComb comb[RV_MAX_CHANNELS][RV_COMBS];
    RvAllpass allpass[RV_MAX_CHANNELS][RV_ALLPASSES];
    float feedback, damp, wet1, wet2, dry;
    float highCutCoef, highCutState[RV_MAX_CHANNELS];
} ReverbNode;

static void rv_set(ReverbNode* r, float roomsize, float damp, float wet, float dry, float width, float highCutHz)
{
    r->feedback = roomsize * 0.28f + 0.7f;
    r->damp = damp * 0.4f;
    r->wet1 = wet * 3.0f * (width / 2.0f + 0.5f);
    r->wet2 = wet * 3.0f * ((1.0f - width) / 2.0f);
    r->dry = dry;
    r->highCutCoef = highCutHz > 0.0f && highCutHz < g_rate * 0.49f ? (float)(1.0 - exp(-2.0 * 3.14159265 * highCutHz / g_rate)) : 1.0f;
}

static void rv_process(ma_node* node, const float** in, ma_uint32* inFrames, float** out, ma_uint32* outFrames)
{
    ReverbNode* r = (ReverbNode*)node;
    ma_uint32 frames = *inFrames < *outFrames ? *inFrames : *outFrames;
    uint32_t ch = r->channels;
    for (ma_uint32 f = 0; f < frames; f++)
    {
        float input = 0.0f;
        for (uint32_t c = 0; c < ch; c++) input += in[0][f * ch + c];
        input *= 0.015f;
        float outL = 0.0f, outR = 0.0f;
        for (uint32_t c = 0; c < ch && c < RV_MAX_CHANNELS; c++)
        {
            float acc = 0.0f;
            for (int i = 0; i < RV_COMBS; i++)
            {
                RvComb* cb = &r->comb[c][i];
                float y = cb->buf[cb->index];
                cb->filterStore = y * (1.0f - r->damp) + cb->filterStore * r->damp;
                cb->buf[cb->index] = input + cb->filterStore * r->feedback;
                if (++cb->index >= cb->size) cb->index = 0;
                acc += y;
            }
            for (int i = 0; i < RV_ALLPASSES; i++)
            {
                RvAllpass* ap = &r->allpass[c][i];
                float bufout = ap->buf[ap->index];
                float y = -acc + bufout;
                ap->buf[ap->index] = acc + bufout * 0.5f;
                if (++ap->index >= ap->size) ap->index = 0;
                acc = y;
            }
            if (c == 0) outL = acc; else outR = acc;
        }
        if (ch == 1) outR = outL;
        float wets[2] = { outL * r->wet1 + outR * r->wet2, outR * r->wet1 + outL * r->wet2 };
        for (uint32_t c = 0; c < ch; c++)
        {
            float w = wets[c < 2 ? c : 1];
            if (r->highCutCoef < 1.0f && c < RV_MAX_CHANNELS)
            {
                r->highCutState[c] += r->highCutCoef * (w - r->highCutState[c]);
                w = r->highCutState[c];
            }
            out[0][f * ch + c] = in[0][f * ch + c] * r->dry + w;
        }
    }
    *inFrames = frames;
    *outFrames = frames;
}
static ma_node_vtable g_reverbVtable = { rv_process, NULL, 1, 1, 0 };

static void rv_free_buffers(ReverbNode* r)
{
    for (uint32_t c = 0; c < RV_MAX_CHANNELS; c++)
    {
        for (int i = 0; i < RV_COMBS; i++) free(r->comb[c][i].buf);
        for (int i = 0; i < RV_ALLPASSES; i++) free(r->allpass[c][i].buf);
    }
}

static ReverbNode* rv_create(void)
{
    ReverbNode* r = (ReverbNode*)calloc(1, sizeof(ReverbNode));
    r->channels = g_channels;
    float scale = g_rate / 44100.0f;
    for (uint32_t c = 0; c < RV_MAX_CHANNELS; c++)
    {
        for (int i = 0; i < RV_COMBS; i++)
        {
            r->comb[c][i].size = (int)((rv_combTuning[i] + c * RV_STEREO_SPREAD) * scale);
            r->comb[c][i].buf = (float*)calloc(r->comb[c][i].size, sizeof(float));
        }
        for (int i = 0; i < RV_ALLPASSES; i++)
        {
            r->allpass[c][i].size = (int)((rv_allpassTuning[i] + c * RV_STEREO_SPREAD) * scale);
            r->allpass[c][i].buf = (float*)calloc(r->allpass[c][i].size, sizeof(float));
        }
    }
    ma_node_config cfg = ma_node_config_init();
    ma_uint32 channels = g_channels;
    cfg.vtable = &g_reverbVtable;
    cfg.pInputChannels = &channels;
    cfg.pOutputChannels = &channels;
    if (ma_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, r) != MA_SUCCESS)
    {
        rv_free_buffers(r);
        free(r);
        return NULL;
    }
    return r;
}

static void rv_destroy(ReverbNode* r)
{
    ma_node_uninit(r, &g_allocCallbacks);
    rv_free_buffers(r);
    free(r);
}

// ---- the mix matrix: a node at the end of the voice ----
// AudioSource.ApplyPan pans a mono clip with the matrix [cos, sin] of the pan angle (0.707 to each side in the
// middle) and folds a stereo clip marked ForceToMono to mono with half of that for each channel; other clips get
// wa_voice_set_pan. The PC's library applies the matrix to what the sound puts out in the engine's channels (a mono
// clip duplicated to both) as the voice's last stage, after the effects; set_pan and set_3d(off) turn it off again,
// and a 3D voice gets a matrix of its own (voice_mix_3d). Without it a mono sound played 3 dB louder than on the PC
// and could not be panned.
#define MIX_MAX_CHANNELS 8

typedef struct MixNode
{
    ma_node_base base;
    uint32_t channels;
    float matrix[MIX_MAX_CHANNELS * MIX_MAX_CHANNELS]; // [out][in], both in the engine's channels
} MixNode;

static void mix_process(ma_node* node, const float** in, ma_uint32* inFrames, float** out, ma_uint32* outFrames)
{
    MixNode* m = (MixNode*)node;
    ma_uint32 frames = *inFrames < *outFrames ? *inFrames : *outFrames;
    uint32_t ch = m->channels;
    for (ma_uint32 f = 0; f < frames; f++)
    {
        const float* x = in[0] + (size_t)f * ch;
        float* y = out[0] + (size_t)f * ch;
        for (uint32_t o = 0; o < ch; o++)
        {
            float sum = 0.0f;
            for (uint32_t i = 0; i < ch; i++) sum += m->matrix[o * ch + i] * x[i];
            y[o] = sum;
        }
    }
    *inFrames = frames;
    *outFrames = frames;
}
static ma_node_vtable g_mixVtable = { mix_process, NULL, 1, 1, 0 };

static MixNode* mix_create(void)
{
    if (g_channels > MIX_MAX_CHANNELS) return NULL;
    MixNode* m = (MixNode*)calloc(1, sizeof(MixNode));
    m->channels = g_channels;
    ma_node_config cfg = ma_node_config_init();
    ma_uint32 channels = g_channels;
    cfg.vtable = &g_mixVtable;
    cfg.pInputChannels = &channels;
    cfg.pOutputChannels = &channels;
    if (ma_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, m) != MA_SUCCESS)
    {
        free(m);
        return NULL;
    }
    return m;
}

// ---- voices ----
static void voice_rebuild_chain(Voice* v)
{
    ma_node* target = v->group ? (ma_node*)v->group : ma_engine_get_endpoint(&g_engine);
    ma_node* nodes[6];
    int n = 0;
    if (v->lpfOn) nodes[n++] = &v->lpf;
    if (v->hpfOn) nodes[n++] = &v->hpf;
    if (v->echoOn) nodes[n++] = &v->delay;
    if (v->reverbOn && v->reverb) nodes[n++] = v->reverb;
    if (v->tap) nodes[n++] = v->tap;
    if (v->mixOn && v->mix) nodes[n++] = v->mix; // last, and after the tap: the PC's tap sees the signal before it
    ma_node* next = target;
    for (int i = n - 1; i >= 0; i--)
    {
        ma_node_attach_output_bus(nodes[i], 0, next, 0);
        next = nodes[i];
    }
    ma_node_attach_output_bus(&v->snd, 0, next, 0);
    if (!v->lpfOn && v->lpfInit) ma_node_detach_output_bus(&v->lpf, 0);
    if (!v->hpfOn && v->hpfInit) ma_node_detach_output_bus(&v->hpf, 0);
    if (!v->echoOn && v->delayInit) ma_node_detach_output_bus(&v->delay, 0);
    if (!v->reverbOn && v->reverb) ma_node_detach_output_bus(v->reverb, 0);
    if (!v->mixOn && v->mix) ma_node_detach_output_bus(v->mix, 0);
}

// A pan (wa_voice_set_pan) or leaving 3D (wa_voice_set_3d) takes the matrix's place, as in the PC's library.
static void voice_mix_off(Voice* v)
{
    if (v->mixOn) { v->mixOn = 0; voice_rebuild_chain(v); }
}

// A 3D voice is panned by its matrix as well, as in the PC's library, and miniaudio only attenuates it by distance.
// miniaudio's own panning, with the speakers straight to the left and the right, put a sound straight ahead (where a
// sound below the map's camera is) at half level on each side, 3 dB below the PC, and kept a fifth of the level on
// the far side. The pan is the cosine of the angle between the direction from the listener to the voice and the
// listener's right, cross(forward, up) as the engine passed them; the matrix is AudioSource.ApplyPan's law, cos and
// sin of (pan + 1) * pi/4 for the left and the right channel, other channels as they are. A voice within 0.0001 of
// the listener (the game's on-camera sustainers sit on it), or a listener whose right is no longer than that (forward
// and up parallel), is in the middle. As on the PC, the matrix is worked out again in set_3d, set_3d_position and
// set_listener, and a pan or a matrix from the engine replaces it until then. Returns the voice's distance from the
// listener, for the log.
static float voice_mix_3d(Voice* v)
{
    ma_vec3f p = ma_sound_get_position(&v->snd);
    ma_vec3f l = ma_engine_listener_get_position(&g_engine, 0);
    ma_vec3f f = ma_engine_listener_get_direction(&g_engine, 0);
    ma_vec3f u = ma_engine_listener_get_world_up(&g_engine, 0);
    float dx = p.x - l.x, dy = p.y - l.y, dz = p.z - l.z;
    float rx = f.y * u.z - f.z * u.y, ry = f.z * u.x - f.x * u.z, rz = f.x * u.y - f.y * u.x;
    float distance = sqrtf(dx * dx + dy * dy + dz * dz), right = sqrtf(rx * rx + ry * ry + rz * rz);
    float pan = distance > 0.0001f && right > 0.0001f ? (rx * dx + ry * dy + rz * dz) / (distance * right) : 0.0f;
    float angle = (fminf(fmaxf(pan, -1.0f), 1.0f) + 1.0f) * ((float)M_PI / 4.0f);
    uint32_t ch = v->mix->channels;
    float m[MIX_MAX_CHANNELS * MIX_MAX_CHANNELS] = { 0 };
    for (uint32_t c = 0; c < ch; c++) m[c * ch + c] = 1.0f;
    if (ch >= 2) { m[0] = cosf(angle); m[ch + 1] = sinf(angle); }
    memcpy(v->mix->matrix, m, sizeof m);
    if (!v->mixOn) { v->mixOn = 1; voice_rebuild_chain(v); }
    return distance;
}

static void voice_release_locked(Voice* v)
{
    if (!v->used) return;
    if (v->sndInit) { ma_sound_uninit(&v->snd); v->sndInit = 0; }
    if (v->lpfInit) { ma_lpf_node_uninit(&v->lpf, &g_allocCallbacks); v->lpfInit = 0; }
    if (v->hpfInit) { ma_hpf_node_uninit(&v->hpf, &g_allocCallbacks); v->hpfInit = 0; }
    if (v->delayInit) { ma_delay_node_uninit(&v->delay, &g_allocCallbacks); v->delayInit = 0; }
    if (v->tap) { ma_node_uninit(v->tap, &g_allocCallbacks); free(v->tap->ring); free(v->tap); v->tap = NULL; }
    if (v->reverb) { rv_destroy(v->reverb); v->reverb = NULL; }
    if (v->mix) { ma_node_uninit(v->mix, &g_allocCallbacks); free(v->mix); v->mix = NULL; }
    if (v->decoderInit) { ma_decoder_uninit(&v->decoder); v->decoderInit = 0; }
    if (v->userInit) { ma_data_source_uninit(&v->userSource); v->userInit = 0; }
    if (v->usesPcm && v->sound) { v->sound->pcmUsers--; v->usesPcm = 0; }
    sound_unref(v->sound);
    v->sound = NULL;
    v->used = 0;
    v->gen++;
}

static void on_sound_end(void* user, ma_sound* sound)
{
    (void)sound;
    Voice* v = (Voice*)user;
    v->ended = 1;
}

EXPORT uint64_t wa_voice_play(uint64_t sound, uint64_t group, int startPaused, int autoRelease)
{
    Sound* s = (Sound*)(uintptr_t)sound;
    if (!g_initialized || !s) { g_lastError = MA_INVALID_ARGS; return 0; }
    pthread_mutex_lock(&g_lock);
    Voice* v = NULL;
    uint32_t index = 0;
    for (uint32_t i = 0; i < MAX_VOICES; i++) if (!g_voices[i].used) { v = &g_voices[i]; index = i; break; }
    if (!v) { pthread_mutex_unlock(&g_lock); g_lastError = MA_OUT_OF_MEMORY; LOGW("audio: out of voices"); return 0; }
    uint32_t gen = v->gen;
    memset(v, 0, sizeof *v);
    v->gen = gen;
    v->used = 1;
    v->sound = s;
    s->refs++;
    v->volume = 1.0f;
    v->autoRelease = autoRelease;
    Group* g = group_of(group);
    v->group = g ? &g->group : NULL;
    ma_data_source* ds = NULL;
    ma_result r = MA_SUCCESS;
    switch (s->kind)
    {
    case KIND_PCM:
        if (s->lazy && !s->pcm)
        {
            // Not decoded (never played, or dropped from the cache since). A short one is decoded now, which takes
            // a few milliseconds once; a long one plays from a decoder of its own like a stream and is not kept.
            if ((size_t)(s->frames * s->channels * sizeof(int16_t)) <= g_decodeNowBytes && sound_decode_locked(s))
            {
                cache_add_newest(s);
            }
            else
            {
                ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
                r = ma_decoder_init_memory(s->data, s->length, &cfg, &v->decoder);
                if (r == MA_SUCCESS) { v->decoderInit = 1; ds = &v->decoder; g_streamedLazy++; }
                break;
            }
        }
        else if (s->cached)
        {
            cache_unlink(s);
            cache_add_newest(s);
        }
        r = ma_audio_buffer_ref_init(ma_format_s16, s->channels, s->pcm, s->pcmFrames, &v->bufref);
        v->bufref.sampleRate = s->rate;
        ds = &v->bufref;
        v->usesPcm = 1;
        s->pcmUsers++;
        if (s->lazy) cache_trim();
        break;
    case KIND_MEMORY_STREAM:
    case KIND_FILE_STREAM:
    {
        ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
        r = s->kind == KIND_MEMORY_STREAM ? ma_decoder_init_memory(s->data, s->length, &cfg, &v->decoder) : ma_decoder_init_file(s->path, &cfg, &v->decoder);
        if (r == MA_SUCCESS) { v->decoderInit = 1; ds = &v->decoder; }
        break;
    }
    case KIND_USER:
    {
        ma_data_source_config cfg = ma_data_source_config_init();
        cfg.vtable = &g_userVtable;
        r = ma_data_source_init(&cfg, &v->userSource.base);
        v->userSource.sound = s;
        v->userSource.cursor = 0;
        if (r == MA_SUCCESS) { v->userInit = 1; ds = &v->userSource; }
        break;
    }
    }
    if (r == MA_SUCCESS)
    {
        r = ma_sound_init_from_data_source(&g_engine, ds, 0, v->group, &v->snd);
    }
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        voice_release_locked(v);
        pthread_mutex_unlock(&g_lock);
        LOGW("audio: voice not created: %s", ma_result_description(r));
        return 0;
    }
    v->sndInit = 1;
    if (++g_voicesCreated <= 8) LOGI("audio: voice %llu: sound kind %d, %llu frames, %u ch, %u Hz, group %s, paused %d, auto %d", (unsigned long long)g_voicesCreated, s->kind, (unsigned long long)s->frames, s->channels, s->rate, v->group ? "yes" : "none", startPaused, autoRelease);
    ma_sound_set_spatialization_enabled(&v->snd, MA_FALSE);
    ma_sound_set_end_callback(&v->snd, on_sound_end, v);
    if (!startPaused) ma_sound_start(&v->snd);
    uint64_t handle = make_handle(index, gen);
    pthread_mutex_unlock(&g_lock);
    return handle;
}

EXPORT void wa_voice_stop(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    if (v)
    {
        ma_sound_stop(&v->snd);
        v->stopped = 1; // released at the next update: the engine has no release call for voices
    }
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_voice_is_playing(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    int playing = v && !v->stopped && ma_sound_is_playing(&v->snd);
    pthread_mutex_unlock(&g_lock);
    return playing;
}

EXPORT uint64_t wa_voice_get_position_frames(uint64_t voice)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    ma_uint64 cursor = 0;
    if (v) ma_sound_get_cursor_in_pcm_frames(&v->snd, &cursor);
    pthread_mutex_unlock(&g_lock);
    return cursor;
}

EXPORT int wa_voice_set_position_frames(uint64_t voice, uint64_t frame)
{
    pthread_mutex_lock(&g_lock);
    Voice* v = voice_of(voice);
    ma_result r = v ? ma_sound_seek_to_pcm_frame(&v->snd, frame) : MA_INVALID_ARGS;
    pthread_mutex_unlock(&g_lock);
    return (int)r;
}

#define WITH_VOICE(body) do { pthread_mutex_lock(&g_lock); Voice* v = voice_of(voice); if (v) { body; } pthread_mutex_unlock(&g_lock); } while (0)

EXPORT void wa_voice_set_volume(uint64_t voice, float volume) { WITH_VOICE({ v->volume = volume; if (!v->muted) ma_sound_set_volume(&v->snd, volume); }); }
EXPORT void wa_voice_set_mute(uint64_t voice, int mute) { WITH_VOICE({ v->muted = mute; ma_sound_set_volume(&v->snd, mute ? 0.0f : v->volume); }); }
EXPORT void wa_voice_set_pitch(uint64_t voice, float pitch) { WITH_VOICE({ ma_sound_set_pitch(&v->snd, pitch > 0.001f ? pitch : 0.001f); }); }
EXPORT void wa_voice_set_pan(uint64_t voice, float pan) { WITH_VOICE({ voice_mix_off(v); ma_sound_set_pan(&v->snd, pan); }); }
EXPORT void wa_voice_set_loop(uint64_t voice, int loop) { WITH_VOICE({ ma_sound_set_looping(&v->snd, loop ? MA_TRUE : MA_FALSE); }); }
EXPORT void wa_voice_set_priority(uint64_t voice, int priority) { (void)voice; (void)priority; }
EXPORT void wa_voice_set_doppler(uint64_t voice, float factor) { WITH_VOICE({ ma_sound_set_doppler_factor(&v->snd, factor); }); }
EXPORT void wa_voice_set_paused(uint64_t voice, int paused) { WITH_VOICE({ if (paused) ma_sound_stop(&v->snd); else ma_sound_start(&v->snd); }); }
EXPORT void wa_voice_set_start_time(uint64_t voice, uint64_t absoluteFrames) { WITH_VOICE({ ma_sound_set_start_time_in_pcm_frames(&v->snd, absoluteFrames); }); }
EXPORT void wa_voice_set_group(uint64_t voice, uint64_t group) { WITH_VOICE({ Group* g = group_of(group); v->group = g ? &g->group : NULL; voice_rebuild_chain(v); }); }
// The engine's matrix is outChannels x inChannels, row by row (the clip's channels in); it is laid onto the engine's
// channels as the PC's library does, and what does not fit is left out. The sound's own pan goes back to the middle.
EXPORT void wa_voice_set_mix_matrix(uint64_t voice, float* matrix, uint32_t outChannels, uint32_t inChannels)
{
    WITH_VOICE({
        if (matrix && !v->mix) v->mix = mix_create();
        if (matrix && v->mix)
        {
            uint32_t ch = v->mix->channels;
            uint32_t outs = outChannels < ch ? outChannels : ch;
            uint32_t ins = inChannels < ch ? inChannels : ch;
            float m[MIX_MAX_CHANNELS * MIX_MAX_CHANNELS] = { 0 };
            for (uint32_t o = 0; o < outs; o++)
                for (uint32_t i = 0; i < ins; i++) m[o * ch + i] = matrix[o * inChannels + i];
            memcpy(v->mix->matrix, m, sizeof m);
            ma_sound_set_pan(&v->snd, 0.0f);
            if (!v->mixOn) { v->mixOn = 1; voice_rebuild_chain(v); }
            if (++g_mixMatrices <= 4 && ch >= 2) LOGI("audio: mix matrix %ux%u on voice %u: left %.3f %.3f, right %.3f %.3f", outChannels, inChannels, (unsigned)(v - g_voices) + 1, m[0], m[1], m[ch], m[ch + 1]);
        }
    });
}
// AudioSource.Play passes a 3D voice's first position right after set_3d, which works the matrix out while the voice
// is still at the origin, and AudioSource.Update passes it again every frame. The first four voices' matrices are
// logged at their first position, as set_mix_matrix logs the first four of the engine's: nothing else in the log
// tells this panning from miniaudio's.
EXPORT void wa_voice_set_3d_position(uint64_t voice, float x, float y, float z)
{
    WITH_VOICE({
        ma_sound_set_position(&v->snd, x, y, z);
        if (v->spatial && v->mix)
        {
            float distance = voice_mix_3d(v);
            uint32_t ch = v->mix->channels;
            if (!v->placed && ++g_matrices3d <= 4 && ch >= 2) LOGI("audio: 3D matrix on voice %u, %.1f from the listener: left %.3f, right %.3f", (unsigned)(v - g_voices) + 1, distance, v->mix->matrix[0], v->mix->matrix[ch + 1]);
            v->placed = 1;
        }
    });
}
EXPORT void wa_voice_set_3d(uint64_t voice, int enabled, float minDistance, float maxDistance, int rolloff)
{
    WITH_VOICE({
        v->spatial = enabled != 0;
        ma_sound_set_spatialization_enabled(&v->snd, enabled ? MA_TRUE : MA_FALSE);
        if (enabled)
        {
            ma_sound_set_positioning(&v->snd, ma_positioning_absolute);
            // The distances as the PC's library passes them on: the minimum at least 0.0001, and a maximum that is
            // not above the engine's minimum becomes that minimum plus 0.0001. miniaudio plays a sound whose maximum
            // is not above its minimum at full level at any distance; on the PC such a sound (a distRange like
            // 30~30) falls silent with linear rolloff just beyond its minimum.
            ma_sound_set_min_distance(&v->snd, minDistance > 0.0001f ? minDistance : 0.0001f);
            ma_sound_set_max_distance(&v->snd, maxDistance > minDistance ? maxDistance : minDistance + 0.0001f);
            ma_sound_set_attenuation_model(&v->snd, rolloff == 1 ? ma_attenuation_model_linear : ma_attenuation_model_inverse);
            ma_sound_set_rolloff(&v->snd, 1.0f);
            if (!v->mix) v->mix = mix_create();
            if (v->mix)
            {
                ma_sound_set_directional_attenuation_factor(&v->snd, 0.0f);
                voice_mix_3d(v);
            }
        }
        else voice_mix_off(v);
    });
}

EXPORT int wa_voice_set_lowpass(uint64_t voice, int enabled, float cutoffHz, float q)
{
    (void)q;
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            double cutoff = cutoffHz < 10.0f ? 10.0 : cutoffHz > g_rate * 0.49f ? g_rate * 0.49 : cutoffHz;
            if (!v->lpfInit)
            {
                ma_lpf_node_config cfg = ma_lpf_node_config_init(g_channels, g_rate, cutoff, 2);
                result = ma_lpf_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->lpf);
                v->lpfInit = result == MA_SUCCESS;
            }
            else
            {
                ma_lpf_config cfg = ma_lpf_config_init(ma_format_f32, g_channels, g_rate, cutoff, 2);
                result = ma_lpf_node_reinit(&cfg, &v->lpf);
            }
        }
        if (result == MA_SUCCESS && (v->lpfOn != (enabled != 0)))
        {
            v->lpfOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_highpass(uint64_t voice, int enabled, float cutoffHz, float q)
{
    (void)q;
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            double cutoff = cutoffHz < 10.0f ? 10.0 : cutoffHz > g_rate * 0.49f ? g_rate * 0.49 : cutoffHz;
            if (!v->hpfInit)
            {
                ma_hpf_node_config cfg = ma_hpf_node_config_init(g_channels, g_rate, cutoff, 2);
                result = ma_hpf_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->hpf);
                v->hpfInit = result == MA_SUCCESS;
            }
            else
            {
                ma_hpf_config cfg = ma_hpf_config_init(ma_format_f32, g_channels, g_rate, cutoff, 2);
                result = ma_hpf_node_reinit(&cfg, &v->hpf);
            }
        }
        if (result == MA_SUCCESS && (v->hpfOn != (enabled != 0)))
        {
            v->hpfOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_echo(uint64_t voice, int enabled, float delayMs, float feedback, float wet, float dry)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            uint32_t delayFrames = (uint32_t)(delayMs * 0.001f * g_rate);
            if (delayFrames < 1) delayFrames = 1;
            if (!v->delayInit)
            {
                ma_delay_node_config cfg = ma_delay_node_config_init(g_channels, g_rate, delayFrames, feedback);
                cfg.delay.wet = wet;
                cfg.delay.dry = dry;
                result = ma_delay_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, &v->delay);
                v->delayInit = result == MA_SUCCESS;
            }
            else
            {
                ma_delay_node_set_decay(&v->delay, feedback);
                ma_delay_node_set_wet(&v->delay, wet);
                ma_delay_node_set_dry(&v->delay, dry);
            }
        }
        if (result == MA_SUCCESS && (v->echoOn != (enabled != 0)))
        {
            v->echoOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_set_reverb(uint64_t voice, int enabled, float roomsize, float damp, float wet, float dry, float width, float highCutHz)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        result = MA_SUCCESS;
        if (enabled)
        {
            if (!v->reverb) v->reverb = rv_create();
            if (!v->reverb) result = MA_OUT_OF_MEMORY;
            else rv_set(v->reverb, roomsize, damp, wet, dry, width, highCutHz);
        }
        if (result == MA_SUCCESS && (v->reverbOn != (enabled != 0)))
        {
            v->reverbOn = enabled != 0;
            voice_rebuild_chain(v);
        }
    });
    return result;
}

EXPORT int wa_voice_tap_enable(uint64_t voice, uint32_t capacityFrames)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        if (v->tap) { result = MA_SUCCESS; }
        else
        {
            TapNode* t = (TapNode*)calloc(1, sizeof(TapNode));
            t->capacity = capacityFrames ? capacityFrames : 16384;
            t->channels = g_channels;
            t->ring = (float*)calloc((size_t)t->capacity * t->channels, sizeof(float));
            ma_node_config cfg = ma_node_config_init();
            ma_uint32 channels = g_channels;
            cfg.vtable = &g_tapVtable;
            cfg.pInputChannels = &channels;
            cfg.pOutputChannels = &channels;
            result = ma_node_init(ma_engine_get_node_graph(&g_engine), &cfg, &g_allocCallbacks, t);
            if (result == MA_SUCCESS) { v->tap = t; voice_rebuild_chain(v); }
            else { free(t->ring); free(t); }
        }
    });
    return result;
}

EXPORT int wa_voice_tap_read(uint64_t voice, float* output, uint32_t frames, uint32_t channel)
{
    int result = MA_INVALID_ARGS;
    WITH_VOICE({
        TapNode* t = v->tap;
        if (t && channel < t->channels)
        {
            uint64_t written = t->written;
            uint64_t available = written < t->capacity ? written : t->capacity;
            uint64_t take = frames < available ? frames : available;
            uint64_t start = written - take;
            for (uint32_t i = 0; i < frames; i++)
            {
                if (i < frames - take) { output[i] = 0.0f; continue; }
                uint64_t frame = start + (i - (frames - take));
                output[i] = t->ring[(size_t)(frame % t->capacity) * t->channels + channel];
            }
            result = MA_SUCCESS;
        }
    });
    return result;
}

// ---- groups ----
EXPORT uint64_t wa_group_create(uint64_t parent)
{
    if (!g_initialized) return 0;
    pthread_mutex_lock(&g_lock);
    Group* g = NULL;
    uint32_t index = 0;
    for (uint32_t i = 0; i < MAX_GROUPS; i++) if (!g_groups[i].used) { g = &g_groups[i]; index = i; break; }
    if (!g) { pthread_mutex_unlock(&g_lock); return 0; }
    Group* p = group_of(parent);
    ma_result r = ma_sound_group_init(&g_engine, 0, p ? &p->group : NULL, &g->group);
    if (r != MA_SUCCESS) { g_lastError = r; pthread_mutex_unlock(&g_lock); return 0; }
    g->used = 1;
    g->volume = 1.0f;
    g->muted = 0;
    uint64_t handle = make_handle(index, g->gen);
    pthread_mutex_unlock(&g_lock);
    return handle;
}

EXPORT void wa_group_release(uint64_t group)
{
    pthread_mutex_lock(&g_lock);
    Group* g = group_of(group);
    if (g)
    {
        for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].used && g_voices[i].group == &g->group) { g_voices[i].group = NULL; voice_rebuild_chain(&g_voices[i]); }
        ma_sound_group_uninit(&g->group);
        g->used = 0;
        g->gen++;
    }
    pthread_mutex_unlock(&g_lock);
}

#define WITH_GROUP(body) do { pthread_mutex_lock(&g_lock); Group* g = group_of(group); if (g) { body; } pthread_mutex_unlock(&g_lock); } while (0)
EXPORT void wa_group_set_volume(uint64_t group, float volume) { WITH_GROUP({ g->volume = volume; if (!g->muted) ma_sound_group_set_volume(&g->group, volume); }); }
EXPORT void wa_group_set_mute(uint64_t group, int mute) { WITH_GROUP({ g->muted = mute; ma_sound_group_set_volume(&g->group, mute ? 0.0f : g->volume); }); }
EXPORT void wa_group_set_paused(uint64_t group, int paused) { WITH_GROUP({ if (paused) ma_sound_group_stop(&g->group); else ma_sound_group_start(&g->group); }); }
EXPORT void wa_group_set_pitch(uint64_t group, float pitch) { WITH_GROUP({ ma_sound_group_set_pitch(&g->group, pitch > 0.001f ? pitch : 0.001f); }); }

// ---- the engine ----
// The device's data callback: the engine's mix, as miniaudio's own callback for a device the engine makes (the PC's
// library reads its global engine the same way).
static void device_data(ma_device* device, void* output, const void* input, ma_uint32 frames)
{
    (void)device;
    (void)input;
    ma_engine_read_pcm_frames(&g_engine, output, frames, NULL);
}

// Makes the playback device (at wa_init, and again in the watchdog) with a context of its own, through
// ma_device_init_ex as ma_engine_init did before: AAudio, and OpenSL ES when an AAudio stream does not open, then the
// null backend; or the null backend alone. (A context opened first and the device on it, as the PC's library does,
// would not try OpenSL ES when AAudio's context opens and its stream does not, and wa_init would fail the start.)
// Made again by the watchdog, it leaves the null backend out: the PC's library makes it again on its real context
// only, so a device that cannot be made stays lost and is tried again every second instead of playing nothing.
static ma_result device_make(int again)
{
    ma_backend nullOnly[] = { ma_backend_null };
    ma_backend real[] = { ma_backend_coreaudio };
    const ma_backend* backends = g_nullBackend ? nullOnly : again ? real : NULL;
    ma_uint32 count = g_nullBackend ? 1 : again ? 1 : 0;
    ma_result r = ma_device_init_ex(backends, count, &g_contextConfig, &g_deviceConfig, &g_device);
    if (r != MA_SUCCESS) return r;
    g_deviceInit = 1;
    g_periodFrames = g_device.playback.internalPeriodSizeInFrames;
    return MA_SUCCESS;
}

EXPORT void wa_set_allocator(void* alloc, void* realloc_, void* free_)
{
    g_alloc = (void* (*)(size_t))alloc;
    g_realloc = (void* (*)(void*, size_t))realloc_;
    g_free = (void (*)(void*))free_;
}

// PORT17 心跳：引擎每帧调一次 wa_update（g_updates 计数）。装载期引擎安静（stdout
// 无输出），这行是帧循环死活的唯一信号——15 秒差值为 0 = 帧循环停了（卡在
// Initializing 排查用，配合宿主的主 runloop 心跳区分"主线程忙/引擎循环卡"）。
static void* port17_frame_heartbeat(void* unused)
{
    (void)unused;
    uint64_t last = 0;
    struct timespec ts = { 15, 0 };
    for (;;)
    {
        nanosleep(&ts, NULL);
        uint64_t now = g_updates;  // 引擎帧驱动的更新计数（良性数据竞争，只读）
        LOGI("port17 活性: 引擎帧更新 %llu 次（15 秒内 +%llu）", (unsigned long long)now, (unsigned long long)(now - last));
        last = now;
    }
    return NULL;
}

EXPORT int wa_init(uint32_t sampleRate, uint32_t channels, int nullBackend)
{
    if (g_initialized) return 0;
    // PORT17_AUDIO_LAZY=0: every sound decoded when loaded (the old way, 1.7 GB for the official content);
    // PORT17_AUDIO_CACHE_MB: the budget of the cache of decoded sounds (64); PORT17_AUDIO_DECODE_KB: the largest
    // sound, as samples, that is decoded whole when played (384: four seconds in mono); PORT17_AUDIO_CHECK=1:
    // every quick look at an Ogg stream is compared with what the decoder says.
    const char* e;
    if ((e = getenv("PORT17_AUDIO_LAZY")) && *e) g_lazy = atoi(e);
    if ((e = getenv("PORT17_AUDIO_CACHE_MB")) && *e) g_cacheBudget = (size_t)atoi(e) << 20;
    if ((e = getenv("PORT17_AUDIO_DECODE_KB")) && *e) g_decodeNowBytes = (size_t)atoi(e) << 10;
    if ((e = getenv("PORT17_AUDIO_CHECK")) && *e) g_checkProbe = atoi(e);
    g_allocCallbacks.pUserData = NULL;
    g_allocCallbacks.onMalloc = host_malloc;
    g_allocCallbacks.onRealloc = host_realloc;
    g_allocCallbacks.onFree = host_free;
    // The playback device is made the way ma_engine_init made its own (f32, no pre-silenced buffer, no clipping), at
    // 48 kHz if no rate is given and in stereo if the channels are not 1 to 8, as the PC's library does (the engine
    // passes 48000 and 2). Unlike the PC's library, which since 1.7.4995 opens one context and then the device on it,
    // the device keeps ma_device_init_ex's turn through the backends (see device_make).
    g_nullBackend = nullBackend != 0;
    g_contextConfig = ma_context_config_init();
    g_contextConfig.allocationCallbacks = g_allocCallbacks;
    g_deviceConfig = ma_device_config_init(ma_device_type_playback);
    g_deviceConfig.playback.format = ma_format_f32;
    g_deviceConfig.playback.channels = channels - 1 < 8 ? channels : 2;
    g_deviceConfig.sampleRate = sampleRate ? sampleRate : 48000;
    g_deviceConfig.dataCallback = device_data;
    g_deviceConfig.noPreSilencedOutputBuffer = MA_TRUE;
    g_deviceConfig.noClip = MA_TRUE;
    ma_result r = device_make(0);
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        LOGW("audio: device init failed: %s", ma_result_description(r));
        return (int)r;
    }
    ma_engine_config cfg = ma_engine_config_init();
    cfg.pDevice = &g_device;
    cfg.listenerCount = 1;
    cfg.allocationCallbacks = g_allocCallbacks;
    r = ma_engine_init(&cfg, &g_engine);
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        LOGW("audio: engine init failed: %s", ma_result_description(r));
        ma_device_uninit(&g_device);
        g_deviceInit = 0;
        return (int)r;
    }
    g_rate = ma_engine_get_sample_rate(&g_engine);
    g_channels = ma_engine_get_channels(&g_engine);
    g_masterPaused = 0;
    g_downUpdates = 0;
    g_recoveries = 0;
    g_initialized = 1;
    ma_device* device = ma_engine_get_device(&g_engine);
    LOGI("audio: miniaudio %s on %s, %u Hz, %u channels, period %u frames", ma_version_string(), device ? ma_get_backend_name(device->pContext->backend) : "no device", g_rate, g_channels, device ? device->playback.internalPeriodSizeInFrames : 0);
    // PORT17 心跳：引擎每帧调一次 wa_update（g_updates 计数）。装载期引擎安静（stdout
    // 无输出），这行是帧循环死活的唯一信号——15 秒差值为 0 = 帧循环停了（卡在
    // Initializing 排查用，配合宿主的主 runloop 心跳区分"主线程忙/引擎循环卡"）。
    pthread_t hb;
    if (pthread_create(&hb, NULL, port17_frame_heartbeat, NULL) == 0)
        pthread_detach(hb);
    return 0;
}

EXPORT void wa_shutdown(void)
{
    if (!g_initialized) return;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < MAX_VOICES; i++) voice_release_locked(&g_voices[i]);
    for (int i = 0; i < MAX_GROUPS; i++) if (g_groups[i].used) { ma_sound_group_uninit(&g_groups[i].group); g_groups[i].used = 0; g_groups[i].gen++; }
    ma_engine_uninit(&g_engine); // stops the device, which is not the engine's to uninitialise
    // The device's own context goes with it.
    if (g_deviceInit) { if (ma_device_get_state(&g_device) != ma_device_state_uninitialized) ma_device_uninit(&g_device); g_deviceInit = 0; }
    g_initialized = 0;
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_last_error(void) { return (int)g_lastError; }
EXPORT uint32_t wa_sample_rate(void) { return g_rate; }
EXPORT uint32_t wa_channels(void) { return g_channels; }
// The period of the device as it was made (at wa_init or again in wa_update), as the PC's library keeps it since
// 1.7.4995: it stays readable while the device is lost.
EXPORT uint32_t wa_period_frames(void)
{
    pthread_mutex_lock(&g_lock);
    uint32_t frames = g_initialized ? g_periodFrames : 0;
    pthread_mutex_unlock(&g_lock);
    return frames;
}
EXPORT uint64_t wa_time_frames(void) { return g_initialized ? ma_engine_get_time_in_pcm_frames(&g_engine) : 0; }
EXPORT void wa_set_master_volume(float volume) { if (g_initialized) ma_engine_set_volume(&g_engine, volume); }
// The engine pauses the output while the window has no focus. As in the PC's library, only a change acts, and the
// device watchdog in wa_update leaves a paused device alone.
EXPORT void wa_set_master_paused(int paused)
{
    pthread_mutex_lock(&g_lock);
    if (g_initialized && (paused != 0) != g_masterPaused)
    {
        g_masterPaused = paused != 0;
        if (paused) ma_engine_stop(&g_engine); else ma_engine_start(&g_engine);
    }
    pthread_mutex_unlock(&g_lock);
}
EXPORT void wa_set_listener(float px, float py, float pz, float fx, float fy, float fz, float ux, float uy, float uz)
{
    if (!g_initialized) return;
    pthread_mutex_lock(&g_lock);
    ma_engine_listener_set_position(&g_engine, 0, px, py, pz);
    ma_engine_listener_set_direction(&g_engine, 0, fx, fy, fz);
    ma_engine_listener_set_world_up(&g_engine, 0, ux, uy, uz);
    for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].used && g_voices[i].spatial && g_voices[i].mix) voice_mix_3d(&g_voices[i]);
    pthread_mutex_unlock(&g_lock);
}

EXPORT int wa_playing_count(void)
{
    int n = 0;
    pthread_mutex_lock(&g_lock);
    for (int i = 0; i < MAX_VOICES; i++) if (g_voices[i].used && !g_voices[i].stopped && ma_sound_is_playing(&g_voices[i].snd)) n++;
    pthread_mutex_unlock(&g_lock);
    return n;
}

// The device watchdog of the PC's library (1.7.4995), g_lock held: a device that has stopped without the engine
// pausing the output, or that could not be made again, is acted on at the 60th update it has been down (about a
// second): started again, and when that fails uninitialised and made again with the same configuration at the same
// address (the engine keeps its pointer). Every time it plays again counts as a recovery (wa_device_recoveries).
// On AAudio, miniaudio itself reopens a disconnected stream (a route change); when that fails it stops the device
// with its stream closed, which only making the device again brings back.
static void device_watchdog_locked(void)
{
    static int failuresLogged;
    if (g_masterPaused || (g_deviceInit && ma_device_get_state(&g_device) != ma_device_state_stopped))
    {
        g_downUpdates = 0;
        return;
    }
    if (++g_downUpdates <= 59) return;
    g_downUpdates = 0;
    if (g_deviceInit)
    {
        ma_result started = ma_device_start(&g_device);
        if (started == MA_SUCCESS)
        {
            g_recoveries++;
            failuresLogged = 0;
            LOGI("audio: output device started again (%d)", g_recoveries);
            return;
        }
        if (failuresLogged++ < 3) LOGW("audio: output device does not start (%s), making it again", ma_result_description(started));
        if (ma_device_get_state(&g_device) != ma_device_state_uninitialized) ma_device_uninit(&g_device);
        g_deviceInit = 0;
    }
    ma_result r = device_make(1);
    if (r != MA_SUCCESS)
    {
        g_lastError = r;
        // Tried again every second while it lasts: only the first few are logged.
        if (failuresLogged++ < 3) LOGW("audio: output device lost, cannot make it again: %s", ma_result_description(r));
        return;
    }
    if (ma_device_start(&g_device) == MA_SUCCESS)
    {
        g_recoveries++;
        failuresLogged = 0;
        LOGI("audio: output device made again on %s, period %u frames (%d)", ma_get_backend_name(g_device.pContext->backend), g_periodFrames, g_recoveries);
    }
}

// How many times the watchdog brought the output back, and whether it is lost (no device could be made); the engine's
// MiniAudioBackend.Update reads both after wa_update every frame since 1.7.4995 (and logs "Audio output device
// restarted." and "Audio output device lost.").
EXPORT int wa_device_recoveries(void)
{
    pthread_mutex_lock(&g_lock);
    int n = g_recoveries;
    pthread_mutex_unlock(&g_lock);
    return n;
}
EXPORT int wa_device_lost(void)
{
    pthread_mutex_lock(&g_lock);
    int lost = g_initialized && !g_deviceInit;
    pthread_mutex_unlock(&g_lock);
    return lost;
}

// Once per frame: the device watchdog, then voices that have stopped or ended on their own are released.
EXPORT void wa_update(void)
{
    if (!g_initialized) return;
    pthread_mutex_lock(&g_lock);
    device_watchdog_locked();
    int playing = 0, used = 0;
    for (int i = 0; i < MAX_VOICES; i++)
    {
        Voice* v = &g_voices[i];
        if (v->used && (v->stopped || (v->autoRelease && v->ended))) voice_release_locked(v);
        if (v->used) { used++; if (ma_sound_is_playing(&v->snd)) playing++; }
    }
    if (++g_updates % 600 == 0)
    {
        LOGI("audio: %d voices playing of %d live, %llu created, engine time %llu frames", playing, used, (unsigned long long)g_voicesCreated, (unsigned long long)ma_engine_get_time_in_pcm_frames(&g_engine));
        if (g_lazy) LOGI("audio: %llu sounds kept compressed (%llu of them as copies); decoded on play %llu times, %.1f MiB in %.0f ms (slowest %.1f ms), %llu played as streams; cache %.1f of %.0f MiB, %llu dropped from it%s",
            (unsigned long long)g_lazyLoaded, (unsigned long long)g_lazyCopied, (unsigned long long)g_decodes, g_decodedBytes / 1048576.0, g_decodeMicros / 1000.0, g_slowestDecodeMicros / 1000.0,
            (unsigned long long)g_streamedLazy, g_cacheBytes / 1048576.0, g_cacheBudget / 1048576.0, (unsigned long long)g_dropped, g_checkProbe ? "; lengths checked against the decoder" : "");
    }
    pthread_mutex_unlock(&g_lock);
}
