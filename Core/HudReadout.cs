using System;
using Il2CppTLD.Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LanternBelt.Core;

/// <summary>
/// 挂腰时在 HUD 上补一行「灯油 / 耐久」。
///
/// 为什么要自己画：原版只在物品**拿在手上**时由 <c>EquipItemPopup</c> 画那根耐久条，
/// <c>Update</c> 里按类型分派取数（<c>TorchItem.GetBurnProgress</c> /
/// <c>FlareItem.GetNormalizedBurnTimeLeft</c> / <c>FlashlightItem.GetNormalizedCharge</c> /
/// <c>NoiseMakerItem.GetNormalizedWickTimeLeft</c> / 兜底 <c>GearItem.GetNormalizedCondition</c>），
/// 其中**没有提灯分支**——灯挂到腰上以后，手是空的，这条信息就彻底断了。
/// 灯油更没有任何 HUD 出口（<c>KeroseneLampItem</c> 只借 HUD 取消加注进度条）。
///
/// 做法与 <c>Panel_HUD</c> 上既有第三方 HUD 文本一致：自建一个 <c>UILabel</c>，
/// 字体取 <c>FontManager.GetUIFontForCharacterSet</c>，位置锚在原版耐久条
/// （<c>EquipItemPopup.m_ObjectDuration</c>）上，这样缩放与布局跟原版一起走。
///
/// 为什么不挂在那根条下面：弹出条整套隐藏时会把子物体一起停用，挂上去等于跟着一起消失。
/// 所以只借它的坐标，父节点仍是常驻的 HUD 面板根。
/// </summary>
internal static class HudReadout
{
	private const string LabelName = "LanternBelt_HudReadout";

	/// <summary>压在 HUD 常规文字之上，但仍受面板裁剪。</summary>
	private const int LabelDepth = 100;

	private static GameObject? s_root;
	private static UILabel? s_label;
	private static string s_lastText = string.Empty;
	private static bool s_buildErrorLogged;
	private static bool s_missingAnchorLogged;

	/// <summary>HUD 面板每帧更新时调用；面板实例由补丁直接递进来，省一次全局查找。</summary>
	internal static void OnHudUpdate(Panel_HUD hud)
	{
		if (hud == null)
		{
			return;
		}

		GearItem? lamp = LanternBeltManager.HungLantern;
		Settings? cfg = LanternBeltMod.settings;

		if (lamp == null || cfg == null || !cfg.ShowHudReadout)
		{
			Hide();
			return;
		}

		try
		{
			// 面板被停用（读档、过场、菜单）时不必画，反正也看不见
			if (!hud.gameObject.activeInHierarchy)
			{
				Hide();
				return;
			}

			string text = Loc.HudLine(FuelPercent(lamp), ConditionPercent(lamp));

			if (!IsAlive() && !Build(hud, cfg))
			{
				return;
			}

			if (s_label != null && text != s_lastText)
			{
				s_label.text = text;
				s_lastText = text;
			}

			if (s_root != null && !s_root.activeSelf)
			{
				s_root.SetActive(true);
			}
		}
		catch (Exception ex)
		{
			if (!s_buildErrorLogged)
			{
				s_buildErrorLogged = true;
				ModLog.Warning("HUD 提示绘制失败，本局不再重试：" + ex.Message);
			}

			Hide();
		}
	}

	/// <summary>场景切换 / 读档时 HUD 面板会连同子物体一起销毁，这里把句柄清干净。</summary>
	internal static void Reset()
	{
		DestroyStale();
	}

	// ────────────────────────── 绘制 ──────────────────────────

