// UI/诊断模块边界（launcher.m 实现，main.m 调）：日志地面真相、日志钩子安装、
// 启动器控制器与它的诊断动作。main.m 保留生命周期与启动决策（引擎模式 / setup /
// 诊断保持），界面与按钮全部在这里。
#ifndef PORT17_LAUNCHER_H
#define PORT17_LAUNCHER_H

#import <UIKit/UIKit.h>

// 诊断 scene 的根控制器（SceneDelegate 建）。
@interface LauncherVC : UIViewController <UIDocumentPickerDelegate>
@end

// 持久日志：Documents/port17-ui.log（任意线程）。main.m 的启动/生命周期打点沿用原名。
void boot_log(NSString *line);

// 上屏 + 落盘（任意线程；UITextView 在主队列刷）。host_log 的 sink 主体，
// [port17-status] 行在这里被识别并转状态仓 + 卡片刷新。
void append_log(const char *msg);

// 引擎模式下宿主 UIKit 未起时就要收日志：main() 早期装 host_log sink 与
// libvulkan / libwaylithaudio / libvma 的日志钩子（一次）。
void port17_ui_install_sinks(void);

// 诊断保持开关的文件路径（main() 的启动判定与启动器的切换按钮共用）。
NSString *port17_diag_hold_path(void);

// [port17-status] 行入口（append_log 的前缀钩子转发；外部也可直接喂）。
// 格式 "[port17-status] key=value"——managed 侧 PortMobile.Entry 的约定。
void port17_launcher_status_line(const char *line);

// setup 用掉本进程 CoreCLR 后由 main() 打标记：启动器的引擎按钮据此指引
// 重开 app，不去撞 CoreCLR 单实例的 0x80131022。
void port17_launcher_note_clr_spent(void);

#endif
