# Lantern Belt · 开发记录

面向维护者。玩家向说明见根目录 [README.md](../README.md)。

## 1. 一句话机制

提灯挂腰 = **让提灯留在背包里，但把它的物体摆到玩家腰侧、打开渲染、保持 active，
并让 `KeroseneLampItem` 处于「已点燃」状态**——点燃走原版 `TurnOn(suppressStartAudio: true)` 拿状态
（`m_On` / 热量 / 耐久判定），三段音效（悬挂 / 点火 / 燃烧）与点火闪灭由本 mod 按时间线自己排；
点火音是随包带的 WAV（Wwise 塞不进外部音频），经 `Core/LampAudio.cs` 解包后用系统接口 `winmm!PlaySound` 播；
第一人称手势由补丁逐处拦掉，热量则靠 `HeatSource.TurnOn()` 手动补登。

原版没有任何「离手还在烧」的路径：`PlayerManager.EquipItem` 换手时会调 `KeroseneLampItem.TurnOff`。
所以挂腰必须是「我们自己维持点燃状态」，而不是「借用原版某种收纳状态」。

## 2. 文件地图

| 文件 | 职责 |
|---|---|
| `LanternBelt.csproj` | net6.0 + STBlade 2.55.0；`ModSettings.dll` 从实机 `Mods` 引用、`Private=false`；`Assets/ignite.wav` 以 `EmbeddedResource` 打进 dll |
| `BuildInfo.cs` | 版本 / 作者 / loader 版本唯一来源（README、ChangeLog 必须与之一致） |
| `LanternBeltMod.cs` | MelonMod 入口：注册设置、绑定日志、每帧驱动、`L` 键 |
| `Settings.cs` | ModSettings 面板与落盘（`Mods/LanternBelt.json`） |
| `Core/LanternBeltManager.cs` | 状态机 + 每帧校正 + 渲染/热量接管 + 挂点 + 读档恢复 + 音效时间线 |
| `Core/LampAudio.cs` | 自定义点火音：内嵌 WAV → 解包到 dll 旁 → `winmm!PlaySound` 直放（音量靠预缩放采样） |
| `Core/HudReadout.cs` | 挂腰时的 HUD 提示：克隆原版耐久条做油量条 + 自建 `UILabel` 显示百分比 |
| `Assets/ignite.wav` | 点火音本体（48kHz 立体声 PCM16，`2.61s`，已裁掉前导底噪）；换它就要重量闪灭时间线 |
| `Patches/PlayerManager_EquipItem.cs` | 物品栏切换入口、丢弃前收起、原版开关灯同步 |
| `Patches/PlayerAnimation_IgniteConfirm.cs` | 三处第一人称手势拦截；`UpdateEffects` 的 Postfix 同时推进点火闪灭 |
| `Patches/ItemDescriptionPage_EquipButtonLabel.cs` | 装备按钮文案「悬挂/卸下」（恒定文案） |
| `Patches/ItemDescriptionPage_Description.cs` | 悬挂时在物品介绍末尾追加「[已悬挂在腰带上]」 |
| `Patches/Localization_Get.cs` | 运行时翻译本 mod 的 `LANTERNBELT_*` 键 |
| `Patches/PanelHUD_Update.cs` | 借 HUD 每帧更新刷新挂腰提示 |
| `Loc.cs` | 屏幕提示与按钮文案的中英对照 |
| `ModLog.cs` | 静态日志出口（静态类拿不到实例 Logger） |

## 3. 状态机

只有两个状态，互斥且可逆，**入口只有一个：物品栏点击**。

```
        ToggleHolster(gi)                       ToggleHolster(gi)
[ 未挂 ] ────────────────► [ 挂腰: s_hung=gi ] ────────────────► [ 未挂 ]
                                  │
                                  └─ 丢弃 / 进容器 / 换场景 → 自动 Unhang
```

- `s_hung`：唯一真值，null 即未挂。同一时刻只允许一盏。
- `s_wantLit`：**我们希望的点燃状态**。实际状态每帧向它靠拢（`ReconcileLamp`），
  兜住三件事：原版卸下顺手熄灯、油尽、点燃有延迟。
- `s_restoreDeadline`：进场景后 30 秒内允许一次自动恢复，之后不再尝试，
  以防玩家手动收起后又被自动挂回去。
- `s_toggleGuardUntil` / `s_toggleGuardTarget`：**去抖**。物品检视面板的一次点击实测会到达
  两次 `PlayerManager.EquipItem`（13:15:11.162 悬挂 / .164 卸下，间隔 2ms），
  不去抖会「挂上即被自己卸下」，玩家只看到「提灯已收起」。同一指针 0.3 秒内的重复请求直接吞掉。

### 双触发为什么必须靠去抖，而不是找「唯一入口」

`EquipItem` 已经是最上游的汇合点，但链路上任何一个 mod 的 Prefix 手动回调原方法都会
再进来一次；本机 275 个 mod 里 FuelManager 就活跃在同一条链上。去抖是按「同一个 GearItem
在同一瞬间的重复请求」判定的，与调用者是谁无关，因此比绑死某个入口稳。

### 为什么是「每帧校正」而不是「一次性摆好」

第一人称武器 / 身体的变换每帧都在被动画改写，原版也会在某些时机（检视模式、装备、卸下）
重新摆放物品。挂腰的位置、渲染状态、点燃状态都必须在动画之后重新断言一次。
与 HuntingRoads 每帧把镜体钉在准星-照门中点上是同一个思路（见
`projects/HuntingRoads/SCOPE_MOUNT.md`）。

## 4. 机制依据（2.55 实机反汇编，改代码前先复核）

| 结论 | 依据 |
|---|---|
| 卸下 / 换手必然熄灯 | `PlayerManager.EquipItem` 内 `call KeroseneLampItem::TurnOff`；只针对 `m_ItemInHands`，**不遍历背包** |
| 灯不检查是否在手 | `KeroseneLampItem.Update`（RVA `0x120b3e0`）只读 `m_On`(0xa4)、`m_FlareIsPaused`(0xe8) |
| `TurnOn` 会自己开热量 | RVA `0x120ba30`：`HeatSource.TurnOn`（`0x1807d9630`）→ `mov WORD PTR [rdi+0xa4],1`（`m_On`+`m_TurnOnComplete`），再按 `FirstPersonHandsEnabled` 决定是否播点燃动画 |
| **热量靠自注册**，不在身上就没热 | `HeatSource.TurnOn` 内含 `List<object>.AddWithResize`，往 `HeatSourceManager.m_HeatSources` 加自己；对象离场后重新激活**不会重跑 Start**，所以必须手动补登 |
| 离手不能调 `Toggle()` | `KeroseneLampItem.Toggle` 先过 `PlayerAnimation.FirstPersonHandsEnabled` + `IsCancelableActionInProgress` |
| 背包物品不渲染 | `GearItem.m_IsHidden` + `m_MeshRenderers` / `m_SkinnedMeshRenderers`；只把 GameObject 激活会得到「有光没灯」 |
| 装备按钮文案可改 | `ItemDescriptionPage.GetEquipButtonLocalizationId(GearItem)` 是 `public static`，返回本地化键，再由 `Localization.Get(string)` 翻译 |
| 音效只能借原版 | `TurnOn` / `TurnOffDelayed` / `TurnOff` 内部各自 `GameAudioManager.PlaySound(m_*Event, gameObject)`；自己摆字段绕过它们 = 一点声音都没有（1.8.0 的起因） |

关键成员（编译面已核对，命名空间按 Il2CppInterop 扁平化规则）：

