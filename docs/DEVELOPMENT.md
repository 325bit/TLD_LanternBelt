# Lantern Belt · 开发记录

面向维护者。玩家向说明见根目录 [README.md](../README.md)。

## 1. 一句话机制

提灯挂腰 = **让提灯留在背包里，但把它的物体挂在玩家腰侧、保持 active、并把 `KeroseneLampItem.m_On` 钉在 true**。

原版没有任何「离手还在烧」的路径：`PlayerManager.EquipItem` 换手时会调 `KeroseneLampItem.TurnOff`，
`UnequipItemInHandsInternal` 同样会熄灯（见 §4 反汇编依据）。所以挂腰必须是「我们自己维持点燃状态」，
而不是「借用原版某种收纳状态」。

## 2. 文件地图

| 文件 | 职责 |
|---|---|
| `LanternBelt.csproj` | net6.0 + STBlade 2.55.0；`ModSettings.dll` 从实机 `Mods` 引用、`Private=false` |
| `BuildInfo.cs` | 版本 / 作者 / loader 版本唯一来源（README、ChangeLog 必须与之一致） |
| `LanternBeltMod.cs` | MelonMod 入口：注册设置、绑定日志、每帧驱动、两个热键 |
| `Settings.cs` | ModSettings 面板与落盘（`Mods/LanternBelt.json`） |
| `Core/LanternBeltManager.cs` | 状态机 + 每帧校正 + 挂点 + 读档恢复 |
| `Patches/Panel_Inventory_Examine_OnEquip.cs` | 装备栏入口 |
| `Patches/PlayerManager_EquipItem.cs` | 拿回手上 / 原版开关灯的状态同步 |
| `Loc.cs` | 屏幕提示中英对照 |
| `ModLog.cs` | 静态日志出口（静态类拿不到实例 Logger） |

## 3. 状态机

只有两个状态，互斥且可逆：

```
        Hang(gi)                       Unhang()
[ 未挂 ] ──────────► [ 挂腰: s_hung=gi ] ──────────► [ 未挂 ]
   ▲                        │
   └──────── EquipItem(gi) ─┘   ← 玩家把灯拿到手上时自动解除
```

- `s_hung`：唯一真值，null 即未挂。同一时刻只允许一盏。
- `s_wantLit`：**我们希望的点燃状态**。实际状态每帧向它靠拢（`ReconcileLamp`），
  这样能同时兜住三件事：原版卸下顺手熄灯、油尽、点燃有延迟。
- `s_restoreDeadline`：进场景后 30 秒内允许一次自动恢复，之后不再尝试，
  以防玩家手动收起后又被自动挂回去。

### 为什么是「每帧校正」而不是「一次性摆好」

第一人称武器 / 身体的变换每帧都在被动画改写，原版也会在某些时机（检视模式、装备、卸下）
重新摆放物品。挂腰的位置与点燃状态都必须在动画之后重新断言一次，否则会被拉回去。
这和 HuntingRoads 把镜体每帧钉在准星-照门中点上是同一个思路（参见
`projects/HuntingRoads/SCOPE_MOUNT.md`）。

## 4. 机制依据（2.55 实机反汇编，改代码前先复核）

| 结论 | 依据 |
|---|---|
| 卸下 / 换手必然熄灯 | `PlayerManager.EquipItem` 内 RVA `0xcd940c`、`0xceac8c` 两次 `call KeroseneLampItem::TurnOff` |
| 灯不检查是否在手 | `KeroseneLampItem.Update`（RVA `0x120b3e0`）只读 `m_On`(0xa4)、`m_FlareIsPaused`(0xe8) 与一个全局状态，没有 hands 判断 |
| 离手不能调 `Toggle()` | `KeroseneLampItem.Toggle`（RVA `0x120c170`）先调 `PlayerAnimation.FirstPersonHandsEnabled`，再查 `IsCancelableActionInProgress`、油量、面板引用；离手会被前两关拦掉 |
| `TurnOn(bool)` / `TurnOff(bool)` 可直接调 | interop 把 private 方法暴露成 public（`MetaPeek` 核对 `Il2CppTLD.Gear.KeroseneLampItem`） |
| 灯亮状态进存档 | `KeroseneLampItemSaveDataProxy.m_OnProxy` |
| 装备栏「装备」入口是 `Panel_Inventory_Examine.OnEquip()` | 该面板的动作方法均为 `public void OnXxx()`，菜单项是 NGUI 预制件直连 |

关键成员（编译面已核对，命名空间按 Il2CppInterop 扁平化规则）：

