# RimWorld iOS 30fps 根因分析与修复计划

> 状态：0.4.28 E0 与 `swapchainImages=3` 真机裁决通过，H1 成立；0.4.29 已把引擎默认改为 3 图。基于 0.4.27 /
> `test_work/m4-touch-30` 的真机日志与当前反编译树。
>
> 目标：把稳定 30fps 提到稳定 60fps；如果 60fps 不可达，必须用数据证明上限来自 GPU 帧时间，而不是 present/drawable 等待。

## 0.4.28 实施状态与真机裁决（H1 成立）

待测包：`test_work/m4-touch-31/port17host-m2.ipa`（run 45499640519，提交 `a4e8951`）。
IPA 哈希、关键 DLL 哈希和真机观测点见 `docs/PLAN.md` 的 0.4.28 包落位。

0.4.28 在 `libvulkan.dylib` 里包装 `vkGetInstanceProcAddr` / `vkGetDeviceProcAddr`，对
`vkExportMetalObjectsEXT`、`vkQueueSubmit`、`vkQueuePresentKHR`、`vkAcquireNextImageKHR`、
`vkWaitForFences`、`vkCreateSwapchainKHR`、`vkGetSwapchainImagesKHR` 分开计时，每 5 秒输出
median/99%/max、返回码分布和实际图像数。它同时记录 surface caps 与 present modes。默认
`PORT17_SWAPCHAIN_IMAGES` 为空，完全不改行为；`Documents/game/port17-perf.txt` 写
`swapchainImages=3` 后，下一次冷启动才把同一个 surface 的 `minImageCount` 抬到 3，并且
只在 caps 允许时生效。三角形支持 `triangleImages=2|3`，并单独输出 submit/present 的
300 帧聚合。MVK 诊断开关也由同一个文件设置，但默认关闭。

`test_work/m4-touch-31` 裁决：caps 为 `minImages=2 maxImages=3`，`min=2 request=3 forced`
真实生效，MoltenVK 创建 3 张 2360x1640 图像。稳定窗口 59.8–60.1fps，15 秒活性 +899；
interval median 16.7–16.8ms，throttle/backlog 接近 0。Vulkan 分段显示等待主要落在
`exportDrawable`（多窗 median 2.46–14.51ms，99% 可到 16–19ms），`queueSubmit`
median 0.13–0.82ms，`queuePresent` median 约 0.01ms。这证明 33ms 是 2 图 swapchain 的
`nextDrawable` 等待，不是 GPU 帧时间或 present 队列本身。

## 0.4.29 固化 3 图

interposer 对 Waylith 引擎实例默认请求 3 张图；caps 不允许时不改。显式
`PORT17_SWAPCHAIN_IMAGES=0` 关闭，`=2` 回 2 图对照。`port17-triangle` 应用实例不吃该默认，
继续用自身 `triangleImages=2|3` 控制。文件不存在即默认 3；要关闭或对照时才需要写
`port17-perf.txt`。

## 0. 一句话结论

当前 30fps 不是 CPU，也不是 display link，而是 **present/drawable 侧的等待**。

最强假设是：引擎在 FIFO 呈现模式下只向 MoltenVK 申请了 **2 张 swapchain 图像**；MoltenVK 1.4.2 在 iOS 上把
`[CAMetalLayer nextDrawable]` 推迟到提交/导出阶段。2 张图时，compositor 占一张、渲染队列占一张，第三帧必须等
2 个 60Hz vsync，于是每帧稳定 33.3ms。参考项目 `MHGURecomp-iOS` 在同一套 MoltenVK 上明确使用 3 张图
（`max(3, minImageCount + 1)`）并稳定 60fps。

但当前日志里的 `submit 33.1ms` 不能直接等同于 `vkQueueSubmit + vkQueuePresentKHR`：
`TrySubmitFrame` 在 `vkQueueSubmit` 之前还调用了 `AcquireMetalDrawable()`，而设备启用了
`VK_EXT_metal_objects`，所以 `vkExportMetalObjectsEXT` 也会强制取 drawable。第一步必须把
`vkExportMetalObjectsEXT`、`vkQueueSubmit`、`vkQueuePresentKHR`、`vkWaitForFences` 分开计时，
再决定修哪一段。

## 1. 已确认的证据

### 1.1 真机日志（0.4.27，`test_work/m4-touch-30`）

