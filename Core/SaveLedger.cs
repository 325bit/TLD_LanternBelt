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
/// 记录格式：<c>挂腰|物品名|燃油百分比|亮着</c>，例如
/// <c>1|GEAR_KeroseneLamp_Spelunkers|97.166|0</c>。
/// 物品名 + 燃油百分比用来在同一存档的多盏提灯里认出「就是这一盏」。
/// 注意燃油是**百分比 0~100**（<c>GetPercentFuelRemaining</c> 的原样值），不是 0~1 的比例。
/// </summary>
internal static class SaveLedger
{
	private const string ManagerName = "LanternBelt";

	private static readonly ModDataManager s_data = new ModDataManager(ManagerName, false);

	/// <summary>上一次写进去的内容，避免同一条记录反复刷日志。</summary>
	private static string s_lastWritten = string.Empty;

	private static bool s_warned;

	internal static void SaveHung(GearItem gi, bool lit)
	{
		Write(ItemName(gi), FuelPercent(gi), lit);
	}

	/// <summary>
	/// 只凭字段写记录，不需要 GearItem 还活着——场景切换时旧物体可能已经销毁，
	/// 但我们仍然要把它「应该挂着」这件事留在存档里。
	/// </summary>
	internal static void Write(string itemName, float fuelPercent, bool lit)
	{
		try
		{
			string payload = string.Format(
				CultureInfo.InvariantCulture,
				"1|{0}|{1:0.000}|{2}",
				string.IsNullOrEmpty(itemName) ? "?" : itemName,
				fuelPercent,
				lit ? "1" : "0");

			if (payload == s_lastWritten)
			{
				return;
			}

			if (!s_data.Save(payload))
			{
				WarnOnce("写入失败：当前没有活动存档（主菜单 / 读图中），本次挂腰不会被记住");
				return;
			}

			s_lastWritten = payload;

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				ModLog.Msg("[存档] 写入 " + payload);
			}
		}
		catch (Exception ex)
		{
			WarnOnce("写入异常：" + ex.Message);
		}
	}

	/// <summary>卸下 / 灯被拿走时清掉记录，别让下次进场景凭空挂回去。</summary>
	internal static void Clear(string reason)
	{
		try
		{
			if (s_lastWritten == "0")
			{
				return;
			}

			s_lastWritten = "0";
			s_data.Save("0");

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				ModLog.Msg("[存档] 清除记录（" + reason + "）");
			}
		}
		catch (Exception ex)
		{
			WarnOnce("清除记录异常：" + ex.Message);
		}
	}

	/// <summary>读出上次挂腰的那盏灯；没有记录或记录已清空时返回 false。</summary>
	internal static bool TryLoad(out string itemName, out float fuelPercent, out bool lit)
	{
		itemName = string.Empty;
		fuelPercent = -1f;
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

			if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fuelPercent))
			{
				fuelPercent = -1f;
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

	/// <summary>剩余燃油百分比（0~100），原样来自 <c>GetPercentFuelRemaining</c>。</summary>
	internal static float FuelPercent(GearItem gi)
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
