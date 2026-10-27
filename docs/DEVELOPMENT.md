# Lantern Belt · 开发记录

面向维护者。玩家向说明见根目录 [README.md](../README.md)。

## 1. 一句话机制

提灯挂腰 = **让提灯留在背包里，但把它的物体摆到玩家腰侧、打开渲染、保持 active，
并把 `KeroseneLampItem.m_On` 与它的 `HeatSource` 一起钉在「开」**。

原版没有任何「离手还在烧」的路径：`PlayerManager.EquipItem` 换手时会调 `KeroseneLampItem.TurnOff`。
所以挂腰必须是「我们自己维持点燃状态」，而不是「借用原版某种收纳状态」。

## 2. 文件地图

| 文件 | 职责 |
|---|---|
| `LanternBelt.csproj` | net6.0 + STBlade 2.55.0；`ModSettings.dll` 从实机 `Mods` 引用、`Private=false` |
| `BuildInfo.cs` | 版本 / 作者 / loader 版本唯一来源（README、ChangeLog 必须与之一致） |
| `LanternBeltMod.cs` | MelonMod 入口：注册设置、绑定日志、每帧驱动、`L` 键 |
| `Settings.cs` | ModSettings 面板与落盘（`Mods/LanternBelt.json`） |
| `Core/LanternBeltManager.cs` | 状态机 + 每帧校正 + 渲染/热量接管 + 挂点 + 读档恢复 |
| `Patches/PlayerManager_EquipItem.cs` | 物品栏切换入口、丢弃前收起、原版开关灯同步 |
| `Patches/ItemDescriptionPage_EquipButtonLabel.cs` | 装备按钮文案「悬挂/卸下」（恒定文案） |
| `Patches/ItemDescriptionPage_Description.cs` | 悬挂时在物品介绍末尾追加「[已悬挂在腰带上]」 |
| `Patches/Localization_Get.cs` | 运行时翻译本 mod 的 `LANTERNBELT_*` 键 |
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

关键成员（编译面已核对，命名空间按 Il2CppInterop 扁平化规则）：

| 用途 | 成员 |
|---|---|
| 手持物品 | `PlayerManager.m_ItemInHands` |
| 装备 / 卸下 | `PlayerManager.EquipItem(GearItem,bool)`、`UnequipItemInHands()` |
| 检视中判定 | `PlayerManager.GearItemBeingInspected()` |
| 提灯组件 | `GearItem.m_KeroseneLampItem` |
| 开关灯 | `KeroseneLampItem.IsOn()`、`TurnOn(bool)`、`TurnOff(bool)`、`GetPercentFuelRemaining()` |
| 热量 | `HeatSource.TurnOn()`、`IsTurnedOn()`、`TurnOffImmediate()`；`HeatSourceManager.m_HeatSources`、`AddHeatSource()`；入口 `GameManager.GetHeatSourceManagerComponent()` |
| 可见性 | `GearItem.m_IsHidden`、`m_MeshRenderers`；`GameObject.layer` |
| 背包 | `GameManager.GetInventoryComponent().m_Items` |
| 玩家 / 相机 | `GameManager.GetPlayerTransform()`、`GetVpFPSCamera()` |
| 手部状态 | `PlayerAnimation.FirstPersonHandsEnabled(GearItem)`（实例方法，经 `GameManager.GetPlayerAnimationComponent()` 取） |

### 为什么必须绕开原版 `TurnOn`（按 L 卡手的唯一根因）

原版有**两处**会触发第一人称点燃手势，两处都得处理：

| # | 位置 | 触发条件 |
|---|---|---|
| 1 | `KeroseneLampItem.TurnOn` 收尾（RVA `0x120BCB3`） | `PlayerAnimation.FirstPersonHandsEnabled(m_GearItem)` 为真 |
| 2 | `KeroseneLampItem.UpdateEffects` 第一段（RVA `0x120CE3F`） | `!m_TurnOnComplete` 且手部启用 |

两处调的是不同方法：第 1 处是 `Trigger_Generic_Ignite`，第 2 处是 `Trigger_Generic_Ignite_Confirm`。
**1.2.0 只挡了第 2 处**，而按 `L` 点亮走的是 `TurnOn` → 第 1 处，所以卡手照旧——这也是那一版
看起来"修了却没效果"的原因。

这个手势要求提灯位于第一人称手部槽位。挂在腰上时手上没有灯，动画没有可绑定的模型，
`PlayerAnimation` 的状态机就停在「正在点燃」：**不能装备枪械 / 石块 / 木炭，也不能与物品互动**。

修法（1.3.0 起）：

