using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 把物品检视面板上的「装备」按钮改成「挂腰 / 收起」。
///
/// 只改文案不动控件：按钮本身还是原版那一个，事件链、手柄提示、键位全都沿用，
/// 与 HuntingRoads 给瞄准镜加「安装」按钮的做法相同。
/// 文案用的是自定义键，由 <see cref="Localization_Get"/> 在运行时翻译成当前语言。
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

		__result = LanternBeltManager.IsHungLantern(gi)
			? Localization_Get.StowKey
			: Localization_Get.HangKey;
	}
}
