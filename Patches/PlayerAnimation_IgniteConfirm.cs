using HarmonyLib;
using Il2CppTLD.Gear;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 兜底拦截「点燃确认」手部动画。
///
/// 原版 <c>KeroseneLampItem.UpdateEffects</c> 在点燃特效计时到点后会调
/// <c>PlayerAnimation.Trigger_Generic_Ignite_Confirm</c> —— 那是第一人称抬手点灯的手势。
/// 挂腰时提灯根本不在手上，这段动画没有对应模型可以收尾，手部状态机会一直卡在
/// 「正在点燃」，于是装备枪械 / 石块 / 木炭、与物品互动全部失效。
///
/// 主路径已经在 <c>ReconcileLamp</c> 里通过 <c>m_TurnOnComplete = true</c> 跳过这段；
/// 这里再拦一层，防止某个时序分支仍然漏过去。判定只针对**挂腰的那盏灯**，
/// 玩家点篝火、点火把时的手部动画完全不受影响。
/// </summary>
[HarmonyPatch(typeof(KeroseneLampItem), "UpdateEffects")]
internal static class KeroseneLampItem_UpdateEffects
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
