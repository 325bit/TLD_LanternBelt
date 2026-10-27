using System;
using System.Collections.Generic;
using Il2CppTLD.Gear;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LanternBelt.Core;

/// <summary>
/// 挂腰状态机：把背包里那盏提灯搬到腰上，并保持它「亮着、发热、看得见」。
///
/// 机制依据（2.55 反汇编，勿凭印象改）：
/// · <c>KeroseneLampItem.TurnOn</c> 内部会对自己的 <c>HeatSource</c> 调 <c>TurnOn</c>、
///   把 <c>m_On</c> 置位；<c>Update</c> 只认 <c>m_On</c> / <c>m_FlareIsPaused</c>，不看是否在手。
/// · 但 <c>HeatSource.TurnOn</c> 是**自己往 <c>HeatSourceManager.m_HeatSources</c> 里注册**的
///   （列表 AddWithResize）。灯一旦离场被移除，重新 SetActive 不会补注册，所以必须手动登记，
///   否则「有光没热」。
/// · 背包里的物品渲染器是关着的（<c>GearItem.m_MeshRenderers</c> / <c>m_IsHidden</c> 决定），
///   只把 GameObject 激活并不会让它显形，必须逐个打开 Renderer 并把层设回主相机可见层。
/// · <c>PlayerManager.EquipItem</c> 只会熄灭**手上**那件物品，不会碰背包里的灯，
///   所以挂腰与手持枪 / 信号棒互不影响。
/// </summary>
internal static class LanternBeltManager
{
	/// <summary>进场景后等多久还找不到点着的提灯就放弃恢复（存档反序列化有延迟）。</summary>
	private const float RestoreWindowSeconds = 30f;

	private const float RestoreIntervalSeconds = 2f;

	private const float ErrorLogIntervalSeconds = 15f;

	/// <summary>同一次点击里重复到达的切换请求在这段时间内被吸收。</summary>
	private const float ToggleGuardSeconds = 0.3f;

	private const string KeyPrefix = "LANTERNBELT_";

	// ────────────────────────── 运行状态 ──────────────────────────

	private static GearItem? s_hung;

	private static Transform? s_anchor;

	/// <summary>我们希望的点燃状态；实际状态每帧向它靠拢。</summary>
	private static bool s_wantLit;

	// —— 挂腰前的原始摆放，收起时原样还回去 ——
	private static Transform? s_origParent;
	private static Vector3 s_origLocalPos;
	private static Quaternion s_origLocalRot;
	private static Vector3 s_origLocalScale;
	private static bool s_origActive;
	private static bool s_origWasInHands;

	// —— 挂腰期间要压掉物理，否则灯会被重力拖走 ——
	private static bool s_hadRigidbody;
	private static bool s_origKinematic;

	// —— 我们打开过的渲染器，收起时按原样恢复 ——
	private static readonly List<Renderer> s_renderers = new List<Renderer>();
	private static readonly List<bool> s_rendererWasEnabled = new List<bool>();
	private static bool s_renderStaged;
	private static bool s_noMeshLogged;

	private static HeatSource? s_heat;

	private static float s_restoreDeadline;
	private static float s_nextRestoreTime;
	private static bool s_anchorMissingLogged;
	private static float s_nextErrorLogTime;
	private static bool s_diagLogged;

	// —— 去抖：记录上一次切换的目标与时间，吸收同一帧内重复到达的请求 ——
	private static IntPtr s_toggleGuardTarget;
	private static float s_toggleGuardUntil;

	/// <summary>蹲下时挂点已经下移的量（米），插值跟随，避免蹲/起瞬间跳变。</summary>
	private static float s_crouchDrop;

	internal static bool HasHungLantern
	{
		get { return s_hung != null; }
	}

	internal static GearItem? HungLantern
	{
		get { return s_hung; }
	}

	private static Settings? Cfg
	{
		get { return LanternBeltMod.settings; }
	}

	// ────────────────────────── 查询 ──────────────────────────