| 证据 | 位置 | 含义 |
|---|---|---|
| `Created 2 swapchain images with size (2360, 1640) and contents scale 2.0 in layer WaylithUIView` | `coreclr-stderr.log:206` | 引擎实际只拿到 2 张 swapchain 图；这是最关键的一条 |
| `render 33.3 ms (acquire 0.0, record 0.1, submit 33.1)` | `coreclr-stderr.log:347` | 每帧 33.3ms，正好 2 个 60Hz vsync；acquire 和 record 几乎不花时间 |
| `render 32.9 ms (acquire 0.0, record 0.0, submit 32.8)` | `coreclr-stderr.log:346` | 启动后第一个稳态窗口同样是 33ms |
| `interval median 33.3 ms, 99% 33.9, longest 34` | `coreclr-stderr.log:347` | 不是偶发抖动，是稳定 30fps |
| `work median 0.8, 99% 1.8` | `coreclr-stderr.log:347` | 主线程每帧只有 0.8ms 左右，CPU 不是瓶颈 |
| `throttle median 32.3, backlog median 32.2` | `coreclr-stderr.log:347` | 主线程几乎全部时间在等渲染线程 backlog |
| `cap 60 fps from monitor refresh (game target -1, present mode FifoKHR, safety net while vsync paces); pacing False` | `coreclr-stderr.log:347` | 引擎自己的 present pacing 已关，FIFO vsync 在限速 |
| `port17 活性: 引擎帧更新 1613 次（15 秒内 +450）` | `coreclr-stderr.log:396` | 450/15s = 30.0fps，与帧时间一致 |
| `[mobile] settings: ... renderHeight=0` | `coreclr-stderr.log:258` | 当前明确是原生 2360x1640，不是降分辨率后的结果 |
| `[mobile] resolution: IOSWindow.HandleLayout patched` | `coreclr-stderr.log:311` | 分辨率补丁已装上，但因为 `renderHeight=0` 没有实际缩放 |
| `MoltenVK instance version 1.4.357` | `port17-ui.log:30` | MoltenVK 1.4.2，Vulkan 1.4.357 |
| `VK_EXT_metal_objects v2` | `coreclr-stderr.log:201` | 设备启用了 metal objects，`exportMetalDrawables` 为 true |

### 1.2 引擎代码（`build/decomp/Waylith.decompiled.cs`）

`WindowRenderContext.CreateSwapchain` 的调用点把 `requestedImageCount` 写死为 2：

```csharp
// build/decomp/Waylith.decompiled.cs:62450
swapchain = CreateSwapchain(Device, PhysicalDevice, Graphics.Family, queue.Family,
    presentMode, 2, surface, oldSwapchain);
```

`CreateSwapchain` 内部的图像数计算：

```csharp
// build/decomp/Waylith.decompiled.cs:62393
int num = System.Math.Max(
    (vkPresentModeKHR == VkPresentModeKHR.MailboxKHR)
        ? System.Math.Max(requestedImageCount, 3)
        : requestedImageCount,
    vkSurfaceCapabilitiesKHR.MinImageCount);
```

也就是说：只有 MAILBOX 才会被抬到至少 3 张；iOS 上 MoltenVK 只给 FIFO，所以保持 2 张。

`EngineApplicationOptions.FramesInFlight` 默认也是 2：

```csharp
// build/decomp/Waylith.decompiled.cs:7270
public int FramesInFlight { get; init; } = 2;
```

`TrySubmitFrame` 的顺序：

```csharp
// build/decomp/Waylith.decompiled.cs:62704
AcquireMetalDrawable();
// ...
// build/decomp/Waylith.decompiled.cs:62709
VkResult vkResult = graphicsDevice.SubmitGraphicsAndPresent(
    waitSemaphores, waitStages, new Span<CommandBuffer>(ref reference),
    signalSemaphores, SyncObjects.Fence, PresentFrame, ...);
```

`AcquireMetalDrawable` 在 `exportMetalDrawables` 为 true 时会调用：

```csharp
// build/decomp/Waylith.decompiled.cs:62779
Device.ExportMetalObjectsEXT(ref pMetalObjectsInfo);
```

而 `exportMetalDrawables` 的来源是：

```csharp
// build/decomp/Waylith.decompiled.cs:62312
exportMetalDrawables = graphicsDevice.DeviceEnabledExtensions.Contains("VK_EXT_metal_objects");
```

日志确认 `VK_EXT_metal_objects v2` 已启用，所以这条路径是活的。

`TryAcquireNextFrame` 在 acquire 之前会等当前 sync object 的 fence：

```csharp
// build/decomp/Waylith.decompiled.cs:60942
Device.WaitForFences(new ReadOnlySpan<VkFence>((VkFence)fence), true, -1);
VkResult nextFrameIndex = GetNextFrameIndex(out FrameIndex);
```

当前 `acquire 0.0ms` 说明这个 fence 已经 signal，且 `vkAcquireNextImageKHR` 立即返回；它不能证明当前帧的 GPU 工作很快，只能证明两帧前的 fence 已完成。

`PresentFrame` 最终调用 `queue.PresentKHR`：

```csharp
// build/decomp/Waylith.decompiled.cs:62679
return queue.PresentKHR(inPresentInfo);
```

### 1.3 MoltenVK 的行为

