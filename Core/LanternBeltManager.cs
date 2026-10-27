using System;
using Il2CppTLD.Gear;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LanternBelt.Core;

/// <summary>
/// 挂腰状态机 + 每帧校正。
///
/// 机制依据（2.55 实机反汇编，勿凭印象改）：
/// · <c>PlayerManager.EquipItem</c> 在换手时会调 <c>KeroseneLampItem.TurnOff</c>，
///   所以「卸下」必然熄灯，挂腰后必须由我们自己重新点亮。
/// · <c>KeroseneLampItem.Update</c> 只认自己的 <c>m_On</c> / <c>m_FlareIsPaused</c>，
///   不检查是否在手——只要物体 active 就会继续烧油、继续发光。这是挂腰可行的前提。
/// · <c>KeroseneLampItem.Toggle</c> 带 <c>PlayerAnimation.FirstPersonHandsEnabled</c> 等一整串
///   前置判断，离手状态下调用会被拦掉；因此开关灯一律走 <c>TurnOn(bool)</c> / <c>TurnOff(bool)</c>。
/// </summary>
internal static class LanternBeltManager
{
	private const float RestoreIntervalSeconds = 2f;

	/// <summary>进场景后等多久还找不到点着的提灯就放弃恢复（存档反序列化有延迟）。</summary>
	private const float RestoreWindowSeconds = 30f;

	private const float ErrorLogIntervalSeconds = 15f;

	/// <summary>当前挂在腰上的提灯；null 表示没有。</summary>
	private static GearItem? s_hung;

	/// <summary>我们自建的挂点（玩家根节点或相机下的一个空物体）。</summary>
	private static Transform? s_anchor;

	// —— 挂起前的原始摆放，收起时原样还回去，避免污染游戏自己的对象池 ——
	private static Transform? s_origParent;
	private static Vector3 s_origLocalPos;
	private static Quaternion s_origLocalRot;
	private static Vector3 s_origLocalScale;
	private static bool s_origActive;
	private static bool s_origWasInHands;

	// —— 挂腰期间要压掉物理，否则灯会被重力拖走 ——
	private static bool s_hadRigidbody;
	private static bool s_origKinematic;

	/// <summary>自动恢复的截止时刻（Time.unscaledTime）；0 表示当前不需要恢复。</summary>
	private static float s_restoreDeadline;

	/// <summary>我们希望的点燃状态；实际状态每帧向它靠拢。</summary>
	private static bool s_wantLit;

	private static float s_nextRestoreTime;
	private static bool s_anchorMissingLogged;
	private static float s_nextErrorLogTime;

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

	/// <summary>装备栏 / 快捷键的统一入口：已挂就收起，没挂就挂上。</summary>
	internal static void ToggleHolster(GearItem gi)
	{
		if (IsHungLantern(gi))
		{
			Unhang();
			return;
		}

		Hang(gi);
	}

	/// <summary>快捷键入口：有挂着的就收起，否则在背包里找一盏提灯挂上。</summary>
	internal static void ToggleHolsterFromHotkey()
	{
		if (s_hung != null)
		{
			Unhang();
			return;
		}

		GearItem? found = FindLanternInInventory(preferLit: true);
		if (found == null)
		{
			Notify(Loc.NoLantern);
			return;
		}

		Hang(found);
	}

	internal static void Hang(GearItem gi)
	{
		if (gi == null || !IsLantern(gi))
		{
			return;
		}

		if (s_hung != null)
		{
			// 一次只挂一盏：先把旧的还回去
			Unhang(quiet: true);
		}

		bool inHands = IsInHands(gi);

		// 先记原始状态，再动手——挂腰结束后要能原样还原
		if (!CaptureOriginal(gi))
		{
			LogError("记录原始摆放失败，放弃挂腰");
			return;
		}

		s_origWasInHands = inHands;

		if (inHands)
		{
			// 原版卸下路径会顺手熄灯，紧接着由 ReconcileLamp 重新点亮
			try
			{
				PlayerManager? pm = GameManager.GetPlayerManagerComponent();
				if (pm != null)
				{
					pm.UnequipItemInHands();
				}
			}
			catch (Exception ex)
			{
				LogError("卸下手持提灯失败：" + ex.Message);
			}
		}

		// 挂腰的灯必须静止：关掉碰撞、把刚体设成运动学，否则每帧设位置会和物理打架
		SuspendPhysics(gi);

		s_hung = gi;
		s_wantLit = Cfg?.LightOnHang ?? true;

		Apply();

		// 检视界面里 Apply 会提前返回，保证「挂上即点亮」不依赖面板是否打开
		ReconcileLamp(gi, playerInitiated: true);

		ModLog.Msg($"[挂腰] {Describe(gi)} 原始父节点={(s_origParent == null ? "<无>" : s_origParent.name)} 原先active={s_origActive}");

		Notify(Loc.HangOn);
	}

