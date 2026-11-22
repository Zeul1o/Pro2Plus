## Pro2+ v1.0.3

修复陀螺仪映射到右摇杆时的静差问题。中文版与英文版同步更新，绿色单文件，双击即用。

### 修复

- **小角度持续移动被「吃掉」**：朝一个方向缓慢转动时，零偏跟踪会把真实输入误当成零漂吸收，表现为准星转一会儿突然不动、反向格外敏感、再转回原方向推不动。现在只有真正静止才修正零偏，并且限制累计修正量。
- **死区边界跳变**：小角度输出原来只有「0」和「18%」两档，越过死区会瞬间跳变，表现为颗粒感和单边漂移。改为连续过渡。
- **物理摇杆静态偏置**：拇指轻搭在右摇杆上时带来的 20~60 LSB 偏置不再参与叠加，避免把合成值整体推向一侧。

### 下载

| 版本 | 文件 |
| :-- | :-- |
| 中文界面 | [Pro2+-1.0.3.exe](https://github.com/Zeul1o/Pro2Plus/releases/download/v1.0.3/Pro2+-1.0.3.exe) |
| English UI | [Pro2+-1.0.3-en.exe](https://github.com/Zeul1o/Pro2Plus/releases/download/v1.0.3/Pro2+-1.0.3-en.exe) |

### 来源 / Credits

本项目基于 [LeonChrome/XinHeLianSheng-Pro2-Bridge](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge)（Apache License 2.0）开发，沿用了蓝牙（BLE）输入链路与连接调优、触觉反馈与体感这两条链路；改动声明见仓库内的 `NOTICE`。

第三方组件：Viiper（GPL-3.0）、usbip-win2（BSD-2-Clause）、libusb（LGPL-2.1），许可证原文见 `licenses/`。Viiper 以独立子进程运行，经本机端口通信，不与本项目源码链接。随包的 `viiper-haptic-v2.8.exe` 是上游基于 VIIPER 改造的构建，对应源码位于上游仓库 `source/final-three-in-one/tools/viiper/haptic-src`（标签 `pro2-v3.2.7`）。

上游项目与本项目无隶属关系，也未对本项目作任何背书。

转发 exe 时请一并给出 `LICENSE`、`NOTICE` 和 `licenses/`。