MoltenVK 1.4.2 的 `vkAcquireNextImageKHR` **不会**调用 `[CAMetalLayer nextDrawable]`；它只从自己的可用性跟踪里挑一张图并 signal semaphore/fence。真正的 `nextDrawable` 被推迟到
`MVKPresentableSwapchainImage::getCAMetalDrawable()`，发生在编码或提交阶段。MoltenVK 上游 issue #2815
明确记录了这个行为，并给出 iOS 上的实测：`vkQueueSubmit` 约 10ms、`vkAcquireNextImageKHR` 约 0.05ms，
等待被错误地记在 submit 上。

参考：

- <https://github.com/KhronosGroup/MoltenVK/issues/2815>
- <https://skia.googlesource.com/external/github.com/KhronosGroup/MoltenVK/+/HEAD/Docs/MoltenVK_Runtime_UserGuide.md>

MoltenVK Runtime User Guide 的 Swapchains 章节明确建议：

> MoltenVK supports using either 2 or 3 swapchain images. For best performance, it is recommended
> that you use 3 swapchain images (triple-buffering), to ensure that at least one swapchain image
> will be available when you need to render to it.

Khronos Vulkan Samples 的 “Choosing the right number of swapchain images” 也说明：双缓冲在错过一次
vsync 后会把帧率锁到 30fps，三缓冲则始终有一张已处理完、可立即提交的图。

- <https://docs.vulkan.org/samples/latest/samples/performance/swapchain_images/README.html>

### 1.4 参考项目 `MHGURecomp-iOS`

`runtime/nvn/vulkan/device.c` 的 swapchain 图像数：

```c
// runtime/nvn/vulkan/device.c:369
uint32_t count = caps.minImageCount + 1 < 3 ? 3 : caps.minImageCount + 1;
if (caps.maxImageCount && count > caps.maxImageCount) count = caps.maxImageCount;
```

它还使用 `FRAMES 3`、`vkAcquireNextImageKHR(..., UINT64_MAX, ...)`、`vkQueueSubmit` 后再
`vkQueuePresentKHR`，并有独立的 vsync 线程按 `max(60, target)` 节拍 signal 事件。即使 iOS 上
`present_mode()` 因为只有 FIFO 而回退，它仍然是 3 张图 + 3 frames in flight。

当前仓库的三角形测试也用了 3 张图：

```c
// host/app/triangle.c:351
g_imageCount = caps.minImageCount + 1;
```

三角形在 2360x1640、FIFO 下实测 60.5fps、1200+ 帧 0 失败。这说明同一台设备、同一层、同一
MoltenVK、同一 FIFO 路径，在 GPU 负载极低时可以 60fps；它不能单独证明 2 张图是唯一原因，但把
“3 张图 + FIFO 可行”这件事钉死了。

## 2. 当前调用链与计时缺口

当前 `FrameTime` 的分段是：

```text
RenderThread.RenderFrame
  ├─ TryAcquireNextFrame
  │    ├─ vkWaitForFences          ← 当前 0.0ms
  │    └─ vkAcquireNextImageKHR    ← 当前 0.0ms
  ├─ RecordFrame                   ← 当前 0.1–2.6ms
  └─ TrySubmitFrame                ← 当前 32–33ms
       ├─ AcquireMetalDrawable
       │    └─ vkExportMetalObjectsEXT   ← 可能在这里等 nextDrawable
       ├─ vkQueueSubmit                  ← 也可能在这里等 nextDrawable
       └─ vkQueuePresentKHR              ← 等 GPU 完成后 present
```

`FrameTime` 只把 `TrySubmitFrame` 整体记为 `submit`，所以现在的 `submit 33.1ms` 至少混合了三件事：

1. `vkExportMetalObjectsEXT` 强制取 drawable 的等待；
2. `vkQueueSubmit` 内部的 `nextDrawable` 等待；
3. `vkQueuePresentKHR` 等待当前帧 GPU 完成 / compositor。

在拆开这三段之前，任何“GPU 慢”或“present 慢”的结论都只是猜测。第一步必须只加计时，不改行为。

## 3. 假设列表与优先级

