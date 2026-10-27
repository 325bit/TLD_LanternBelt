using UnityEngine;

namespace LanternBelt;

/// <summary>
/// 少量屏幕提示文本与按钮文案的中英对照。本 mod 不往游戏本地化表里塞键值：
/// 按钮文案走 <c>Localization.Get</c> 的运行时替换（见 Patches/Localization_Get.cs），
/// 屏幕提示直接由这里生成。
/// </summary>
internal static class Loc
{
	private static bool UseChinese
	{
		get
		{
			int mode = LanternBeltMod.settings?.Language ?? 0;
			if (mode == 1)
			{
				return true;
			}

			if (mode == 2)
			{
				return false;
			}

			SystemLanguage lang = Application.systemLanguage;
			return lang == SystemLanguage.ChineseSimplified || lang == SystemLanguage.ChineseTraditional;
		}
	}

	internal static string HangOn => UseChinese ? "提灯已悬挂在腰上（按 L 开关）" : "Lantern hung on your belt (press L to toggle)";

	internal static string HangOff => UseChinese ? "提灯已从腰上卸下" : "Lantern taken off your belt";

	internal static string NoLantern => UseChinese ? "背包里没有提灯" : "No lantern in your pack";

	internal static string NoFuel => UseChinese ? "提灯没油了" : "The lantern is out of fuel";

	internal static string LampOn => UseChinese ? "提灯已点亮" : "Lantern lit";

	internal static string LampOff => UseChinese ? "提灯已熄灭" : "Lantern extinguished";

	/// <summary>把本 mod 的本地化键翻成文本；不认识的键原样返回。</summary>
	internal static string Term(string key)
	{
		switch (key)
		{
			case "LANTERNBELT_Mount":
				return UseChinese ? "悬挂/卸下" : "Hang / Take off";

			case "LANTERNBELT_Mounted":
				return UseChinese ? "[已悬挂在腰带上]" : "[Hung on your belt]";

			default:
				return key;
		}
	}
}
