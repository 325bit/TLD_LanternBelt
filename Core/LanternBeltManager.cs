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
/// 依赖四条原版行为：<c>TurnOn</c> 自己置 <c>m_On</c> 并开热量、<c>HeatSource</c> 靠自注册进
/// <c>m_HeatSources</c>、背包物品的 Renderer 是关着的、换手只熄手上那件。逐条依据见文档 §4。
/// 音效与闪灭的时间线、熄灭为何需要自己收尾，同见 <c>docs/DEVELOPMENT.md</c> §4。
/// </summary>
internal static class LanternBeltManager
{
	/// <summary>进场景后等多久还找不到那盏提灯就放弃恢复（存档反序列化有延迟）。</summary>
	private const float RestoreWindowSeconds = 30f;

	private const float RestoreIntervalSeconds = 2f;

	private const float ErrorLogIntervalSeconds = 15f;

	/// <summary>同一次点击里重复到达的切换请求在这段时间内被吸收。</summary>
	private const float ToggleGuardSeconds = 0.3f;

	/// <summary>
	/// 换场景后这段时间里不判定「灯离开了背包」。
	/// 场景加载中途 <c>m_InPlayerInventory</c> 会短暂为假，照它执行会把自己的挂腰状态当场拆掉。
	/// </summary>
	private const float SceneSettleSeconds = 8f;

	/// <summary>「灯不在背包里」要连续成立这么久才算真的离手。</summary>
	private const float InventoryGoneGraceSeconds = 1.5f;

	/// <summary>找不到灯时，恢复失败日志的最小间隔。</summary>
	private const float RestoreDiagIntervalSeconds = 10f;

	/// <summary>
	/// 熄灭流程的兜底超时。原版熄灭分两级：先播起始音，<c>m_TurnOffDelay</c> 秒后
	/// 由灯自己的 <c>Update</c> 播第二声并真正关掉。计时要靠那个 <c>Update</c> 在跑，
	/// 超时还没关就直接一次性关掉，别让灯一直亮着。
	/// </summary>
	private const float ExtinguishWatchdogSeconds = 5f;

	/// <summary>
	/// 玩家亲手悬挂时，先响「拿出提灯」那一声，隔这么久再点火。
	/// 实测原版装备声只有 ~30ms、点火声 ~135ms，挤在同一帧里就分不出是两声。
	/// </summary>
	private const float GearAudioLeadSeconds = 0.45f;

	/// <summary>收起时的金属声比熄灭音晚这么多：原版录像里收纳声跟在熄灭之后，不与它重叠。</summary>
	private const float StowAudioDelaySeconds = 0.35f;

	/// <summary>
	/// 熄灭起始音到真正关灯的默认间隔。原版 prefab 给的是 3.67s，熄灭声早在前半秒就落下了，
	/// 中间全是干等；默认取 2.8s，留一点余韵，玩家嫌慢可以在设置里继续调小。
	/// </summary>
	private const float DefaultTurnOffDelaySeconds = 2.8f;

	/// <summary>熄灭延迟的上限，与设置滑条的范围一致。</summary>
	private const float MaxTurnOffDelaySeconds = 5f;

	// —— 点火闪灭的明暗表（秒，相对点火瞬间）——
	//
	// 数字照着我们随包带的点火音（Assets/ignite.wav）量出来，且音频已裁掉开头 0.038s 的底噪，
	// 所以「播放起点」就等于「第一声开口」。三个尖峰在新文件里的位置：
	// 0.036s 第一下打火石（峰值 14612）、0.618s 第二下（峰值 10964）、1.642s 燃烧声涌起。
	// 闪灭压在这三拍上：第一声亮到余韵散、第二声再亮一下、第三声转长亮。
	/// <summary>第一声的余韵散尽，先灭着等第二声。</summary>
	private const float FlickerFirstOff = 0.09f;

	/// <summary>第二下打火石（尖峰 0.618s）——提前一点起亮，光才像被这一下点着的。</summary>
	private const float FlickerSecondOn = 0.61f;

	/// <summary>第二声落，再灭着等点燃那一拍。</summary>
	private const float FlickerSecondOff = 0.70f;

	/// <summary>火焰真正烧起来的那一拍；从这里开始光照不再闪灭，交还常亮。</summary>
	private const float FlickerFinalOn = 1.64f;

	/// <summary>燃烧循环音从这里接上，和点火音尾部交叠一小段，避免出现无声的空档。</summary>
	private const float FlickerLoopAt = 2.45f;

	/// <summary>整段闪灭的长度；到点把灯光交还给原版 <c>UpdateEffects</c>。</summary>
	private const float FlickerDurationSeconds = 2.61f;

	/// <summary>
	/// 闪灭期间把原版计时器冻在这个值。它每帧都在 <c>+= deltaTime</c>，
	/// 冻成 -1 的话撑不住两秒半就会被越过、原版抢先点灯；留出整段时长的余量才安全。
	/// </summary>
	private const float FlickerTimerFreeze = -4f;

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

	/// <summary>挂腰前的 <c>m_TurnOffDelay</c>；小于 0 表示没记到、收起时不还原。</summary>
	private static float s_origTurnOffDelay = -1f;

	// —— 我们打开过的渲染器，收起时按原样恢复 ——
	private static readonly List<Renderer> s_renderers = new List<Renderer>();
	private static readonly List<bool> s_rendererWasEnabled = new List<bool>();
	private static bool s_renderStaged;
	private static bool s_noMeshLogged;

	private static HeatSource? s_heat;

	private static float s_restoreDeadline;
	private static float s_nextReconcileTime;
	private static bool s_anchorMissingLogged;
	private static float s_nextErrorLogTime;
	private static bool s_diagLogged;