| 编号 | 假设 | 优先级 | 支持证据 | 反证/待验证 |
|---|---|---|---|---|
| H1 | 2 张 swapchain 图导致 `nextDrawable` 等 2 个 vsync | 高 | 日志明确 2 张；MoltenVK 文档建议 3 张；参考项目 3 张 60fps；三角形 3 张 60.5fps | 3 张图实验后仍 30fps |
| H2 | GPU 帧时间本身 >16.7ms（2360x1640 的 UI/overdraw） | 中高 | `submit` 可能包含 `vkQueuePresentKHR` 等 GPU | 1080p 仍 30fps；GPU timestamp <16.7ms |
| H3 | present/compositor/layer 配置导致额外等待 | 中 | iOS 只有 FIFO；Direct to Display 可能持有图像更久 | 3 张图 + 正确 layer 配置后改善 |
| H4 | MoltenVK 同步提交/预填配置把等待记到不同调用 | 低中 | `MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS`、`PREFILL_METAL_COMMAND_BUFFERS` 会改变等待位置 | 只改变归属，不改变总时长 |
| H5 | 主线程 pacing/backlog 叠加 | 低 | `targetFrameRate=-1` 后 `pacing False`，但 `WaitForBacklogAtMost(1)` 仍会等渲染线程 | 渲染线程 16.7ms 后 backlog 自然消失 |
| H6 | display link / ProMotion / 120Hz | 低 | `FrameCallbacksEnabled` 从未置真，display link 一直 paused | 30fps 与 120Hz 无关 |
| H7 | Vulkan fence/semaphore 使用不当 | 低 | `acquire 0.0ms` 说明 fence 已 signal；`vkQueueSubmit` 的 wait semaphore 是标准用法 | 3 张图后仍异常 |
| H8 | swapchain 每帧 suboptimal/重建 | 低 | 当前日志没有 rebuild 风暴，但没打印 present/acquire 返回码 | 加日志后排除 |
| H9 | CPU 录命令/shader 编译 | 低 | `record` 只有 0.1–2.6ms | 稳态 30fps 与 record 无关 |
| H10 | 调试器附加开销 | 低 | 当前日志是 `CS_DEBUGGED=1` 的 StikDebug 会话 | 无调试器复测可能略好，但不会从 30 变 60 |

## 4. 实验矩阵

### 4.1 2x2 主实验

|  | 2 张图 | 3 张图 |
|---|---|---|
| 2360x1640 | 当前：30fps | 待测 |
| 1080p | 待测 | 待测 |

判定：

- 3 张图 + 2360x1640 达到 60fps：H1 成立，GPU 帧时间 <16.7ms，直接做 3 图修复。
- 2 张图 + 1080p 达到 60fps：H2 成立，GPU 帧时间在原生分辨率下 >16.7ms，需要降分辨率/优化 GPU。
- 3 张图 + 2360x1640 仍 30fps，但 1080p + 3 张图达到 60fps：GPU 帧时间介于 16.7–33ms，3 图不够，仍需降负载。
- 3 张图 + 1080p 仍 30fps：H3/H4/H5，转向 present/layer/pacing 排查。

### 4.2 实验清单

| 编号 | 实验 | 改动 | 观测 | 目的 |
|---|---|---|---|---|
| E0 | 只加计时/日志 | 无行为变化 | `vkExportMetalObjectsEXT`、`vkQueueSubmit`、`vkQueuePresentKHR`、`vkWaitForFences`、`vkAcquireNextImageKHR` 的 median/99%/max；swapchain create info/caps/images | 找出 33ms 的归属 |
| E1 | 三角形 2 vs 3 张图 | `PORT17_TRIANGLE_IMAGES=2|3` | `vkQueuePresentKHR` 时间、fps | 在无游戏负载下复现 2 图 30fps、3 图 60fps |
| E2 | 引擎强制 3 张图 | interposer 把 `minImageCount` 抬到 3 | `interval`、`submit`、`backlog`、`port17 活性` | H1 的直接 A/B |
| E3 | 2x2 分辨率/图像数 | `renderHeight=1080` × 2/3 图 | fps、GPU 时间 | 分离 GPU 负载与 present 等待 |
| E4 | MoltenVK 性能跟踪 | `MVK_CONFIG_PERFORMANCE_TRACKING=1` | drawable 等待、GPU 执行时间 | 不改代码也能看 GPU vs present |
| E5 | 同步/预填配置 | `MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS=0`、`MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS=1` 分别单独开 | 等待从哪个调用迁移 | 只作诊断，不作最终修复 |
| E6 | Metal GPU trace | Xcode/Metal System Trace 或 `MVK_CONFIG_AUTO_GPU_CAPTURE_SCOPE` | `nextDrawable` 等待 vs GPU 执行 | 金标准 |
| E7 | 参考项目对照 | MHGU 同设备运行 | swapchain images、present mode、fps | 确认 3 图 + FIFO 可 60 |
| E8 | swapchain 重建检查 | 记录 `vkAcquireNextImageKHR`/`vkQueuePresentKHR` 返回码 | `VK_SUBOPTIMAL_KHR`/`OUT_OF_DATE` 次数 | 排除重建风暴 |
| E9 | 无调试器复测 | 不用 StikDebug 拉起 | fps | 排除 H10 |

## 5. 分阶段执行计划

### Phase 0：只加观测，不改行为（最高优先级）

目标：把 `TrySubmitFrame` 的 33ms 拆成可解释的四段。

改动：

