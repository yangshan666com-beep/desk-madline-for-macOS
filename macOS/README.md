# Desk Madeline for macOS

桌宠会把可见窗口的边框当作地形。打开 `Desk Madeline.app` 后默认进入玩家操控。WASD 或方向键移动和瞄准，S 或 ↓ 下蹲，C 跳，X 冲刺，Z、V 或左 Shift 抓墙。用左键按住角色可以拖动，松开后会重新落到地形上。按 Esc 或切换应用释放键盘，点击角色或从菜单栏选择「控制 Madeline」可恢复操控。

本地构建默认打包角色、道具、表情和水晶之心使用的《蔚蓝》图像。公开 DMG 从本机安装的《蔚蓝》读取图像。原版音效和手柄震动也需要本机游戏提供对应的库和资源。可在菜单栏选择「选择《Celeste》…」指定游戏位置。

Madeline 站在窗口边缘或抓住侧边时会跟随窗口的拖动。窗口换层后，露出的原接触面仍能支撑她。窗口最小化、吸附跳位或支撑被盖住时，她会从当前位置落下。显示器断开让她离开可见区域时，会把她放回附近能站稳的位置。

通过蓝牙或 USB 连接 DualSense。左摇杆或十字键移动和瞄准，× 或 △ 跳跃，□ 或 ○ 冲刺，L1、R1、L2、R2 抓墙。摇杆死区、冲刺角度和跳跃及冲刺的输入缓冲按原版《蔚蓝》的默认配置判定。

从菜单栏点击「Desk Madeline」，选择「桌面道具…」并把道具拖到桌面。macOS 26 及更新版本使用系统 Liquid Glass 面板。道具可拖动或右键删除，位置会保留到下次启动。在同一菜单打开「自定义按键…」可设置移动、跳跃、冲刺和抓墙的主键。方向键、V 和左 Shift 提供备用操作。

在「Badeline 追逐」选择关闭或 1 至 6 个分身。分身依次沿 Madeline 先前的路线追来，碰到她会触发普通死亡和复活。复活后移动会开启下一轮追逐。打开追逐时会从本机《蔚蓝》播放第二章旧址的追逐音乐，关闭追逐后立即停止。在「辅助」可以独立开启无限体力，并将冲刺设为一次、两次或无限次。两组设置都会保留到下次启动。

红绿灯平台会在 Madeline 搭上去后沿缆绳移动。方形款横向移动，竖长款上下移动，放置时会按屏幕空间安排缆绳。绿泡泡放出一次普通冲刺，红泡泡会沿瞄准方向持续推进，撞到地形时结束。两种泡泡都可以按冲刺键提前离开泡泡。

黄金山脊的砖块车有上、下、左、右四种方向。站在左右款上时可用上下键转向，抓住上行款的侧面时可用左右键转向。下行款沿箭头直行。砖块车撞到障碍后会解体，并在放置点重组。

煤球碰到会使 Madeline 死亡。蓝云可从下方穿过，从上方落上去会被抛起。金色羽毛提供约 2 秒的飞行，用方向键、WASD 或手柄转向。地面弹簧会吸附到窗口平台或桌面地板。冲刺回复晶体约 2.5 秒重生，补满冲刺和攀爬体力。

道具栏还可放置墙面弹簧、粉云、双冲刺晶体、弹射球、梦境方块、碎裂平台、交换方块、凯文方块、河豚、水母、草莓和水晶之心。墙面弹簧吸附在朝向对应的窗口侧边。用抓键举起水母，松开可抛出，按住向下再松开可以放下。拖动交换方块或移动门旁的端点可以改变路线。手摆的触碰开关与移动门共用一组，四种颜色的节拍方块共享节拍，冰火开关切换手摆冰火块的状态。

把鼠标移到手摆梦境方块或任意颜色节拍方块的边缘或四角，出现缩放光标后即可拖动。尺寸按游戏的 8 像素网格对齐，拖动中间则移动整块。尺寸和位置会一起保存。

冲刺撞击手摆的水晶之心可触发收集演出。所在显示器会闪白，随后在暗场中显示收集光点和蓝心。草莓和水晶之心收集后会复原。

DualSense 震动默认开启，可在菜单栏选择「手柄震动」关闭或减半。震动通过本机《蔚蓝》自带的 SDL 发送，沿用原版对应动作的强度、时长和覆盖规则。若游戏的 SDL 库不可用或未识别手柄，输入由 macOS Game Controller 提供。

Madeline 成功做出 Hyper 时会微笑，攀爬体力快耗尽时会露出不高兴的表情，被鼠标拖起时会惊讶。10 秒内左键点击她三次，她会露出愤怒表情，在地形允许的普通冲刺、Super 和 Wavedash 中随机选择一种逃跑。接下来的 10 秒内，每次短按都会让她再尝试逃跑一次。逃跑中的短按会等当前动作结束，逃跑结束后也可以继续拖动她。肖像取自《蔚蓝》原版对话，并会在像素边框里弹出。在菜单栏取消勾选「显示表情」即可关闭，设置会保留到下次启动。

Desk Madeline uses windows on the desktop as Celeste terrain. Local builds bundle the original artwork by default. The public DMG reads artwork from a local Celeste installation. Original sound and controller rumble also use resources from that installation. Click **Desk Madeline** in the menu bar to open the controls and settings.

## Controls

Keyboard control is active at launch. Press **Esc** or switch apps to release it. Click Madeline or choose **Control Madeline** from the menu bar to take over again. Hold the left mouse button on the sprite to pick her up and drag her. Open **Customize Keys…** to set the primary movement, jump, dash and grab keys.