	// —— 去抖：记录上一次切换的目标与时间，吸收同一帧内重复到达的请求 ——
	private static IntPtr s_toggleGuardTarget;
	private static float s_toggleGuardUntil;

	/// <summary>蹲下时挂点已经下移的量（米），插值跟随，避免蹲/起瞬间跳变。</summary>
	private static float s_crouchDrop;

	/// <summary>上一次发起「熄灭」的时间，只给兜底超时用。</summary>
	private static float s_extinguishAt;

	/// <summary>
	/// 该真正关灯的时刻；0 表示没有待处理的熄灭。
	///
	/// 不能指望原版 <c>Update</c> 来收尾：它那段熄灭计时的前面有一句
	/// <c>if (FirstPersonHandsEnabled(m_GearItem)) skip</c>，挂腰时双手是空的，这句恒为真，
	/// 于是 <c>m_TurnOffDelay</c> 从来没被用上——只能等 5 秒兜底才灭。
	/// </summary>
	private static float s_turnOffAt;

	/// <summary>上一次尝试点燃的时间。原版拒绝点燃时（比如油量判零）别每帧重试。</summary>
	private static float s_lastIgniteAt;

	/// <summary>这段时间之前不许点火：等悬挂的金属声响完。</summary>
	private static float s_igniteNotBefore;

	// —— 点火闪灭（短亮 / 短亮 / 长亮）——
	private static bool s_flickerActive;
	private static float s_flickerStart;
	private static IntPtr s_flickerLamp;

	/// <summary>这一段的火焰特效是否已经点起来（到 FlickerFinalOn 那一拍才置真）。</summary>
	private static bool s_flickerFireLit;

	/// <summary>燃烧循环音是否已经接上（自定义点火音快放完时才接，避免重复）。</summary>
	private static bool s_flickerLoopPlayed;

	/// <summary>这一次要不要闪（设置里关掉闪灭时仍然走同一条时间线，只是灯一直亮着）。</summary>
	private static bool s_flickerFlickers;

	/// <summary>这一次要不要出声：控制点火音与到点后的燃烧循环。</summary>
	private static bool s_flickerAudio;

	/// <summary>延后播放的音效（收起的金属声要等熄灭音先响）。</summary>
	private static readonly List<PendingSound> s_pendingSounds = new List<PendingSound>();

	private struct PendingSound
	{
		public float At;
		public Il2CppAK.Wwise.Event? Evt;
		public string Tag;
	}

	// —— 「应该挂在腰上」的意图快照 ——
	//
	// 跨场景恢复只认这份内存快照，不依赖 ModData 的读取时机，也不怕中途有谁把记录擦掉。
	// 玩家的显式操作（卸下 / 丢弃 / 塞进容器）才会清掉它；场景切换、物体销毁都不清。
	private static bool s_intentValid;
	private static string s_intentName = string.Empty;
	private static float s_intentFuel = -1f;
	private static bool s_intentLit;

	/// <summary>第一次在背包里找不到那盏灯的时刻，0 表示当前找得到。</summary>
	private static float s_intentMissingSince;

	private static float s_nextDiagTime;
	private static float s_sceneSettleUntil;
	private static float s_invGoneSince;

	/// <summary>正在按记录恢复：此时的背包校验放宽，别把刚要挂上去的灯又拆下来。</summary>
	private static bool s_restoring;

