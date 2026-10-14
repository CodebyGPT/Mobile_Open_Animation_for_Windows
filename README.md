<p align="center">
  <img src="icon.png" alt="The program's icon: a square with a curve across it" width="160">
</p>

<h1 align="center">Mobile Open Animation for Windows</h1>

<p align="center"><a href="#english">English</a> | <a href="#中文">中文</a></p>

---

<h3 id="english">Give Windows a smartphone-style opening animation.</h3>

### Features

- **0% injection, minimally invasive**
- **Single file, compact**
- **No telemetry, no network access**
- **No installer, nothing written to the registry**
- **Uses the .NET Framework 4.8 runtime**
- **Follows Windows settings**
- **Not hardcoded - any resolution, scaling or monitor layout**
- **Dynamic corner radius**
- **Material 3 emphasized curves**
- **Custom opening ad**

### Limitations

- **UWP / WinUI apps** - the window is drawn by the framework itself, so there is nothing to take over.
- **Online games with anti-cheat, and some security software** - their windows do not accept a change of state from another program.
- **Dialogs, popups and tool windows** - windows with an owner, windows the system marks as tool windows, dialogs, and the splash or helper windows that are too small when they appear, are all left alone.
- **Apps that quit as they close** - opening animation only. The window is gone before its position and size can be read, so there is nothing to shrink from.
- **The same app sometimes animates and sometimes does not** - the program has to take the window over before it appears. When it loses that race the window is left alone rather than drawn over, so that launch has no animation.

### Building

Running the program needs no build - take `MobileOpenAnimation.exe` from
[Releases](https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows/releases). Building it needs a .NET SDK,
and this repository does not carry one: the SDK used here is a portable one of a few hundred megabytes, and it lives
in `.toolchain/`, which git ignores.

- **Portable SDK, nothing installed system-wide.** Extract a .NET SDK zip so that `dotnet.exe` sits at
  `.toolchain\dotnet\dotnet.exe`, then run `.\build.ps1`. That script redirects NuGet's cache, the CLI home and
  `%APPDATA%`/`%LOCALAPPDATA%` under `.toolchain` too, so the build writes nothing outside this folder. The exe
  lands at `src\bin\Release\net48\MobileOpenAnimation.exe`, carrying the time and the commit it was built from.
- **SDK already installed.** `dotnet build src\Moa.csproj -c Release` does the same thing. The version it reports
  then reads `unknown`, because the time and the commit are what `build.ps1` passes in; add
  `-p:InformationalVersion=` yourself if you want them.

The first build needs the network once. The project targets .NET Framework 4.8 - so that the built exe runs on
Windows 10 1903 and later with nothing installed - and for that target the SDK fetches
`Microsoft.NETFramework.ReferenceAssemblies` from NuGet unless the 4.8 targeting pack is already on the machine.
Once that package is in the cache under `.toolchain/nuget`, later builds have nothing to restore and do not reach
the network at all.

### License

Released under the **GNU Affero General Public License, version 3 or later** (AGPL-3.0-or-later).

- A modified version: based on [Nico6719/windhawk-mod-mobile-open-animation](https://github.com/Nico6719/windhawk-mod-mobile-open-animation) (also under AGPL-3.0) and rewritten as a standalone program in **October 2026**.
- Copyright (C) 2026 CodebyGPT
- There is **no warranty**; you may convey it under the terms of the licence. The full licence text is in [`LICENSE`](LICENSE).
- Source: <https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows>

### Icon source

The icon is from ByteDance's official icon library, [ByteDance IconPark](https://github.com/bytedance/IconPark)
(the "curve adjustment" icon), used under the permissive **Apache License**.

---

<h3 id="中文">让 Windows 拥有类似智能手机的开屏动画。</h3>

### 特性

- **零注入、侵入性低**
- **单文件、体积小**
- **无遥测、不联网**
- **免安装、不写注册表**
- **使用 .NET 4.8 运行时**
- **遵循 Windows 设置**
- **不硬编码，适配各种分辨率、缩放与多屏**
- **支持动态圆角**
- **采用 Material 3 Emphasized 曲线**
- **支持自定义开屏广告**

### 局限性

- **UWP / WinUI 应用** —— 窗口由框架自己绘制，接管不了。
- **带反作弊的在线游戏、部分安全软件** —— 这类程序的窗口不接受外部程序改变其状态。
- **对话框、弹窗、工具窗口** —— 有属主的窗口、系统标记为工具窗口的窗口、对话框，以及创建时太小的启动画面和助手窗口，都不显示动画。
- **关闭窗口的同时就退出程序的** —— 只有开屏动画，没有关闭动画：窗口消失得太快，来不及读到它的位置和尺寸，也就没有可收缩的对象。
- **同一个应用有时有动画、有时没有** —— 程序要抢在窗口显示出来之前接管它；没抢到时就直接放过，不在用户已经看到的窗口上做动画，这一次就没有动画。

### 如何编译

**运行**它不需要编译：直接从 [Releases](https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows/releases)
下载 `MobileOpenAnimation.exe` 即可。想自己编则需要一个 .NET SDK，而本仓库不附带 SDK——这里用的是几百 MB
的便携版，放在被 git 忽略的 `.toolchain/` 里。

- **用便携版（系统里什么都不装）**：把 .NET SDK 解压到 `.toolchain\dotnet\dotnet.exe` 这个位置，然后运行
  `.\build.ps1`。该脚本还会把 NuGet 缓存、CLI home 以及 `%APPDATA%`/`%LOCALAPPDATA%` 一并重定向到
  `.toolchain` 下，因此构建过程不会在本文件夹之外写任何东西。产物是
  `src\bin\Release\net48\MobileOpenAnimation.exe`，其中带有编译时间与所依据的提交号。
- **机器上已装好 SDK**：`dotnet build src\Moa.csproj -c Release` 效果相同。这样编出来的版本号显示为
  `unknown`，因为时间和提交号是 `build.ps1` 传进去的；需要的话自行加上 `-p:InformationalVersion=`。

首次编译需要联网一次：目标框架是 .NET Framework 4.8（这样编出来的 exe 在 Win10 1903 及以上无需安装任何依赖），针对该框架
SDK 会从 NuGet 获取 `Microsoft.NETFramework.ReferenceAssemblies`，除非机器上已装 4.8 目标包（targeting pack）。该包进入
`.toolchain/nuget` 缓存之后，后续编译不会再还原任何东西，也完全不碰网络。

### 许可

本项目按 **GNU Affero 通用公共许可证第 3 版或更高版本**（AGPL-3.0-or-later）发布。

- 以 [Nico6719/windhawk-mod-mobile-open-animation](https://github.com/Nico6719/windhawk-mod-mobile-open-animation)（同为 AGPL-3.0 授权）为基础，于 **2026 年 10 月**改写为独立程序。
- 版权声明：Copyright (C) 2026 CodebyGPT
- 本程序**不提供任何担保**；你可以依据许可证条款传播它。许可证全文见根目录的 [`LICENSE`](LICENSE)。
- 源代码：<https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows>

### 图标来源

本程序图标来自字节跳动官方图标库 [ByteDance IconPark](https://github.com/bytedance/IconPark)（"曲线调整"图标），使用宽松的
**Apache License** 许可。