| 用途 | 成员 |
|---|---|
| 手持物品 | `PlayerManager.m_ItemInHands` |
| 装备 / 卸下 | `PlayerManager.EquipItem(GearItem,bool)`、`UnequipItemInHands()` |
| 检视中判定 | `PlayerManager.GearItemBeingInspected()` |
| 提灯组件 | `GearItem.m_KeroseneLampItem` |
| 开关灯 | `KeroseneLampItem.IsOn()`、`TurnOn(bool)`、`TurnOff(bool)`、`TurnOffDelayed()`、`HideEffects(bool)`、`StopLoopingAudio()`、`GetPercentFuelRemaining()` |
| 热量 | `HeatSource.TurnOn()`、`IsTurnedOn()`、`TurnOffImmediate()`；`HeatSourceManager.m_HeatSources`、`AddHeatSource()`；入口 `GameManager.GetHeatSourceManagerComponent()` |
| 可见性 | `GearItem.m_IsHidden`、`m_MeshRenderers`；`GameObject.layer` |
| 背包 | `GameManager.GetInventoryComponent().m_Items` |
| 玩家 / 相机 | `GameManager.GetPlayerTransform()`、`GetVpFPSCamera()` |
| 手部状态 | `PlayerAnimation.FirstPersonHandsEnabled(GearItem)`（实例方法，经 `GameManager.GetPlayerAnimationComponent()` 取） |

### 第一人称手势：三处，必须逐处挡（按 L 卡手的根因）

挂在腰上时手上没有灯，第一人称动画没有可绑定的模型，`PlayerAnimation` 的状态机就停在
「正在点燃 / 熄灭中」：**不能装备枪械 / 石块 / 木炭，也不能与物品互动**。

| # | 调用点 | 方法 | 挡法 |
|---|---|---|---|
| 1 | `KeroseneLampItem.TurnOn` 收尾（`0x120BCB3`） | `Trigger_Generic_Ignite(bool)` | 返回假即可（调用方不看返回值） |
| 2 | `KeroseneLampItem.UpdateEffects` 第一段（`0x120CE3F`） | `Trigger_Generic_Ignite_Confirm(...)` | 返回假即可（同上） |
| 3 | `KeroseneLampItem.TurnOffDelayed` 开头（`0x120C09A`） | `Trigger_Generic_Extinguish(cb)` | **必须返回真** |

第 3 处和前两处不一样：调用方写的是

```csharp
if (!PlayerAnimation.Trigger_Generic_Extinguish(cb)) { Debug.LogError(...); return; }
```

返回假就整段退出，连熄灭起始音都不会播、`m_TurningOff` 也不会置位。所以补丁跳过原方法、
把 `__result` 写成 `true`，等于「假装手势已经开始」。

触发条件都是 `PlayerAnimation.FirstPersonHandsEnabled(m_GearItem)` 为真——**空手时为真**，
所以挂腰时这三处全都会命中，不能指望「手上没东西就不会触发动效」。

**1.2.0 只挡了第 2 处**，而按 `L` 点亮走的是 `TurnOn` → 第 1 处，所以卡手照旧。

判定只认「挂腰的那一盏」：`TurnOn` / `UpdateEffects` / `TurnOffDelayed` 各挂一个 Prefix/Postfix
记录 `Current`，手势补丁里用 `IsHungLampComponent(Current)` 比指针。玩家点篝火、点火把不受影响。

### 音效与闪灭：三段声，各管各的（1.9.0 / 1.10.0）

版本演进有三步，前两步别退回去：

1. **≤1.7.5** 是 `TurnOnSafely`：直接摆 `m_On = true` / `m_TurnOnComplete = true` / `m_TurningOff = false` /
   `m_TurnOnEffectsTimer = 0`，再 `m_FXGameObject.SetActive(true)`。能点亮，但整段点火流程被跳过——
   没火花、没点火音、没循环音、没「延时点亮」那一下闪，连 `GearItem.CheckForBreakOnUse()` 都省了。
2. **1.8.0** 改回调原版 `TurnOn(false)`。音有了，但原版那一句是**两个事件同一帧**：
   `m_TurnOnEvent`（点火音）+ `m_LoopWithDelayEvent`（燃烧循环）一起播，听上去是一声混响；
   而「拿出提灯」的金属声因为装备流程被 `EquipItem` 拦掉，压根没响过。
3. **1.9.0** 把三段声拆开各归各的动作，但点火音仍是原版那个 `0.135s` 的短促擦火，
   与「呲—呲—点燃」的听感差得远。

1.10.0 的分法——三段声各归各的动作：

| 动作 | 用什么 | 时机 |
|---|---|---|
| 悬挂 | `gi.m_GearItemData.m_PickupAudio`（原版「拿出」） | 点按钮那一帧 |
| 点火 | **本 mod 自带音频**（`Assets/ignite.wav`，`2.61s`） | 悬挂声之后 `0.45s`（`GearAudioLeadSeconds`） |
| 燃烧 | `lamp.m_LoopWithDelayEvent` | 点火音快放完的 `2.50s`（`FlickerLoopAt`） |
| 收起 | `gi.m_GearItemData.m_StowAudio`（原版「收回」） | 收起后 `0.35s`（`StowAudioDelaySeconds`），排在熄灭音之后 |

`IgniteLamp` 因此改传 `TurnOn(suppressStartAudio: true)`：原版只摆 `m_On`、热量、耐久判定，
不播那两个事件、不点火花、也不触发第一人称手势（少一处要挡的），音效由本 mod 按上表自己排。
那条分支会顺手起 `m_LoopEvent`（`0x58`）的循环音，所以紧接着 `StopLoopingAudio()`。
自己播的 id 要写回 `m_TurnOnAudioID` / `m_LoopAudioID`，否则 `TurnOff` 停不掉，关灯后火还在响。

### 自定义点火音：为什么绕开 Wwise（1.10.0 起，播放通路 1.10.3 定）

Wwise 的事件是编译进 `.bnk` 的 id，运行时塞不进外部音频；想换音色只有两条路：
改 bank（不可行），或者干脆不走 Wwise。`Core/LampAudio.cs` 走后者，最终用的是**系统接口**：

- 音频以 **PCM16 WAV** 嵌在程序集里（`<EmbeddedResource Include="Assets/ignite.wav" />`）。
- 运行时解包到 dll 旁边的 `LanternBelt_ignite.wav`，再用
  `winmm!PlaySound(path, 0, SND_FILENAME | SND_ASYNC | SND_NODEFAULT)` 直放。
  WAV 由 Windows 解码，与游戏的 Wwise 混音互不干扰；停止用 `SND_PURGE`。
- 系统接口没有音量参数：**音量靠预缩放采样**——按当前音量另存一份 `*_v.wav` 再播，
  同一音量只生成一次。所以设置里的「点火声音量」照常有效。
- **Unity 的两条路在 2.55 上都断了，别再试**：
  - `AudioClip.Create` + `SetData` 拿到的是 length / frequency / channels 全 `0` 的空壳，
    `PlayOneShot` 静默失败、连异常都不抛（1.10.0，日志 `已载入 0.00s 0Hz 0ch`）；
  - `UnityWebRequestMultimedia.GetAudioClip` 内部要 `new DownloadHandlerAudioClip(...)`，
    而这个构造函数在 il2cpp 桩里被剥离，运行时报
    `Method not found: Void DownloadHandlerAudioClip..ctor(String, AudioType)`（1.10.1）。
  两条路都是**静默退回原版**，于是听到的是原版那声 `Play_LanternLight`，闪灭却按 2.6 秒走。
- 拿不到音频（资源缺失 / 系统接口拒绝）时 `Play` 返回假，`PlayIgniteAudio` 退回原版
  `m_TurnOnEvent`——音色不对但至少有声音，灯与闪灭时间线不受影响。
- 日志判据：`[挂腰] 自定义点火音已就绪 2.61s 48000Hz 2ch（系统播放）`。
  出现 `自定义点火音不可用，退回原版音效：…` 就是没走通，后面那行 `音效 点火(原版)` 会说明它退了。

