# Taskbar Color Sorter

一键把 Windows 任务栏的图标按**主题颜色**排成彩虹。

不只是固定的图标——**正在运行的窗口按钮也一起参与排序**。开一堆程序之后点一下，整条任务栏从红排到紫，黑白灰的图标单独成段排在末尾。

不需要管理员权限，不写注册表，不重启 explorer，不注入任何 DLL。

> 截图占位：此处应有截图，懒得截图了。

---

## 功能

- **一键排序**：托盘图标双击，或右键菜单「按颜色排序」。
- **固定项和运行中窗口一视同仁**，同一套机制处理，运行中的未固定窗口可以被拖到固定项前面。
- **颜色靠截屏识别**，不解析 `.lnk` / AUMID / AppxManifest，所以 UWP 应用和普通 exe 没有任何区别，也不受图标缓存影响。
- **每次排序都重新取色**，从不缓存——图标可能带未读角标，也可能随系统主题变化。
- **随时中止**：排序过程中按 <kbd>Esc</kbd>，或点托盘菜单「中止」。
- **不抢鼠标**：默认用合成触摸指针注入，真实鼠标指针位置不会被移动（见下方「已知限制」）。

### 排序规则

1. **有彩色图标**按色相升序排成彩虹；色相相同时按饱和度降序。
2. **无彩色图标**（黑 / 白 / 灰）没有色相，按明度**由深到浅**单独成段，接在彩虹末尾。

---

## 运行要求

| 项目   | 要求                                                                  |
| ------ | --------------------------------------------------------------------- |
| 系统   | Windows 10 1809+ / Windows 11（在 Win11 23H2 Build 22631 上开发实测） |
| 权限   | 普通用户即可，**不需要管理员**                                  |
| 运行时 | 无。发布版是自包含单文件，不用装 .NET                                 |
| 构建   | .NET SDK 8.0+                                                         |

**只排主显示器的任务栏。** 任务栏设为自动隐藏时会拒绝执行（拖拽需要按钮稳定可见）。

---

## 使用

双击 `TaskbarColorSorter.exe`，程序驻留系统托盘：

- **双击托盘图标** 或 右键 →「按颜色排序」→ 开始排序
- 排序中 <kbd>Esc</kbd> 或 右键 →「中止」
- 右键 →「退出」

也支持一次性模式，排完即退出，适合自己绑快捷键或做自动化：

```powershell
TaskbarColorSorter.exe --sort
```

退出码：`0` 成功 / 已有序 / 无需排序，`1` 失败，`2` 被中止。

排序 16 个图标大约 20～25 秒。这个速度是拖拽可靠性换来的，时序常量在 [src/TaskbarColorSorter/IDragDriver.cs](src/TaskbarColorSorter/IDragDriver.cs) 的 `DragTiming` 里，可以自己调快。

---

## 架构

Windows **没有提供任何重排任务栏的官方 API**（详见下方「为什么是这个方案」），所以整个流程是「看屏幕 → 算顺序 → 模拟拖拽」：

```
UI Automation 枚举按钮  →  截屏提取每个图标主色  →  算目标顺序  →  逐个拖到位并验证
   TaskbarScanner            ColorExtract           ColorSort        SortEngine + IDragDriver
```

### 模块

| 文件                                                           | 职责                                                  |
| -------------------------------------------------------------- | ----------------------------------------------------- |
| [Program.cs](src/TaskbarColorSorter/Program.cs)                 | 入口。设 DPI 感知、单实例互斥、`--sort` 一次性模式  |
| [TrayApp.cs](src/TaskbarColorSorter/TrayApp.cs)                 | 托盘 UI。图标是运行时画出来的彩虹直方图，不带图片资源 |
| [SortEngine.cs](src/TaskbarColorSorter/SortEngine.cs)           | 流程编排。选择排序 + 每步验证 + 失败重试              |
| [TaskbarScanner.cs](src/TaskbarColorSorter/TaskbarScanner.cs)   | UIA 枚举任务栏按钮，取矩形和身份标识                  |
| [ColorExtract.cs](src/TaskbarColorSorter/ColorExtract.cs)       | 截屏提取图标主色（算法细节见下）                      |
| [ColorSort.cs](src/TaskbarColorSorter/ColorSort.cs)             | 目标顺序                                              |
| [IDragDriver.cs](src/TaskbarColorSorter/IDragDriver.cs)         | 拖拽执行器接口 + 时序常量 + 坐标钳制                  |
| [TouchDragDriver.cs](src/TaskbarColorSorter/TouchDragDriver.cs) | **首选**：合成触摸指针注入，不移动真实鼠标      |
| [MouseDragDriver.cs](src/TaskbarColorSorter/MouseDragDriver.cs) | 兜底：`SendInput` 鼠标，结束后恢复原指针位置        |
| [Native.cs](src/TaskbarColorSorter/Native.cs)                   | P/Invoke                                              |