1. `host/vulkan/vulkan_ios.c`
   - 包装 `vkGetInstanceProcAddr`：当 `name == "vkGetDeviceProcAddr"` 时返回自己的
     `hook_vkGetDeviceProcAddr`，其余转发给 MoltenVK。
   - `hook_vkGetDeviceProcAddr` 对以下函数返回 hook：
     `vkExportMetalObjectsEXT`、`vkQueueSubmit`、`vkQueuePresentKHR`、
     `vkAcquireNextImageKHR`、`vkWaitForFences`、`vkCreateSwapchainKHR`、
     `vkGetSwapchainImagesKHR`。
   - 每个 hook 用 `clock_gettime(CLOCK_MONOTONIC)` 计时，按 60 帧聚合，输出
     median/99%/max 和调用次数；不要每帧打印。
   - `vkCreateSwapchainKHR` 打印：`minImageCount`、`imageExtent`、`imageFormat`、
     `presentMode`、`imageUsage`、`compositeAlpha`、`preTransform`、`clipped`、
     `oldSwapchain != 0`；同时打印 `VkSurfaceCapabilitiesKHR` 的
     `minImageCount`/`maxImageCount`/`currentExtent`/`supportedUsageFlags`。
   - `vkGetSwapchainImagesKHR` 打印实际图像数。
   - `vkQueuePresentKHR`/`vkAcquireNextImageKHR` 打印返回码分布，尤其
     `VK_SUBOPTIMAL_KHR`。
2. `mobile/FrameTime.cs`
   - 在 `Report` 里增加 `exportDrawable`、`queueSubmit`、`queuePresent`、
     `waitFences`、`swapchainImages` 字段，直接读 interposer 的聚合结果；
     或者让 interposer 自己每 5 秒打印一行 `[vulkan] frame timing: ...`，
     与 `[mobile] frame:` 的时间窗对齐。
3. `host/app/main.m`
   - 增加 `MVK_CONFIG_PERFORMANCE_TRACKING` 等实验开关的 setenv 入口，默认关闭；
     只在 A/B 包里打开。

验收：

- 日志能明确回答：33ms 主要在 `vkExportMetalObjectsEXT`、`vkQueueSubmit` 还是
  `vkQueuePresentKHR`。
- 日志能明确回答：实际 swapchain 图像是 2 还是 3，`caps.maxImageCount` 是否允许 3。
- 不改变任何渲染行为；`port17 活性` 仍是约 +450/15s。

### Phase 1：2 vs 3 张图的 A/B

目标：直接验证 H1。

实验 A：interposer 强制 3 张图

- 在 `hook_vkCreateSwapchainKHR` 里复制 `VkSwapchainCreateInfoKHR`，若
  `minImageCount < 3` 则改成 3；记录修改前后的值。
- 必须先检查 `caps.maxImageCount`：如果 `maxImageCount > 0 && maxImageCount < 3`，
  不强制，打印 `cannot force 3 images (max=N)`。
- 只对引擎的 swapchain 做，不要影响 ImGui overlay 或其它 swapchain；可以用
  `pInfo->surface`/`imageExtent`/`oldSwapchain` 或调用栈特征做 guard。
- 先用环境变量 `PORT17_SWAPCHAIN_IMAGES=3` 控制，默认 2；确认修复后再决定是否默认开。

实验 B：三角形 2 vs 3 张图

- 给 `host/app/triangle.c` 加 `PORT17_TRIANGLE_IMAGES=2|3`：
  - 3：保持现在的 `caps.minImageCount + 1`；
  - 2：强制 `minImageCount = 2`（如果 caps 允许）。
- 在 `vkQueuePresentKHR` 前后计时，每 300 帧打印 median/99%。
- 预期：2 张图在 2360x1640 FIFO 下出现约 33ms 的 present 等待；3 张图回到
  约 16.7ms 或更低。若三角形 2 张图仍 60fps，说明三角形的 GPU 负载太低，
  不能复现，需要以引擎 A/B 为准。

实验 C：引擎 2 vs 3 张图

- 同一存档、同一场景、同一分辨率、冷启动后等 30 秒再采样。
- 对比：
  - `interval median`
  - `render` / `exportDrawable` / `queueSubmit` / `queuePresent`
  - `throttle` / `backlog`
  - `port17 活性`（目标约 +900/15s）
- 如果 3 张图达到 60fps，H1 成立；如果只从 30 提到 40–50，说明 GPU 帧时间
  仍 >16.7ms，进入 Phase 2。

### Phase 2：GPU 负载 A/B

目标：判断 GPU 帧时间是否 >16.7ms。

1. 在 `Documents/game/mobile.txt` 写 `renderHeight=1080`，冷启动。
2. 确认日志出现：
   - `[mobile] resolution: the engine's layer scaled 2 -> 1.317 (1080p)`
   - `[mobile] resolution: rendering at 1552x1080`
   - MoltenVK 的 `Created N swapchain images with size (1552, 1080)`
3. 对比 1080p 与 2360x1640 的：
   - `interval median`
   - `render` / `submit` / `queuePresent`
   - `port17 活性`
4. 如果 1080p 明显改善，说明 GPU 填充/UI overdraw 是主因；继续测 720p，
   找到 60fps 的分辨率上限。
