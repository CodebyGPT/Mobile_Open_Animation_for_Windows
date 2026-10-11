[English](#english) | [中文](#中文)

# Mobile Open Animation for Windows

<img src="icon.png" alt="The program's icon: a square with a curve across it" width="96" align="right">

---

## English

Give Windows a smartphone-style opening animation.

### Features

- **No injection, no API hooks**
- **No installer, nothing written to the registry**
- **Nothing to install on Windows 10 1903 or later**
- **Dynamic corner radius**
- **Material 3 emphasized curves**
- **Follows the Windows animation setting**
- **Launch ad and logon ad** (put your own videos in the `Ads` folder in the configuration directory)
- **No telemetry, no network access**

### Limitations

- **UWP / WinUI apps**
- **Online games with anti-cheat, and some security software** (their windows do not accept a change of state from another program)
- **Dialogs, popups and tool windows**
- **Windows the user did not open themselves**

### License

Released under the **GNU Affero General Public License, version 3 or later** (AGPL-3.0-or-later).

- A modified version: based on [Nico6719/windhawk-mod-mobile-open-animation](https://github.com/Nico6719/windhawk-mod-mobile-open-animation) (also under AGPL-3.0) and rewritten as a standalone program in **October 2026**.
- Copyright (C) 2026 CodebyGPT
- There is **no warranty**; you may convey it under the terms of the licence. The full licence text is in [`LICENSE`](LICENSE).
- Source: <https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows>

### Icon

The icon is from ByteDance's official icon library, [ByteDance IconPark](https://github.com/bytedance/IconPark)
(the "curve adjustment" icon), used under the permissive **Apache License**. The program draws it at run time
rather than shipping it as a picture: the outline is the accent colour Windows is using and the fill is white on
a light system and black on a dark one, so it is drawn again when either changes. The icon the program's own file
and its About box carry is the same artwork left in its own colours, and the SVG both are drawn from is
[`icon/曲线调整_curve-adjustment.svg`](icon/曲线调整_curve-adjustment.svg).

---

## 中文

让 Windows 拥有类似智能手机的开屏动画。

### 特性

- **免注入、不使用 API Hook**
- **免安装、不写注册表**
- **Win10 1903+ 无需安装额外依赖**
- **支持动态圆角**
- **采用 Material 3 Emphasized 曲线**
- **遵循 Windows 个性化设置**
- **支持开屏广告和开机广告**（需要自行将视频素材放入配置目录中的 Ads 文件夹）
- **无遥测、无联网**

### 局限性

- **UWP / WinUI 应用**
- **带反作弊的在线游戏、部分安全软件**（这类程序的窗口不接受外部程序改变其状态）
- **对话框、弹窗、工具窗口**
- **不是通过用户点击弹出来的窗口**

### 许可

本项目按 **GNU Affero 通用公共许可证第 3 版或更高版本**（AGPL-3.0-or-later）发布。

- 以 [Nico6719/windhawk-mod-mobile-open-animation](https://github.com/Nico6719/windhawk-mod-mobile-open-animation)（同为 AGPL-3.0 授权）为基础，于 **2026 年 10 月**改写为独立程序。
- 版权声明：Copyright (C) 2026 CodebyGPT
- 本程序**不提供任何担保**；你可以依据许可证条款传播它。许可证全文见根目录的 [`LICENSE`](LICENSE)。
- 源代码：<https://github.com/CodebyGPT/Mobile_Open_Animation_for_Windows>

### 图标

本程序图标来自字节跳动官方图标库 [ByteDance IconPark](https://github.com/bytedance/IconPark)（"曲线调整"图标），
使用宽松的 **Apache License** 许可。程序在运行时绘制该图标而不是附带一张图片：描边为系统当前的主题色，
填充在浅色模式下为白色、深色模式下为黑色，因此两者任一改变时图标会重新绘制。程序自身文件与"关于"窗口中
使用的则是同一套图形保留原有配色的版本，两者都由
[`icon/曲线调整_curve-adjustment.svg`](icon/曲线调整_curve-adjustment.svg) 绘制而来。