### 枚举按钮

主任务栏窗口类名 `Shell_TrayWnd`，用托管 `System.Windows.Automation` 遍历。Win11 的应用按钮统一是 `Taskbar.TaskListButtonAutomationPeer`，按 ClassName 含 `TaskListButton` 过滤即可——开始 / 搜索 / 输入法 / 小组件 / 系统托盘用的是别的 peer，天然被排除，**不需要维护黑名单**。Win10 走 `ReBarWindow32 > MSTaskSwWClass > MSTaskListWClass` 的回退链。

**固定项和运行中窗口在 UIA 树里完全同构**，唯一区别是 Name 后缀 `- N running window(s)`。这就是运行中窗口能一起排序的原因。

### 提取主色

直接截任务栏矩形，按按钮 rect 裁**中心 62% 的正方形**分析。三个关键设计，都是实测踩坑后定下来的：

1. **色相用 ±25° 循环滑动窗口取累积权重最大处**，而不是取直方图峰值分箱。
   渐变图标（Edge 的蓝绿渐变散布在 175～230°）会被分箱切碎，峰值反被小面积杂色抢走，判成完全错误的颜色。
2. **每个像素乘一个以图标中心为峰的高斯空间权重**（sigma = 边长 × 0.30）。
   未读消息角标在图标角落，否则会把主色带偏——Teams 的紫色图标曾被右上角的红色角标判成红色。
3. **把无彩色区域也当成一个候选簇，跟各彩色簇比面积**；无彩色面积更大就判为黑 / 白 / 灰。主色取**面积最大**的簇，不是最鲜艳的那簇。
   黑底 + 一小块高饱和色的图标（MobaXterm）平均彩度并不低，只有比面积才能识别出"主体是黑的"；反过来 Chrome 那种中心白圆 + 四块彩色的图标，白色占比达不到多数，仍会正确归为彩色。

### 拖拽重排

选择排序：从左往右，逐个把「该在第 i 位」的图标拖过来。因为前 i 项已就位，要拖的项一定在右侧，所以**永远是向左拖**。

每步拖完重新枚举验证是否真的到位，没到位就重试；**每次重试都重新采样源和目标坐标**，因为上一次失败的拖拽可能已经改变了布局。

拖拽实现要点：

- 按下后先做 4 次 4px 微移越过系统的拖拽启动阈值，直接大幅移动会被判成点击
- Y 坐标全程钳制在任务栏矩形内，拖出去会误触"取消固定"
- `finally` 里兜底抬起，异常退出不会卡在按下状态

按钮身份优先用 UIA `RuntimeId`；万一任务栏重建了 XAML peer 导致 RuntimeId 变化，回退到按名称匹配，且**只在名称唯一时才用**，避免在"从不合并"模式下认错同名按钮。

---

## 为什么是这个方案

排查过的路，都验证过：

| 方案                           | 结论                                                                                                                                       |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------ |
| 改注册表`Taskband\Favorites` | ✗ 只存**固定项**顺序，运行中未固定的窗口根本不在里面。而且要重启 explorer                                                           |
| UI Automation 官方接口         | ✗ 任务栏按钮`GetSupportedPatterns()` 只返回 `Invoke, ScrollItem`，没有 `DragPattern` / `TransformPattern`                         |
| `ITaskbarList3/4`            | ✗ 只管进度条 / 覆盖图标 / 缩略图，不能重排                                                                                                |
| `TB_MOVEBUTTON` 等窗口消息   | ✗ Win11 任务栏是 XAML，单个按钮没有 HWND                                                                                                  |
| 注入 DLL 进 explorer.exe       | ✗ 技术上可行（7+ Taskbar Tweaker / Windhawk 路线），但必被杀软误报、需按系统版本维护偏移、可能崩 explorer。对一个要分享出去的工具是致命的 |
| **模拟拖拽**             | ✓ 唯一既能覆盖运行中窗口、又不碰系统内部的办法                                                                                            |