5. 用 `MVK_CONFIG_PERFORMANCE_TRACKING=1` 或 Metal GPU trace 测 GPU 时间：
   - GPU <16.7ms 但 present 33ms：H1/H3。
   - GPU >16.7ms：H2，进入渲染优化。

### Phase 3：present/compositor 排查

只在 Phase 1/2 没有解释时做。

1. 记录 `CAMetalLayer` 的实际属性：
   - `maximumDrawableCount`
   - `drawableSize`
   - `contentsScale`
   - `presentsWithTransaction`
   - `displaySyncEnabled`
   - `framebufferOnly`
   - `pixelFormat`
2. 记录 `vkGetPhysicalDeviceSurfacePresentModesKHR` 的完整列表；确认 iOS 是否真的
   只有 FIFO。如果存在 `VK_PRESENT_MODE_FIFO_RELAXED_KHR` 或 `IMMEDIATE`，单独 A/B。
3. 分别单独测试：
   - `MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS=0`
   - `MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS=1`
   - 关闭 `VK_EXT_metal_objects`（让 `exportMetalDrawables=false`），观察等待是否
     从 `vkExportMetalObjectsEXT` 迁移到 `vkQueueSubmit`
4. 检查 swapchain 重建：
   - `vkAcquireNextImageKHR` 返回 `VK_SUBOPTIMAL_KHR` 的频率
   - `vkQueuePresentKHR` 返回 `VK_SUBOPTIMAL_KHR` 的频率
   - `RebuildCount` / `LastRebuildDescription`
5. 如果等待始终在 `nextDrawable`，考虑：
   - 3 张图（首选）
   - 在独立线程提前 `nextDrawable`（复杂，最后再试）
   - 离屏渲染 + present 时 blit（大改，最后再试）

### Phase 4：修复落地

如果 H1 成立：

1. 先用 interposer 强制 3 张图，作为最小侵入修复；确认 60fps 后再决定是否把
   逻辑移到引擎 Harmony patch。
2. 永久方案优先级：
   - 方案 A：interposer 在 `vkCreateSwapchainKHR` 把 `minImageCount` 抬到 3，
     并在日志里打印实际图像数。优点：不依赖引擎版本；缺点：全局生效，需要
     guard 住非引擎 swapchain。
   - 方案 B：Harmony patch `Waylith.Graphics.WindowRenderContext.CreateSwapchain`
     的两个调用点，把 `requestedImageCount` 从 2 改成 3。优点：只影响引擎；
     缺点：引擎升级后 patch 目标可能变。
   - 方案 C：同时把 `FramesInFlight` 提到 3。只在 3 张图仍不够时试；会增加
     延迟和资源占用。
3. 更新 `docs/ENGINE-NATIVE-CONTRACT.md`：写明 iOS FIFO 下必须 3 张图，以及
   interposer 的 guard 规则。
4. 更新 `docs/PLAN.md`：记录 0.4.28/0.4.29 的 IPA 哈希、包内 DLL 哈希、smoke
   结果和真机裁决。

如果 H2 成立：

1. 先用 1080p/720p 找到 60fps 的分辨率上限。
2. 用 Metal System Trace / MoltenVK 性能统计找 GPU 热点：
   - UI overdraw / 全屏面板
   - draw call 数量
   - render pass 切换
   - descriptor/pipeline 切换
   - 纹理上传/解码
   - 着色器变体编译
3. 按热点优化，不要先改 swapchain；2 张图在 GPU <16.7ms 时本来可以 60fps。

## 6. 代码落点

### 6.1 `host/vulkan/vulkan_ios.c`

当前文件只转发 5 个全局入口，`vkGetInstanceProcAddr` 原样返回 MoltenVK 的函数；
设备级函数没有 hook。需要增加：

```c
static PFN_vkGetDeviceProcAddr g_real_gdpa;
static PFN_vkExportMetalObjectsEXT g_real_export_metal;
static PFN_vkQueueSubmit g_real_queue_submit;
static PFN_vkQueuePresentKHR g_real_queue_present;
static PFN_vkAcquireNextImageKHR g_real_acquire;
static PFN_vkWaitForFences g_real_wait_fences;
static PFN_vkCreateSwapchainKHR g_real_create_swapchain;
static PFN_vkGetSwapchainImagesKHR g_real_get_swapchain_images;

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
hook_vkGetDeviceProcAddr(VkDevice device, const char* name) {
    if (!name) return NULL;
    if (!strcmp(name, "vkExportMetalObjectsEXT")) {
        if (!g_real_export_metal)
            g_real_export_metal = (PFN_vkExportMetalObjectsEXT)g_real_gdpa(device, name);
        return (PFN_vkVoidFunction)hook_vkExportMetalObjectsEXT;
    }
    if (!strcmp(name, "vkQueueSubmit")) {
        if (!g_real_queue_submit)
            g_real_queue_submit = (PFN_vkQueueSubmit)g_real_gdpa(device, name);
        return (PFN_vkVoidFunction)hook_vkQueueSubmit;
    }
    // vkQueuePresentKHR / vkAcquireNextImageKHR / vkWaitForFences /
    // vkCreateSwapchainKHR / vkGetSwapchainImagesKHR 同理
    return g_real_gdpa(device, name);
}

API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL
vkGetInstanceProcAddr(VkInstance instance, const char* name) {
    if (!ready()) return NULL;
    if (name && !strcmp(name, "vkGetDeviceProcAddr")) {
        g_real_gdpa = (PFN_vkGetDeviceProcAddr)g_real_gipa(instance, name);
        return (PFN_vkVoidFunction)hook_vkGetDeviceProcAddr;
    }
    return g_real_gipa(instance, name);
}
```