音频三个瞬态是闪灭时间线的依据（`ffmpeg` 抽 1ms 峰值量的）。**量之前先把前导底噪裁掉**，
否则「播放起点」不等于「第一声开口」，闪灭会整体提前——这正是 1.10.1 的毛病：
原文件 0~`0.040` 秒只是一层底噪，第一下打火石在 `0.074` 秒，而闪灭从按下开灯键就开始计时，
于是光比声早约 `40` 毫秒，听得出来。现在 `Assets/ignite.wav` 已从 `0.038` 秒处裁过，
三个尖峰相对新起点是：

| 时刻 | 现象 | 对应闪灭 |
|---|---|---|
| `0.036s` | 第一下打火石，峰值 `14612` | 此刻是亮的（`0 ~ 0.09`） |
| `0.09s` | 第一声余韵散尽 | 灭着等第二声 |
| `0.618s` | 第二下，峰值 `10964` | 再亮一下（`0.61 ~ 0.70`） |
| `1.642s` | 低频火声涌起，过零率掉到 `0.01` | 转长亮、`HideEffects(false)` |
| `2.607s` | 音频结束 | `2.45` 起循环音接上，交叠一小段 |

换音频文件就得重新量这三个点，改 `FlickerFirstOff` / `FlickerSecondOn` / `FlickerSecondOff` /
`FlickerFinalOn` / `FlickerLoopAt` / `FlickerDurationSeconds`；否则闪光会和声音错位。
裁过的音频交给 `EnsureFile` 解包到 `Mods/`，它按时间戳判断新旧：文件比 dll 旧就当作
上一版的缓存覆盖掉，比 dll 新（玩家自己换的）则保留。

### 闪灭：短亮 — 短亮 — 长亮

`UpdateEffects` 每帧做三件事：`timer += deltaTime`；`if (timer <= m_TurnOnEffectsDelay) return;`；
然后按 `Weather.UseOutdoorLightingForLightSources()` 开关 `m_LightOutdoor` / `m_LightIndoor` /
`m_LightIndoorCore` 的 `enabled`。闪灭就靠两点：

- **冻住它的计时器**：闪灭期间每帧写 `m_TurnOnEffectsTimer = -4f`（`FlickerTimerFreeze`）。
  负值让它永远落在 `timer <= delay` 那一支里，直接早退，光源就不会被它重写。
  **不能只写 `-1`**：它每帧 `+= deltaTime`，整段两秒半下来 `-1` 会被加过门槛、原版抢先点灯；
  `-4` 才有余量。也不能置 `0`，那样一帧就越过了。
- **抢在它后面写**：光源写在 `KeroseneLampItem.UpdateEffects` 的 **Postfix** 里。
  `MelonMod.OnUpdate` 与 MonoBehaviour 的 `Update` 之间没有顺序保证，只有 Postfix 一定在原版之后。

明暗表是常量（`FlickerFirstOff` / `FlickerSecondOn` / `FlickerSecondOff` / `FlickerFinalOn`）：
`0 ~ 0.09` 亮、`0.09 ~ 0.61` 灭、`0.61 ~ 0.70` 亮、`0.70 ~ 1.64` 灭、`1.64` 起常亮（长亮）。
**时间表的每一项都是「相对第一声开口」**，因为音频已经裁过前导底噪，`t = 0` 就是第一下打火石，
所以 `FlickerLit` 里没有「先灭一会儿」的分支。`1.64` 才 `HideEffects(false)`，`2.45` 接上燃烧循环，
`2.61` 收尾：计时器推成 `delay + 0.01`，下一帧原版重新接管光源（按天气选室内 / 室外，别自己瞎开三盏）。

`SetLampVisual` 同时改 `m_FXGameObject` 与三盏 `Light`：火焰对象与光照一起明灭才像擦火；
只改 `Light.enabled` 的话，若光源挂在特效对象下面，父物体一关就白写。

闪灭中途按 `L` 关灯：`ExtinguishLamp` 先 `EndFlicker`，否则它每帧都在抢光源；
灯自己的 `Update` 没在跑时（被停用 / 销毁）由 `Tick` 里 `FlickerDurationSeconds + 1f` 的超时兜底收尾。

### 熄灭：两级流程，自己收尾

`TurnOffDelayed()` 只做一半——播**熄灭起始音**（`m_ExtinguishStartEvent`）、置
`m_TurningOff = true`、`m_TurnOffTimer = 0`；真正的 `TurnOff(playAudio: true)` 本该由灯自己的
`Update` 在 `m_TurnOffDelay` 秒后调用，那时才播第二声（`m_TurnOffEvent`）并停掉循环音、
关热量、关火焰特效。

**但挂腰时那段计时永远不会跑（1.10.3 查明）**。`KeroseneLampItem.Update`（RVA `0x120B3E0`）里
熄灭分支的头两句是：

```
call  PlayerAnimation.FirstPersonHandsEnabled(m_GearItem)
test  al, al
jne   skip                      ; ← 为真就跳过整段熄灭计时
```

挂腰时双手是空的，`FirstPersonHandsEnabled` 恒为真，永远走 `skip`。所以 `m_TurnOffDelay`
设成多少都没用，灯只能等 `ExtinguishWatchdogSeconds`（5 秒）兜底才灭——玩家看到的就是
「按完关灯，灯还亮五秒」。

**现在由本 mod 自己收尾**：`ExtinguishLamp` 在 `TurnOffDelayed()` 之后记下
`s_turnOffAt = now + delay`，`Tick` 里的 `PumpPendingTurnOff()` 到点调 `TurnOff(true)`
（真关灯 + 播第二声）。`m_TurnOffDelay` 照样写上，万一原版条件变了它也能自己走完。
`CancelExtinguish` 与 `ClearState` 都要清 `s_turnOffAt`，否则撤销之后下一帧又被灭掉。

**延迟取设置值，默认 `2.8s`**（`DefaultTurnOffDelaySeconds`）。2.55 的 prefab 原本是 `3.67s`，
而熄灭起始音自己不到 `0.1s` 就落下——对照实机录像：`0.525s` 一声短促爆发，画面亮度一直是
`64.3`，直到 `4.20s` 才一步掉到 `31.8`。`m_TurnOffDelay` 是共享字段，所以 `Hang` 时记进
`s_origTurnOffDelay`、`Unhang` 时还回去，不把手持提灯的观感一起改掉。

状态机里 `!s_wantLit && IsOn()` 时**只调一次** `TurnOffDelayed`，之后靠 `m_TurningOff`
跳过；每帧校正不会重复触发。三个坑：

- **熄灭途中灯仍是亮的**（`IsOn()` 还是真）。这时不要把 `s_wantLit` 当成已被执行，
  也不要立刻 `SetHeat(false)`——热量由原版在 `TurnOff` 里关。
- **`PumpPendingTurnOff` 到点后 `m_TurningOff` 已被别处清掉**（比如连按两下触发
  `CancelExtinguish`），那就什么都不做，灯保持亮着。5 秒兜底只在 `Tick` 长时间不跑时才会命中，
  日志出现 `[挂腰] 熄灭超时，直接关灯` 说明正常路径没走通，先查 `s_turnOffAt` 是不是被清了。
- **熄灭途中又按开灯键**：`TurnOn` 开头就 `if (m_On) return;`，直接再调是空的。要先把
  `m_TurningOff` / `m_TurnOffTimer` 清掉（`CancelExtinguish`），灯就保持亮着了。

### 音效字段（2.55 偏移）

`KeroseneLampItem`（`TLD.Gear`）：

