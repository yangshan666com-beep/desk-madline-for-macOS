# desk-madline for macOS

Desk Madeline 的 macOS 移植版。Madeline 会把桌面窗口当作地形，可以在窗口之间跑、跳、冲刺和爬墙。菜单栏提供桌面道具、表情、自定义按键、Badeline 追逐和辅助模式。

## 下载与安装

1. 在 [GitHub Releases](https://github.com/yangshan666com-beep/desk-madline-for-macOS/releases/latest) 下载 `DeskMadeline-1.8.1-macOS.dmg`。
2. 打开 DMG，将 **Desk Madeline** 拖进「应用程序」，再启动应用。
3. 公布版从本机安装的《蔚蓝》（Celeste）读取原版资源。应用会查找 Steam 安装，也可以从菜单栏手动选择 `Celeste.app`。

需要 **macOS 15 或更新版本**。Apple 芯片 Mac 通过 Rosetta 运行此 x64 安装包。macOS 26 及更新版本的道具栏采用系统 Liquid Glass。

## 操控

- **WASD 或方向键**移动与瞄准，**C** 跳跃，**X** 冲刺，**Z、V 或左 Shift** 抓墙。
- 左键按住 Madeline 可拖动她。按 **Esc** 释放键盘，点击角色或通过菜单栏恢复控制。
- 支持通过蓝牙或 USB 连接 **DualSense**。震动使用本机《蔚蓝》提供的兼容 SDL 库。

[完整玩法、按键和源码构建说明](macOS/README.md)

## 上游与致谢

本项目基于 **solstice23/desk-madeline** 的 Windows 源码移植。根据上游仓库的致谢，Desk Madeline 最初由 Eisyiah 创建。 [Windows 上游仓库](https://github.com/solstice23/desk-madeline)

源码采用 [MIT 许可证](LICENSE)。这是非官方同人项目，《蔚蓝》及其游戏素材归各自权利人所有。
