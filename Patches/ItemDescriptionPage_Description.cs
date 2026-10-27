using HarmonyLib;
using LanternBelt.Core;

namespace LanternBelt.Patches;

/// <summary>
/// 悬挂姿态下，在物品介绍正文末尾补一行「[已悬挂在腰带上]」，卸下后这行字消失。
///
/// 与 HuntingRoads 给装了瞄准镜的枪加「[已安装在步枪上]」用的是同一个注入点
/// （<c>ItemDescriptionPage.BuildItemDescription</c> 的返回值），不改任何 UI 控件。
/// </summary>
[HarmonyPatch(typeof(ItemDescriptionPage), "BuildItemDescription")]
internal static class ItemDescriptionPage_Description
{
	private static void Postfix(GearItem gi, ref string __result)
	{
		if (gi == null || string.IsNullOrEmpty(__result))
		{
			return;
		}

		if (!LanternBeltManager.IsHungLantern(gi))
		{
			return;
		}

		string line = Localization.Get(Localization_Get.MountedKey);
		if (string.IsNullOrEmpty(line) || __result.Contains(line))
		{
			return;
		}

		__result = __result + "\n" + line;
	}
}