| 偏移 | 字段 | 用途 |
|---|---|---|
| `0x30` | `m_TurnOnEffectsDelay` | 点火后多久把光源点亮 |
| `0x34` | `m_TurnOffDelay` | 熄灭起始音到真正关灯的间隔 |
| `0x38` | `m_TurnOnEvent` | 点火音 |
| `0x40` | `m_ExtinguishStartEvent` | 熄灭第一声 |
| `0x48` | `m_TurnOffEvent` | 熄灭第二声 |
| `0x50` | `m_LoopWithDelayEvent` | 点火后延迟进入的燃烧循环音 |
| `0x58` | `m_LoopEvent` | `suppressStartAudio = true` 时直接起的循环音 |
| `0x98` / `0x9C` / `0xA0` | `m_TurnOnAudioID` / `m_ExtinguishStartAudioID` / `m_LoopAudioID` | `GameAudioManager.PlaySound` 返回的播放 id，`TurnOff` 用 `StopPlayingID(..., 100)` 掐掉 |
| `0xA4` / `0xA5` / `0xCC` | `m_On` / `m_TurnOnComplete` / `m_TurningOff` | 状态位 |
| `0xA8` / `0xB0` / `0xB8` / `0xC0` | `m_FXGameObject` / `m_LightIndoor` / `m_LightIndoorCore` / `m_LightOutdoor` | 火焰对象与三盏光源（`UnityEngine.Light`），闪灭就改这四个 |

`GearItemData`（`GearItem.m_GearItemData`，`0x348`）：

| 偏移 | 字段 | 用途 |
|---|---|---|
| `0xB8` | `m_PickupAudio` | 「拿出」装备的金属声，`PlayerManager` 的捡起路径用它 |
| `0xC0` | `m_StowAudio` | 「收回」装备的金属声，`PlayStowAudio()` 用它 |
| `0xC8` | `m_PutBackAudio` | 放回容器 / 背包的声（本 mod 未用） |

播放 API：`GameAudioManager.PlaySound(AK.Wwise.Event, GameObject)` 返回 `uint`。
灯的四个事件用 `lamp.gameObject` 当声源（`Update` 每帧 `SetAudioSourceTransform` 跟住它，
所以挂腰时声音从腰上那盏灯发出）；装备音用 `InterfaceManager.GetSoundEmitter()`——
收起时灯已被隐藏，挂在它身上的声源不会再发声。

> 命名空间注意：Wwise 类型在互操作程序集 `Il2CppAK.Wwise.Unity.API.WwiseTypes.dll` 里，
> 命名空间是 **`Il2CppAK.Wwise`**（不是 dump 里的 `AK.Wwise`），写 `AK.Wwise.Event` 编译不过。

## 5. 注入点

| 补丁 | 类型 | 做什么 | 为什么在这里 |
|---|---|---|---|
| `PlayerManager.EquipItem` | Prefix（返回 false 吞掉原版） | 提灯 → `ToggleHolster`；其它物品放行 | 所有「装备」路径的汇合点（面板按钮 / 快速装备 / 拾取自动装备），与 HuntingRoads 挂瞄准镜同法 |
| `ItemDescriptionPage.GetEquipButtonLocalizationId` | Postfix | 提灯 → 返回 `LANTERNBELT_Mount` | 不改控件只改文案，手柄提示与原版事件链全部保留 |
| `ItemDescriptionPage.BuildItemDescription` | Postfix | 悬挂时 `__result += "\n" + Get("LANTERNBELT_Mounted")` | 与 HuntingRoads 给装镜的枪加描述行完全同法，不碰 UI 控件 |
| `KeroseneLampItem.UpdateEffects` | Prefix / Postfix | Prefix 记下「正在更新特效的是哪盏灯」；Postfix 推进点火闪灭（写光源必须在原版之后） | Prefix 给手势判据，Postfix 是唯一能稳定压过原版光源写入的位置 |
| `KeroseneLampItem.TurnOn` | Prefix / Postfix | 记下「正在点亮的是哪盏灯」 | 给下面两行提供判定依据 |
| `PlayerAnimation.Trigger_Generic_Ignite` | Prefix（返回 false） | 发起者是挂腰灯 → 吞掉 | `TurnOn` 收尾那次，按 L 卡手就是它 |
| `PlayerAnimation.Trigger_Generic_Ignite_Confirm` | Prefix（返回 false） | 发起者是挂腰灯 → 吞掉 | `UpdateEffects` 里那次，兜底 |
| `Localization.Get` | Postfix | 只替换 `LANTERNBELT_*` 前缀的键 | 不引入本地化库、不污染游戏字符串表 |
| `PlayerManager.Drop(GameObject,bool)` | Prefix | 挂腰的灯被丢弃 → 先 `Unhang(restoreActive:false, forget:true)` | 否则父节点还在玩家身上，落地物体会跟着人走；丢弃是玩家表态，意图一起清 |
| `KeroseneLampItem.Toggle` | Postfix | 同步 `s_wantLit` | 玩家通过原版途径开关灯时，别让每帧校正把操作改回去 |
| `Panel_HUD.Update` | Postfix | 挂着灯时刷新油量条与「灯油 / 耐久」文字，否则隐藏 | 面板实例直接可拿（不必 `GetPanel<T>()`），HUD 停用时 `Update` 不跑，菜单状态天然不用判断 |

驱动：`MelonMod.OnUpdate` → `LanternBeltManager.Tick()`（每帧：挂着就 `Apply`，没挂就 `ReconcileIntent`）
+ `OnSceneWasLoaded` → 更新意图快照的镇定期并重写一次记录。

### 跨场景 / 读档恢复（意图快照 + SaveLedger）

早期实现是「猜」的：原版把灯亮灭写进 `KeroseneLampItemSaveDataProxy.m_OnProxy`，而卸下必然熄灯，
所以「背包里有一盏点着的提灯」被当成挂腰的证据。**这条路在玩家熄灯后出门时会彻底失效**
（实测 19:55:12 熄灯 → 19:55:19 换场景 → 日志里没有任何恢复记录），挂腰状态凭空消失。

1.5.0 改用 `ModData` 明确记录，写法照 `HuntingRoads/SaveLedger.cs`：

```csharp
private static readonly ModDataManager s_data = new ModDataManager("LanternBelt", false);
s_data.Save("1|GEAR_KeroseneLamp_Spelunkers|97.166|0");   // 挂腰|物品名|燃油百分比|亮着
```

燃油是 `KeroseneLampItem.GetPercentFuelRemaining()` 的**原样百分比（0~100）**，不是 0~1 的比例；
ModData 侧 `[ModData] Cache Saved (212)` 与 `Mods/ModData/sandbox3.moddata` 里的 zip 条目可以直接对照验证。

#### 为什么「只有点亮着的提灯」能跨区域（1.5.0 的真 bug）

实测日志（同一局，三种跨区域）：

| 时刻 | 灯状态 | 结果 |
| --- | --- | --- |
| 22:11:38 CoastalHouseE → CoastalRegion | 悬挂、**熄灭** | **没有恢复**，玩家 22:11:51 手动重挂 |
| 22:12:08 CoastalRegion → CoastalHouseE | 悬挂、点亮 | 22:12:10 `按存档记录恢复悬挂（…，灯亮）` |
| 22:12:23 CoastalHouseE → CoastalRegion | 悬挂、点亮 | 22:12:29 恢复 |
| 22:12:56 CoastalRegion → HighwayTransitionZone | 悬挂、点亮 | 22:12:58 恢复 |

亮不亮本身不参与任何判定，真正的差别是**记录有没有活到恢复那一刻**：

1. `Apply()` 里有一条「灯不在背包里就 `Unhang`」的守卫。跨区域是「保存 → 销毁世界 → 重建世界」，
   重建过程中 `GearItem.m_InPlayerInventory` 会短暂为假，于是我们自己把灯拆了。
2. `Unhang` 会顺带 `SaveLedger.Clear()`（写 `"0"`），记录当场作废。
3. 记录没了以后只剩「背包里点着灯」的兜底猜测——**恰好只有点亮着的那盏猜得回来**。

所以症状看起来是「灯亮才存得住」，实际是「记录被自己擦掉，只剩兜底」。

#### 1.5.1 的修法

- **内存意图快照**：`s_intentValid / s_intentName / s_intentFuel / s_intentLit`。它是跨场景恢复的第一依据，
  不受 ModData 读取时机影响，也不怕中途有人把记录擦掉；**只有玩家的显式操作才清它**。
