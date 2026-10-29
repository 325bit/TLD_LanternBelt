using HarmonyLib;
using Il2CppTLD.Gear;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 拦掉挂腰提灯触发的「第一人称点燃 / 熄灭手势」——不拦就会把双手锁死在动画里。
///
/// 原版三处会触发：<c>TurnOn</c> 收尾、<c>UpdateEffects</c> 第一段、<c>TurnOffDelayed</c> 开头。
/// 前两处返回假即可；第三处的调用方检查返回值，必须跳过原方法再把 <c>__result</c> 写成真。
/// 判定只认挂腰的那一盏，玩家点篝火 / 火把照常。三处的完整依据见 <c>docs/DEVELOPMENT.md</c> §4。
/// </summary>
[HarmonyPatch(typeof(KeroseneLampItem), "TurnOn")]
internal static class KeroseneLampItem_TurnOn
{
	internal static KeroseneLampItem? Current;

	private static void Prefix(KeroseneLampItem __instance)
	{
		Current = __instance;
	}

	private static void Postfix()
	{
		Current = null;
	}
}

[HarmonyPatch(typeof(KeroseneLampItem), "UpdateEffects")]
internal static class KeroseneLampItem_UpdateEffects
{
	internal static KeroseneLampItem? Current;

	private static void Prefix(KeroseneLampItem __instance)
	{
		Current = __instance;
	}

	/// <summary>
	/// Postfix 同时是「点火闪灭」的落点：原版每帧在这里按天气重写三盏光源的 enabled，
	/// 只有排在它后面写，闪灭才不会被当场抹平（早退分支也会走到这里）。
	/// </summary>
	private static void Postfix(KeroseneLampItem __instance)
	{
		LanternBeltManager.OnLampUpdated(__instance);
		Current = null;
	}
}

/// <summary>对应 TurnOn 收尾的那一次。</summary>
[HarmonyPatch(typeof(PlayerAnimation), "Trigger_Generic_Ignite")]
internal static class PlayerAnimation_Ignite
{
	private static bool Prefix()
	{
		KeroseneLampItem? lamp = KeroseneLampItem_TurnOn.Current;
		if (lamp == null)
		{
			return true;
		}

		return !LanternBeltManager.IsHungLampComponent(lamp);
	}
}

/// <summary>对应 UpdateEffects 第一段的那一次。</summary>
[HarmonyPatch(typeof(PlayerAnimation), "Trigger_Generic_Ignite_Confirm")]
internal static class PlayerAnimation_IgniteConfirm
{
	private static bool Prefix()
	{
		KeroseneLampItem? lamp = KeroseneLampItem_UpdateEffects.Current;
		if (lamp == null)
		{
			return true;
		}

		return !LanternBeltManager.IsHungLampComponent(lamp);
	}
}

/// <summary>记录「正在熄灭的是哪盏灯」，给下面的 extinguish 手势判定用。</summary>
[HarmonyPatch(typeof(KeroseneLampItem), "TurnOffDelayed")]
internal static class KeroseneLampItem_TurnOffDelayed
{
	internal static KeroseneLampItem? Current;

	private static void Prefix(KeroseneLampItem __instance)
	{
		Current = __instance;
	}

	private static void Postfix()
	{
		Current = null;
	}
}

/// <summary>
/// 对应 <c>TurnOffDelayed</c> 开头的那一次。
///
/// 这里不能像上面两个那样返回假——调用方写的是
/// <c>if (!Trigger_Generic_Extinguish(cb)) { Debug.LogError(...); return; }</c>，
/// 返回假就整段退出，熄灭音与熄灭计时器都不会发生。所以跳过原方法，但把结果改成真。
/// </summary>
[HarmonyPatch(typeof(PlayerAnimation), "Trigger_Generic_Extinguish")]
internal static class PlayerAnimation_Extinguish
{
	private static bool Prefix(ref bool __result)
	{
		KeroseneLampItem? lamp = KeroseneLampItem_TurnOffDelayed.Current;
		if (lamp == null)
		{
			return true;
		}

		if (!LanternBeltManager.IsHungLampComponent(lamp))
		{
			return true;
		}

		__result = true;
		return false;
	}
}