---

## 已知限制

- **排序过程中鼠标指针会消失 / 闪烁。** 合成触摸注入会让 Windows 切到「触摸输入模式」从而隐藏指针，此时移动实体鼠标又会切回来，于是来回闪。这是输入模式切换的固有行为，除非做驱动级输入否则绕不开。指针**位置**本身不会被劫持。
- **运行中窗口的顺序是易失的**——关掉程序或重启 explorer 就没了。这是 Windows 的机制。
- 只处理主显示器的任务栏。
- 任务栏自动隐藏时不工作。
- 排序中若有程序启动 / 退出导致图标数变化，会中途停下（可能停在中间状态，再排一次即可）。
- 多色图标（如 Chrome）的"正确颜色"本来就没有标准答案，取面积最大的那簇。

---

## 构建

```powershell
cd src/TaskbarColorSorter
dotnet build
```

输出在 `bin/Debug/net8.0-windows/win-x64/`。

## 打包

```powershell
cd src/TaskbarColorSorter
Remove-Item bin,obj -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -o <输出目录>
```

产物是约 68 MB 的自包含单文件 exe（`win-x64`，含 .NET 运行时，不裁剪——WinForms/WPF 大量依赖反射，裁剪会运行时崩溃）。

> **`Remove-Item bin,obj` 不能省。** 增量 publish 会产出一个约 15 MB 的坏包：双击立刻退出，退出码 `-2147450726`，事件日志报找不到同名 `.dll`（apphost 没被写成 bundle）。
>
> **验证时一定要把 exe 拷到别的目录再运行**，只看 publish 成功不算数。

分发提醒：exe 没有代码签名，别人首次运行会弹 SmartScreen，需要点「更多信息 → 仍要运行」。

---

## 测试

`probe/` 是开发期的验证工具，用来在不动任务栏的前提下检查算法。它**直接链接正式项目的 `ColorExtract.cs` 和 `ColorSort.cs`**，所以看到的就是正式程序的真实行为。

```powershell
cd probe
dotnet build
[Console]::OutputEncoding=[Text.Encoding]::UTF8   # 不加会中文乱码
.\bin\Debug\net8.0-windows\TaskbarProbe.exe <命令>
```

| 命令          | 作用                                                                                                       |
| ------------- | ---------------------------------------------------------------------------------------------------------- |
| `dump`      | 枚举任务栏按钮，导出完整 UIA 树到`out/uia-tree.txt`                                                      |
| `colors`    | **最常用。** 打印每个图标的主色、无彩占比、各色簇面积占比和目标顺序，并生成对比图 `out/colors.png` |
| `patterns`  | 打印按钮支持的 UIA 模式（用来证明没有官方重排 API）                                                        |
| `dragtest`  | 用`SendInput` 鼠标试拖一次并还原                                                                         |
| `touchdrag` | 用合成触摸指针试拖一次并还原                                                                               |
| `all`       | `dump` + `colors`                                                                                      |

调颜色算法的流程：跑 `colors` → 看 `out/colors.png` 和终端里的色簇数据 → 改 [ColorExtract.cs](src/TaskbarColorSorter/ColorExtract.cs) 开头的阈值常量 → 重跑。

判读要点：`无彩占比 > 最大色簇占比` 的图标会被归到无彩色段。

端到端测试（会真的动任务栏，建议先手动拖乱，否则会直接返回「已经有序」）：

```powershell
TaskbarColorSorter.exe --sort; $LASTEXITCODE
```

---

## 贡献

欢迎 issue 和 PR。改动颜色算法或拖拽时序时，请附上 `probe colors` 的输出或 `out/colors.png` 作为依据——这个项目里几乎每个常量都是实测调出来的，凭直觉改很容易退化。

## License

MIT，见 [LICENSE](LICENSE)。
