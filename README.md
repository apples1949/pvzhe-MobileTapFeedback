# 点击反馈与自动拾取（MobileTapFeedback）

> 手机版两个独立小功能：**③ 关卡/章节点击有按压反馈**、**④ 图鉴-工具页可关自动拾取**。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `mobiletapfeedback` |
| 程序集 | `JTYMobileTapFeedback` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `MobileTapFeedbackEntry` |
| 当前版本 | 1.0.0 |
| 分类 | 手机适配 |
| 来源 | 从「手机操作优化」（`MobileUXFixes`）**拆出来**的独立包 |

> 本包只含 ③④。原来的 ①② 留在 [`pvzhe-MobileUXFixes`](https://github.com/apples1949/pvzhe-MobileUXFixes)，
> **两个包要分别构建、分别装机**。

## ③ 关卡/章节/入口按压反馈

**根因**：`DragMenuSelectItem`（关卡 `DragMenuSelectItemlevel`、章节 `DragMenuSelectItemChapter`
的共同基类）只挂了 `button.Pressed`，**没有任何按压视觉** ⇒ 点下去毫无反应，像卡住了。

**方案**：挂 `ButtonDown / ButtonUp / MouseExited`，把 `graphics` **压暗 + 缩小**，抬起还原 ——
与游戏自家 `NinePatchButtonBase` 用 `ButtonDown` 做按压视觉是同一套路。

⚠️ **手机端时序坑（实测踩过）**：手机端"触摸 → 鼠标"是**同一帧里按下 + 抬起**
（还会紧跟一个 `MouseExited`）。若立刻还原，**变暗那一帧根本不会被渲染**
⇒ 玩家依然看不到任何反馈。**必须按下后至少保持 `PressVisibleMs` 毫秒再还原**。

## ④ 图鉴-工具页「自动拾取」开关框

**根因**（源码实证，阳光与金币是**两个独立**的存档特性键）：

```csharp
// TowerDefenseSunBase
autoCollect  = GetFeatureValue("SunCollect") != 0;
// TowerDefenseCoinBase
autoCollect  = GetFeatureValue("CoinCollect") > 0 && 场上无吸金磁;
```

游戏**没给玩家任何入口**去改它们（自动拾取永远是开着的）。

**方案**：在 **图鉴 → 工具页（`PropLayer`）** 注入一个「自动拾取」框，含**两个独立开关**
（阳光 / 金币），点击写 `GameSaveManager.SetFeatureValue(...)`（**进存档，重启保留**），
并**同步场上已生成**的阳光/金币（这两个类的 `autoCollect` 是 `public` 字段，可直接改）。

## 内部开关（默认全开）

```csharp
internal static readonly bool FixPressVisual  = true;   // ③ 按压反馈
internal static readonly bool FixAutoCollect  = true;   // ④ 自动拾取开关框
private  static readonly bool EnableLog       = false;  // 诊断日志
```

改开关需**重新编译打包**。

## 目录结构

```
MobileTapFeedback/
├── mod.json
├── build_mod.py
├── runtime_src/MobileTapFeedbackEntry.cs      入口（约 21 KB）
├── Runtime/ModAssembly.dll
└── dist/MobileTapFeedback.pmod
```

## 构建

```powershell
python mods\MobileTapFeedback\build_mod.py             # 编译 + 打包
python mods\MobileTapFeedback\build_mod.py --install   # 继续装机
```

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`
3. 包内**绝不允许**出现 `.cs`（`ModLoader.IsExecutablePackageFile` 白名单拒收）

## 已知坑

1. **手机端按下/抬起同帧** ⇒ 按压视觉必须"保持一小段时间"才可见（见 ③）。
2. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 自定义 `_Process` / `_Input` 不会被调用；
   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
3. **`TowerDefenseInGamePacketShow` 是被到处复用的卡类**（卡池/卡槽/图鉴/商店）
   ⇒ 对它做任何操作前**必须先用祖先链确认它在哪个界面**，否则会误伤别的界面。
4. **卡走对象池**：`ResetForPool()` 会 `ClearEventHandlers()` ⇒ 事件钩子必须按
   "在不在目标容器里"**逐帧重挂**；更稳的做法是干脆不用事件钩子，改为**逐帧读状态边沿**。

## 诊断

```csharp
private static readonly bool EnableLog = false;   // 置 true 重新打包
```

## 与相关 Mod 的关系

| Mod | 内容 |
| --- | --- |
| **本包** `mobiletapfeedback` | ③ 按压反馈 / ④ 自动拾取开关 |
| [`pvzhe-MobileUXFixes`](https://github.com/apples1949/pvzhe-MobileUXFixes) | ① 选卡滑动误选撤销 / ② 战斗误触取消 |
| [`pvzhe-DoubleSpeedToggle`](https://github.com/apples1949/pvzhe-DoubleSpeedToggle) | 与「加速」按钮并排的三倍加速 |
