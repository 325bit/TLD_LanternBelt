using System;
using System.Collections.Generic;
using Il2CppTLD.Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LanternBelt.Core;

/// <summary>
/// 挂腰时在 HUD 上补一笔「灯油」读数：一根白色条 + 一行文字。
///
/// 为什么要自己画：原版只在物品**拿在手上**时由 <c>EquipItemPopup</c> 画那根耐久条，
/// <c>Update</c> 里按类型分派取数（<c>TorchItem.GetBurnProgress</c> /
/// <c>FlareItem.GetNormalizedBurnTimeLeft</c> / <c>FlashlightItem.GetNormalizedCharge</c> /
/// <c>NoiseMakerItem.GetNormalizedWickTimeLeft</c> / 兜底 <c>GearItem.GetNormalizedCondition</c>），
/// 其中**没有提灯分支**——灯挂到腰上以后，手是空的，这条信息就彻底断了。
/// 灯油更没有任何 HUD 出口（<c>KeroseneLampItem</c> 只借 HUD 取消加注进度条）。
///
/// 条的做法是**克隆原版那根耐久条**（<c>EquipItemPopup.m_ObjectDuration</c>），
/// 而不是照着它的 sprite 名自己拼一条：atlas、sprite、九宫格边框、宽高、pivot、颜色
/// 全部原样继承，分辨率或皮肤改动后也自动跟上。克隆出来的填充 sprite
/// （<c>m_SpriteFillDuration</c> 的同位子物体）只改 <c>fillAmount</c>。
///
/// 为什么整块不挂进原版条的子树：弹出条隐藏时会把子物体一起停用，挂上去等于跟着一起消失。
/// 所以只借它的位置与外观，父节点仍是常驻的 HUD 面板根。
/// </summary>
internal static class HudReadout
{
	private const string LabelName = "LanternBelt_HudReadout";

	private const string BarName = "LanternBelt_FuelBar";

	/// <summary>压在 HUD 常规文字之上，但仍受面板裁剪。</summary>
	private const int LabelDepth = 100;

	/// <summary>克隆条整体抬高的层数，保持背景与填充的相对前后关系。</summary>
	private const int BarDepthBoost = 60;

	/// <summary>找不到原版条模板时的重试间隔（秒）。</summary>
	private const float BarRetrySeconds = 3f;

	private static GameObject? s_root;
	private static UILabel? s_label;
	private static GameObject? s_bar;
	private static UISprite? s_fill;
	private static string s_lastText = string.Empty;
	private static float s_lastFill = -1f;
	private static float s_barRetryAt;
	private static bool s_buildErrorLogged;
	private static bool s_missingAnchorLogged;
	private static bool s_missingBarLogged;

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

			if (!IsAlive() && !Build(hud, cfg))
			{
				return;
			}

			float fuel = FuelPercent(lamp);
			float condition = ConditionPercent(lamp);

			// 条：原版模板可能比 HUD 本体晚就绪，缺了就按间隔补建，别一直只剩文字
			if (cfg.ShowHudBar && !BarAlive())
			{
				TryBuildBar(hud, cfg);
			}

			if (s_fill != null && cfg.ShowHudBar)
			{
				float target = Mathf.Clamp01(fuel * 0.01f);
				if (Mathf.Abs(target - s_lastFill) > 0.002f)
				{
					s_fill.fillAmount = target;
					s_lastFill = target;
				}
			}

			string text = Loc.HudLine(fuel, condition);
			if (s_label != null && text != s_lastText)
			{
				s_label.text = text;
				s_lastText = text;
			}

			if (s_root != null && !s_root.activeSelf)
			{
				s_root.SetActive(true);
			}