1. **主路径不再调用 `TurnOn`**，改为 `TurnOnSafely` 直接摆字段：
   `m_On = true`、`m_TurnOnComplete = true`、`m_TurningOff = false`、`m_TurnOnEffectsTimer = 0`，
   再 `m_FXGameObject.SetActive(true)`。`Update` 每帧会跑 `UpdateEffects` 与 `ReduceFuel`，
   光照、烧油、热量全部照旧，只是不碰手部动画。顺带也躲开了 `TurnOn` 里的
   `GearItem.CheckForBreakOnUse()`（每次点灯都掷一次损坏判定）。
2. **兜底**：`TurnOn` 与 `UpdateEffects` 各挂一个 Prefix/Postfix 记录「当前是哪盏灯」，
   两处手势 Prefix 只在调用者是挂腰那盏灯时返回 false。玩家点篝火、点火把的手势不受影响。

## 5. 注入点

| 补丁 | 类型 | 做什么 | 为什么在这里 |
|---|---|---|---|
| `PlayerManager.EquipItem` | Prefix（返回 false 吞掉原版） | 提灯 → `ToggleHolster`；其它物品放行 | 所有「装备」路径的汇合点（面板按钮 / 快速装备 / 拾取自动装备），与 HuntingRoads 挂瞄准镜同法 |
| `ItemDescriptionPage.GetEquipButtonLocalizationId` | Postfix | 提灯 → 返回 `LANTERNBELT_Mount` | 不改控件只改文案，手柄提示与原版事件链全部保留 |
| `ItemDescriptionPage.BuildItemDescription` | Postfix | 悬挂时 `__result += "\n" + Get("LANTERNBELT_Mounted")` | 与 HuntingRoads 给装镜的枪加描述行完全同法，不碰 UI 控件 |
| `KeroseneLampItem.UpdateEffects` | Prefix / Postfix | 记下「正在更新特效的是哪盏灯」 | 只用于给下一行提供判定依据 |
| `KeroseneLampItem.TurnOn` | Prefix / Postfix | 记下「正在点亮的是哪盏灯」 | 给下面两行提供判定依据 |
| `PlayerAnimation.Trigger_Generic_Ignite` | Prefix（返回 false） | 发起者是挂腰灯 → 吞掉 | `TurnOn` 收尾那次，按 L 卡手就是它 |
| `PlayerAnimation.Trigger_Generic_Ignite_Confirm` | Prefix（返回 false） | 发起者是挂腰灯 → 吞掉 | `UpdateEffects` 里那次，兜底 |
| `Localization.Get` | Postfix | 只替换 `LANTERNBELT_*` 前缀的键 | 不引入本地化库、不污染游戏字符串表 |
| `PlayerManager.Drop(GameObject,bool)` | Prefix | 挂腰的灯被丢弃 → 先 `Unhang(restoreActive:false)` | 否则父节点还在玩家身上，落地物体会跟着人走 |
| `KeroseneLampItem.Toggle` | Postfix | 同步 `s_wantLit` | 玩家通过原版途径开关灯时，别让每帧校正把操作改回去 |

驱动：`MelonMod.OnUpdate` → `LanternBeltManager.Tick()`（每帧）+ `OnSceneWasLoaded` → 允许一次自动恢复。

## 6. 挂点

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
5. **自动恢复的窗口期 30 秒**：读档反序列化慢于 30 秒时会错过恢复，重新点一次物品栏按钮即可。

## 8. 排查方法

1. 开设置里的「输出详细日志」，挂一次灯，看 `MelonLoader/Latest.log`：
   - `[挂腰] 已打开 N 个渲染器，层=0` → 渲染接管成功
   - `[挂腰] 诊断 物体=… active=True layer=0 渲染器=N/N 网格父层=… 世界坐标=… 缩放=… 热量源=已开`
     → 一行看全：看不看得见看 `渲染器` 与 `layer`，暖不暖看 `热量源`，位置对不对看 `世界坐标`
2. 灯亮不亮：不亮先看油量（`GetPercentFuelRemaining`）。
3. 按钮文案不对：看 `Harmony 补丁已应用，共 N 个方法` 的 N 是否比上次多 3（文案 / 本地化 / 丢弃）。
4. 想确认当前是否挂腰：屏幕提示、日志，或按 `L` 看有没有反应（`L` 只在挂腰时响应）。

## 9. 设计约束（改代码时别破坏）

- 不修改物品定义、燃油速率、光照强度——所有数值都走原版。
- 不额外写存档字段：状态从「背包里有一盏点着的提灯」反推，卸载 mod 后存档干净。
- 挂腰期间必须压制提灯自身物理（关碰撞 + 刚体设为运动学），否则每帧定位会和质量/重力打架。
- 悬挂状态只能由物品栏点击切换；`L` 只负责亮 / 灭，不能反过来兼职挂腰。
- 挂腰期间不得拦截其它物品的装备动作——手必须是真的空。
- 所有跨 il2cpp 边界的调用都要 `try/catch`，异常只降级不抛出。