- **`Unhang(..., forget)` 参数**：`forget` 默认 `false`。只有「玩家亲手卸下 / 丢弃 / 塞进容器」传 `true`；
  场景切换、物体销毁这类内部路径一律保留意图。这是本次修复的核心——不再有任何内部路径能擦掉记录。
- **场景镇定期**：`OnSceneChanged` 之后 8 秒内（`SceneSettleSeconds`）不做背包校验；
  过了镇定期，`!m_InPlayerInventory` 还要连续成立 1.5 秒（`InventoryGoneGraceSeconds`）才认账。
- **对账循环 `ReconcileIntent()`**：`Tick` 里只要没挂着灯就每 2 秒对一次账，不再依赖「30 秒窗口是否开着」。
  没有快照时先 `SaveLedger.TryLoad` 接回来（读档 / 重启游戏），接不回来且在新场景窗口里才走旧的猜测。
  找不到灯时每 10 秒打一行 `[挂腰] 还在等那盏提灯进背包（…）`，30 秒后才放弃并清记录。
- **进新区域时重写记录**：`OnSceneChanged` 用快照字段（不碰 `GearItem`，旧物体可能已销毁）
  再写一遍 `SaveLedger.Write`，把「应该挂着」钉进当前存档槽。
- **主菜单 / `Empty` 是会话边界**：跳过恢复窗口，并作废内存快照（记录留在 ModData 里，按存档槽分开），
  否则退到主菜单等满窗口会把记录当成「找不到那盏灯」清掉。

恢复时 `s_restoring = true`，`Apply` 会放宽背包校验——`FindLantern` 已经证明那盏灯在
`Inventory.m_Items` 里，不需要再被自己的守卫拆一次。

### HUD 提示：为什么必须自己画（1.6.0 ~ 1.7.1）

玩家要的「挂腰时看得见耐久 / 灯油」，原版没有现成出口：

- `EquipItemPopup` 才是画那根耐久条的地方，而它由 `PlayerManager.m_ItemInHands` 驱动——
  **灯挂到腰上时手是空的**，所以整块不显示。
- 就算把手里的东西硬塞成提灯，`EquipItemPopup.Update`（RVA `0x66e630`）的取数分支是
  `TorchItem.GetBurnProgress` / `FlareItem.GetNormalizedBurnTimeLeft` / `FlashlightItem.GetNormalizedCharge` /
  `NoiseMakerItem.GetNormalizedWickTimeLeft` / 兜底 `GearItem.GetNormalizedCondition`，
  **没有 `KeroseneLampItem` 分支**——灯油永远不会出现在那根条上。
- `KeroseneLampItem.m_HUD` 引用只用于 `Panel_HUD.CancelItemProgressBar`（加注被中断时收进度条），
  与显示无关。

所以 `Core/HudReadout.cs` 自建一个 `UILabel`：

- 字体 `GameManager.GetFontManager().GetUIFontForCharacterSet(...)`：中文走 `CharacterSet.CJK`，
  英文走 `Latin`（拉丁字库缺中文字形，反之亦然）。
- 父节点固定 `Panel_HUD.transform`，跟着 HUD 一起显隐；`depth = 100` 压在常规 HUD 文字之上。
  层级必须是**三层**：容器（空物体，只管位置）→ 文字子物体 + 条子物体，
  **绝不把 `UILabel` 加在容器身上**（1.7.5 踩过，见下）。
  整块挂在 `Panel_HUD` 下，**不挂进原版控件的子树**：那棵子树整套 `SetActive(false)` 时会一起消失。
- 数值：灯油 `KeroseneLampItem.GetPercentFuelRemaining()`（0~100），耐久 `GearItem.m_CurrentHP`（0~100），
  两者都取整——文字只在整数变化时才重新赋值，避免每帧重建 NGUI 网格。

驱动点在 `Panel_HUD.Update` 的 Postfix：面板实例直接递进来，不必走 `InterfaceManager.GetPanel<T>()`
（面板未加载时会抛）；HUD 停用时 `Update` 本来就不跑，菜单 / 过场状态不用额外判断。
场景切换时 `OnSceneChanged` 调 `HudReadout.Reset()`，因为 HUD 面板连同子物体一起被销毁。

#### 定位：不要用原版控件当锚点（1.7.1 实机踩坑）

1.6.0 / 1.7.0 把锚点取在 `Panel_HUD.m_EquipItemPopup.m_ObjectDuration` 上。**实测这条链是断的**：
该字段在 2.55 里取不到，代码退回 `m_EquipPopupWidget`，而它在弹出框隐藏时位于原点——
结果整块提示飘到**屏幕正中央**（玩家截图：文字横在视野中心，条根本没建出来）。

现在按屏幕右下角定位，一步不依赖原版控件。**主路径是相机投影**（1.7.4 起）：

```csharp
Camera cam = UICamera.currentCamera ?? UICamera.mainCamera;   // 只用 UI 相机
Vector3 world = cam.ViewportToWorldPoint(new Vector3(1f, 0f, cam.nearClipPlane));
Vector3 corner = hudRoot.InverseTransformPoint(world);
```

相机投影的单位天然正确，不受 UIRoot 缩放风格、面板偏移影响。**不要用 `Camera.main` 兜底**：
那是玩家视角的世界相机，视口坐标与 UI 面板不在同一空间，算出来会把整块提示扔出屏幕。
拿不到 UI 相机才退回推算：

```csharp
int h = uiRoot.activeHeight;                       // 2.55 的 UIRoot 只有 activeHeight
float w = h * Screen.width / Screen.height;        // **没有 activeWidth，必须自己按宽高比算**
Vector3 center = hudRoot.InverseTransformPoint(uiRoot.transform.position);  // 面板偏移也要算进去
Vector3 bottomRight = center + new Vector3(w * 0.5f, -h * 0.5f, 0f);
```

`UIRoot` 拿不到时退到 `UIPanel.width/height`，再不行按 1920×1080 估。

边距的语义（1.7.5 起）按玩家会理解的方式定义：**距屏幕右边缘**量的是油量条**右端**到屏幕右边的距离，
**距屏幕底边缘**量的是那行文字**底边**到屏幕底边的距离。所以容器原点（= 整块中心）要再内缩半个块：

```csharp
blockW = max(条宽, 文字宽);  blockH = 条高 + 间隙 + 文字高;
containerPos = corner + new Vector3(-marginRight - blockW * 0.5f,
                                     marginBottom + blockH * 0.5f, 0f);
```

块内再按「顶边对齐条、底边对齐文字」排：条中心 `y = +blockH/2 - 条高/2`，
文字中心 `y = -blockH/2 + 文字高/2`。1.7.4 及以前量的是「整块中心到两边」，
条会有一半伸到屏幕外，边距数值与观感对不上。

#### 「拖了没反应」怎么定位（1.7.5）

这条日志**不受「输出详细日志」开关控制**，拖任意一项就打印一次：

```
[挂腰/布局] 设置 宽=188.3 粗=9.1 右=165.5 底=128.0 → 条=188x9 实测=188x9（模板 200x10）
            整块=188x40 容器=1234.5,-512.0 文字=-22.0 角=1420.0,-396.0（相机投影）
```

三种读数对应三种病因：

| 现象 | 结论 |
|---|---|
| 日志不出现 | `Panel_HUD.Update` 没跑（面板被停用），或挂腰状态丢了 |
| 「设置」那半截不跟着变 | 值没进 `Settings` 字段，问题在 ModSettings 绑定 |
| 「条=」不变 | 写入被 early-return 吃掉，或 `s_widgets` 里不是真正绘制的那批控件 |
| 「实测=」与「条=」差很远 | 模板基线不对（混进了标签 / 隐藏控件），或 `pivotOffset` 读错 |
| 「容器=」不变 | 有别的代码在写同一个 `Transform`（1.7.5 的病因就是这个） |
| 「角=」后面写「兜底 1920x1080」 | UI 相机与 UIRoot 都没取到，位置是估的 |

#### 容器位置被自己覆盖（1.7.5 真因）