	internal static bool IsHungLantern(GearItem? gi)
	{
		if (gi == null || s_hung == null)
		{
			return false;
		}

		try
		{
			return gi.Pointer == s_hung.Pointer;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsHungLantern(GameObject? go)
	{
		if (go == null || s_hung == null)
		{
			return false;
		}

		try
		{
			GameObject self = s_hung.gameObject;
			return self != null && self.Pointer == go.Pointer;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>只有带 KeroseneLampItem 的 GearItem 才算提灯——同时覆盖 Spelunkers 与 LampB，不靠名字匹配。</summary>
	internal static bool IsLantern(GearItem? gi)
	{
		if (gi == null)
		{
			return false;
		}

		try
		{
			return gi.m_KeroseneLampItem != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsInHands(GearItem gi)
	{
		try
		{
			PlayerManager? pm = GameManager.GetPlayerManagerComponent();
			if (pm == null || pm.m_ItemInHands == null)
			{
				return false;
			}

			return pm.m_ItemInHands.Pointer == gi.Pointer;
		}
		catch
		{
			return false;
		}
	}

	// ────────────────────────── 入口 ──────────────────────────

	/// <summary>
	/// 物品栏「悬挂 / 卸下」按钮的统一入口。返回 true 表示已经处理，调用方应吞掉原版装备。
	///
	/// 带去抖：物品检视面板的一次点击实测会触发两次装备请求，不去抖的话
	/// 第一下挂上、第二下立刻收起，玩家只看到「提灯已收起」而腰上什么都没有。
	/// </summary>
	internal static bool ToggleHolster(GearItem gi)
	{
		if (gi == null || !IsLantern(gi))
		{
			return false;
		}

		if (IsRepeatToggle(gi))
		{
			return true;
		}

		if (IsHungLantern(gi))
		{
			Unhang();
			return true;
		}

		return Hang(gi);
	}

	/// <summary>同一次点击产生的后续请求：吞掉，但不改变状态。</summary>
	private static bool IsRepeatToggle(GearItem gi)
	{
		float now = Time.unscaledTime;

		try
		{
			if (s_toggleGuardTarget != IntPtr.Zero && s_toggleGuardTarget == gi.Pointer && now < s_toggleGuardUntil)
			{
				if (Cfg?.Verbose ?? false)
				{
					ModLog.Msg("[挂腰] 忽略同一次点击产生的重复切换请求");
				}

				return true;
			}

			s_toggleGuardTarget = gi.Pointer;
		}
		catch
		{
			// 指针读不到时不拦，交给正常流程
		}

		s_toggleGuardUntil = now + ToggleGuardSeconds;
		return false;
	}

	internal static bool Hang(GearItem gi, bool? forcedLit = null)
	{
		if (gi == null || !IsLantern(gi))
		{
			return false;
		}

		if (s_hung != null)
		{
			// 一次只挂一盏：先把旧的还回去
			Unhang(quiet: true);
		}

		bool inHands = IsInHands(gi);

		if (inHands)
		{
			// 用 SkipAnimation 版本：无卸下动画，物体立刻回到背包位置，
			// 后面 CaptureOriginal 记到的才是「背包里的摆放」。
			try
			{
				PlayerManager? pm = GameManager.GetPlayerManagerComponent();
				if (pm != null)
				{
					pm.UnequipItemInHandsSkipAnimation();
				}
			}
			catch (Exception ex)
			{
				LogError("卸下手持提灯失败：" + ex.Message);
			}
		}

		// 卸下之后再记录：这时才是「回到背包」的真实摆放
		if (!CaptureOriginal(gi))
		{
			LogError("记录原始摆放失败，放弃挂腰");
			return false;
		}

		s_origWasInHands = inHands;
		s_diagLogged = false;

		SuspendPhysics(gi);

		s_hung = gi;

		// 从存档恢复时按「当时是亮还是灭」还原，不要用设置覆盖玩家上次的选择
		s_wantLit = forcedLit ?? (Cfg?.LightOnHang ?? true);

		Apply(force: true);

		if (s_hung == null)
		{
			// Apply 发现物体已经不在背包里，退回原状——别点亮一盏已经离手的灯
			return false;
		}

		ReconcileLamp(gi, playerInitiated: true);

		SaveLedger.SaveHung(gi, s_wantLit);

		Notify(Loc.HangOn);
		return true;
	}

	internal static void Unhang(bool quiet = false, bool restoreActive = true)
	{
		GearItem? gi = s_hung;
		if (gi == null)
		{
			ClearState();
			SaveLedger.Clear();
			return;
		}

		Transform? origParent = s_origParent;
		Vector3 pos = s_origLocalPos;
		Quaternion rot = s_origLocalRot;
		Vector3 scale = s_origLocalScale;
		bool inHandsBefore = s_origWasInHands;
		bool origActive = s_origActive;

		try
		{
			var lamp = gi.m_KeroseneLampItem;
			if (lamp != null && (Cfg?.ExtinguishOnUnhang ?? true) && lamp.IsOn())
			{
				lamp.TurnOff(true);
			}

			SetHeat(gi, false);
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("收起时关灯失败：" + ex.Message);
			}
		}

		RestoreRenderers();

		// 原本拿在手上的灯，收起后应当是「躺在背包里」——物体必须重新隐藏
		RestoreTransform(gi, origParent, pos, rot, scale, restoreActive && origActive && !inHandsBefore);
		ResumePhysics(gi);

		ClearState();
		SaveLedger.Clear();

		if (!quiet)
		{
			Notify(Loc.HangOff);
		}
	}

	/// <summary>L 键：只有挂着的时候才应答，避免和游戏原有按键冲突。</summary>
	internal static void ToggleLampFromHotkey()
	{
		if (s_hung == null)
		{
			return;
		}

		s_wantLit = !s_wantLit;

		ReconcileLamp(s_hung, playerInitiated: true);
		SaveLedger.SaveHung(s_hung, s_wantLit);

		if (Cfg?.Verbose ?? false)
		{
			ModLog.Msg("[挂腰] 快捷键切灯" + DescribeHandsState() + " 灯=" + DescribeLighting(s_hung));
		}

		Notify(s_wantLit ? Loc.LampOn : Loc.LampOff);
	}

	/// <summary>排查「按 L 之后手被卡住」时看这一行：手部是否启用、手上有没有东西。</summary>
	private static string DescribeHandsState()
	{
		try
		{
			bool hands = false;
			try
			{
				PlayerAnimation? anim = GameManager.GetPlayerAnimationComponent();
				hands = anim != null && anim.FirstPersonHandsEnabled(s_hung);
			}
			catch
			{
				// 某些场景下该方法不可用
			}

			PlayerManager? pm = GameManager.GetPlayerManagerComponent();
			GearItem? inHands = pm?.m_ItemInHands;
			string held = inHands == null ? "<空手>" : inHands.gameObject.name;

			return $" 手部启用={hands} 手持={held}";
		}
		catch
		{
			return string.Empty;
		}
	}

	/// <summary>原版开关灯生效后，把我们的期望状态同步成实际状态。</summary>
	internal static void SyncWantLitFrom(KeroseneLampItem lamp)
	{
		GearItem? gi = s_hung;
		if (gi == null)
		{
			return;
		}

		try
		{
			var current = gi.m_KeroseneLampItem;
			if (current == null)
			{
				return;
			}

			if (lamp == null || lamp.Pointer != current.Pointer)
			{
				return;
			}

			s_wantLit = current.IsOn();
			SaveLedger.SaveHung(gi, s_wantLit);
		}
		catch
		{
			// 对象已销毁，忽略
		}
	}

	// ────────────────────────── 每帧 ──────────────────────────

	internal static void Tick()
	{
		if (s_hung == null)
		{
			MaybeAutoRestore();
			return;
		}

		Apply();
	}

	internal static void OnSceneChanged(string sceneName)
	{
		// 换场景时旧对象多半已经销毁，直接清状态，等 AutoRestore 重新挂
		if (s_hung != null)
		{
			try
			{
				if (s_hung.gameObject == null)
				{
					ClearState();
				}
			}
			catch
			{
				ClearState();
			}
		}

		// 主菜单 / 加载壳不参与恢复：否则退到主菜单等满一个窗口，
		// 会把存档记录当成「找不到那盏灯」清掉，再进世界就恢复不了了。
		if (string.IsNullOrEmpty(sceneName)
			|| sceneName.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase)
			|| sceneName == "Empty")
		{
			s_restoreDeadline = 0f;
			return;
		}

		s_nextRestoreTime = 0f;
		s_restoreDeadline = Time.unscaledTime + RestoreWindowSeconds;
		ModLog.Msg($"[挂腰] 场景切换：{sceneName}");
	}

	// ────────────────────────── 摆放与显示 ──────────────────────────

	private static void Apply(bool force = false)
	{
		GearItem? gi = s_hung;
		if (gi == null)
		{
			return;
		}

		try
		{
			GameObject go = gi.gameObject;
			if (go == null)
			{
				ClearState();
				return;
			}

			// 被拖进容器 / 丢在地上 / 移出背包：这盏灯已经和我们无关了，交给游戏
			if (!gi.m_InPlayerInventory || gi.m_InsideContainer)
			{
				Unhang(quiet: true, restoreActive: false);
				return;
			}

			// 玩家正在检视这盏灯时让位，别和面板抢物体
			PlayerManager? pm = GameManager.GetPlayerManagerComponent();
			if (!force && pm != null)
			{
				GearItem? inspected = pm.GearItemBeingInspected();
				if (inspected != null && SameGear(inspected, gi))
				{
					return;
				}
			}

			if (!go.activeSelf)
			{
				go.SetActive(true);

				// 重新激活会把渲染器带回「背包里」的关闭状态，采集表也要重来
				ResetRenderStage();
			}

			Transform? anchor = EnsureAnchor();
			if (anchor == null)
			{
				return;
			}

			Transform t = gi.transform;
			if (t == null)
			{
				ClearState();
				return;
			}

			if (t.parent != anchor)
			{
				t.SetParent(anchor, false);
			}

			if (force || (Cfg?.FollowAnchor ?? true))
			{
				t.localPosition = Vector3.zero;
				t.localRotation = Quaternion.identity;
				t.localScale = Vector3.one * Mathf.Clamp(Cfg?.Scale ?? 1f, 0.05f, 4f);
			}

			StageRenderers(gi);

			ReconcileLamp(gi, playerInitiated: false);

			if (!s_diagLogged && (Cfg?.Verbose ?? false))
			{
				s_diagLogged = true;
				LogDiagnostics(gi, anchor);
			}
		}
		catch (Exception ex)
		{
			LogError("每帧校正失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 把背包里那盏灯的渲染器全部打开，并把层设回主相机可见层。
	/// 背包物品本来是不渲染的，只激活 GameObject 只会得到「有光没灯」。
	/// </summary>
	private static void StageRenderers(GearItem gi)
	{
		if (s_renderStaged)
		{
			return;
		}

		s_renderStaged = true;
		s_renderers.Clear();
		s_rendererWasEnabled.Clear();

		try
		{
			GameObject go = gi.gameObject;
			if (go == null)
			{
				return;
			}

			int layer = LayerMask.NameToLayer("Default");
			if (layer < 0)
			{
				layer = 0;
			}

			try
			{
				gi.m_IsHidden = false;
			}
			catch
			{
				// 某些版本没有这个字段，忽略
			}

			go.layer = layer;

			var found = go.GetComponentsInChildren<Renderer>(true);
			foreach (Renderer r in found)
			{
				if (r == null)
				{
					continue;
				}

				s_renderers.Add(r);
				s_rendererWasEnabled.Add(r.enabled);
				r.enabled = true;

				// 逐个子物体改层，避免模型被第一人称相机裁掉
				r.gameObject.layer = layer;
			}

			if (s_renderers.Count == 0 && !s_noMeshLogged)
			{
				s_noMeshLogged = true;
				ModLog.Warning("[挂腰] 这盏提灯没有找到任何 Renderer，模型会看不见（只剩光）。请在日志里反馈这一行。");
			}
			else if (Cfg?.Verbose ?? false)
			{
				ModLog.Msg($"[挂腰] 已打开 {s_renderers.Count} 个渲染器，层={layer}");
			}
		}
		catch (Exception ex)
		{
			LogError("打开提灯渲染失败：" + ex.Message);
		}
	}

	private static void ResetRenderStage()
	{
		s_renderers.Clear();
		s_rendererWasEnabled.Clear();
		s_renderStaged = false;
	}

	private static void RestoreRenderers()
	{
		if (!s_renderStaged)
		{
			return;
		}

		try
		{
			int count = Mathf.Min(s_renderers.Count, s_rendererWasEnabled.Count);
			for (int i = 0; i < count; i++)
			{
				Renderer r = s_renderers[i];
				if (r == null)
				{
					continue;
				}

				r.enabled = s_rendererWasEnabled[i];
			}
		}
		catch
		{
			// 对象可能已随场景销毁
		}

		s_renderers.Clear();
		s_rendererWasEnabled.Clear();
		s_renderStaged = false;
	}

	private static void RestoreTransform(GearItem gi, Transform? origParent, Vector3 pos, Quaternion rot, Vector3 scale, bool active)
	{
		try
		{
			Transform t = gi.transform;
			if (t == null)
			{
				return;
			}

			if (origParent != null)
			{
				t.SetParent(origParent, false);
			}
			else
			{
				t.SetParent(null, false);
			}

			t.localPosition = pos;
			t.localRotation = rot;
			t.localScale = scale;

			GameObject go = gi.gameObject;
			if (go != null && go.activeSelf != active)
			{
				go.SetActive(active);
			}
		}
		catch (Exception ex)
		{
			LogError("还原提灯摆放失败：" + ex.Message);
		}
	}

	private static Transform? EnsureAnchor()
	{
		Transform? parent = ResolveAnchorParent();
		if (parent == null)
		{
			LogOnce("找不到玩家或相机，无法建立挂点");
			return null;
		}

		if (s_anchor == null)
		{
			GameObject go = new GameObject("LanternBelt_Anchor");
			s_anchor = go.transform;
		}

		Transform anchor = s_anchor!;
		if (anchor == null)
		{
			s_anchor = null;
			return null;
		}

		s_anchorMissingLogged = false;

		if (anchor.parent != parent)
		{
			anchor.SetParent(parent, false);
		}

		Settings? cfg = Cfg;
		float drop = UpdateCrouchDrop();
		anchor.localPosition = new Vector3(
			cfg?.OffsetX ?? -0.22f,
			(cfg?.OffsetY ?? 0.95f) - drop,
			cfg?.OffsetZ ?? 0.12f);
		anchor.localRotation = Quaternion.identity;
		anchor.localScale = Vector3.one;

		return anchor;
	}

	/// <summary>
	/// 蹲下时挂点跟着往下落。
	///
	/// 挂点挂在玩家根节点（脚底）上，是个固定高度，所以蹲下时提灯会悬在原来的腰部位置。
	/// 这里读 <c>PlayerManager.PlayerIsCrouched()</c>，蹲下时把挂点往下挪一个可调的米数，
	/// 并用插值跟随蹲/起的过渡（原版蹲下本身有 0.2~0.3 秒的相机落位）。
	/// </summary>
	private static float UpdateCrouchDrop()
	{
		Settings? cfg = Cfg;

		float target = 0f;
		if (cfg?.FollowCrouch ?? true)
		{
			try
			{
				PlayerManager? pm = GameManager.GetPlayerManagerComponent();
				if (pm != null && pm.PlayerIsCrouched())
				{
					target = Mathf.Clamp(cfg?.CrouchDropAmount ?? 0.35f, 0f, 1.5f);
				}
			}
			catch
			{
				// 场景切换中读不到，本帧按站立处理
			}
		}

		float k = Mathf.Clamp01(Time.deltaTime * 8f);
		s_crouchDrop = Mathf.Lerp(s_crouchDrop, target, k);

		// 差得极小时直接吸附，避免长期停在一个几乎不可见的偏移上
		if (Mathf.Abs(s_crouchDrop - target) < 0.002f)
		{
			s_crouchDrop = target;
		}

		return s_crouchDrop;
	}

	private static Transform? ResolveAnchorParent()
	{
		bool useCamera = (Cfg?.AnchorMode ?? 0) == 1;

		if (useCamera)
		{
			var cam = GameManager.GetVpFPSCamera();
			if (cam != null)
			{
				return cam.transform;
			}
		}

		Transform player = GameManager.GetPlayerTransform();
		if (player != null)
		{
			return player;
		}

		var fallback = GameManager.GetVpFPSCamera();
		return fallback != null ? fallback.transform : null;
	}

	// ────────────────────────── 灯光与热量 ──────────────────────────

	private static void ReconcileLamp(GearItem gi, bool playerInitiated)
	{
		var lamp = gi.m_KeroseneLampItem;
		if (lamp == null)
		{
			return;
		}

		bool isOn = lamp.IsOn();

		if (!s_wantLit)
		{
			if (isOn)
			{
				lamp.TurnOff(!playerInitiated);
			}

			SetHeat(gi, false);
			return;
		}

		if (!isOn)
		{
			float fuel = lamp.GetPercentFuelRemaining();
			if (fuel <= 0.0001f)
			{
				s_wantLit = false;
				SaveLedger.SaveHung(gi, false);
				Notify(Loc.NoFuel);
				return;
			}

			TurnOnSafely(lamp);
		}

		// 关键：灯亮 ≠ 有热。HeatSource 是自己注册到 HeatSourceManager 的，
		// 离场后重新激活不会再注册一次，所以每次校正都顺手补登。
		SetHeat(gi, true);
	}

	/// <summary>
	/// 点亮提灯，但**不调用原版 <c>TurnOn</c>**。
	///
	/// 反汇编（RVA 0x120BA30）显示 <c>TurnOn</c> 的收尾是：
	/// <code>
	/// if (PlayerAnimation.FirstPersonHandsEnabled(m_GearItem))
	///     PlayerAnimation.Trigger_Generic_Ignite(null, null);   // ← 第一人称点灯手势
	/// </code>
	/// 这个手势要求提灯在第一人称手部槽位。挂腰时手上没有灯，动画收不回来，
	/// <c>PlayerAnimation</c> 的状态机就停在「正在点燃」——表现为**双手锁死：不能装备枪械 /
	/// 石块 / 木炭，也不能与物品互动**。
	///
	/// 所以这里直接把原版那几个字段摆成「已点亮」：<c>m_On = true</c> 之后
	/// <c>Update</c> 每帧会跑 <c>UpdateEffects</c> 与 <c>ReduceFuel</c>，光照、烧油、热量全都照旧，
	/// 只是不会去碰手部动画。
	/// </summary>
	private static void TurnOnSafely(KeroseneLampItem lamp)
	{
		try
		{
			lamp.m_On = true;

			// UpdateEffects 第一段就是 if (!m_TurnOnComplete) { …手势… }，置真即可整段跳过
			lamp.m_TurnOnComplete = true;
			lamp.m_TurningOff = false;
			lamp.m_TurnOnEffectsTimer = 0f;

			GameObject? fx = lamp.m_FXGameObject;
			if (fx != null && !fx.activeSelf)
			{
				fx.SetActive(true);
			}
		}
		catch (Exception ex)
		{
			// 字段写不进去（版本差异）时退回原版，但至少把手势压掉
			ModLog.Warning("直接点亮失败，回退原版 TurnOn：" + ex.Message);

			try
			{
				lamp.TurnOn(true);
				lamp.m_TurnOnComplete = true;
			}
			catch (Exception inner)
			{
				LogError("点亮提灯失败：" + inner.Message);
			}
		}
	}

	/// <summary>这个提灯组件是否就是挂在腰上的那一盏（用于拦截它的手部动画）。</summary>
	internal static bool IsHungLampComponent(KeroseneLampItem lamp)
	{
		GearItem? gi = s_hung;
		if (gi == null || lamp == null)
		{
			return false;
		}

		try
		{
			var current = gi.m_KeroseneLampItem;
			return current != null && current.Pointer == lamp.Pointer;
		}
		catch
		{
			return false;
		}
	}

	private static void SetHeat(GearItem gi, bool want)
	{
		HeatSource? hs = s_heat;
		if (hs == null)
		{
			try
			{
				hs = gi.GetComponentInChildren<HeatSource>(true);
			}
			catch
			{
				hs = null;
			}

			s_heat = hs;
		}

		if (hs == null)
		{
			return;
		}

		try
		{
			if (!want)
			{
				if (hs.IsTurnedOn())
				{
					hs.TurnOffImmediate();
				}

				return;
			}

			if (!hs.enabled)
			{
				hs.enabled = true;
			}

			if (!hs.IsTurnedOn())
			{
				hs.TurnOn();
			}

			RegisterHeat(hs);
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("同步提灯热量失败：" + ex.Message);
			}
		}
	}

	/// <summary>把 HeatSource 补登进管理器的列表，否则温度判定扫不到它。</summary>
	private static void RegisterHeat(HeatSource hs)
	{
		try
		{
			HeatSourceManager? mgr = GameManager.GetHeatSourceManagerComponent();
			if (mgr == null)
			{
				return;
			}

			var list = mgr.m_HeatSources;
			if (list == null)
			{
				mgr.AddHeatSource(hs);
				return;
			}

			int count = list.Count;
			for (int i = 0; i < count; i++)
			{
				HeatSource item = list[i];
				if (item != null && item.Pointer == hs.Pointer)
				{
					return;
				}
			}

			mgr.AddHeatSource(hs);
		}
		catch
		{
			// 管理器还没建好时忽略，下一帧会重试
		}
	}

	// ────────────────────────── 物理 ──────────────────────────

	private static bool CaptureOriginal(GearItem gi)
	{
		try
		{
			Transform t = gi.transform;
			if (t == null)
			{
				return false;
			}

			GameObject go = gi.gameObject;

			s_origParent = t.parent;
			s_origLocalPos = t.localPosition;
			s_origLocalRot = t.localRotation;
			s_origLocalScale = t.localScale;
			s_origActive = go != null && go.activeSelf;
			return true;
		}
		catch (Exception ex)
		{
			LogError("读取原始摆放失败：" + ex.Message);
			return false;
		}
	}

	private static void ClearState()
	{
		if (s_anchor != null)
		{
			try
			{
				Object.Destroy(s_anchor.gameObject);
			}
			catch
			{
				// 场景卸载中销毁会抛，忽略
			}
		}

		s_anchor = null;
		s_hung = null;
		s_origParent = null;
		s_origWasInHands = false;
		s_hadRigidbody = false;
		s_origKinematic = false;
		s_wantLit = false;
		s_heat = null;
		s_diagLogged = false;
		s_crouchDrop = 0f;
	}

	private static void SuspendPhysics(GearItem gi)
	{
		try
		{
			Rigidbody rb = gi.GetComponent<Rigidbody>();
			if (rb != null)
			{
				s_hadRigidbody = true;
				s_origKinematic = rb.isKinematic;
				rb.isKinematic = true;
			}

			gi.ToggleColliders(false);
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("压制提灯物理失败（不影响挂腰）：" + ex.Message);
			}
		}
	}

	private static void ResumePhysics(GearItem gi)
	{
		try
		{
			if (s_hadRigidbody)
			{
				Rigidbody rb = gi.GetComponent<Rigidbody>();
				if (rb != null)
				{
					rb.isKinematic = s_origKinematic;
				}
			}

			gi.ToggleColliders(true);
		}
		catch
		{
			// 对象可能已随场景销毁
		}
	}

	// ────────────────────────── 存档恢复 ──────────────────────────

	private static GearItem? FindLanternInInventory(bool preferLit)
	{
		try
		{
			Inventory? inv = GameManager.GetInventoryComponent();
			if (inv == null || inv.m_Items == null)
			{
				return null;
			}

			GearItem? fallback = null;
			int count = inv.m_Items.Count;

			for (int i = 0; i < count; i++)
			{
				GearItem gi = inv.m_Items[i];
				if (gi == null || !IsLantern(gi))
				{
					continue;
				}

				if (preferLit && gi.m_KeroseneLampItem.IsOn())
				{
					return gi;
				}

				if (fallback == null)
				{
					fallback = gi;
				}
			}

			return fallback;
		}
		catch (Exception ex)
		{
			LogError("扫描背包提灯失败：" + ex.Message);
			return null;
		}
	}

	/// <summary>
	/// 换场景 / 读档后把提灯挂回去。
	///
	/// 优先按 <see cref="SaveLedger"/> 里明确记录恢复——灯当时是灭的就恢复成灭的。
	/// 早期版本靠「背包里有一盏点着的提灯」反推，玩家熄灯后出门就再也猜不出来（实测丢状态），
	/// 所以那条只剩兜底作用，服务没有记录的老存档。
	/// </summary>
	private static void MaybeAutoRestore()
	{
		if (s_restoreDeadline <= 0f || !(Cfg?.AutoRestore ?? true))
		{
			return;
		}

		float now = Time.unscaledTime;
		if (now < s_nextRestoreTime)
		{
			return;
		}

		s_nextRestoreTime = now + RestoreIntervalSeconds;

		if (now > s_restoreDeadline)
		{
			// 等满一整个窗口还没落位：记录作废，免得之后每次进场景都白等一轮
			s_restoreDeadline = 0f;

			if (GameManager.GetPlayerTransform() == null)
			{
				// 世界还没起来（加载中 / 退到主菜单），记录留着
				return;
			}

			if (SaveLedger.TryLoad(out _, out _, out _))
			{
				SaveLedger.Clear();
				ModLog.Msg("[挂腰] 等不到上次挂腰的那盏提灯，已作废记录");
			}

			return;
		}

		if (GameManager.GetPlayerTransform() == null)
		{
			return;
		}

		// ① 有明确记录：照记录恢复，灯亮灭也照旧
		if (SaveLedger.TryLoad(out string recordedName, out float recordedFuel, out bool recordedLit))
		{
			GearItem? recorded = FindLantern(recordedName, recordedFuel);
			if (recorded == null || IsInHands(recorded))
			{
				// 场景反序列化有延迟，下一拍再看
				return;
			}

			s_restoreDeadline = 0f;
			ModLog.Msg($"[挂腰] 按存档记录恢复悬挂（{SaveLedger.ItemName(recorded)}，灯{(recordedLit ? "亮" : "灭")}）");

			Hang(recorded, recordedLit);
			return;
		}

		// ② 老存档没有记录：退回「背包里点着灯」的猜测
		GearItem? lit = FindLanternInInventory(preferLit: true);
		if (lit == null || !IsLantern(lit) || IsInHands(lit) || !lit.m_KeroseneLampItem.IsOn())
		{
			return;
		}

		s_restoreDeadline = 0f;

		ModLog.Msg("[挂腰] 检测到背包里有点着的提灯，自动恢复挂腰");

		Hang(lit);
	}

	/// <summary>
	/// 按「物品名 + 燃油比例」在背包里认出上次那盏灯。
	/// 同型号多盏时取燃油最接近的；名字对不上也允许（不同世界的同名提灯），只是排序靠后。
	/// </summary>
	private static GearItem? FindLantern(string itemName, float fuel)
	{
		try
		{
			Inventory? inv = GameManager.GetInventoryComponent();
			if (inv == null || inv.m_Items == null)
			{
				return null;
			}

			GearItem? best = null;
			float bestScore = float.MaxValue;
			int count = inv.m_Items.Count;

			for (int i = 0; i < count; i++)
			{
				GearItem gi = inv.m_Items[i];
				if (gi == null || !IsLantern(gi))
				{
					continue;
				}

				float score = 0f;

				if (!string.IsNullOrEmpty(itemName) && SaveLedger.ItemName(gi) != itemName)
				{
					score += 1000f;
				}

				float f = SaveLedger.FuelFraction(gi);
				if (fuel >= 0f && f >= 0f)
				{
					score += Mathf.Abs(f - fuel) * 100f;
				}

				if (score < bestScore)
				{
					bestScore = score;
					best = gi;
				}
			}

			return best;
		}
		catch (Exception ex)
		{
			LogError("按记录查找提灯失败：" + ex.Message);
			return null;
		}
	}

	// ────────────────────────── 工具 ──────────────────────────

	private static bool SameGear(GearItem a, GearItem b)
	{
		try
		{
			return a.Pointer == b.Pointer;
		}
		catch
		{
			return false;
		}
	}

	private static void LogDiagnostics(GearItem gi, Transform anchor)
	{
		try
		{
			GameObject go = gi.gameObject;
			Transform t = gi.transform;
			int enabledCount = 0;
			foreach (Renderer r in s_renderers)
			{
				if (r != null && r.enabled)
				{
					enabledCount++;
				}
			}

			ModLog.Msg("[挂腰] 诊断"
				+ $" 物体={go.name}"
				+ $" active={go.activeSelf}"
				+ $" layer={go.layer}"
				+ $" 渲染器={enabledCount}/{s_renderers.Count}"
				+ $" 网格父层={anchor.parent?.name ?? "<无>"}"
				+ $" 世界坐标={t.position}"
				+ $" 缩放={t.lossyScale}"
				+ $" 热量源={(s_heat == null ? "无" : (s_heat.IsTurnedOn() ? "已开" : "关"))}"
				+ $" 灯={DescribeLighting(gi)}");
		}
		catch
		{
			// 纯诊断，失败不影响功能
		}
	}

	private static string DescribeLighting(GearItem gi)
	{
		try
		{
			var lamp = gi.m_KeroseneLampItem;
			if (lamp == null)
			{
				return "无组件";
			}

			GameObject? fx = lamp.m_FXGameObject;
			int lights = 0;
			Light? a = lamp.m_LightIndoor;
			Light? b = lamp.m_LightOutdoor;
			Light? c = lamp.m_LightIndoorCore;
			if (a != null && a.enabled) lights++;
			if (b != null && b.enabled) lights++;
			if (c != null && c.enabled) lights++;

			return $"on={lamp.IsOn()} 特效={(fx == null ? "无" : (fx.activeSelf ? "开" : "关"))} 光源={lights}";
		}
		catch
		{
			return "读取失败";
		}
	}

	private static void Notify(string message)
	{
		// 屏幕中间的红色警告字一律不弹：灯亮不亮肉眼可见，悬挂状态只放在物品介绍里提示。
		// 保留日志，排查时仍然有据可查。
		ModLog.Msg(message);
	}

	private static void LogOnce(string message)
	{
		if (s_anchorMissingLogged)
		{
			return;
		}

		s_anchorMissingLogged = true;
		ModLog.Warning(message);
	}

	private static void LogError(string message)
	{
		float now = Time.unscaledTime;
		if (now < s_nextErrorLogTime)
		{
			return;
		}

		s_nextErrorLogTime = now + ErrorLogIntervalSeconds;
		ModLog.Error(message);
	}

	// ────────────────────────── 供本地化钩子使用 ──────────────────────────

	internal static bool IsLanternLocalizationKey(string key)
	{
		return !string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix, StringComparison.Ordinal);
	}
}