`hook_vkCreateSwapchainKHR` 的实验版本：

```c
static VKAPI_ATTR VkResult VKAPI_CALL
hook_vkCreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* info,
                          const VkAllocationCallbacks* alloc, VkSwapchainKHR* out) {
    VkSwapchainCreateInfoKHR copy = *info;
    int want = port17_swapchain_images(); /* 默认 0 = 不改 */
    if (want > 0 && (int)copy.minImageCount < want)
        copy.minImageCount = (uint32_t)want;
    log_line("vkCreateSwapchainKHR: min=%u want=%d extent=%ux%u mode=%d usage=0x%x",
             info->minImageCount, want, info->imageExtent.width,
             info->imageExtent.height, (int)info->presentMode,
             (unsigned)info->imageUsage);
    return g_real_create_swapchain(device, &copy, alloc, out);
}
```

注意：

- `vkCreateSwapchainKHR` 的 `pCreateInfo` 是 `const`，必须复制后修改，不能原地写。
- `caps.maxImageCount` 必须在 `vkGetPhysicalDeviceSurfaceCapabilitiesKHR` 里读；
  如果 `maxImageCount > 0 && maxImageCount < 3`，不要强制 3。
- `vkGetDeviceProcAddr` 的 hook 必须保留所有未列出的函数，否则引擎会直接崩。
- 计时用 `clock_gettime(CLOCK_MONOTONIC)`，不要用 `Stopwatch` 托管代码；聚合后
  每 5 秒打印一次，避免每帧日志影响帧率。

### 6.2 `mobile/FrameTime.cs`

当前只测 `TryAcquireNextFrame`、`RecordFrame`、`TrySubmitFrame`。建议：

- 保留现有 `acquire/record/submit` 三段，增加 `exportDrawable`、`queueSubmit`、
  `queuePresent`、`waitFences` 四段；如果 interposer 已经打印，托管侧可以只做
  对齐和时间窗。
- `Report` 里增加 `swapchainImages`、`presentResult`、`acquireResult`，便于和
  MoltenVK 的 `[mvk-info]` 行对照。
- 不要在每帧分配字符串；继续用 5 秒窗口聚合。

### 6.3 `mobile/ResolutionChoice.cs`

`renderHeight=1080` 的路径已经存在，但必须确认它真的改了引擎的 layer：

- `BeforeHandleLayout` 里 `SetRenderScale` 之后，打印 `layer.drawableSize` 和
  `view.contentScaleFactor`。
- `ApplyRenderScale` 之后，确认 `IOSWindow.HandleLayout` 报出的 surface 尺寸是
  1552x1080，而不是 2360x1640。
- 如果 `drawableSize` 没变，先修这个，再做 GPU 负载 A/B；否则 1080p 实验无效。

### 6.4 `host/app/triangle.c`

增加 `PORT17_TRIANGLE_IMAGES`：

```c
const char* e = getenv("PORT17_TRIANGLE_IMAGES");
int want = e ? atoi(e) : 0;
g_imageCount = caps.minImageCount + 1;
if (caps.maxImageCount > 0 && g_imageCount > caps.maxImageCount)
    g_imageCount = caps.maxImageCount;
if (want > 0) g_imageCount = (uint32_t)want;
```

并在 `vkQueuePresentKHR` 前后计时，每 300 帧打印 median/99%。

### 6.5 `host/app/main.m`

实验开关建议放在 `Documents/game/port17-perf.txt`，由宿主在 MoltenVK 装载前读取，
避免每次改包：

```text
swapchainImages=3
mvkPerformanceTracking=1
mvkSynchronousQueueSubmits=0
mvkPrefillMetalCommandBuffers=1
```

默认全部关闭；只在 A/B 包里打开。注意 `MVK_CONFIG_*` 必须在 `vkCreateInstance`
之前 setenv，当前 `main.m` 已经在这个位置设置 residency sets。

## 7. 判定矩阵