1.7.3 / 1.7.4 报「边距拖了不动」时，日志显示 `条=` 会变、`容器=` 恒定 `0.0,-36.2`。
原因不在相机、也不在 `UIRoot`，而是**层级设计错了**：

```csharp
UILabel label = root.AddComponent<UILabel>();          // 文字长在容器身上
...
s_label.transform.localPosition = new Vector3(0, y, 0); // 于是这句就是写容器位置
```

`s_label.transform == s_root.transform`，`PlaceBarAndLabel` 每帧把刚按边距算好的容器位置
覆盖成「文字相对条的中心偏移」——边距算出来的值连一帧都活不过。
**结论：布局容器必须是空物体，任何 `UIWidget`（包括 `UILabel`）都不能加在它身上。**

#### 油量条：克隆而不是照抄 sprite 名（1.7.0）

1.6.0 只有一行文字，玩家仍要读数字才知道剩多少油，要求「跟原版那根白条一样」。做法是
**`Object.Instantiate` 原版 `m_ObjectDuration`**，而不是读它的 `spriteName` 再拼一个 `UISprite`：

- 克隆一次性继承 atlas、sprite、九宫格边框（`UISprite.type` 与 border）、`width`/`height`、
  `pivot`、`color`、`depth` 相对关系。手拼任何一项猜错，条就会歪或变形，而克隆不会。
- 克隆体里的 `UILabel`（原版百分比，量的是耐久）全部 `SetActive(false)`，否则会出现两个数字打架。
- 所有 `UIWidget.depth` 统一 `+60`，只抬高不打乱背景 / 填充的先后。
- 填充 sprite 靠 `m_SpriteFillDuration` 在模板里的**层级路径**对位（`Transform.Find`），
  对不上再取 depth 最高的 sprite 兜底；找不到只警告一次，条保持静态，不重建。
- 位置不做换算：克隆体 `localPosition = Vector3.zero`、保留自己的 pivot 与 scale，
  于是「偏移填 0」就等于「长在原版耐久条待的地方」。条中心与尺寸用
  `UIWidget.pivotOffset`（0~1 比例）配合 `width`/`height` 算，文字据此压在条下方正中。
- 每帧只写 `fillAmount = fuel / 100`，且差值 > 0.002 才赋值——它会 `MarkAsChanged` 重建网格。
- 模板可能比 HUD 本体晚就绪：`BarAlive()` 只看条本体，缺模板时按 3 秒间隔补建，
  文字先立起来，不至于整块不显示。

#### 尺寸与设置热更新（1.7.2 → 1.7.3）

1.7.1 上实机后玩家报「条横贯半个屏幕」「所有滑条都没反应」「显示油量条关不掉」；1.7.2 修完后
又报「粗细和两个边距拖了没感觉」「文字压在条上」。踩过的坑按顺序记：

- **缩放基准不能取「第一个 widget」**。克隆体常常是个没有 `UIWidget` 的容器，第一个子 widget
  可能只是条上的一小块；拿它当分母，比例会算成好几倍，整条被撑开。
- **不要靠「设置变了就重建」来响应滑条**。重建本身偏重，玩家改动量小时（边距十几像素、
  粗细两像素）视觉上等于没反应，还伴随闪烁。1.7.3 改成 `ApplyLayout` **每帧直接写**：
  位置、条宽、条粗、字号、文字落点，全部当场生效。为了不把 NGUI 打爆，每个值都先跟
  「上次写进去的值」比过，没变就不赋值（`s_appliedWidth` / `s_appliedHeight` / `s_appliedFontSize`）。
  只有**换语言**才重建，因为要换字体与字形集。
- **尺寸走「基线绝对映射」而不是「乘比例」**。克隆时把每个控件的 `width` / `height` /
  `localPosition` 存进 `BarBase[]`，之后按 `目标 / 基线` 独立换算：
  `width = base.Width * (目标宽 / 基线宽)`、`height = base.Height * (目标高 / 基线高)`。
  最终包围盒必然等于设置里的数字，反复拖滑条也不会累积误差，模板再怪也能被压回目标尺寸。
- **基线只能统计真正画出来的 sprite**（1.7.5）。`GetComponentsInChildren<UIWidget>(true)` 会把
  原版耐久条上那行「100%」百分比标签也算进来，它又宽又高，基线被撑成 `200x61`（真实条体只有几像素），
  于是按 `目标高 ÷ 61` 缩出来的条永远在 3~11 像素之间打转——玩家看到的「粗细没反应」就是这个。
  现在 `CaptureBaseline` 与 `TryBarBounds` 都过一道 `IsDrawable()`：`UILabel` 与
  `!activeInHierarchy` 的控件一律排除。
- **文字落点要减掉文字自己的半高**。块内按顶边/底边对齐：条中心 `= +块高/2 - 条高/2`，
  文字中心 `= -块高/2 + 文字高/2`；只减间隙会让文字上半截压在条上（实机截图就是这样）。
- **布尔开关必须双向写**。`if (x && !active) SetActive(true)` 少了「关掉时隐藏」这一半，
  开关就成了摆设。正确写法：`if (bar.activeSelf != cfg.ShowHudBar) bar.SetActive(cfg.ShowHudBar)`。
- **文字宽度要缓存**。`MeasureLabel` 里的 `UILabel.ProcessText()` 只在文案或字号变化时重算，
  否则每帧让 NGUI 重排文本是白花钱。

条的中心与尺寸取**所有可见 sprite 的并集包围盒**（`TryBarBounds`，用 `pivotOffset` 换算每条边），
不是某一个 widget 的 pivot——根节点没有 widget 时单看一个会把文字放偏。

## 6. 挂点

蹲下补偿：挂点挂在玩家根节点（脚底）上，是固定高度，所以蹲下时提灯会悬在原来的腰位。
`EnsureAnchor` 每帧读 `PlayerManager.PlayerIsCrouched()`，蹲下时把 `localPosition.y` 减去
`Settings.CrouchDropAmount`（默认 0.35 米），并用 `Mathf.Lerp(dt*8)` 跟随原版蹲、起的过渡。
之所以不直接拿相机高度差换算：相机站立≈1.7m、蹲下≈1.0m，但腰胯只降约 0.35m，
1:1 换算会让灯掉到小腿。

`EnsureAnchor()` 每次挂腰时新建一个空物体 `LanternBelt_Anchor`，父节点二选一：

- **玩家身体**（默认）：`GameManager.GetPlayerTransform()`，偏移按「脚底起算的米数」解释，`y=0.95` 约在胯部。
- **相机**：`GameManager.GetVpFPSCamera().transform`，跟随视角，晃动明显，需要压缩放。

提灯物体的 `localPosition` 归零、`localScale` 由设置给，位置与朝向全部由锚点承担——
这样「每帧重新定位」只写一个物体，不会和提灯的原始 transform 打架。

### 为什么不做骨骼挂点

玩家对象没有公开可靠的骨盆 / 腰带骨骼索引，按名字猜会在不同角色模型（男 / 女 / 剧情手）上失效。
用玩家根节点 + 可调偏移，稳定性优先。

## 7. 已知风险 / 待实机校准

1. **模型可见性**：已做「逐 Renderer 打开 + 层设回 Default」。若某盏灯的模型根本不随物品加载
   （`Addressables` 释放了 mesh），诊断行会显示 `渲染器=0/0`，此时才需要改成复制一盏纯展示灯。
2. **热量补登的时机**：`HeatSourceManager` 在关卡加载早期可能还没建好，`RegisterHeat` 失败后
   由每帧校正重试；若实机发现「挂上几秒后才开始回暖」，属于这种情况，不是 bug。
3. **收起后原摆放的还原**：`Unhang` 会把物体还给 `s_origParent`；父节点已随场景销毁时静默失败，
   此时只把物体 deactivate，交给原版装备流程重新摆放。
