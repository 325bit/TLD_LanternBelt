using HarmonyLib;
using Il2CppTLD.Gear;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 拦掉挂腰提灯触发的「第一人称点燃手势」。
///
/// 原版有两处会触发它，两处都要挡：
/// <list type="number">
/// <item><c>KeroseneLampItem.TurnOn</c> 收尾：<c>if (FirstPersonHandsEnabled(m_GearItem)) Trigger_Generic_Ignite()</c></item>
/// <item><c>KeroseneLampItem.UpdateEffects</c> 第一段：<c>if (!m_TurnOnComplete) Trigger_Generic_Ignite_Confirm()</c></item>
/// </list>
///
/// 灯拿在手上时这是正常的抬手点灯动作；挂在腰上时手部槽位里没有灯，动画收不回来，
/// <c>PlayerAnimation</c> 的状态机就一直停在「点燃中」——玩家看到的是**双手锁死**：
/// 不能装备枪械 / 石块 / 木炭，也不能与物品互动。
///
/// 判定只针对**挂腰的那盏灯**，玩家点篝火、点火把的手势照常播放。
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

	private static void Postfix()
	{
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
