using System;
using System.Globalization;
using Il2CppTLD.Gear;
using MelonLoader;
using ModData;

namespace LanternBelt.Core;

/// <summary>
/// 把「哪盏提灯挂在腰上、灯亮不亮」写进 ModData，跨场景 / 读档后照原样恢复。
///
/// 为什么必须真存、不能猜：早期版本靠「背包里有一盏**点着**的提灯」反推挂腰状态，
/// 但玩家完全可以熄灯之后再出门——那样就再也猜不出来，挂腰状态凭空消失（19:55 实测日志）。
/// HuntingRoads 的瞄准镜能跨场景保留，用的也是这套 <see cref="ModDataManager"/>。
///
/// 记录格式：<c>挂腰|物品名|燃油比例|亮着</c>，例如
/// <c>1|GEAR_KeroseneLamp_Spelunkers|0.734|0</c>。
/// 物品名 + 燃油比例用来在同一存档的多盏提灯里认出「就是这一盏」。
/// </summary>
internal static class SaveLedger
{
	private const string ManagerName = "LanternBelt";

	private static readonly ModDataManager s_data = new ModDataManager(ManagerName, false);

	private static bool s_warned;

	internal static void SaveHung(GearItem gi, bool lit)
	{
		try
		{
			string name = ItemName(gi);
			float fuel = FuelFraction(gi);
			string payload = string.Format(
				CultureInfo.InvariantCulture,
				"1|{0}|{1:0.000}|{2}",
				name,
				fuel,
				lit ? "1" : "0");

			if (!s_data.Save(payload))
			{
				WarnOnce("写入失败：当前没有活动存档（主菜单 / 读图中），本次挂腰不会被记住");
			}
		}
		catch (Exception ex)
		{
			WarnOnce("写入异常：" + ex.Message);
		}
	}

	/// <summary>卸下 / 灯被拿走时清掉记录，别让下次进场景凭空挂回去。</summary>
	internal static void Clear()
	{
		try
		{
			s_data.Save("0");
		}
		catch (Exception ex)
		{
			WarnOnce("清除记录异常：" + ex.Message);
		}
	}

	/// <summary>读出上次挂腰的那盏灯；没有记录或记录已清空时返回 false。</summary>
	internal static bool TryLoad(out string itemName, out float fuel, out bool lit)
	{
		itemName = string.Empty;
		fuel = -1f;
		lit = false;

		try
		{
			string? text = s_data.Load();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			string[] parts = text.Split('|');
			if (parts.Length < 4 || parts[0].Trim() != "1")
			{
				return false;
			}

			itemName = parts[1].Trim();

			if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fuel))
			{
				fuel = -1f;
			}

			lit = parts[3].Trim() == "1";
			return true;
		}
		catch (Exception ex)
		{
			WarnOnce("读取异常：" + ex.Message);
			return false;
		}
	}

	internal static string ItemName(GearItem gi)
	{
		try
		{
			UnityEngine.GameObject go = gi.gameObject;
			return go == null ? string.Empty : go.name;
		}
		catch
		{
			return string.Empty;
		}
	}

	internal static float FuelFraction(GearItem gi)
	{
		try
		{
			KeroseneLampItem? lamp = gi.m_KeroseneLampItem;
			return lamp == null ? -1f : lamp.GetPercentFuelRemaining();
		}
		catch
		{
			return -1f;
		}
	}

	private static void WarnOnce(string message)
	{
		if (s_warned)
		{
			return;
		}

		s_warned = true;
		MelonLogger.Warning("[挂腰/存档] " + message);
	}
}
