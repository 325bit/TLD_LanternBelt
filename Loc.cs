using UnityEngine;

namespace LanternBelt;

/// <summary>
/// 少量屏幕提示文本的中英对照。本 mod 不往游戏本地化表里塞键值，
/// 因为没有任何新增 UI 控件——只有 HUD 上的一行提示。
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

	internal static string HangOn => UseChinese ? "提灯已挂到腰上" : "Lantern hung on your belt";

	internal static string HangOff => UseChinese ? "提灯已收起" : "Lantern stowed";

	internal static string NoLantern => UseChinese ? "背包里没有提灯" : "No lantern in your pack";

	internal static string NoFuel => UseChinese ? "提灯没油了" : "The lantern is out of fuel";

	internal static string LampOn => UseChinese ? "提灯已点亮" : "Lantern lit";

	internal static string LampOff => UseChinese ? "提灯已熄灭" : "Lantern extinguished";
}