4. **`Time.timeScale > 0` 作为按键闸门**：假设开背包不暂停时间；若暂停，`L` 在背包里不响应，无副作用。
5. **恢复不再依赖「窗口是否开着」**：意图快照在内存里，跨场景 / 读档任意时刻都能对上；
   30 秒只是「背包里一直找不到那盏灯」的放弃时限，超时才会清记录。
6. **HUD 提示的落点按屏幕角算，不跟原版控件走**（1.7.1）：整块摆在「右下角 − 边距」处，
   换分辨率、换 UI 缩放都会跟着走，也不需要玩家校准像素值。块内顶边是条、底边是文字，
   间隙 `max(2, 字号 × 0.2)`，两者不重叠。
7. **布局容器不能带 `UIWidget`**（1.7.5）：容器是空物体，文字与条是它的子物体。
   把 `UILabel` 加在容器上会让「排文字」那一句直接改写容器位置，边距随之失效。
   若日志里 `面板=False`（HUD 根上找不到 `UIPanel`），标签不会被任何面板绘制，
   此时应改为挂到某个 `UIPanel` 子树下。

## 8. 排查方法

1. 开设置里的「输出详细日志」，挂一次灯，看 `MelonLoader/Latest.log`：
   - `[挂腰] 已打开 N 个渲染器，层=0` → 渲染接管成功
   - `[挂腰] 诊断 物体=… active=True layer=0 渲染器=N/N 网格父层=… 世界坐标=… 缩放=… 热量源=已开`
     → 一行看全：看不看得见看 `渲染器` 与 `layer`，暖不暖看 `热量源`，位置对不对看 `世界坐标`
2. 灯亮不亮：不亮先看油量（`GetPercentFuelRemaining`）。
3. 按钮文案不对：看 `Harmony 补丁已应用，共 N 个方法` 的 N 是否比上次多 3（文案 / 本地化 / 丢弃）。
4. 想确认当前是否挂腰：屏幕提示、日志，或按 `L` 看有没有反应（`L` 只在挂腰时响应）。
5. 跨场景没恢复，按顺序看这几行：
   - `[挂腰] 场景切换：<区域>（应恢复悬挂）` → 意图快照还在（没有后缀则说明快照已丢）
   - `[挂腰] 按记录恢复悬挂（…，灯亮/灭）` → 恢复了，亮灭照原样
   - `[挂腰] 还在等那盏提灯进背包（…）` → 背包里暂时没有那盏灯（或它在手上）
   - `[存档] 清除记录（…）` → 谁把它清了，括号里就是原因
   - `Mods/ModData/<存档槽>.moddata` 是个 zip，条目 `LanternBelt` 里能直接读到
     `1|GEAR_KeroseneLamp_Spelunkers|97.166|0`（挂腰|物品名|燃油百分比|亮着）
6. 音效 / 闪灭不对：开详细日志，按顺序看这几种行
   - `[挂腰] 音效 悬挂 id=… 事件=…` → 悬挂声；`id=0` 说明 `GearItemData.m_PickupAudio` 是空
   - `[挂腰] 自定义点火音已载入 2.61s 48000Hz 2ch 就绪=True` → 内嵌音频正常解开了
   - `[挂腰] 点火 闪灭=开 音效=开 时间线 亮到0.09/再亮0.61/长亮1.64s 循环音 2.45s 共 2.61s` → 时间线起点
   - `[挂腰] 音效 点火 自定义 2.61s 音量=1.00` → 播的是自带音频；
     变成 `[挂腰] 音效 点火(原版) id=…` 说明音频没载入、退回原版事件了
   - `[挂腰] 音效 燃烧循环 …` → `2.50s` 才有一行，早于这个时刻说明走的是别的分支
   - `[挂腰] 熄灭起始（0.20s 后关灯）` → 关灯第一级，括号里是实际生效的延迟
   - `[挂腰] 音效 收起 …` → 收起的金属声
   - 事件名（`事件=Play_…`）能直接告诉你哪个字段对上了哪个音；`<空>` 是字段没填，
     `<未知>` 表示 `Event.Name` 读不出来（不影响播放，只能听）
   - 音效=静 → 设置里「开关灯音效」被关了，或走的是读档恢复路径（那条路本来就静音）
   - 没看到「自定义点火音已载入」而出现「没找到内嵌的点火音资源」→ csproj 的
     `EmbeddedResource` 掉了，重新编译一遍；资源名必须是 `LanternBelt.Assets.ignite.wav`
   - 「音频载入失败：…」/「Unity 没有解出可播放的音频」→ 解包出的 WAV 有问题：
     删掉 dll 旁的 `LanternBelt_ignite.wav` 让它重解包；自己换文件时必须是 PCM16 WAV
   - 「自定义点火音已载入 0.00s 0Hz 0ch」→ 历史问题（1.10.0 用 `AudioClip.Create` 造出的空壳），
     1.10.1 起不会再出现；真出现说明又走回了动态造 clip 的老路
   - 「系统播放接口拒绝了这个 WAV」→ 系统不让放这个文件（路径 / 格式）；换回自带的 WAV 再试
   - 「生成音量副本失败」→ dll 所在目录不可写，把音量调回 1.0 可绕过（1.0 直接播原件）
   - 亮度不对（声音和闪光错位）→ 换过 `Assets/ignite.wav` 却没重量时间线，见 §4「自定义点火音」
   - 出现「原版点火失败，改为直接点亮」：`TurnOn` 抛异常了，灯会亮但没有声音，
     这时要看异常文本（多半是某个字段/方法在 2.55 上对不上）
   - 关灯后灯一直亮：`[挂腰] 熄灭超时，直接关灯` 会兜底，出现它就说明灯的 `Update` 没在跑
   - 只有光在闪、火焰不亮：`m_FXGameObject` 与 `m_LightIndoor` 不是同一层级——
     闪灭同时改这两处，缺一个就会出现「有光没火」或「有火没光」
7. HUD 提示不出现 / 位置不对：开详细日志看
   `[挂腰] HUD 提示已建立：锚点=(x, y, z) 条=已建/缺模板 面板=True/False`。
   - 锚点是容器（整块中心）的位置，正常应是右下角方向的正 x、负 y（如 `(810, -430, 0)`）；
     `x` 恒为 0 或与边距无关，说明另有代码在写同一个 `Transform`（见 §6 第 7 条）；
     明显为 `(0, 0, 0)` 说明 `UIRoot` 与面板都没拿到，走了 1920×1080 兜底
   - `[挂腰/布局]` 那行末尾带 `角=…（相机投影 / UIRoot … / 面板 … / 兜底 …）`，
     直接告诉你位置是按哪条路径算出来的
   - `面板=False` 说明没挂进任何 `UIPanel`，标签不会被绘制
   - `条=缺模板` 说明三级查找都没找到条模板，会按 3 秒间隔重试
   - `[挂腰] 油量条已建立：模板=… 尺寸=WxH 填充=… sprite=…` 一次给全条的尺寸与精灵名，
     `尺寸=0x0` 或 `填充=未找到` 就说明模板结构与预期不同（尺寸是**拉宽前**的模板原始尺寸）
   - `[挂腰] 油量条建好了但没认出填充 sprite` → 条会是静态的，把上一行一起发作者

## 9. 设计约束（改代码时别破坏）

