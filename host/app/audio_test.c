// The M2 audio proof: waylithaudio (the engine's miniaudio wrapper, libwaylithaudio.dylib) on the
// CoreAudio backend — the engine's own API surface (wa_*), nothing private. Makes the device at
// 48 kHz stereo, plays two seconds of sine (440 Hz then 880 Hz) as a PCM sound through a voice with
// auto-release, and watches the play position advance; wa_update runs the device watchdog as the
// engine's own update loop does.
#include <math.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

void host_log(const char *msg);

// The engine's P/Invoke surface (the signatures from the Android stub list, host/stubs there).
extern int wa_init(uint32_t sampleRate, uint32_t channels, int nullBackend);
extern int wa_last_error(void);
extern uint32_t wa_sample_rate(void);
extern uint32_t wa_channels(void);
extern uint32_t wa_period_frames(void);
extern void wa_update(void);
extern void wa_shutdown(void);
extern uint64_t wa_sound_load_pcm(void *data, uint64_t frames, uint32_t channels, uint32_t rate, int format);
extern void wa_sound_release(uint64_t sound);
extern uint64_t wa_sound_length_frames(uint64_t sound);
extern uint64_t wa_voice_play(uint64_t sound, uint64_t group, int startPaused, int autoRelease);
extern int wa_voice_is_playing(uint64_t voice);
extern uint64_t wa_voice_get_position_frames(uint64_t voice);
extern uint64_t wa_time_frames(void);

static void logf_(const char *fmt, ...)
{
    char text[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    host_log(text);
}

int audio_test(void)
{
    logf_("音频：wa_init（CoreAudio 后端，48kHz 立体声）");
    int r = wa_init(48000, 2, 0);
    if (r != 0)
    {
        logf_("音频：wa_init 失败（返回 %d，wa_last_error=%d）", r, wa_last_error());
        return 1;
    }
    logf_("音频：设备就绪，%u Hz，%u 声道，period %u 帧", wa_sample_rate(), wa_channels(), wa_period_frames());

    // Two seconds of s16 stereo PCM: A4 for the first second, A5 for the second.
    const uint64_t frames = 96000;
    int16_t *pcm = malloc(frames * 2 * sizeof(int16_t));
    if (!pcm) { logf_("音频：PCM 缓冲分配失败"); wa_shutdown(); return 2; }
    for (uint64_t i = 0; i < frames; i++)
    {
        double freq = i < frames / 2 ? 440.0 : 880.0;
        int16_t sample = (int16_t)(sinf(2.0 * M_PI * freq * (double)i / 48000.0) * 0.25 * 32767.0);
        pcm[i * 2] = sample;
        pcm[i * 2 + 1] = sample;
    }
    uint64_t sound = wa_sound_load_pcm(pcm, frames, 2, 48000, 1);
    free(pcm);
    if (!sound) { logf_("音频：wa_sound_load_pcm 失败（wa_last_error=%d）", wa_last_error()); wa_shutdown(); return 3; }
    logf_("音频：PCM 声音就绪，%llu 帧", (unsigned long long)wa_sound_length_frames(sound));

    uint64_t voice = wa_voice_play(sound, 0, 0, 1); // group 0 = the endpoint, auto-release
    if (!voice) { logf_("音频：wa_voice_play 失败"); wa_sound_release(sound); wa_shutdown(); return 4; }
    logf_("音频：voice %llu 开播（应先 440Hz 后 880Hz，共 2 秒）", (unsigned long long)voice);

    uint64_t last = 0;
    int stalls = 0;
    for (int step = 0; step < 10; step++)
    {
        usleep(250 * 1000);
        wa_update(); // the engine's own beat: the device watchdog runs here
        uint64_t at = wa_voice_get_position_frames(voice);
        int playing = wa_voice_is_playing(voice);
        logf_("音频：%d/10 位置 %llu 帧（时间轴 %llu），%s",
              step + 1, (unsigned long long)at, (unsigned long long)wa_time_frames(),
              playing ? "播放中" : "已结束");
        if (playing && at <= last && at != 0) stalls++;
        last = at;
    }
    wa_sound_release(sound);
    wa_shutdown();
    if (stalls > 2)
    {
        logf_("音频：播放位置多次不前进（%d 次）——设备可能没真正出声", stalls);
        return 5;
    }
    logf_("音频：自检完成（听不到声音但位置推进正常 = 出声设备静音/音量为零，看日志即可判断）");
    return 0;
}