			if (s_bar != null && cfg.ShowHudBar && !s_bar.activeSelf)
			{
				s_bar.SetActive(true);
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
			s_barRetryAt = 0f;

			// 条依赖 EquipItemPopup 的模板，模板没就绪也先把文字立起来，之后按间隔补
			TryBuildBar(hud, cfg);

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				// 「面板=」为 False 说明 HUD 根上找不到 UIPanel，标签不会被任何面板绘制，
				// 那种情况下日志是唯一线索，所以把结论先打出来。
				ModLog.Msg($"[挂腰] HUD 提示已建立：锚点={anchor.name}"
					+ $" 本地坐标={root.transform.localPosition}"
					+ $" 条={(BarAlive() ? "已建" : "缺模板")}"
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

	/// <summary>
	/// 克隆原版耐久条做油量条。失败不是致命的：文字仍然在，下次再试。
	///
	/// 位置不做任何换算——把克隆体直接摆在容器原点、保留它自己的 pivot 与缩放，
	/// 于是「偏移填 0」就等于「长在原版耐久条该在的地方」。
	/// </summary>
	private static void TryBuildBar(Panel_HUD hud, Settings cfg)
	{
		if (s_root == null)
		{
			return;
		}

		float now = Time.unscaledTime;
		if (now < s_barRetryAt)
		{
			return;
		}

		s_barRetryAt = now + BarRetrySeconds;

		try
		{
			EquipItemPopup? popup = hud.m_EquipItemPopup;
			GameObject? template = popup?.m_ObjectDuration;

			if (popup == null || template == null)
			{
				if (!s_missingBarLogged)
				{
					s_missingBarLogged = true;
					ModLog.Msg("[挂腰] 还没拿到原版耐久条模板，油量条稍后补建");
				}

				return;
			}

			GameObject bar = Object.Instantiate(template);
			bar.name = BarName;
			bar.transform.SetParent(s_root.transform, false);
			bar.transform.localPosition = Vector3.zero;
			bar.SetActive(true);

			// 模板自带百分比标签，那是原版耐久值，留着会误导
			foreach (UILabel stray in bar.GetComponentsInChildren<UILabel>(true))
			{
				if (stray != null)
				{
					stray.gameObject.SetActive(false);
				}
			}

			// 抬高整条，保持背景/填充的相对前后
			foreach (UIWidget widget in bar.GetComponentsInChildren<UIWidget>(true))
			{
				if (widget != null)
				{
					widget.depth += BarDepthBoost;
				}
			}

			UISprite? fill = FindFill(popup, template, bar);

			s_bar = bar;
			s_fill = fill;
			s_lastFill = -1f;
			s_missingBarLogged = false;

			if (fill == null)
			{
				// 条本身看得见，但油量填不进去：只报一次，别再重建
				ModLog.Warning("油量条建好了但没认出填充 sprite，条会保持静态；请在日志里把这一行连同「油量条已建立」发作者");
			}

			PlaceLabelBelowBar(cfg);

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				UIWidget? w = BarWidget();
				ModLog.Msg("[挂腰] 油量条已建立：模板=" + template.name
					+ $" 尺寸={(w == null ? 0 : w.width)}x{(w == null ? 0 : w.height)}"
					+ $" 填充={(fill == null ? "未找到" : fill.name)}"
					+ $" sprite={(fill == null ? "-" : fill.spriteName)}");
			}
		}
		catch (Exception ex)
		{
			ModLog.Warning("建立油量条失败，稍后重试：" + ex.Message);
			s_bar = null;
			s_fill = null;
		}
	}

	/// <summary>
	/// 在克隆体里找出与 <c>m_SpriteFillDuration</c> 对应的那个 sprite。
	///
	/// 先按层级路径对位（最准），对不上再取层数最高的 sprite 兜底——
	/// 填充条画在背景之上，深度通常就是最高的那个。
	/// </summary>
	private static UISprite? FindFill(EquipItemPopup popup, GameObject template, GameObject clone)
	{
		try
		{
			UISprite? original = popup.m_SpriteFillDuration;
			if (original != null)
			{
				string path = RelativePath(original.transform, template.transform);
				if (path.Length > 0)
				{
					Transform? found = clone.transform.Find(path);
					UISprite? sprite = found == null ? null : found.GetComponent<UISprite>();
					if (sprite != null)
					{
						return sprite;
					}
				}
			}
		}
		catch
		{
			// 落到下面的兜底
		}

		UISprite? best = null;
		foreach (UISprite sprite in clone.GetComponentsInChildren<UISprite>(true))
		{
			if (sprite == null)
			{
				continue;
			}

			if (best == null || sprite.depth > best.depth)
			{
				best = sprite;
			}
		}

		return best;
	}

	/// <summary>文字压在条的下方正中。条的 pivot 可能是 Left/Top，所以用 pivotOffset 换算中心。</summary>
	private static void PlaceLabelBelowBar(Settings cfg)
	{
		if (s_label == null)
		{
			return;
		}

		float centerX = 0f;
		float centerY = 0f;
		float halfHeight = 0f;

		UIWidget? w = BarWidget();
		if (w != null && s_bar != null)
		{
			Vector3 scale = s_bar.transform.localScale;

			// pivotOffset 是 0~1 的比例，(0.5 - p) * 尺寸 = 中心相对原点的位移
			Vector2 po = w.pivotOffset;
			centerX = (0.5f - po.x) * w.width * scale.x;
			centerY = (0.5f - po.y) * w.height * scale.y;
			halfHeight = w.height * Mathf.Abs(scale.y) * 0.5f;
		}

		float gap = Mathf.Max(4f, cfg.HudFontSize * 0.55f);

		s_label.transform.localPosition = new Vector3(centerX, centerY - halfHeight - gap, 0f);
	}

	private static UIWidget? BarWidget()
	{
		if (s_bar == null)
		{
			return null;
		}

		UIWidget? w = s_bar.GetComponent<UIWidget>();
		if (w != null)
		{
			return w;
		}

		return s_bar.GetComponentInChildren<UIWidget>(true);
	}

	private static string RelativePath(Transform node, Transform root)
	{
		if (node == null || root == null || node == root)
		{
			return string.Empty;
		}

		List<string> parts = new List<string>();
		Transform? cur = node;

		while (cur != null && cur != root)
		{
			parts.Insert(0, cur.name);
			cur = cur.parent;
		}

		return cur == root ? string.Join("/", parts) : string.Empty;
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

	private static bool BarAlive()
	{
		if (s_bar == null)
		{
			return false;
		}

		try
		{
			// 只认条本体：填充 sprite 万一没认出来，也不该每几秒重建一次整根条
			return (Object)s_bar != null;
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
		s_bar = null;
		s_fill = null;
		s_lastText = string.Empty;
		s_lastFill = -1f;
		s_barRetryAt = 0f;
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
