using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 装备栏入口：在物品检视面板里选中提灯点「装备」时，改为挂腰 / 收起。
///
/// 为什么不新加一个按钮：这个面板的菜单项是 NGUI 预制件，逐个绑了 onClick 到
/// OnHarvest / OnRefuel 这类具体方法，克隆一个按钮还要重接事件委托，代价与风险都高于收益。
/// 直接接管已有的「装备」动作，交互路径和原版完全一致，也自动获得手柄 / 键位提示。
/// </summary>
[HarmonyPatch(typeof(Panel_Inventory_Examine), "OnEquip")]
internal static class Panel_Inventory_Examine_OnEquip
{
	private static bool Prefix(Panel_Inventory_Examine __instance)
	{
		Settings? cfg = LanternBeltMod.settings;
		if (cfg == null || !cfg.PanelToggle || cfg.PanelKeepsVanilla)
		{
			return true;
		}

		GearItem gi = __instance.GearItem;
		if (!LanternBeltManager.IsLantern(gi))
		{
			return true;
		}

		LanternBeltManager.ToggleHolster(gi);

		// 吞掉原版「拿到手上」，否则刚挂上就被卸下来
		return false;
	}
}
