#include "status.h"

#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

// 读写都在这一把锁后面：写方（engine/setup/main 的任意线程、stdout 泵）与读方
// （启动器主线程刷新卡片）并发时，保证整文件替换的原子序——tmp + rename 在锁内，
// 读方永远看到完整的一版。
static pthread_mutex_t g_status_lock = PTHREAD_MUTEX_INITIALIZER;

static void status_path(char *out, size_t cap, const char *home)
{
    snprintf(out, cap, "%s/Documents/port17-status.txt", home);
}

void port17_status_set(const char *home, const char *key, const char *value)
{
    if (home == NULL || key == NULL || value == NULL)
        return;
    char path[600], tmp[640];
    status_path(path, sizeof path, home);
    snprintf(tmp, sizeof tmp, "%s.tmp", path);

    pthread_mutex_lock(&g_status_lock);
    // 读入既有行：同名键就地替换，其余原样保留（值里不带换行——调用方保证）
    FILE *in = fopen(path, "r");
    FILE *out = fopen(tmp, "w");
    if (out != NULL)
    {
        size_t klen = strlen(key);
        int replaced = 0;
        if (in != NULL)
        {
            char line[512];
            while (fgets(line, sizeof line, in) != NULL)
            {
                if (strncmp(line, key, klen) == 0 && line[klen] == '=')
                {
                    fprintf(out, "%s=%s\n", key, value);
                    replaced = 1;
                }
                else
                    fputs(line, out);
            }
            fclose(in);
        }
        if (!replaced)
            fprintf(out, "%s=%s\n", key, value);
        fclose(out);
        if (rename(tmp, path) != 0)
        {
            // Windows CRT 的 rename 不覆盖既有目标（EEXIST）；Darwin 静默替换走不到这。
            // 删旧再试（窗口极短，状态文件非关键账本，最坏丢一拍由下次 set 补上）。
            unlink(path);
            if (rename(tmp, path) != 0)
                unlink(tmp); // 半写的 tmp 不留尸（原文件未动，下次再试）
        }
    }
    else if (in != NULL)
        fclose(in);
    pthread_mutex_unlock(&g_status_lock);
}

int port17_status_get(const char *home, const char *key, char *buf, size_t cap)
{
    buf[0] = '\0';
    if (home == NULL || key == NULL)
        return 0;
    char path[600];
    status_path(path, sizeof path, home);
    pthread_mutex_lock(&g_status_lock);
    FILE *in = fopen(path, "r");
    int found = 0;
    if (in != NULL)
    {
        size_t klen = strlen(key);
        char line[512];
        while (fgets(line, sizeof line, in) != NULL)
        {
            size_t n = strlen(line);
            if (n > 0 && line[n - 1] == '\n')
                line[n - 1] = '\0';
            if (strncmp(line, key, klen) == 0 && line[klen] == '=')
            {
                // 剥掉行尾换行（连同 \r——Windows 文本模式产物；Darwin 上只有 \n）
                size_t n2 = strlen(line);
                while (n2 > 0 && (line[n2 - 1] == '\n' || line[n2 - 1] == '\r'))
                    line[--n2] = '\0';
                snprintf(buf, cap, "%s", line + klen + 1);
                found = 1; // 不 break：最后一行生效
            }
        }
        fclose(in);
    }
    pthread_mutex_unlock(&g_status_lock);
    return found;
}
