## Pro2+ vX.Y.Z

Pro2 手柄的 Windows 全功能适配，绿色单文件，双击即用。

### 下载 / Downloads

| 版本 | 文件 |
| :-- | :-- |
| 中文界面 | `Pro2+-X.Y.Z.exe` |
| English UI | `Pro2+-X.Y.Z-en.exe` |

### 本次新增 / What's new

- （填写）

### 本次修复 / Fixes

- （填写）

### 自动连接排查 / If auto-connect misbehaves

如仍遇到自动连接问题，请到主页「连接手柄」卡片，点「连接手柄」或「重新检测」手动搜索连接。

If auto-connect still misbehaves, open the home page's **Connect Controller** card and use
**Connect Controller** or **Rescan** to search and connect manually.

### 来源 / Credits

本项目基于 [LeonChrome/XinHeLianSheng-Pro2-Bridge](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge)（Apache License 2.0）开发，沿用了以下两条链路：
蓝牙（BLE）输入链路与连接调优；触觉反馈与体感。改动声明见仓库内的 `NOTICE`。

第三方组件：Viiper（GPL-3.0）、usbip-win2（BSD-2-Clause）、libusb（LGPL-2.1），许可证原文见 `licenses/`。
Viiper 以独立子进程运行，经本机端口通信，不与本项目源码链接。随包的 `viiper-haptic-v2.8.exe` 是上游基于 VIIPER 改造的构建，
对应源码位于上游仓库 `source/final-three-in-one/tools/viiper/haptic-src`（标签 `pro2-v3.2.7`）。

This project is based on [LeonChrome/XinHeLianSheng-Pro2-Bridge](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge)
(Apache License 2.0) and carries over two chains: Bluetooth (BLE) input and connection tuning, and haptics/motion.
See `NOTICE` for the change statement.

Third-party components: Viiper (GPL-3.0), usbip-win2 (BSD-2-Clause) and libusb (LGPL-2.1); license texts are in `licenses/`.
Viiper runs as a separate child process over a local port and is not linked with this project's source.
The bundled `viiper-haptic-v2.8.exe` is a modified build derived from VIIPER by the upstream project; its corresponding
source is the upstream repository's `source/final-three-in-one/tools/viiper/haptic-src` at tag `pro2-v3.2.7`.

上游项目与本项目无隶属关系，也未对本项目作任何背书。The upstream project is not affiliated with this project and does not endorse it.