| 用途 | 成员 |
|---|---|
| 手持物品 | `PlayerManager.m_ItemInHands`（属性，get/set） |
| 装备 / 卸下 | `PlayerManager.EquipItem(GearItem,bool)`、`UnequipItemInHands()` |
| 检视中判定 | `PlayerManager.GearItemBeingInspected()` |
| 提灯组件 | `GearItem.m_KeroseneLampItem` |
| 开关灯 | `KeroseneLampItem.IsOn()`、`TurnOn(bool)`、`TurnOff(bool)`、`GetPercentFuelRemaining()` |
| 背包 | `GameManager.GetInventoryComponent().m_Items` |
| 玩家 / 相机 | `GameManager.GetPlayerTransform()`、`GetVpFPSCamera()` |
| HUD 提示 | `InterfaceManager.GetPanel<Panel_HUD>().DisplayWarningMessage(string)` |

## 5. 注入点

| 补丁 | 类型 | 做什么 | 为什么在这里 |
|---|---|---|---|
| `Panel_Inventory_Examine.OnEquip` | Prefix（返回 false 吞掉原版） | 提灯 → `ToggleHolster` | 玩家理解的「装备栏」就是这里；不新建按钮，避免重接 NGUI 事件委托 |
| `PlayerManager.EquipItem` | Prefix | 挂腰中的灯被拿到手上 → 先 `Unhang` | 让原版从干净状态接管，避免我们和装备流程抢同一个物体 |
| `KeroseneLampItem.Toggle` | Postfix | 同步 `s_wantLit` | 玩家通过原版途径（背包「使用」）开关灯时，别让每帧校正把操作改回去 |

驱动：`MelonMod.OnUpdate` → `LanternBeltManager.Tick()`（每帧）+ `OnSceneWasLoaded` → 允许一次自动恢复。

## 6. 挂点

`EnsureAnchor()` 每次挂腰时新建一个空物体 `LanternBelt_Anchor`，父节点二选一：

- **玩家身体**（默认）：`GameManager.GetPlayerTransform()`，偏移按「脚底起算的米数」解释，`y=0.9` 约在胯部。
- **相机**：`GameManager.GetVpFPSCamera().transform`，跟随视角，晃动明显，需要压缩放。

提灯物体的 `localPosition` 归零、`localScale` 由设置给，位置与朝向全部由锚点承担——
这样「每帧重新定位」只写一个物体，不会和提灯的原始 transform 打架。

### 为什么不做骨骼挂点

玩家对象没有公开可靠的骨盆 / 腰带骨骼索引，按名字猜会在不同角色模型（男 / 女 / 剧情手）上失效。
用玩家根节点 + 可调偏移，稳定性优先；需要更贴身的方案时再考虑骨骼搜索，并在设置里给开关。

## 7. 已知风险 / 待实机校准

1. **背包内提灯物体的初始状态未知**：假设游戏把它 deactivate 后挂在对象池下。
   若实机发现物体被销毁，`Hang` 会在下一帧 `Apply` 里 `ClearState` 退出——现象是「挂上但立刻掉状态」，
   日志会打 `[挂腰]` 之后紧跟一次校正失败。这种情况需要改成 `GearItem.InstantiateGearItem` 复制一盏纯展示灯，
   并把真实提灯留在背包（届时光照要单独接管）。
2. **模型可见性依赖渲染层**：物品模型原本属于世界层，挂到相机下可能被武器相机裁掉。
   现象是「有光没灯」。解法是把挂点父节点换成玩家根节点（默认就是），或在未来加一个强制层选项。
3. **收起后原摆放的还原**：`Unhang` 会尝试把物体还给 `s_origParent`；父节点已随场景销毁时静默失败，
   此时只把物体 deactivate，交给原版装备流程重新摆放。
4. **`Time.timeScale > 0` 作为热键闸门**：假设开背包会暂停时间。若实机不是这样，`L` 在背包里也会生效，
   影响仅限「面板里按 L 也能开关灯」，不构成 bug。
5. **自动恢复的窗口期 30 秒**：读档反序列化慢于 30 秒（极慢硬盘 + 大存档）时会错过恢复，此时按一次 `H` 即可。

## 8. 排查方法

1. 开启设置里的「输出详细日志」，进游戏挂一次灯，看 `MelonLoader/Latest.log`：
   - 出现 `[挂腰] <物品名> 原始父节点=... 原先active=...` → 挂腰流程跑通
   - 之后每帧没有 `校正失败` → 挂点解析正常
2. 灯亮不亮：灯亮说明 `TurnOn` 生效；不亮先看油量（`GetPercentFuelRemaining`）。
3. 灯看不见但有光：按 §7.2 处理渲染层。
4. 想确认当前是否处于挂腰状态：屏幕提示 / 日志，或按 `L` 看有没有反应（`L` 只在挂腰时响应）。

## 9. 设计约束（改代码时别破坏）

- 不修改物品定义、燃油速率、光照强度——所有数值都走原版。
- 不额外写存档字段：状态从「背包里有一盏点着的提灯」反推，卸载 mod 后存档干净。
- 挂腰期间必须压制提灯自身物理（关碰撞 + 刚体设为运动学），否则每帧定位会和质量/重力打架。
- 所有跨 il2cpp 边界的调用都要 `try/catch`，异常只降级不抛出——静态状态机一旦抛穿，挂腰会处于半挂状态。