| 观测 | 解释 | 下一步 |
|---|---|---|
| `exportDrawable` ≈33ms，`queueSubmit`/`queuePresent` ≈0 | `vkExportMetalObjectsEXT` 在等 `nextDrawable` | 强制 3 张图；H1 |
| `queueSubmit` ≈33ms，`exportDrawable` ≈0 | MoltenVK 把 `nextDrawable` 推迟到 submit | 强制 3 张图；H1 |
| `queuePresent` ≈33ms，前两段 ≈0 | 等 GPU 完成或 compositor | 测 GPU 时间；H2/H3 |
| `waitFences` ≈33ms | GPU 帧时间 >16.7ms | 降分辨率/GPU 优化；H2 |
| `acquire` ≈33ms | swapchain/compositor 饥饿 | 3 张图 + layer 配置；H1/H3 |
| `record` ≈33ms | CPU 录命令/shader 编译 | 查 record 热点；H9 |
| 所有段 ≈0，但 `interval` ≈33ms | 主线程 pacing/backlog | 查 `WaitForBacklogAtMost`/`ThrottleToTargetFrameRate`；H5 |
| 2 图 30fps，3 图 60fps | H1 成立 | 固化 3 图修复 |
| 2 图 30fps，3 图 45fps | GPU 帧时间约 22ms | 3 图 + 降分辨率/GPU 优化 |
| 2 图 30fps，1080p 60fps | GPU 帧时间 >16.7ms | 降分辨率；H2 |
| 3 图 1080p 仍 30fps | present/layer/pacing | Phase 3 |

## 8. 风险与注意事项

1. **`caps.maxImageCount` 可能是 2**：MoltenVK/Metal 通常允许 3，但必须以日志为准；
   如果 max=2，强制 3 会被 clamp 或报错，H1 不能靠这个修。
2. **3 张图的内存/延迟**：2360x1640 BGRA8 一张约 15.5MB，3 张约 46MB；对 M4
   可接受，但仍要在真机看内存和发热。三缓冲最多增加一帧延迟，RimWorld 可接受。
3. **`FramesInFlight=2` 与 3 张图**：引擎设计上支持 MAILBOX 的 3 图 + 2 frames，
   所以 `FrameResources` 能到 3；但要确认 `ImageCount`/`SyncObjects` 的索引不会越界。
4. **不要在 2 张图下继续调 pacing**：`targetFrameRate=-1` 已经关掉 present pacing，
   `pacing False` 已确认；再改 `PreferredFrameRate` 不会解决 33ms。
5. **不要混用旧日志**：每轮用新的 `test_work/m4-touch-*` 目录；当前 0.4.27 的
   日志只作为基线。
6. **调试器 confound**：当前日志是 `CS_DEBUGGED=1` 的 StikDebug 会话；最终裁决
   最好无调试器冷启动复测一次，排除 H10。
7. **1080p 实验必须先确认 layer 真的变小**：否则 `renderHeight=1080` 只是改了
   宿主 layer，引擎 swapchain 仍是 2360x1640，实验无效。
8. **interposer 不能破坏其它 swapchain**：ImGui overlay、三角形测试、引擎重建
   swapchain 都可能走同一个 `vkCreateSwapchainKHR`；guard 要按 surface/尺寸/
   调用来源区分，至少先日志确认。

## 9. 验收标准

修复后的 0.4.28/0.4.29 真机包需要同时满足：

1. `interval median` ≈16.7ms（允许 16.5–17.5ms 的 vsync 抖动）。
2. `port17 活性` ≈+900/15s（60fps）。
3. `render`/`submit` 的 median 不再是 33ms；`exportDrawable`/`queueSubmit`/
   `queuePresent` 三段之和能解释一帧。
4. 菜单和殖民地各跑 5 分钟，无触控回退、无 swapchain rebuild 风暴、无 device lost。
5. 日志里 `Created 3 swapchain images`（或实际图像数为 3），且 `caps.maxImageCount`
   允许。
6. 包内 `PortMobile.dll`、`PortHooks.dll`、`0Harmony.dll` 哈希与 prebuilt 一致；
   `patch-harmony smoke prebuilt/0Harmony.dll` 退出码 0。

## 10. 参考链接

- MoltenVK deferred `nextDrawable` issue:
  <https://github.com/KhronosGroup/MoltenVK/issues/2815>
- MoltenVK Runtime User Guide（Swapchains 章节，建议 3 张图）:
  <https://skia.googlesource.com/external/github.com/KhronosGroup/MoltenVK/+/HEAD/Docs/MoltenVK_Runtime_UserGuide.md>
- MoltenVK Configuration Parameters:
  <https://android.googlesource.com/platform/external/moltenvk/+/HEAD/Docs/MoltenVK_Configuration_Parameters.md>
- Khronos Vulkan Samples: Choosing the right number of swapchain images:
  <https://docs.vulkan.org/samples/latest/samples/performance/swapchain_images/README.html>
- 参考项目 `MHGURecomp-iOS` 的 swapchain 实现:
  `D:\Data\Code\reverse\MHGURecomp-iOS\runtime\nvn\vulkan\device.c`
