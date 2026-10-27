using System;
using LanternBelt.Core;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(LanternBelt.LanternBeltMod), LanternBelt.BuildInfo.GUIName, LanternBelt.BuildInfo.Version, LanternBelt.BuildInfo.Author)]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace LanternBelt;

/// <summary>
/// 把煤油提灯挂到腰上：双手空出来，灯照样亮着、照样发热。
///
/// 入口两处：
/// · 物品栏里对提灯点「悬挂/卸下」——悬挂姿态的唯一切换入口（与瞄准镜的「安装/卸下」同形）
/// · 快捷键 L（可改）——只在挂腰状态生效，开关提灯
/// </summary>
public class LanternBeltMod : MelonMod
{
	internal static Settings? settings;

	public override void OnInitializeMelon()
	{
		ModLog.Bind(LoggerInstance);

		try
		{
			settings = new Settings();
			settings.AddToModSettings(BuildInfo.GUIName);
		}
		catch (Exception ex)
		{
			LoggerInstance.Error("初始化设置失败，将使用内置默认值：" + ex);
		}

		LoggerInstance.Msg($"{BuildInfo.GUIName} {BuildInfo.Version} 已加载（开关灯键={settings?.LampKey}）");

		try
		{
			HarmonyInstance.PatchAll();

			int patched = 0;
			foreach (System.Reflection.MethodBase _ in HarmonyInstance.GetPatchedMethods())
			{
				patched++;
			}

			LoggerInstance.Msg($"Harmony 补丁已应用，共 {patched} 个方法");
		}
		catch (Exception ex)
		{
			LoggerInstance.Error("应用 Harmony 补丁失败，挂腰功能不会生效：" + ex);
		}
	}

	public override void OnSceneWasLoaded(int buildIndex, string sceneName)
	{
		try
		{
			LanternBeltManager.OnSceneChanged(sceneName);
		}
		catch (Exception ex)
		{
			LoggerInstance.Error("场景切换处理失败：" + ex);
		}
	}

	public override void OnUpdate()
	{
		try
		{
			LanternBeltManager.Tick();
		}
		catch (Exception ex)
		{
			LoggerInstance.Error("挂腰状态校正异常：" + ex);
		}

		if (settings == null || !IsGameplayActive())
		{
			return;
		}

		// L 只在挂腰时生效；没挂腰时直接忽略，不会干扰游戏原有按键
		if (Pressed(settings.LampKey))
		{
			LanternBeltManager.ToggleLampFromHotkey();
		}
	}

	private static bool Pressed(KeyCode key)
	{
		if (key == KeyCode.None)
		{
			return false;
		}

		try
		{
			return Input.GetKeyDown(key);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsGameplayActive()
	{
		if (Time.timeScale <= 0f)
		{
			return false;
		}

		try
		{
			return GameManager.GetPlayerTransform() != null;
		}
		catch
		{
			return false;
		}
	}
}