	internal static void Unhang(bool quiet = false)
	{
		GearItem? gi = s_hung;
		Transform? origParent = s_origParent;
		Vector3 pos = s_origLocalPos;
		Quaternion rot = s_origLocalRot;
		Vector3 scale = s_origLocalScale;
		bool origActive = s_origActive && !s_origWasInHands;

		ClearState();

		if (gi != null)
		{
			try
			{
				var lamp = gi.m_KeroseneLampItem;
				if (lamp != null && (Cfg?.ExtinguishOnUnhang ?? true) && lamp.IsOn())
				{
					lamp.TurnOff(true);
				}

				Transform t = gi.transform;
				if (t != null)
				{
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
				}

				ResumePhysics(gi);

				GameObject go = gi.gameObject;
				if (go != null && !origActive)
				{
					go.SetActive(false);
				}
			}
			catch (Exception ex)
			{
				LogError("收起提灯时还原对象失败：" + ex.Message);
			}
		}

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

		// ReconcileLamp 可能因为没油把 s_wantLit 改回 false
		Notify(s_wantLit ? Loc.LampOn : Loc.LampOff);
	}

	/// <summary>原版开关灯生效后，把我们的期望状态同步成实际状态（见 KeroseneLampItem_Toggle 补丁）。</summary>
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
		}
		catch
		{
			// 对象已销毁，忽略
		}
	}

	/// <summary>每帧：把提灯钉在挂点上，并把点燃状态拉回我们期望的值。</summary>
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

		s_nextRestoreTime = 0f;
		s_restoreDeadline = Time.unscaledTime + RestoreWindowSeconds;
		ModLog.Msg($"[挂腰] 场景切换：{sceneName}");
	}

	// ────────────────────────── 内部实现 ──────────────────────────

	private static void Apply()
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
				ClearState();
				return;
			}

			// 检视界面把物品挪到检视空间，这时候别抢
			PlayerManager? pm = GameManager.GetPlayerManagerComponent();
			if (pm != null && pm.GearItemBeingInspected() != null)
			{
				return;
			}

			if (!go.activeSelf)
			{
				go.SetActive(true);
			}

			Transform t = gi.transform;
			if (t == null)
			{
				ClearState();
				return;
			}

			Transform? anchor = EnsureAnchor();
			if (anchor == null)
			{
				return;
			}

			if ((Cfg?.FollowAnchor ?? true) || t.parent != anchor)
			{
				if (t.parent != anchor)
				{
					t.SetParent(anchor, false);
				}

				t.localPosition = Vector3.zero;
				t.localRotation = Quaternion.identity;
				t.localScale = Vector3.one * Mathf.Max(0.05f, Cfg?.Scale ?? 1f);
			}

			ReconcileLamp(gi, playerInitiated: false);
		}
		catch (Exception ex)
		{
			LogError("每帧校正失败：" + ex.Message);
		}
	}

	/// <summary>把实际点燃状态拉向 <see cref="s_wantLit"/>。原版卸下、油尽、动画延迟都靠这里兜住。</summary>
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

			return;
		}

		if (!isOn)
		{
			float fuel = lamp.GetPercentFuelRemaining();
			if (fuel <= 0.0001f)
			{
				s_wantLit = false;
				Notify(Loc.NoFuel);
				return;
			}

			lamp.TurnOn(!playerInitiated);
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
		anchor.localPosition = new Vector3(cfg?.OffsetX ?? -0.2f, cfg?.OffsetY ?? 0.9f, cfg?.OffsetZ ?? 0.1f);
		anchor.localRotation = Quaternion.identity;
		anchor.localScale = Vector3.one;

		return anchor;
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
	/// 读档恢复：原版把「灯是点着的」写进存档（KeroseneLampItemSaveDataProxy.m_OnProxy），
	/// 而卸下必然熄灯，所以「背包里有一盏点着的提灯」只可能来自挂腰状态——据此还原，不必额外写存档标记。
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
			// 进场景后等了足够久还没等到点着的提灯，本次不再尝试
			s_restoreDeadline = 0f;
			return;
		}

		if (GameManager.GetPlayerTransform() == null)
		{
			return;
		}

		GearItem? lit = FindLanternInInventory(preferLit: true);
		if (lit == null || !IsLantern(lit) || IsInHands(lit) || !lit.m_KeroseneLampItem.IsOn())
		{
			return;
		}

		s_restoreDeadline = 0f;

		ModLog.Msg("[挂腰] 检测到背包里有点着的提灯，自动恢复挂腰");

		Hang(lit);
	}

	private static void Notify(string message)
	{
		if (Cfg?.HudMessages ?? true)
		{
			try
			{
				Panel_HUD? hud = InterfaceManager.GetPanel<Panel_HUD>();
				if (hud != null)
				{
					hud.DisplayWarningMessage(message);
				}
			}
			catch
			{
				// HUD 不可用时只留日志
			}
		}

		Melon<LanternBeltMod>.Logger.Msg(message);
	}

	private static string Describe(GearItem gi)
	{
		try
		{
			return gi.name;
		}
		catch
		{
			return "<unnamed>";
		}
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
}