Choose **Badeline Chase** to enable one to six chasers or turn the chase off. They follow past positions and trigger the usual death and respawn when they catch Madeline. Starting to move after a respawn starts a new chase. Enabling the chase plays the original Old Site music from the local Celeste installation. Turning the chase off stops its music immediately. Under **Assist**, toggle Infinite Stamina and choose one, two, or unlimited dashes. Both settings are saved across launches.

Madeline follows a window while standing on or climbing its exposed edge. Minimizing, snapping or covering the edge releases her where she is. If a display disconnects and leaves her outside the work area, she returns to a nearby clear standing position.

| Action | Keyboard | DualSense |
| --- | --- | --- |
| Move or aim | WASD or arrows | Left stick or D-pad |
| Crouch | S or ↓ | Down |
| Jump | C | Cross or Triangle |
| Dash | X | Square or Circle |
| Grab a wall | Z, V or left Shift | L1, R1, L2 or R2 |
| Climb or slide | W / S or ↑ / ↓ while grabbing | Up / down while grabbing |

**Explore Automatically** gives control to the upstream idle director when keyboard control is released. **Window Material** switches the desktop geometry between solid borders, dream blocks, and water.

Open **Desktop Items…** and drag any tile onto the desktop. The palette stays open for additional drops. Drag a placed object to move it, or right-click it to remove it. Placements persist across launches. **Clear Desktop Items** removes all placed objects. On macOS 26 or later, the panel uses Liquid Glass.

Zip movers run along their cables when Madeline rides them. The square version travels horizontally and the tall version travels vertically. Green boosters launch a regular dash. Red boosters sustain travel in the aimed direction until a collision or an escape input. Press Dash while inside either bubble to launch early.

Golden Ridge move blocks have up, down, left and right versions. Steer the horizontal versions up or down while standing on top. Steer the upward version left or right while climbing its side. The downward version follows its arrow. Move blocks break against obstacles and reform at their placement point.

The dust bunny is lethal on contact. The reusable blue cloud launches Madeline after she lands on it. The golden feather enables two seconds of steerable flight. Floor springs attach to a window ledge or the desktop floor. Dash refill crystals restore dashes and stamina and reappear after about 2.5 seconds.

The palette includes wall springs, fragile pink clouds, two-dash refills, bumpers, dream blocks, crumble platforms, swap blocks, Kevin blocks, pufferfish, jellyfish, strawberries, and Crystal Hearts. Wall springs attach to a facing window side. Use Grab to pick up jellyfish, then release it to throw or hold Down to drop. Drag the destination marker of a swap block or gate to adjust its route. Placed touch switches and gates share one group. Cassette blocks share a beat, and core switches toggle placed bounce blocks between hot and cold.

Resize a hand-placed dream block or any cassette block by dragging an edge or corner when the resize cursor appears. Sizes snap to the original 8-pixel grid and are saved with the placement. Drag the center to move the whole block.

Dash into a placed Crystal Heart to collect it. The display flashes white and dims as the orbs gather. The blue heart appears before the scene fades out. Loose hearts and strawberries reappear for repeated play.

**Controller Rumble** is on by default and offers Celeste's Half and Off settings. The app uses the installed game's SDL library for the DualSense motors and preserves Celeste's action triggers, intensities, and timing. It uses macOS Game Controller for input when the game library is unavailable or cannot recognize the controller.

Madeline smiles on a successful Hyper, looks upset when low on climbing stamina, and looks surprised when dragged with the mouse. Three left clicks within ten seconds make her show her angry portrait and escape with a randomly chosen dash, superdash or wavedash that fits the terrain. Each quick click in the next ten seconds requests another escape. A click during an escape waits for the current move to finish, and you can drag her again after she stops fleeing. The original-resolution portraits bounce into place inside pixel-art frames. Uncheck **Show Expressions** in the menu bar to turn them off. The preference persists across launches.

## Build

Run `./macOS/build.sh` from the source checkout with the .NET 10 SDK and the macOS 26 SDK or newer. The output is `macOS/bin/Release/Desk Madeline.app`, with the .NET runtime included. It targets macOS 15 or later. The default x64 build runs through Rosetta on Apple silicon and loads the Intel FMOD libraries shipped with Steam's Mac release of Celeste. `DESKMADELINE_ARCH=arm64` builds an ARM executable for a game install with matching FMOD 1.10 libraries.

The build includes artwork from the default Steam or Applications install. Set `CELESTE_PATH` to a different `Celeste.app` or its `Contents/Resources` directory when building. Use `DESKMADELINE_BUNDLE_ART=0` to build an app that loads artwork from a local install at runtime.

At runtime the app finds audio and controller resources in Steam's library folders and the usual Applications folders. Use **Choose Celeste…** for another install. Set `CELESTE_PATH` to supply a path when running from a terminal.

## Checks

Run `./macOS/check.sh --sound` with Celeste installed to run the upstream gameplay regressions and the macOS input, rumble, portrait, terrain, rendering, and FMOD bank checks. Pass an output directory to save rendered movement and reaction frames.

## Scope

The macOS shell uses AppKit for the click-through display and keyboard focus and WindowServer metadata for terrain. It runs the upstream C# player at 60 Hz with the original Gameplay atlas and Sprites.xml. A window obscured by a higher window contributes only its visible borders. Fullscreen foreground windows hide the pet on their display. The bottom of each macOS work area serves as the floor.

This build covers Madeline, desktop terrain, and the 35 items in the desktop palette. The Windows build's skin picker and draggable moon and Kevin windows are outside this macOS shell. Celeste's level scripts and scene-wide effects require the game.

Based on the MIT-licensed Windows project at revision `8404a89`. [solstice23/desk-madeline](https://github.com/solstice23/desk-madeline/tree/8404a89e2a90cbafe35a97ef40f640083afd3c4c)

Celeste is by Extremely OK Games. The build copies the original graphics from the local installation into the app bundle.
