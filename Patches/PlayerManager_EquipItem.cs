using System;
using HarmonyLib;
using Il2CppTLD.Gear;
using LanternBelt.Core;
using UnityEngine;

namespace LanternBelt.Patches;

/// <summary>
/// 物品栏点击入口：在物品检视面板里对提灯点「挂腰 / 收起」，切换悬挂姿态。
///
/// 这里拦的是 <c>PlayerManager.EquipItem</c> 而不是面板上的按钮方法，
/// 因为它是所有「装备」路径（检视面板按钮、快速装备、拾取后自动装备）的汇合点，
/// 与 HuntingRoads 挂瞄准镜的做法一致。
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "EquipItem")]
internal static class PlayerManager_EquipItem
{
	private static bool Prefix(GearItem gi)
	{
		Settings? cfg = LanternBeltMod.settings;
		if (cfg == null || cfg.PanelKeepsVanilla)
		{
			return true;
		}

		if (!LanternBeltManager.IsLantern(gi))
		{
			// 枪、信号棒等其它物品一律交回原版，挂腰的灯不占手
			return true;
		}

		// 切换成功就吞掉原版「拿到手上」；失败时退回原版，别让玩家点了没反应
		return !LanternBeltManager.ToggleHolster(gi);
	}
}

/// <summary>
/// 丢弃前先收起：否则提灯的父节点还挂在玩家身上，落地物体会跟着人走。
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "Drop", new Type[]
{
	typeof(GameObject),
	typeof(bool)
})]
internal static class PlayerManager_Drop
{
	private static void Prefix(GameObject go)
	{
		if (LanternBeltManager.IsHungLantern(go))
		{
			// 灯被丢在地上：确实不该再挂回去了，连记录一起清
			LanternBeltManager.Unhang(quiet: true, restoreActive: false, forget: true);
		}
	}
}

/// <summary>
/// 挂腰期间如果玩家通过原版途径开关了一次灯，把我们的期望状态同步过去，
/// 别让每帧校正把玩家的操作改回去。
/// </summary>
[HarmonyPatch(typeof(KeroseneLampItem), "Toggle")]
internal static class KeroseneLampItem_Toggle
{
	private static void Postfix(KeroseneLampItem __instance)
	{
		LanternBeltManager.SyncWantLitFrom(__instance);
	}
}
