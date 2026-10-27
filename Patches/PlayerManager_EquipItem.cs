using HarmonyLib;
using LanternBelt.Core;
using Il2CppTLD.Gear;

namespace LanternBelt.Patches;

/// <summary>
/// 玩家自己把挂腰的提灯拿到手上时，先解除挂腰，让原版从干净状态接管。
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "EquipItem")]
internal static class PlayerManager_EquipItem
{
	private static void Prefix(GearItem gi)
	{
		if (LanternBeltManager.IsHungLantern(gi))
		{
			LanternBeltManager.Unhang(quiet: true);
		}
	}
}

/// <summary>
/// 挂腰期间如果玩家通过原版途径（背包里对提灯「使用」）开关了一次灯，
/// 把我们的期望状态同步过去，别让每帧校正把玩家的操作改回去。
/// </summary>
[HarmonyPatch(typeof(KeroseneLampItem), "Toggle")]
internal static class KeroseneLampItem_Toggle
{
	private static void Postfix(KeroseneLampItem __instance)
	{
		LanternBeltManager.SyncWantLitFrom(__instance);
	}
}