	private static bool Build(Panel_HUD hud, Settings cfg)
	{
		DestroyStale();

		try
		{
			Transform hudRoot = hud.transform;

			FontManager? fonts = GameManager.GetFontManager();
			if (fonts == null)
			{
				return false;
			}

			UIFont? font = fonts.GetUIFontForCharacterSet(Loc.UseChineseText ? CharacterSet.CJK : CharacterSet.Latin);
			if (font == null)
			{
				return false;
			}

			// 借原版耐久条的坐标；拿不到就退到面板原点，玩家用设置里的偏移自己挪
			Transform anchor = FindAnchor(hud, hudRoot);
			Vector3 local = hudRoot.InverseTransformPoint(anchor.position);

			GameObject root = new GameObject(LabelName);
			root.transform.SetParent(hudRoot, false);
			root.transform.localScale = Vector3.one;
			root.transform.localPosition = local + new Vector3(cfg.HudOffsetX, cfg.HudOffsetY, 0f);

			UILabel label = root.AddComponent<UILabel>();
			label.font = font;
			label.fontSize = Mathf.Max(8, Mathf.RoundToInt(cfg.HudFontSize));
			label.color = new Color(0.9f, 0.95f, 1f, 1f);
			label.effectStyle = UILabel.Effect.Outline;
			label.effectColor = new Color(0.125f, 0.094f, 0.094f, 0.85f);
			label.effectDistance = new Vector2(1.5f, 1.5f);
			label.overflowMethod = UILabel.Overflow.ResizeFreely;
			label.alignment = NGUIText.Alignment.Center;
			label.pivot = UIWidget.Pivot.Center;
			label.depth = LabelDepth;
			label.text = string.Empty;

			s_root = root;
			s_label = label;
			s_lastText = string.Empty;

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				// 「面板=」为 False 说明 HUD 根上找不到 UIPanel，标签不会被任何面板绘制，
				// 那种情况下日志是唯一线索，所以把结论先打出来。
				ModLog.Msg($"[挂腰] HUD 提示已建立：锚点={anchor.name}"
					+ $" 本地坐标={root.transform.localPosition}"
					+ $" 面板={(hud.GetComponentInParent<UIPanel>() != null)}");
			}

			return true;
		}
		catch (Exception ex)
		{
			if (!s_buildErrorLogged)
			{
				s_buildErrorLogged = true;
				ModLog.Warning("建立 HUD 提示失败，本局不再重试：" + ex.Message);
			}

			return false;
		}
	}

	private static Transform FindAnchor(Panel_HUD hud, Transform fallback)
	{
		try
		{
			EquipItemPopup? popup = hud.m_EquipItemPopup;
			if (popup != null)
			{
				if ((Object)popup.m_ObjectDuration != null)
				{
					return popup.m_ObjectDuration.transform;
				}

				if ((Object)popup.m_EquipPopupWidget != null)
				{
					return popup.m_EquipPopupWidget.transform;
				}
			}
		}
		catch
		{
			// 面板还没建好，走兜底
		}

		if (!s_missingAnchorLogged)
		{
			s_missingAnchorLogged = true;
			ModLog.Msg("[挂腰] 找不到原版耐久条，HUD 提示改用面板原点，位置请用设置里的偏移微调");
		}

		return fallback;
	}

	private static void Hide()
	{
		if (s_root == null)
		{
			return;
		}

		try
		{
			if ((Object)s_root == null)
			{
				DestroyStale();
				return;
			}

			if (s_root.activeSelf)
			{
				s_root.SetActive(false);
			}
		}
		catch
		{
			DestroyStale();
		}
	}

	private static bool IsAlive()
	{
		if (s_root == null || s_label == null)
		{
			return false;
		}

		try
		{
			return (Object)s_root != null && (Object)s_label != null;
		}
		catch
		{
			return false;
		}
	}

	private static void DestroyStale()
	{
		try
		{
			if ((Object?)s_root != null)
			{
				Object.Destroy(s_root);
			}
		}
		catch
		{
			// 已经随场景销毁，无需处理
		}

		s_root = null;
		s_label = null;
		s_lastText = string.Empty;
	}

	// ────────────────────────── 取数 ──────────────────────────

	private static float FuelPercent(GearItem gi)
	{
		try
		{
			KeroseneLampItem? lamp = gi.m_KeroseneLampItem;
			return lamp == null ? 0f : lamp.GetPercentFuelRemaining();
		}
		catch
		{
			return 0f;
		}
	}

	private static float ConditionPercent(GearItem gi)
	{
		try
		{
			return gi.m_CurrentHP;
		}
		catch
		{
			return 0f;
		}
	}
}
