# Lantern Belt

[English](#english) · [中文](#中文)

![version](https://img.shields.io/badge/version-1.6.0-informational)

![game](https://img.shields.io/badge/The%20Long%20Dark-2.55-success)

![loader](https://img.shields.io/badge/MelonLoader-0.7.3-blueviolet)

![license](https://img.shields.io/badge/license-MIT-blue)

---

## English

A The Long Dark mod that hangs a kerosene lantern on your belt, keeping both hands free.

### Features

**On your belt**

- Select a lantern in your inventory and the "Equip" button becomes "Hang / Take down": one click puts it on your belt and frees both hands, another click puts it back in the bag.
- While it is hung, the item description ends with an extra line "[Lantern hung]"; take the lantern down and that line disappears.
- While it is hung, the HUD shows "Fuel 87% · Condition 100%" next to where the vanilla durability bar sits; the line disappears once you take the lantern down.
- It lights up by itself when hung: light radius, burn rate and warmth all use the vanilla numbers, so the lantern on your belt really burns fuel and really gives off heat.
- Hanging does not use your hands: you can still carry a rifle, hold a flare, climb a rope, light a fire and open doors.

**Switching it on**

- Press `L` while the lantern is hung. Nothing pops up in the middle of the screen — you can see whether the lantern is lit.
- `L` does nothing when the lantern is not hung (in the bag or in your hands), so it never steals a vanilla key.
- With an empty tank, `L` will not produce a lit lantern out of nothing.

### Requirements

- The Long Dark **2.55** (built against 2.55), MelonLoader **0.7.3** or newer
- **ModSettings** and **ModData** (both required: the first is the settings page, the second is how the hung state survives a scene change)

### Installation

1. Make sure `MelonLoader/` exists in the game folder (install MelonLoader and launch the game once if it does not)
2. Copy `LanternBelt.dll` into `TheLongDark/Mods/`
3. Restart the game

### Usage

1. Open your inventory and click a lantern
2. Click "Hang / Take down" on the item panel — the lantern goes to your belt and lights up, and "[Lantern hung]" appears in its description
3. Press `L` to switch the lantern on and off; click "Hang / Take down" again to take it down and the extra line goes away

Settings live under **Options → Mod Settings → Lantern Belt**. The settings page itself is in Chinese; the names below are the in-game text with a short English gloss.

### Settings

| Setting | Effect | Default |
|---|---|---|
| 开关提灯（仅挂腰时） / lantern key | Works only while the lantern is hung; pressing it at any other time does nothing | `L` |
| 挂上去就点亮 / light up on hang | Turn it off and the lantern stays unlit when hung, so you light it yourself | On |
| 收起时熄灯 / extinguish on take down | Turn it off and the lantern keeps burning fuel after it goes back into the bag | On |
| 读档后自动恢复 / restore after loading | After a load or a scene change, put the hung lantern back on your belt automatically | On |
| 保留原版「装备」行为 / keep vanilla "Equip" | On restores the vanilla "Equip" button and disables hanging entirely | Off |
| 挂点基准 / anchor reference | "Player body" follows your body; "Camera" follows the view and shakes more while walking | Player body |
| 左右偏移（米） / side offset | Negative is the left hip, positive is the right hip | -0.22 |
| 上下偏移（米） / height offset | Height above the ground you stand on | 0.95 |
| 前后偏移（米） / forward offset | Positive points forward | 0.12 |
| 蹲下时跟着下移 / follow crouch | Move the lantern down with you when you crouch instead of leaving it at standing hip height | On |
| 蹲下下移量（米） / crouch drop | How far the anchor drops when you crouch. Still too high, raise it; sinking into your legs, lower it | 0.35 |
| 整体缩放 / overall scale | Usually has to come down to about 0.5 when the anchor reference is "Camera" | 1.0 |
| 每帧对齐挂点 / follow anchor every frame | Off leaves the lantern behind in the world; only for troubleshooting | On |
| 显示灯油与耐久 / HUD readout | Show fuel and condition on the HUD while the lantern is hung; off shows nothing at all | On |
| 提示水平偏移 / readout X offset | Move the readout sideways from the vanilla durability bar; positive is right | 0 |
| 提示垂直偏移 / readout Y offset | Positive moves the readout up, negative moves it down | -70 |
| 提示字号 / readout font size | Text size of the readout | 20 |
| 提示语言 / message language | Affects the on-screen messages and the inventory button text | Auto |
| 输出详细日志 / verbose logging | Writes anchor, renderers, layer and heat source into `MelonLoader/Latest.log` | Off |

### Adjusting the belt anchor

The default spot is your left hip. If the lantern floats in the air, clips into your body or stays invisible, these three offsets are all you need; every slider applies live, no restart:

- **Too low / too high**: change the height offset, 0.95 is roughly waist height; the crouched height is controlled separately by the crouch drop
- **Inside the body / invisible**: raise the forward offset and move the side offset towards 0; if clothing still swallows it, switch the anchor reference to "Camera" and drop the scale to about 0.5

### Uninstall

Delete `TheLongDark/Mods/LanternBelt.dll`.

No field is written into the game save. A lantern that was hung turns into a lit lantern in your bag and keeps burning fuel under vanilla rules — take it down before you uninstall.

The hung state lives in a separate file under `Mods/ModData/`; nothing reads it once the mod is gone, so it does not affect your save.

See [ChangeLog.md](ChangeLog.md) for version history.

### Compatibility

- It only takes over the hung lantern's position, rendering and on/off state while that lantern is on your belt; the rest of the time the game is untouched. Item definitions, fuel burn rate and light intensity are not modified.
- No known conflicts with other gear mods (no systematic combination testing was done). What would really clash is another mod that also takes over the lantern's on/off switch.

### FAQ

**I clicked the button and nothing happened.**
Check whether "Keep vanilla 'Equip'" is on; with it on the button performs the vanilla action and hanging does not work.

**`L` does nothing.**
`L` only works while the lantern is hung and does nothing once it is taken down. If it still does nothing while hung, the key is probably taken by another mod — rebind it in the settings.

**The lantern is invisible or gives no heat.**
Turn on "Verbose logging"; the log gains a line starting with `[挂腰] 诊断`. Paste that line and the cause is easy to pin down.

**The lantern is not on my belt after a scene change.**
Make sure `ModData.dll` is in `Mods/` — that is what remembers the hung state across scenes. A "[挂腰/存档] 写入失败" line in the log means it is missing or not working.

**No fuel or condition line on the HUD.**
Check that "HUD readout" is on. If it is on but the text sits on top of something else, move it with the X and Y offset sliders.

### License

MIT — see [LICENSE](LICENSE).

---

## 中文

提灯挂腰，解放双手。

### 功能

**挂腰**

- 在物品界面选中背包里的提灯，原本的「装备」按钮变成「悬挂/卸下」，点一下挂到腰上、双手恢复为空，再点一次收回背包。
- 悬挂时物品介绍正文末尾会多出一行「[提灯已悬挂]」，卸下后这行字消失。
- 悬挂时 HUD 上会显示「灯油 87% · 耐久 100%」，位置贴着原版那根耐久条；卸下后这行字消失。
- 挂上去自动点燃：照明范围、烧油速度、取暖效果全部沿用原版数值，腰上那盏灯是真的在烧油、真的在散热。
- 挂腰时不占用手部：可以同时拿步枪、信号棒、爬绳、生火、开门。

**开关灯**

- 挂腰状态下按 `L` 开关提灯，屏幕中间不会弹任何红字提示——灯亮不亮肉眼可见。
- 没挂腰（收起或拿在手上）时 `L` 完全不响应，不会抢游戏原有按键。
- 灯里没油时按 `L` 不会点出一盏空灯。

### 前置条件

- 《The Long Dark》**2.55**（针对 2.55 编译）、[MelonLoader](https://melonwiki.xyz/) **0.7.3** 或更高
- **ModSettings** 与 **ModData**（都必需：前者是设置页，后者用来跨场景记住悬挂状态）

### 安装

1. 确认游戏目录下已有 `MelonLoader/` 文件夹（没有就先装 MelonLoader 并启动一次游戏）
2. 把 `LanternBelt.dll` 复制到 `TheLongDark/Mods/`
3. 重启游戏

### 使用

1. 打开背包，点击背包里的一盏提灯
2. 在右侧物品面板点「悬挂/卸下」——提灯挂到腰上并亮起，介绍里出现「[提灯已悬挂]」
3. 按 `L` 开关灯；再点一次「悬挂/卸下」即从腰上卸下，那行字随之消失

设置入口：**选项 → Mod Settings → Lantern Belt**。

### 设置

| 项 | 说明 | 默认 |
|---|---|---|
| 开关提灯（仅挂腰时） | 只在提灯挂腰时生效，其余时候按了没有反应 | `L` |
| 挂上去就点亮 | 关掉则挂上时保持熄灭，需要按开关灯键自己点 | 开 |
| 收起时熄灯 | 关掉则收进背包后灯继续烧油 | 开 |
| 读档后自动恢复 | 读档 / 换场景后自动把提灯重新挂回腰上，亮灭照旧 | 开 |
| 保留原版「装备」行为 | 开着则物品栏按钮恢复原版「装备」，悬挂功能整体失效 | 关 |
| 挂点基准 | 「玩家身体」跟着身体走；「相机」跟视角走，晃动更明显 | 玩家身体 |
| 左右偏移（米） | 负值在左胯，正值在右胯 | -0.22 |
| 上下偏移（米） | 相对脚底的高度 | 0.95 |
| 前后偏移（米） | 正值朝前 | 0.12 |
| 蹲下时跟着下移 | 蹲下时提灯一起往下挪，不悬在站立时的腰位上 | 开 |
| 蹲下下移量（米） | 蹲下时挂点降低多少。灯还偏高就调大，压进腿里就调小 | 0.35 |
| 整体缩放 | 挂点选「相机」时通常要降到 0.5 左右 | 1.0 |
| 每帧对齐挂点 | 关掉后灯会留在原地，只在排查问题时关 | 开 |
| 显示灯油与耐久 | 挂腰时在 HUD 上显示灯油与耐久百分比，关掉则什么都不显示 | 开 |
| 提示水平偏移 | 相对原版耐久条左右移动，正值向右 | 0 |
| 提示垂直偏移 | 正值向上、负值向下 | -70 |
| 提示字号 | HUD 提示的文字大小 | 20 |
| 提示语言 | 影响屏幕提示与物品栏按钮文字 | 自动 |
| 输出详细日志 | 把挂点、渲染器、层、热量源写进 `MelonLoader/Latest.log` | 关 |

### 调整挂点位置

默认位置是玩家左胯。如果灯飘在半空、穿进身体或看不见，只调三项偏移即可，全部拖动即时生效，不用重开游戏：

- **灯太低 / 太高**：改「上下偏移」，0.95 大约是腰的高度；蹲下时的高度另由「蹲下下移量」控制
- **灯在身体里面 / 看不见**：把「前后偏移」加大、把「左右偏移」往 0 收；仍被衣服挡住就把「挂点基准」切成「相机」并把缩放压到 0.5 左右

### 卸载

删掉 `TheLongDark/Mods/LanternBelt.dll` 即可。

存档里不会留下本 mod 的字段。已挂腰的提灯在卸载后会变回「背包里一盏点着的灯」，按原版规则它会继续烧油——记得先收起再卸载。

悬挂记录由 `ModData` 存在 `Mods/ModData/` 下的独立文件里，本 mod 卸载后没人再读它，不影响存档。

更新记录见 [ChangeLog.md](ChangeLog.md)。

### 兼容性

- 只在提灯挂腰的那段时间里接管提灯的位置、渲染与开关状态，其余时候对游戏没有任何改动；不修改物品定义、燃油消耗速率与光照强度。
- 与其它装备类 mod 无已知冲突（未做系统性组合测试）；真正会互相影响的是也去接管提灯开关的 mod。

### 常见问题

**点了按钮没反应？**
看设置里「保留原版『装备』行为」是不是被打开了；打开时按钮是原版动作，悬挂功能不生效。

**按 `L` 没反应？**
`L` 只在悬挂状态生效，卸下后不响应。若悬挂后按 `L` 没反应，多半是键位被别的 mod 占用，改绑即可。

**灯看不见 / 没有热度？**
打开「输出详细日志」，日志里会有一行「[挂腰] 诊断」，把那一行贴出来即可定位。

**换个场景提灯就不在腰上了？**
确认 `Mods/` 里有 `ModData.dll`——跨场景记住悬挂状态靠它。日志里出现「[挂腰/存档] 写入失败」就是没装或没生效。

**HUD 上没有灯油 / 耐久那行？**
先看设置里「显示灯油与耐久」是不是被关掉了；开着但字压在别的东西上，就用水平 / 垂直偏移把它挪开。

### 许可证

MIT —— 见 [LICENSE](LICENSE)。
