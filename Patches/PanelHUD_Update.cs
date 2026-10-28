using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 借 HUD 面板自己的每帧更新来刷新挂腰提示。
///
/// 选这个注入点有两个理由：一是面板实例直接递进来，不必走全局查找（面板没加载时会抛）；
/// 二是 HUD 停用时 Update 本来就不跑，提示自然跟着停，不用额外判断菜单状态。
/// </summary>
[HarmonyPatch(typeof(Panel_HUD), "Update")]
internal static class PanelHUD_Update
{
	private static void Postfix(Panel_HUD __instance)
	{
		HudReadout.OnHudUpdate(__instance);
	}
}
