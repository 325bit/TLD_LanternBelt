using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 运行时翻译本 mod 自己的本地化键。
///
/// 不往游戏字符串表里塞键值、也不依赖任何本地化库：只在 <c>Localization.Get</c>
/// 返回之前把 <c>LANTERNBELT_*</c> 这一类查询替换掉，其余一律放行。
/// 这样即使以后游戏改了字符串表加载方式，按钮文案也不会变成一串键名。
/// </summary>
[HarmonyPatch(typeof(Localization), "Get")]
internal static class Localization_Get
{
	internal const string HangKey = "LANTERNBELT_Hang";

	internal const string StowKey = "LANTERNBELT_Stow";

	private static void Postfix(string __0, ref string __result)
	{
		if (!LanternBeltManager.IsLanternLocalizationKey(__0))
		{
			return;
		}

		__result = Loc.Term(__0);
	}
}