| 约束 | 违反会怎样 |
|---|---|
| 不修改物品定义、燃油速率、光照强度——所有数值都走原版 | 与存档里的既有提灯对不上，玩家换 mod 后手感突变 |
| 不写游戏存档字段，挂腰状态只存进 `Mods/ModData/<存档槽>.moddata` | 卸载 mod 后游戏存档会残留脏字段 |
| **内部路径不得清掉意图快照**：`Unhang` 的 `forget` 只有玩家显式操作（卸下 / 丢弃 / 进容器）才为 `true` | 场景切换、物体销毁、加载中途的背包字段抖动都会把记录擦掉，跨场景必然丢状态 |
| 挂腰期间必须压制提灯自身物理（关碰撞 + 刚体设为运动学） | 每帧定位会和质量 / 重力打架，灯在腰上抖 |
| 悬挂状态只能由物品栏点击切换；`L` 只负责亮 / 灭 | 两条入口各写一半状态，`s_wantLit` 会与实际相反 |
| 挂腰期间不得拦截其它物品的装备动作 | 手必须是真空闲，否则玩法被 mod 削掉一半 |
| **自己播的音效 id 必须写回** `m_TurnOnAudioID` / `m_LoopAudioID` | 原版 `TurnOff` 只停这两个 id，漏写就是「灯灭了火还在响」 |
| **闪灭只能写光源，不要碰 `m_TurnOnEffectsDelay`** | 那是原版自己的节拍，改它会让手持提灯也跟着变 |
| **闪灭期间计时器必须冻在 `-4`**（不是 `-1`、更不是 `0`） | 原版每帧 `+= deltaTime`，冻得不够深会被加过门槛、它抢先点灯，闪灭就只亮不闪 |
| **`m_TurnOffDelay` 改完要还回去**（`Hang` 记、`Unhang` 还原） | 它挂在共享的 prefab 数据上，不还原就把「按一下就没」的手感带到手持状态 |
| **换 `Assets/ignite.wav` 必须重量闪灭时间线** | 四个时刻对应音频里的三个瞬态，只换音频不改常量，闪光就会比声音早或晚 |
| **`s_turnOffAt` 必须与熄灭流程同生共死**（`CancelExtinguish` / `ClearState` 都要清） | 撤销之后下一帧又被灭掉，玩家看到「关灯点不动」 |
| 所有跨 il2cpp 边界的调用都要 `try/catch`，异常只降级不抛出 | 一处空引用会连带打断同帧后面的挂腰校正与 HUD 刷新 |

## 10. 注释迁移对照表

源码注释只留「做什么 / 为什么（一句）/ 违反会怎样」，过程叙事一律下沉到本文。下表是已经搬过的块，
再想往注释里补叙事时先在这里对一下该写去哪。

| 原位置 | 迁移到 | 注释里保留 |
|---|---|---|
| `Core/LanternBeltManager.cs` 类头（四条机制清单） | §4 机制依据表 | 一行点名四条原版行为 + 指向 §4 |
| `Core/LanternBeltManager.cs` `ExtinguishLamp` 内（prefab 延迟 `3.67s` 的实测） | §4「熄灭：两级流程，自己收尾」 | 「prefab 里填到近四秒」一句 |
| `Patches/PlayerAnimation_IgniteConfirm.cs` 类头（三处手势的 IL 与双手锁死症状） | §4「第一人称手势：三处，必须逐处挡」 | 三处的方法名 + 第三处必须改写返回值 |
| `Core/SaveLedger.cs` 类头（早期反推方案的失效日志） | §5「跨场景 / 读档恢复」 | 记录格式 + 燃油是 0~100 百分比 |
| `Core/HudReadout.cs` 类头（`EquipItemPopup` 的取数分支清单） | §5「HUD 提示：为什么必须自己画」 | 原版没有提灯分支 + 三层层级 + 容器不能带 `UIWidget` |
| `Core/HudReadout.cs` `ApplyLayout`（为什么逐值比较才写） | §5「尺寸与设置热更新」 | 「写这几个属性会重建 NGUI 网格」+ 三条顺序 |
| `Core/HudReadout.cs` `ApplyBarSize`（基线绝对映射的推导） | §5「尺寸与设置热更新」 | 「按目标 / 基线独立换算，不累积误差」 |
| `Core/HudReadout.cs` `CornerLocal`（`UIRoot` 没有 `activeWidth`） | §5「定位：不要用原版控件当锚点」 | 主路径是相机投影，兜底要自己按宽高比算 |
| `Core/LampAudio.cs` 类头（两条 Unity 音频路的静默失败） | §4「自定义点火音：为什么绕开 Wwise」 | 「Unity 两路在 2.55 上不通，别再试」一句 |

反向规则：注释里**不写**版本号、RVA、裸日志行、排查过程、字段偏移——这些只有本文该有。
写完跑一遍 `check_docs.py`，它会把违反项的 `文件:行号` 直接列出来。

## 11. 维护约定

- **版本号只有一个来源**：`BuildInfo.cs` 的 `Version`。改完必须同步 `README.md` 徽章、
  `ChangeLog.md` 新增条目、`LanternBelt.csproj`；三处对不上就算没发版。
- **`README.md` 面向玩家**：不写字段名、不写类名、不写 Harmony / RVA。`docs/` 与源码注释面向维护者。
  修订 README 走 `tld-mod-readme` skill 的骨架（功能 → 前置 → 安装 → 使用 → 设置表 → 按症状调参 → 卸载 → 许可），
  写完跑 `check_readme.py`。
- **注释与本文按 `tld-mod-docs` 的四层归属落位**：一句结论进注释，叙事进本文，单点故障另开
  `docs/BUG-<症状>.md`（一处坑一篇，含复现条件与判据），版本演进进 `ChangeLog.md`。本文只留「现在为什么这样」。
- **改动触发复核的清单**（任一条命中就回去读对应节）：
  | 改动 | 必须复核 |
  |---|---|
  | 换 `Assets/ignite.wav` | §4 三个瞬态表 + 六个闪灭常量 + §10 对应行 |
  | 动 `ExtinguishLamp` / `PumpPendingTurnOff` | §4「熄灭」全节 + §9 的 `s_turnOffAt` 约束 |
  | 动 `ApplyLayout` / `ApplyBarSize` | §5「尺寸与设置热更新」+ §5「拖了没反应怎么定位」 |
  | 动任何 `Unhang` 调用点 | §5「跨场景 / 读档恢复」的 `forget` 语义 + §9 意图快照约束 |
  | 新增设置项 | `Settings.cs` 的 `[Name]` / `[Description]` / `[Slider]` + README 设置表 + §2 文件地图 |
- **改字段名可以强制丢旧配置值**：ModSettings 按字段名读 `Mods/LanternBelt.json`，改名等于让老键失效、
  取新默认值。默认值语义变了就顺手改名（`TurnOffDelaySeconds` → `TurnOffHoldSeconds` 就是这么做的），
  否则老玩家的旧值会盖住新默认。
- **不许擅自 `git commit`**：作者（Hemalurgic）自己在实机验证后才提交。协作方可以写提交标题与正文、
  可以编译并部署 dll 到实机目录用于测试。

## 12. 编译与部署

```
dotnet build LanternBelt.csproj -c Release
```

产物 `bin/Release/net6.0/LanternBelt.dll`。目标框架 `net6.0`，引用 STBlade `2.55.0` 的互操作程序集；
`ModSettings.dll` 从实机 `Mods` 目录引用且 `Private=false`（不能把它一起打包，否则版本冲突）。

部署（测试用）：

```bash
cp bin/Release/net6.0/LanternBelt.dll \
   "F:/SteamLibrary/steamapps/common/TheLongDark/Mods/"
```

`Assets/ignite.wav` 以 `EmbeddedResource` 编进 dll，**不需要**单独拷贝；mod 首次运行会自己解包到
`Mods/LanternBelt_ignite.wav`（按时间戳判新旧，见 §4）。

上线前自检：

| 检查 | 通过标准 |
|---|---|
| `dotnet build -c Release` | `0 警告 0 错误` |
| 日志 `Harmony 补丁已应用，共 N 个方法` | N 与上一版一致或增多（漏 `PatchAll()` 会加载成功但补丁全不生效） |
| 日志 `[挂腰] 自定义点火音已就绪 2.61s 48000Hz 2ch（系统播放）` | 出现且时长与 `Assets/ignite.wav` 一致 |
| 日志 `[挂腰] 熄灭起始（2.80s 后关灯）` | 括号里是设置值，且约 2.8 秒后真的灭 |
| 挂腰后双手 | 能装备枪械 / 石块，能与物品互动 |
| `check_docs.py . --lang auto` | `0 个 error` |