	/// <summary>本次进入场景已经试过「背包里点着灯」的猜测，别反复猜。</summary>
	private static bool s_guessTried;

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
			// 玩家亲手卸下：这才是「不需要再挂回去」的唯一依据
			Unhang(forget: true);
			return true;
		}

		// 玩家亲手挂上去：这一次点燃要出点火音
		return Hang(gi, audible: true);
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

	internal static bool Hang(GearItem gi, bool? forcedLit = null, bool audible = false)
	{
		if (gi == null || !IsLantern(gi))
		{
			return false;
		}

		if (s_hung != null)
		{
			// 一次只挂一盏：先把旧的还回去（不动意图快照，紧接着就会被新的覆盖）
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

		// 记下原版熄灭延迟：挂腰期间会把它压短，收起时还回去
		s_origTurnOffDelay = -1f;

		try
		{
			var lampComp = gi.m_KeroseneLampItem;
			if (lampComp != null)
			{
				s_origTurnOffDelay = lampComp.m_TurnOffDelay;
			}
		}
		catch
		{
			// 读不到就保持 -1，收起时不做还原
		}

		// 读档 / 换场景恢复时按「当时是亮还是灭」还原，不要用设置覆盖玩家上次的选择
		s_wantLit = forcedLit ?? (Cfg?.LightOnHang ?? true);

		// 玩家亲手挂上腰：先响「拿出提灯」的金属声，点火让路等它响完
		if (audible)
		{
			PlayGearAudio(gi, stow: false);
			s_igniteNotBefore = Time.unscaledTime + GearAudioLeadSeconds;
		}

		// 点火是不是该出声由调用方决定：玩家点按钮要响，读档 / 自动挂回保持静默
		Apply(force: true, audible: audible);

		if (s_hung == null)
		{
			// Apply 发现物体已经不在背包里，退回原状——别点亮一盏已经离手的灯
			if (!s_restoring)
			{
				ModLog.Msg("[挂腰] 挂腰中止：这盏灯已经不在背包里");
			}

			return false;
		}

		ReconcileLamp(gi, playerInitiated: true);

		RememberIntent(gi, s_wantLit);

		Notify(Loc.HangOn);
		return true;
	}

	/// <summary>
	/// 把灯从腰上拿下来。
	///
	/// <paramref name="forget"/> 决定要不要连「玩家希望它挂着」这件事一起忘掉：
	/// 只有玩家亲手卸下、丢弃、塞进容器才忘；场景切换/物体销毁这类内部路径一律保留，
	/// 否则跨场景时记录会被自己擦掉，回到新区域就挂不回去了。
	/// </summary>
	internal static void Unhang(bool quiet = false, bool restoreActive = true, bool forget = false)
	{
		GearItem? gi = s_hung;
		if (gi == null)
		{
			ClearState();

			if (forget)
			{
				ForgetIntent("已经没有挂着的灯");
			}

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
			if (lamp != null)
			{
				if ((Cfg?.ExtinguishOnUnhang ?? true) && lamp.IsOn())
				{
					// 玩家亲手收起：响一声熄灭音；换场景 / 内部路径（quiet）保持静默
					lamp.TurnOff(!quiet);
				}

				// 把挂腰时压短的熄灭延迟换回原版值：拿着灯手动关灯的手感不该被这个 mod 改掉
				if (s_origTurnOffDelay >= 0f)
				{
					lamp.m_TurnOffDelay = s_origTurnOffDelay;
				}
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

		// 玩家亲手收起：熄灭音先响，金属收纳声随后跟上（排在 ClearState 之后，
		// 它会把待播队列一起清掉，是给「内部路径」准备的）
		if (!quiet)
		{
			QueueGearAudio(gi, stow: true);
		}

		if (forget)
		{
			ForgetIntent("玩家收起");
		}

		if (!quiet)
		{
			Notify(Loc.HangOff);
		}
	}

	// ────────────────────────── 意图快照 ──────────────────────────

	/// <summary>
	/// 记下「这盏灯应该挂在腰上、灯是亮/灭的」，并同步写进 ModData。
	///
	/// 内存快照是跨场景恢复的第一依据：场景切换时旧物体随时可能被销毁、
	/// ModData 的读取也可能落在不合适的时机，只有这份快照不受影响。
	/// </summary>
	private static void RememberIntent(GearItem gi, bool lit)
	{
		s_intentValid = true;
		s_intentName = SaveLedger.ItemName(gi);
		s_intentFuel = SaveLedger.FuelPercent(gi);
		s_intentLit = lit;
		s_intentMissingSince = 0f;
		s_invGoneSince = 0f;

		SaveLedger.SaveHung(gi, lit);
	}

	/// <summary>玩家明确表示「不需要挂着」时才调用：此后进任何场景都不会再自动挂回。</summary>
	private static void ForgetIntent(string reason)
	{
		s_intentValid = false;
		s_intentName = string.Empty;
		s_intentFuel = -1f;
		s_intentLit = false;
		s_intentMissingSince = 0f;
		s_invGoneSince = 0f;

		// 玩家已经表态，本次场景内不再自动挂回，也别去猜背包里另一盏灯
		s_restoreDeadline = 0f;
		s_guessTried = true;

		// 记录可能来自上一个会话（内存里还没有快照），所以无条件清，别留陈旧记录
		SaveLedger.Clear(reason);
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
		RememberIntent(s_hung, s_wantLit);

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
			RememberIntent(gi, s_wantLit);
		}
		catch
		{
			// 对象已销毁，忽略
		}
	}

	// ────────────────────────── 每帧 ──────────────────────────

	internal static void Tick()
	{
		// 自定义点火音是异步载入的，与挂腰状态无关，放在最前面每帧推进
		LampAudio.Tick();

		// 先把到点的音效放掉：收起的金属声排在卸下之后，那时 s_hung 已经是空的了
		FlushPendingSounds();

		// 熄灭到点了就自己关灯，不等原版那段被 FirstPersonHandsEnabled 挡住的计时
		PumpPendingTurnOff();

		if (s_hung == null)
		{
			ReconcileIntent();
			return;
		}

		Apply();

		// 灯的 Update 没在跑时（被停用 / 中途熄灭）闪灭会卡住，兜底收尾
		if (s_flickerActive && Time.unscaledTime - s_flickerStart > FlickerDurationSeconds + 1f)
		{
			EndFlicker(TryGetHungLamp());
		}
	}

	/// <summary>取挂着那盏灯的组件，取不到就返回空（调用方一律按可空处理）。</summary>
	private static KeroseneLampItem? TryGetHungLamp()
	{
		try
		{
			GearItem? gi = s_hung;
			return gi == null ? null : gi.m_KeroseneLampItem;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 熄灭的第二级：到点播第二声并真正关灯。
	///
	/// 原版 <c>Update</c> 里那段熄灭计时被 <c>FirstPersonHandsEnabled</c> 挡着（挂腰时双手是空的，
	/// 这句恒为真），所以 <c>m_TurnOffDelay</c> 根本轮不到用——不自己算就只能等 5 秒兜底。
	/// </summary>
	private static void PumpPendingTurnOff()
	{
		if (s_turnOffAt <= 0f || Time.unscaledTime < s_turnOffAt)
		{
			return;
		}

		s_turnOffAt = 0f;

		KeroseneLampItem? lamp = TryGetHungLamp();
		if (lamp == null)
		{
			return;
		}

		try
		{
			if (!lamp.m_TurningOff)
			{
				return;
			}

			lamp.TurnOff(true);

			if (Cfg?.Verbose ?? false)
			{
				ModLog.Msg($"[挂腰] 熄灭第二声并关灯（延迟 {lamp.m_TurnOffDelay:F2}s 到点）");
			}
		}
		catch (Exception ex)
		{
			ModLog.Warning("到点关灯失败：" + ex.Message);
		}
	}

	internal static void OnSceneChanged(string sceneName)
	{
		float now = Time.unscaledTime;

		// HUD 面板会随场景重建，旧提示物体已经作废
		HudReadout.Reset();

		// 新的世界正在加载：这段时间里背包字段不可信，先别急着拆自己的台
		s_sceneSettleUntil = now + SceneSettleSeconds;
		s_invGoneSince = 0f;
		s_nextReconcileTime = 0f;
		s_nextDiagTime = 0f;

		// 换场景时旧对象多半已经销毁，直接清运行时状态；意图快照不动
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
		// 同时这也是会话边界：内存快照作废，只留 ModData 里按存档槽分开的那份记录。
		if (string.IsNullOrEmpty(sceneName)
			|| sceneName.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase)
			|| sceneName == "Empty")
		{
			s_restoreDeadline = 0f;
			s_intentValid = false;
			s_intentMissingSince = 0f;
			s_guessTried = false;
			return;
		}

		s_nextReconcileTime = 0f;
		s_guessTried = false;
		s_restoreDeadline = now + RestoreWindowSeconds;

		// 进入新区域：把「还挂着」再往记录里写一遍。旧物体可能已经销毁，
		// 所以只用快照里的字段写，不碰 GearItem。
		if (s_intentValid)
		{
			SaveLedger.Write(s_intentName, s_intentFuel, s_intentLit);
		}

		ModLog.Msg($"[挂腰] 场景切换：{sceneName}" + (s_intentValid ? "（应恢复悬挂）" : string.Empty));
	}

	// ────────────────────────── 摆放与显示 ──────────────────────────

	private static void Apply(bool force = false, bool audible = false)
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

			// 塞进容器是玩家动手，立刻交还给游戏并忘掉；「不在背包里」可能只是加载中途的假象，
			// 必须连续成立一会儿才算真的离手，而且不清意图快照（灯还会回来）。
			if (gi.m_InsideContainer)
			{
				Unhang(quiet: true, restoreActive: false, forget: true);
				return;
			}

			if (!gi.m_InPlayerInventory && !s_restoring && Time.unscaledTime >= s_sceneSettleUntil)
			{
				if (s_invGoneSince <= 0f)
				{
					s_invGoneSince = Time.unscaledTime;
				}

				if (Time.unscaledTime - s_invGoneSince < InventoryGoneGraceSeconds)
				{
					return;
				}

				ModLog.Msg("[挂腰] 提灯已不在背包里，暂时卸下（意图保留）");
				Unhang(quiet: true, restoreActive: false);
				return;
			}

			s_invGoneSince = 0f;

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

			ReconcileLamp(gi, playerInitiated: audible);

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

	/// <summary>
	/// 这次操作该不该出声：只有玩家亲手触发、并且设置里没关掉音效，才放音。
	/// 内部路径（换场景恢复、状态校正）一律静音。
	/// </summary>
	private static bool Audible(bool playerInitiated)
	{
		return playerInitiated && (Cfg?.PlayLampAudio ?? true);
	}

	// ────────────────────────── 音效 ──────────────────────────

	/// <summary>
	/// 播一个 Wwise 事件，返回播放 id——原版 <c>TurnOff</c> 就是靠
	/// <c>m_LoopAudioID</c> / <c>m_TurnOnAudioID</c> 把循环音停掉的，自己播的必须记回去。
	/// </summary>
	private static uint PlayEvent(Il2CppAK.Wwise.Event? evt, GameObject? source, string what)
	{
		if (evt == null)
		{
			return 0;
		}

		try
		{
			uint id = GameAudioManager.PlaySound(evt, source);

			if (Cfg?.Verbose ?? false)
			{
				ModLog.Msg($"[挂腰] 音效 {what} id={id} 事件={DescribeEvent(evt)}");
			}

			return id;
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning($"音效「{what}」播放失败：{ex.Message}");
			}

			return 0;
		}
	}

	/// <summary>立刻掐掉循环音（原版 <c>StopLoopingAudio</c> 带 100ms 淡出，会盖住点火声）。</summary>
	private static void StopLoopNow(KeroseneLampItem lamp)
	{
		try
		{
			uint id = lamp.m_LoopAudioID;
			if (id != 0)
			{
				GameAudioManager.StopPlayingID(ref id, 0);
			}

			lamp.m_LoopAudioID = 0;
		}
		catch
		{
			// 停止失败不影响后续流程，TurnOff 还会再停一次
		}
	}

	/// <summary>读 Wwise 事件名，只为日志可读；读不到也不影响播放。</summary>
	private static string DescribeEvent(Il2CppAK.Wwise.Event? evt)
	{
		if (evt == null)
		{
			return "<空>";
		}

		try
		{
			string? name = evt.Name;
			return string.IsNullOrEmpty(name) ? "<无名>" : name;
		}
		catch
		{
			return "<未知>";
		}
	}

	/// <summary>
	/// 装备音：悬挂播原版「拿出」、收起播「收回」，都是 <c>GearItemData</c> 上挂的那两个事件，
	/// 与手持装备时听到的完全一致（实测是一声很短的金属碰响，不是持续的摇晃）。
	///
	/// 用 <c>InterfaceManager.GetSoundEmitter()</c> 当声源而不是灯自己：收起时灯已被隐藏，
	/// 挂在它身上的声源不会再发声。
	/// </summary>
	private static void PlayGearAudio(GearItem? gi, bool stow)
	{
		if (!(Cfg?.PlayGearAudio ?? true))
		{
			return;
		}

		try
		{
			GearItemData? data = gi?.m_GearItemData;
			if (data == null)
			{
				return;
			}

			Il2CppAK.Wwise.Event? evt = stow ? data.m_StowAudio : data.m_PickupAudio;
			PlayEvent(evt, InterfaceManager.GetSoundEmitter(), stow ? "收起" : "悬挂");
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("装备音播放失败：" + ex.Message);
			}
		}
	}

	/// <summary>把装备音排到稍后（收起声要跟在熄灭音后面）。</summary>
	private static void QueueGearAudio(GearItem? gi, bool stow)
	{
		if (!(Cfg?.PlayGearAudio ?? true) || gi == null)
		{
			return;
		}

		try
		{
			GearItemData? data = gi.m_GearItemData;
			if (data == null)
			{
				return;
			}

			Il2CppAK.Wwise.Event? evt = stow ? data.m_StowAudio : data.m_PickupAudio;
			if (evt == null)
			{
				return;
			}

			s_pendingSounds.Add(new PendingSound
			{
				At = Time.unscaledTime + StowAudioDelaySeconds,
				Evt = evt,
				Tag = stow ? "收起" : "悬挂"
			});
		}
		catch
		{
			// 记录已失效（物体被销毁），不播就是了
		}
	}

	private static void FlushPendingSounds()
	{
		if (s_pendingSounds.Count == 0)
		{
			return;
		}

		float now = Time.unscaledTime;

		for (int i = s_pendingSounds.Count - 1; i >= 0; i--)
		{
			PendingSound p = s_pendingSounds[i];
			if (now < p.At)
			{
				continue;
			}

			s_pendingSounds.RemoveAt(i);
			PlayEvent(p.Evt, InterfaceManager.GetSoundEmitter(), p.Tag);
		}
	}

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
				// 原版熄灭是两级的：先播「熄灭起始」音，`m_TurnOffDelay` 秒后再播熄灭音并真正关灯。
				// 这段流程里灯还是亮的，别去抢跑，也别重复触发。
				ExtinguishLamp(lamp, Audible(playerInitiated));
				return;
			}

			SetHeat(gi, false);
			return;
		}

		if (isOn && lamp.m_TurningOff)
		{
			// 正在熄灭又被要求点亮：把那一次熄灭撤掉，比「先关掉再点亮」自然得多。
			// 撤完继续往下走——灯本来就是亮的，落到底部的热量补登即可。
			CancelExtinguish(lamp);
		}

		if (!isOn)
		{
			float fuel = lamp.GetPercentFuelRemaining();
			if (fuel <= 0.0001f)
			{
				s_wantLit = false;
				RememberIntent(gi, false);
				Notify(Loc.NoFuel);
				return;
			}

			IgniteLamp(lamp, playerInitiated);
		}

		// 关键：灯亮 ≠ 有热。HeatSource 是自己注册到 HeatSourceManager 的，
		// 离场后重新激活不会再注册一次，所以每次校正都顺手补登。
		SetHeat(gi, true);
	}

	/// <summary>
	/// 点亮提灯：照调原版 <c>TurnOn</c> 拿状态，音效与闪灭自己排。
	/// 传 <c>suppressStartAudio = true</c>：原版只摆 <c>m_On</c> / 热量 / 耐久判定，不把
	/// <c>m_TurnOnEvent</c> 和 <c>m_LoopWithDelayEvent</c> 塞进同一帧，也不触发第一人称手势；
	/// 点火音换成随包带的音频，闪灭按它的节奏排，全程冻住计时器防原版抢点灯。
	/// 音效与闪灭两个开关都关时才直接长亮。
	/// </summary>
	private static void IgniteLamp(KeroseneLampItem lamp, bool playerInitiated)
	{
		// 每帧校正都会走到这里；原版可能因为油量判零之类的理由拒绝点燃，
		// 没有这道闸就会每秒喊六十遍 TurnOn
		if (Time.unscaledTime - s_lastIgniteAt < 1f)
		{
			return;
		}

		// 刚挂上腰：等「拿出提灯」的金属声响完再点火，别把两个音效叠在一起
		if (Time.unscaledTime < s_igniteNotBefore)
		{
			return;
		}

		s_igniteNotBefore = 0f;
		s_lastIgniteAt = Time.unscaledTime;

		bool audible = playerInitiated && (Cfg?.PlayLampAudio ?? true);
		bool flicker = playerInitiated && (Cfg?.PlayIgniteFlicker ?? true);

		try
		{
			lamp.TurnOn(true);

			// suppressStartAudio 那条分支会顺手起一个循环音（m_LoopEvent），当场掐掉：
			// 我们要等「点燃」那一拍才让火开始烧。用 fadeOut = 0 而不是原版
			// StopLoopingAudio 的 100ms 淡出，免得那点尾音和点火声糊在一起。
			StopLoopNow(lamp);

			// 既不出声也不闪（读档恢复、两个开关都关了）：直接点亮最省事
			if (!audible && !flicker)
			{
				lamp.HideEffects(false);
				lamp.m_TurnOnEffectsTimer = lamp.m_TurnOnEffectsDelay + 0.01f;

				if (Cfg?.Verbose ?? false)
				{
					ModLog.Msg("[挂腰] 点亮 静音且不闪 直接长亮");
				}

				return;
			}

			BeginIgniteSequence(lamp, flicker, audible);
		}
		catch (Exception ex)
		{
			// TurnOn 里任何一环失败都别让灯点不着：退回直接摆字段，
			// 只是没有音效和闪灭，至少光还是亮的
			ModLog.Warning("原版点火失败，改为直接点亮：" + ex.Message);
			ForceOnFields(lamp);
		}
	}

	/// <summary>
	/// 点火的时间线起点：先播点火音，再按它的节奏推进灯光。
	///
	/// 后续每帧由 <c>UpdateEffects</c> 的 Postfix 推进（那是最靠后的写光源时机），
	/// 全程冻住 <c>m_TurnOnEffectsTimer</c>，否则原版会中途把灯直接点亮，闪灭就看不见了。
	/// </summary>
	private static void BeginIgniteSequence(KeroseneLampItem lamp, bool flicker, bool audible)
	{
		s_flickerActive = true;
		s_flickerFlickers = flicker;
		s_flickerAudio = audible;
		s_flickerStart = Time.unscaledTime;
		s_flickerFireLit = false;
		s_flickerLoopPlayed = false;

		try
		{
			s_flickerLamp = lamp.Pointer;
		}
		catch
		{
			s_flickerLamp = IntPtr.Zero;
		}

		// 不闪灭时立刻把火焰点起来，只让光源保持常亮；循环音仍然等点火音快放完才接
		if (!flicker)
		{
			lamp.HideEffects(false);
		}

		// 从这一帧起就冻住原版计时，别等下一次 UpdateEffects——延迟设得短的灯会趁那一帧亮起来
		lamp.m_TurnOnEffectsTimer = FlickerTimerFreeze;
		SetLampVisual(lamp, !flicker);

		if (audible)
		{
			PlayIgniteAudio(lamp);
		}

		if (Cfg?.Verbose ?? false)
		{
			ModLog.Msg($"[挂腰] 点火 闪灭={(flicker ? "开" : "关")}"
				+ $" 音效={(audible ? "开" : "静")}"
				+ $" 时间线 亮到{FlickerFirstOff:F2}/再亮{FlickerSecondOn:F2}/长亮{FlickerFinalOn:F2}s"
				+ $" 循环音 {FlickerLoopAt:F2}s 共 {FlickerDurationSeconds:F2}s");
		}
	}

	/// <summary>
	/// 播点火音：优先用随包带的那一段（<see cref="LampAudio"/>），拿不到才退回原版 Wwise 事件。
	/// 时间线是照着自定义音频量的，退回去时节奏对不上，所以退回时只当作声音处理，不影响灯光。
	/// </summary>
	private static void PlayIgniteAudio(KeroseneLampItem lamp)
	{
		float volume = Mathf.Clamp01(Cfg?.IgniteAudioVolume ?? 1f);

		if (volume > 0.001f && LampAudio.Play(volume))
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Msg($"[挂腰] 音效 点火 自定义 {LampAudio.Length:F2}s 音量={volume:F2}");
			}

			return;
		}

		lamp.m_TurnOnAudioID = PlayEvent(lamp.m_TurnOnEvent, lamp.gameObject, "点火(原版)");
	}

	/// <summary>
	/// 每帧推进点火闪灭。挂在 <c>KeroseneLampItem.UpdateEffects</c> 的 Postfix 上，
	/// 这样我们写光源的时间总是晚于原版，不会被它当场覆盖。
	/// </summary>
	internal static void OnLampUpdated(KeroseneLampItem? lamp)
	{
		if (!s_flickerActive || lamp == null)
		{
			return;
		}

		try
		{
			if (lamp.Pointer != s_flickerLamp)
			{
				return;
			}

			float t = Time.unscaledTime - s_flickerStart;

			if (!s_flickerFireLit && t >= FlickerFinalOn)
			{
				// 「点燃」那一拍：火焰真正烧起来
				s_flickerFireLit = true;
				lamp.HideEffects(false);
			}

			if (!s_flickerLoopPlayed && t >= FlickerLoopAt)
			{
				// 自定义点火音快放完了，接上原版的燃烧循环，不留无声的空档
				s_flickerLoopPlayed = true;

				if (s_flickerAudio)
				{
					lamp.m_LoopAudioID = PlayEvent(lamp.m_LoopWithDelayEvent, lamp.gameObject, "燃烧循环");
				}
			}

			if (t >= FlickerDurationSeconds)
			{
				EndFlicker(lamp);
				return;
			}

			// 冻住原版计时：它一旦越过门槛就会把该亮的光源全部打开，闪灭就白做了
			lamp.m_TurnOnEffectsTimer = FlickerTimerFreeze;
			SetLampVisual(lamp, !s_flickerFlickers || FlickerLit(t));
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("点火闪灭推进失败：" + ex.Message);
			}

			EndFlicker(lamp);
		}
	}

	/// <summary>闪灭结束：把计时器推过门槛，灯光重新交给原版 <c>UpdateEffects</c> 管。</summary>
	private static void EndFlicker(KeroseneLampItem? lamp)
	{
		if (!s_flickerActive)
		{
			return;
		}

		s_flickerActive = false;
		s_flickerLamp = IntPtr.Zero;

		if (lamp == null)
		{
			return;
		}

		try
		{
			lamp.HideEffects(false);
			lamp.m_TurnOnEffectsTimer = lamp.m_TurnOnEffectsDelay + 0.01f;
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("结束闪灭失败：" + ex.Message);
			}
		}
	}

	/// <summary>闪灭的明暗表：第一下打火就亮 → 灭 → 第二下再亮 → 灭 → 长亮。</summary>
	private static bool FlickerLit(float t)
	{
		// 音频已裁掉前导静音，t = 0 就是第一下打火石开口，所以一上来就亮
		if (t < FlickerFirstOff)
		{
			return true;
		}

		if (t < FlickerSecondOn)
		{
			return false;
		}

		if (t < FlickerSecondOff)
		{
			return true;
		}

		if (t < FlickerFinalOn)
		{
			return false;
		}

		return true;
	}

	/// <summary>同时改火焰特效与三盏光源，闪灭时两者一致。</summary>
	private static void SetLampVisual(KeroseneLampItem lamp, bool on)
	{
		try
		{
			GameObject? fx = lamp.m_FXGameObject;
			GameObject? self = lamp.gameObject;

			// 特效对象万一就是灯本身，关掉它连 KeroseneLampItem.Update 一起停，闪灭当场卡死
			bool separate = fx != null && self != null && fx.Pointer != self.Pointer;

			if (separate && fx!.activeSelf != on)
			{
				fx.SetActive(on);
			}
		}
		catch
		{
			// 特效对象已被销毁，忽略
		}

		SetLampLights(lamp, on);
	}

	/// <summary>按原版 <c>UpdateEffects</c> 的规则开关光源：室外灯优先，否则室内 + 核心。</summary>
	private static void SetLampLights(KeroseneLampItem lamp, bool on)
	{
		bool outdoor = false;

		try
		{
			Weather? weather = GameManager.GetWeatherComponent();
			outdoor = weather != null && weather.UseOutdoorLightingForLightSources();
		}
		catch
		{
			outdoor = false;
		}

		SetLight(lamp.m_LightIndoor, on && !outdoor);
		SetLight(lamp.m_LightIndoorCore, on && !outdoor);
		SetLight(lamp.m_LightOutdoor, on && outdoor);
	}

	private static void SetLight(Light? light, bool on)
	{
		if (light == null)
		{
			return;
		}

		try
		{
			if (light.enabled != on)
			{
				light.enabled = on;
			}
		}
		catch
		{
			// 组件已销毁，忽略
		}
	}

	/// <summary>把灯强行摆成「已点亮」，只在原版 <c>TurnOn</c> 抛异常时兜底。</summary>
	private static void ForceOnFields(KeroseneLampItem lamp)
	{
		try
		{
			lamp.m_On = true;
			lamp.m_TurnOnComplete = true;
			lamp.m_TurningOff = false;

			// 计时器直接推过门槛：UpdateEffects 下一次就会把光源点亮，
			// 不至于点着了还要黑着等一段 m_TurnOnEffectsDelay
			lamp.m_TurnOnEffectsTimer = lamp.m_TurnOnEffectsDelay + 0.01f;

			GameObject? fx = lamp.m_FXGameObject;
			if (fx != null && !fx.activeSelf)
			{
				fx.SetActive(true);
			}
		}
		catch (Exception ex)
		{
			LogError("点亮提灯失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 熄灭提灯：玩家主动关灯时走原版 <c>TurnOffDelayed</c>——它先播「熄灭起始」音、
	/// 置 <c>m_TurningOff</c>，等 <c>m_TurnOffDelay</c> 之后灯自己的 <c>Update</c>
	/// 再播第二声熄灭音并真正关灯，与手持时听到的完全一致。
	///
	/// 静音路径（换场景、燃料耗尽、关掉音效设置）用 <c>TurnOff(false)</c>，
	/// 免得每次过图都莫名其妙响一声。
	/// </summary>
	private static void ExtinguishLamp(KeroseneLampItem lamp, bool playAudio)
	{
		try
		{
			// 点火闪灭还没走完就要关灯：先把闪灭收尾，否则它每帧都在抢光源
			if (s_flickerActive)
			{
				EndFlicker(lamp);
			}

			// 点火音可能还在「呲」，关灯时一并掐掉，否则熄灭声和它叠在一起
			LampAudio.Stop();

			if (lamp.m_TurningOff)
			{
				// 已经在熄灭流程里：等它自己走完；
				// 万一灯的 Update 没在跑（物体被停用 / 离场），超时就一次性关掉
				if (Time.unscaledTime - s_extinguishAt > ExtinguishWatchdogSeconds)
				{
					ModLog.Warning("[挂腰] 熄灭超时，直接关灯");
					s_turnOffAt = 0f;
					lamp.TurnOff(false);
				}

				return;
			}

			s_extinguishAt = Time.unscaledTime;

			if (playAudio)
			{
				// 原版把「真正关灯」押后 m_TurnOffDelay 秒，prefab 里填到近四秒——
				// 一声「呼」之后灯还亮着，玩家只听到声音、看不到变化。
				float delay = Mathf.Clamp(
					Cfg?.TurnOffHoldSeconds ?? DefaultTurnOffDelaySeconds, 0f, MaxTurnOffDelaySeconds);

				lamp.m_TurnOffDelay = delay;

				lamp.TurnOffDelayed();

				// 原版收不了尾（它的计时被 FirstPersonHandsEnabled 挡着），这里自己记时刻
				s_turnOffAt = Time.unscaledTime + delay;

				if (Cfg?.Verbose ?? false)
				{
					ModLog.Msg($"[挂腰] 熄灭起始（{delay:F2}s 后关灯）");
				}
			}
			else
			{
				lamp.TurnOff(false);
			}
		}
		catch (Exception ex)
		{
			ModLog.Warning("原版熄灭失败，改为直接关灯：" + ex.Message);

			try
			{
				lamp.TurnOff(false);
			}
			catch (Exception inner)
			{
				LogError("关灯失败：" + inner.Message);
			}
		}
	}

	/// <summary>撤回一次还没走完的熄灭（玩家连按两下开关灯键时会发生）。</summary>
	private static void CancelExtinguish(KeroseneLampItem lamp)
	{
		try
		{
			lamp.m_TurningOff = false;
			lamp.m_TurnOffTimer = 0f;
			lamp.m_TurnOnComplete = true;

			// 撤销后就不该再有「到点关灯」这回事，否则下一帧会把它又灭了
			s_turnOffAt = 0f;

			if (Cfg?.Verbose ?? false)
			{
				ModLog.Msg("[挂腰] 撤销熄灭：灯保持点亮");
			}
		}
		catch (Exception ex)
		{
			if (Cfg?.Verbose ?? false)
			{
				ModLog.Warning("撤销熄灭失败：" + ex.Message);
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
		s_origTurnOffDelay = -1f;
		s_wantLit = false;
		s_heat = null;
		s_diagLogged = false;
		s_crouchDrop = 0f;
		s_turnOffAt = 0f;

		// 闪灭与待播音效都跟着这次挂腰一起作废，别让它落到下一盏灯身上
		s_flickerActive = false;
		s_flickerFlickers = false;
		s_flickerAudio = false;
		s_flickerFireLit = false;
		s_flickerLamp = IntPtr.Zero;
		s_flickerLoopPlayed = false;
		s_igniteNotBefore = 0f;
		s_pendingSounds.Clear();

		// 点火音还没放完就把灯收起来了：别让「呲」的尾音跟着玩家走
		LampAudio.Stop();
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
	/// 没挂着灯的时候，每两秒对一次账：有没有一盏「本该挂在腰上」的灯，现在能挂回去。
	///
	/// 依据是内存里的意图快照（<see cref="s_intentValid"/>），没有快照时才从
	/// <see cref="SaveLedger"/> 的记录里接回来（读档 / 重启游戏）。
	/// **灯亮不亮完全不影响这条路径**——记录里存着当时是亮是灭，照原样还原。
	/// </summary>
	private static void ReconcileIntent()
	{
		if (!(Cfg?.AutoRestore ?? true))
		{
			return;
		}

		float now = Time.unscaledTime;
		if (now < s_nextReconcileTime)
		{
			return;
		}

		// 世界还没起来（加载中 / 主菜单）就别查背包，否则会把记录误判成「找不到灯」
		if (GameManager.GetPlayerTransform() == null)
		{
			return;
		}

		s_nextReconcileTime = now + RestoreIntervalSeconds;

		// 没有内存快照：看看存档里有没有该挂回去的灯
		if (!s_intentValid)
		{
			if (s_restoreDeadline <= 0f)
			{
				return;
			}

			if (SaveLedger.TryLoad(out string name, out float fuel, out bool lit))
			{
				s_intentValid = true;
				s_intentName = name;
				s_intentFuel = fuel;
				s_intentLit = lit;
				s_intentMissingSince = 0f;
				ModLog.Msg($"[挂腰] 读到存档记录：{name}，灯{(lit ? "亮" : "灭")}");
			}
			else
			{
				// 老存档没有记录：退回「背包里点着一盏灯」的猜测，且每次进场景只猜一次
				TryGuessLitLantern();
				return;
			}
		}

		GearItem? lamp = FindLantern(s_intentName, s_intentFuel);
		if (lamp == null || IsInHands(lamp))
		{
			if (s_intentMissingSince <= 0f)
			{
				s_intentMissingSince = now;
			}

			float missing = now - s_intentMissingSince;

			if (missing >= RestoreWindowSeconds)
			{
				ModLog.Msg("[挂腰] 等不到上次挂腰的那盏提灯，放弃这次恢复");
				ForgetIntent("找不到那盏灯");
				return;
			}

			if (now >= s_nextDiagTime)
			{
				s_nextDiagTime = now + RestoreDiagIntervalSeconds;
				ModLog.Msg("[挂腰] 还在等那盏提灯进背包"
					+ $"（{missing:0.0}s，{(lamp == null ? "背包里没有提灯" : "提灯还在手上")}）");
			}

			return;
		}

		s_restoring = true;
		bool hung;

		try
		{
			hung = Hang(lamp, s_intentLit);
		}
		finally
		{
			s_restoring = false;
		}

		if (!hung)
		{
			// 找到了灯却挂不上去（物体正在被游戏搬动之类）：继续试，但也要有个尽头
			if (s_intentMissingSince <= 0f)
			{
				s_intentMissingSince = now;
			}

			if (now - s_intentMissingSince >= RestoreWindowSeconds)
			{
				ModLog.Msg("[挂腰] 反复挂不上去，放弃这次恢复");
				ForgetIntent("恢复失败");
			}
			else
			{
				ModLog.Msg("[挂腰] 恢复悬挂失败，下一轮再试");
			}

			return;
		}

		s_intentMissingSince = 0f;
		s_restoreDeadline = 0f;

		ModLog.Msg($"[挂腰] 按记录恢复悬挂（{s_intentName}，灯{(s_intentLit ? "亮" : "灭")}）");
	}

	/// <summary>
	/// 老存档（没有记录）的兜底：背包里有一盏点着的提灯，就认为它本来挂在腰上。
	/// 每次进场景只猜一次，免得玩家刚卸下就被猜回去。
	/// </summary>
	private static void TryGuessLitLantern()
	{
		if (s_guessTried)
		{
			return;
		}

		GearItem? lit = FindLanternInInventory(preferLit: true);
		if (lit == null || !IsLantern(lit) || IsInHands(lit) || !lit.m_KeroseneLampItem.IsOn())
		{
			return;
		}

		s_guessTried = true;
		s_restoreDeadline = 0f;

		ModLog.Msg("[挂腰] 检测到背包里有点着的提灯，自动恢复挂腰");

		Hang(lit);
	}

	/// <summary>
	/// 按「物品名 + 燃油百分比」在背包里认出上次那盏灯。
	/// 同型号多盏时取燃油最接近的；名字对不上也允许（不同世界的同名提灯），只是排序靠后。
	/// 只要背包里有提灯就一定会返回一盏——返回 null 只表示背包里根本没有提灯。
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

				float f = SaveLedger.FuelPercent(gi);
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
