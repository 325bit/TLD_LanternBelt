using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 把物品检视面板上的「装备」按钮改成「悬挂/卸下」。
///
/// 只改文案不动控件：按钮本身还是原版那一个，事件链、手柄提示、键位全都沿用，
/// 与 HuntingRoads 给瞄准镜加「安装/卸下」按钮的做法相同。
/// 文案恒定（不随状态变），因为同一个按钮就是靠点一下切换姿态的。
/// </summary>
[HarmonyPatch(typeof(ItemDescriptionPage), "GetEquipButtonLocalizationId")]
internal static class ItemDescriptionPage_EquipButtonLabel
{
	private static void Postfix(GearItem gi, ref string __result)
	{
		Settings? cfg = LanternBeltMod.settings;
		if (cfg == null || cfg.PanelKeepsVanilla)
		{
			return;
		}

		if (!LanternBeltManager.IsLantern(gi))
		{
			return;
		}

		__result = Localization_Get.MountKey;
	}
}
