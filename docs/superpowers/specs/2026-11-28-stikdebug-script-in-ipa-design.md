# StikDebug 启动脚本随 IPA 打包 — 设计

- 日期：2026-11-28
- 状态：设计已由用户批准（方案 A）；本文是实现前的规格
- 相关：`docs/PLAN-JIT26.md`（JIT26 协议与脚本）、`host/build-ipa.sh`（出包）、`host/app/launcher.m`（启动器）

## 问题

JIT 路线每次启动都要经 StikDebug 运行 `probe/stikdebug/rw17-jit26.js`，但脚本只在仓库里：README 要求「把 rw17-jit26.js 隔空投送到设备并导入 StikDebug」，装新机离不开电脑，脚本版本也可能和包里的 runtime 协议错位。

## 目标 / 非目标

- 目标：脚本随 IPA 进设备；用户在 iPad 上不靠电脑就能把它交给 StikDebug；升级安装后副本自动跟上当前包。
- 非目标：不做「自动导入 StikDebug」（沙盒下 Port17 写不进 StikDebug 容器）；不改 JIT26 协议和脚本内容；不碰解释器路线。

## 用户确认的事实

- 交付方式：**两条都要**——启动器按钮弹分享面板 + 文件 app 可见的副本。
- 现有导入方式：分享面板里选 StikDebug；StikDebug 的导入入口从「文件」选。两条分别对应按钮与 Documents 副本。

## 方案取舍

| | 做法 | 结论 |
|---|---|---|
| A | 脚本进 bundle（只读，跟包一致）+ 启动器启动时同步到 `Documents/stikdebug/` + 「JIT 脚本」按钮 | **采用** |
| B | 打包时直接放进 IPA 的 `Documents/stikdebug/`，不同步 | 否：升级安装不覆盖 Documents，旧脚本残留 |
| C | 只进 bundle + 按钮，不落 Documents | 否：StikDebug「导入 → 文件」这条路走不通 |

## 设计

### 1. 打包（`host/build-ipa.sh`）

- 新增 6.08：`probe/stikdebug/*.js` → `$APP/stikdebug/`。`rw17-jit26.js` 缺失即 `::error::` 退出（fail-closed，同 6.06 / 6.07）。用 `*.js`，不写死文件名。
- 第 8 步（IPA）自检加一条：`Port17Host.app/stikdebug/rw17-jit26.js` 存在且字节数等于仓库文件；把 sha256 打进 CI 日志，供 `docs/PLAN.md` 落位记录比对。
- 资源不是 Mach-O，第 7 步整包 `codesign "$APP"` 已封进 Code Resources，不单独签。

### 2. 启动器（`host/app/launcher.m`）

- `syncStikDebugScripts()`：遍历 `[NSBundle mainBundle]/stikdebug/*.js`，与 `Documents/stikdebug/` 同名文件比 `NSData` 内容，不同就原子写入；失败只记 `boot_log`，不阻断。调用点：`viewDidLoad` 里 `boot_log(@"viewDidLoad（启动器）")` 之后——按钮可点之前副本已就位。
- 新按钮「JIT 脚本」：row2 末尾（与「日志」并排），`launcher_button(@"JIT 脚本", NO, …)` → `jitScriptTapped:`：`UIActivityViewController` 带 `Documents/stikdebug/rw17-jit26.js`（不存在则退回 bundle URL），`popoverPresentationController.sourceView/sourceRect` 锚到按钮（同 `exportTapped:`，iPad 必须有锚点）。
- 底部说明文案（原「JIT 需经 StikDebug 拉起（rw17-jit26.js）；解释器路线装完直接开。」）改为：「JIT 需经 StikDebug 拉起：点「JIT 脚本」把 rw17-jit26.js 交给 StikDebug，或 文件 → 我的 iPad → Port17 → stikdebug；解释器路线装完直接开。」
- 引擎侧等待提示（`host/app/host.c` 两处、`host/app/jit26.c` 一处）：「去 StikDebug 运行 probe/stikdebug/rw17-jit26.js」→「去 StikDebug 运行 rw17-jit26.js（脚本随包：启动器「JIT 脚本」或 文件 app → Port17 → stikdebug）」。只改字符串。

### 3. 文档（与代码同次提交）

- `README.zh-CN.md` / `README.md` 安装步骤：去掉「隔空投送」，改为按钮 / 文件 app 两条路，说明脚本随包、升级后自动更新。
- `docs/GAME-IMPORT.md` 设备内 setup 加同一行；`docs/PLAN-JIT26.md` 步骤 1 补「脚本随 IPA 打包（build-ipa.sh 6.08），启动器同步到 Documents/stikdebug/ 并可一键分享」。
- `docs/PLAN.md`：已定决策表加一行「StikDebug 脚本随包」；里程碑记本轮落位（CI run、IPA 哈希、包内脚本 sha256 = 仓库文件、下次真机看什么）。

## 验证

- 本地：`bash -n host/build-ipa.sh`；Windows 不编 ObjC，靠 CI（push `host/**` 触发 `m2-host.yml`）。CI 绿后解包 IPA：`stikdebug/rw17-jit26.js` sha256 与 `probe/stikdebug/rw17-jit26.js` 一致。
- 真机（下一轮，目录由用户指定）：① 文件 app → Port17 → stikdebug 里有脚本；② 按钮弹出面板，选 StikDebug 后其脚本列表出现 rw17-jit26；③ 用它拉起，`[jit26]` 日志出现竞技场地址，游戏正常进；④ 覆盖安装后 Documents 副本 mtime 更新。

## 实现顺序

1. `host/build-ipa.sh` 6.08 + 第 8 步自检。
2. `host/app/launcher.m`：同步函数、按钮、文案。
3. `host/app/host.c` / `jit26.c` 提示字符串。
4. README zh/en、GAME-IMPORT、PLAN-JIT26、PLAN.md。
5. `bash -n`；提交并 push 触发出包；CI 绿后解包核对哈希并写落位记录。
