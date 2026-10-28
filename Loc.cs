using UnityEngine;

namespace LanternBelt;

/// <summary>
/// 少量屏幕提示文本与按钮文案的中英对照。本 mod 不往游戏本地化表里塞键值：
/// 按钮文案走 <c>Localization.Get</c> 的运行时替换（见 Patches/Localization_Get.cs），
/// 屏幕提示直接由这里生成。
/// </summary>
internal static class Loc
{
	/// <summary>当前是否走中文文案；HUD 提示据此选字体（中文要 CJK 字库，拉丁字库缺字形）。</summary>
	internal static bool UseChineseText
	{
		get { return UseChinese; }
	}

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

	/// <summary>
	/// HUD 上那一行「灯油 / 耐久」。取整到百分比——精确到小数没有意义，
	/// 而且文字只在整数变化时才重设，免得每帧都重建网格。
	/// </summary>
	internal static string HudLine(float fuelPercent, float conditionPercent)
	{
		int fuel = Mathf.Clamp(Mathf.RoundToInt(fuelPercent), 0, 100);
		int condition = Mathf.Clamp(Mathf.RoundToInt(conditionPercent), 0, 100);

		return UseChinese
			? $"灯油 {fuel}% · 耐久 {condition}%"
			: $"Fuel {fuel}% · Condition {condition}%";
	}

	/// <summary>把本 mod 的本地化键翻成文本；不认识的键原样返回。</summary>
	internal static string Term(string key)
	{
		switch (key)
		{
			case "LANTERNBELT_Mount":
				return UseChinese ? "悬挂/卸下" : "Hang / Take off";

			case "LANTERNBELT_Mounted":
				return UseChinese ? "[提灯已悬挂]" : "[Lantern hung]";

			default:
				return key;
		}
	}
}
