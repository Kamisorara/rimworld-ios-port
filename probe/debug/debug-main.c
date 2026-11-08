// macos-debug 用：把 host.c 编成 mac 命令行程序，在 CI 的 macOS runner 上
// 直接跑探针——那里 stderr 全可见，可以快速迭代宿主调用逻辑。
// 用法：macprobe <bundle_dir>
#include "host.h"
#include <stdio.h>

static void sink(const char *msg) { printf("%s\n", msg); }

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: macprobe <bundle_dir>\n");
        return 2;
    }
    host_set_log_sink(sink);
    return run_probe(argv[1]);
}
