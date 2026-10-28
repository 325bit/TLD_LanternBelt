using System;
using System.Collections.Generic;
using Il2CppTLD.Gear;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LanternBelt.Core;

/// <summary>
/// 挂腰时在屏幕右下角补一块「灯油」读数：一根白色条 + 一行文字。
///
/// 为什么自己画：原版只在物品**拿在手上**时由 <c>EquipItemPopup</c> 画那根耐久条，
/// <c>Update</c> 里按类型分派取数（<c>TorchItem.GetBurnProgress</c> /
/// <c>FlareItem.GetNormalizedBurnTimeLeft</c> / <c>FlashlightItem.GetNormalizedCharge</c> /
/// <c>NoiseMakerItem.GetNormalizedWickTimeLeft</c> / 兜底 <c>GearItem.GetNormalizedCondition</c>），
/// 其中**没有提灯分支**——灯挂到腰上以后，手是空的，这条信息就彻底断了。
///
/// 定位**不依赖** <c>EquipItemPopup</c>：那几个序列化字段在 2.55 里可能拿不到
/// （<c>m_ObjectDuration</c> 为空时旧实现退回面板原点，整块就飘到屏幕中央）。
/// 这里让 UI 相机把视口右下角投到面板本地空间，条与文字都相对这个角摆放，
/// 分辨率与 UI 缩放都跟着走。
///
/// 层级是三层：容器（空物体）→ 文字 / 条。容器只承载位置，**不能带任何 <c>UIWidget</c>**，
/// 否则「排文字」那一句写的就是容器自己的 <c>localPosition</c>，边距会当场失效。
///
/// 条的外观仍尽量克隆原版那个模板，atlas / 九宫格边框 / 颜色原样继承；
/// 宽度再按设置拉长到比文字更长。
/// </summary>
internal static class HudReadout
{
	private const string LabelName = "LanternBelt_HudReadout";

	private const string TextName = "LanternBelt_HudLabel";

	private const string BarName = "LanternBelt_FuelBar";

	/// <summary>压在 HUD 常规文字之上，但仍受面板裁剪。</summary>
	private const int LabelDepth = 100;

	/// <summary>克隆条整体抬高的层数，保持背景与填充的相对前后关系。</summary>
	private const int BarDepthBoost = 60;

	/// <summary>找不到原版条模板时的重试间隔（秒）。</summary>
	private const float BarRetrySeconds = 3f;

	/// <summary>条至少要比文字宽出这么多，免得文字顶到条两头。</summary>
	private const float BarTextPadding = 24f;

	private static GameObject? s_root;
	private static UILabel? s_label;
	private static GameObject? s_bar;
	private static UISprite? s_fill;
	private static string s_lastText = string.Empty;
	private static float s_lastFill = -1f;
	private static float s_barRetryAt;

	/// <summary>克隆时每个控件的原始尺寸与位置，所有缩放都以它为基线。</summary>
	private static BarBase[]? s_widgets;

	/// <summary>模板原始包围盒的宽高（未缩放），用来把「目标宽高」映射成比例。</summary>
	private static float s_baseWidth;
	private static float s_baseHeight;

	/// <summary>上一次真正写进控件的目标宽高，用来避免每帧重建 NGUI 网格。</summary>
	private static float s_appliedWidth = -1f;
	private static float s_appliedHeight = -1f;
	private static float s_appliedFontSize = -1f;

	/// <summary>文字实测宽度；文字或字号变了才重算，别每帧让 NGUI 重排一遍文本。</summary>
	private static float s_textWidth = -1f;

	/// <summary>上一次打印过的设置值，只在变化时打日志。</summary>
	private static float s_dbgWidth = float.NaN;
	private static float s_dbgHeight = float.NaN;
	private static float s_dbgRight = float.NaN;
	private static float s_dbgBottom = float.NaN;

	/// <summary>整块（条 + 间隙 + 文字）的实测尺寸，定位时用来按边距内缩。</summary>
	private static float s_blockWidth;
	private static float s_blockHeight;

	/// <summary>上一次量到的条包围盒，只用于日志。</summary>
	private static Vector2 s_barBounds;

	/// <summary>上一次算出的屏幕右下角在面板本地空间的坐标，只用于日志。</summary>
	private static Vector3 s_cornerPos;

	/// <summary>那个角是怎么来的（投影 / UIRoot / 面板 / 兜底），只用于日志。</summary>
	private static string s_cornerSource = "未算";

	private static Transform? s_hudRoot;
	private static UIRoot? s_uiRoot;
	private static bool s_langChinese;
	private static bool s_buildErrorLogged;
	private static bool s_missingBarLogged;

	/// <summary>一个克隆控件的基线快照。</summary>
	private struct BarBase
	{
		public UIWidget Widget;
		public int Width;
		public int Height;
		public Vector3 Position;
	}

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

			float fuel = FuelPercent(lamp);
			float condition = ConditionPercent(lamp);
			string text = Loc.HudLine(fuel, condition);

			// 只有换语言才需要整块重建（字体与字形集要跟着换）。
			// 宽度 / 粗细 / 边距 / 字号一律走下面的 ApplyLayout 就地改——
			// 早先靠「设置变了就重建」来响应滑条，又慢又容易看不出差别。
			if (!IsAlive() || s_langChinese != Loc.UseChineseText)
			{
				if (!Build(hud, cfg, text))
				{
					return;
				}
			}

			if (s_label != null && text != s_lastText)
			{
				s_label.text = text;
				s_lastText = text;
				s_textWidth = -1f;
			}

			// 原版模板可能比 HUD 本体晚就绪，缺了就按间隔补建，别一直只剩文字
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

			if (s_root != null && !s_root.activeSelf)
			{
				s_root.SetActive(true);
			}

			// 开关是双向的：关掉「显示油量条」要把已经建好的条藏起来，
			// 只写「打开时 SetActive(true)」等于这个开关永远关不掉。
			if (s_bar != null && s_bar.activeSelf != cfg.ShowHudBar)
			{
				s_bar.SetActive(cfg.ShowHudBar);
			}

			ApplyLayout(cfg);
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

	/// <summary>
	/// 把当前设置直接写进已经建好的对象：字号、条宽、条粗、文字落点、整块位置。
	///
	/// 每帧调一次，但每个值都先跟「上次写进去的值」比过，没变就不赋值——
	/// 改 `UIWidget.width` / `UILabel.fontSize` / `Transform.localPosition`
	/// 都会让 NGUI 重建网格，每帧无脑写会把 NGUI 打爆。
	///
	/// 顺序不能换：先定条尺寸 → 再排条与文字并量出整块大小 → 最后按整块大小锚定容器。
	/// </summary>
	private static void ApplyLayout(Settings cfg)
	{
		if (s_root == null || s_label == null)
		{
			return;
		}

		try
		{
			int fontSize = Mathf.Max(8, Mathf.RoundToInt(cfg.HudFontSize));
			if (!Mathf.Approximately(s_appliedFontSize, fontSize))
			{
				s_label.fontSize = fontSize;
				s_appliedFontSize = fontSize;
				s_textWidth = -1f;
			}

			ApplyBarSize(cfg);
			PlaceBarAndLabel(cfg);
			AnchorContainer(cfg);
			ReportLayoutChange(cfg);
		}
		catch (Exception ex)
		{
			if (!s_buildErrorLogged)
			{
				s_buildErrorLogged = true;
				ModLog.Warning("HUD 提示布局失败，本局不再重试：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 把容器摆到「右下角再往里缩」的位置。
	///
	/// 边距的语义按玩家会理解的方式来：<c>HudMarginRight</c> 是**块右边缘**到屏幕右边缘的距离，
	/// <c>HudMarginBottom</c> 是**文字底边**到屏幕底边的距离。所以容器原点（块中心）
	/// 要再退半个块宽 / 半个块高——早先直接拿容器原点去贴角，
	/// 条会有一半伸到屏幕外，边距也就跟着对不上。
	/// </summary>
	private static void AnchorContainer(Settings cfg)
	{
		if (s_hudRoot == null || s_root == null)
		{
			return;
		}

		s_cornerPos = CornerLocal(s_hudRoot);

		Vector3 target = s_cornerPos + new Vector3(
			-cfg.HudMarginRight - s_blockWidth * 0.5f,
			cfg.HudMarginBottom + s_blockHeight * 0.5f,
			0f);

		SetLocalPosition(s_root.transform, target);
	}

	/// <summary>
	/// 只在真的变了才写。逐帧写 <c>localPosition</c> 会把 transform 标脏，
	/// 下游的 UIPanel 跟着每帧重算，纯属浪费。
	/// </summary>
	private static void SetLocalPosition(Transform target, Vector3 wanted)
	{
		if ((target.localPosition - wanted).sqrMagnitude > 0.0001f)
		{
			target.localPosition = wanted;
		}
	}

	/// <summary>
	/// 设置里那几项一被拖动就打一行日志。
	///
	/// 这行**不受「输出详细日志」控制**：玩家报「拖了没反应」时，只有它能证明
	/// 到底是值没读进来、没写进控件，还是写进去了但位置/尺寸算歪了。
	/// 只在值变化时打，平时不会刷屏。
	/// </summary>
	private static void ReportLayoutChange(Settings cfg)
	{
		bool changed = !Mathf.Approximately(s_dbgWidth, cfg.HudBarWidth)
			|| !Mathf.Approximately(s_dbgHeight, cfg.HudBarHeight)
			|| !Mathf.Approximately(s_dbgRight, cfg.HudMarginRight)
			|| !Mathf.Approximately(s_dbgBottom, cfg.HudMarginBottom);

		if (!changed)
		{
			return;
		}

		s_dbgWidth = cfg.HudBarWidth;
		s_dbgHeight = cfg.HudBarHeight;
		s_dbgRight = cfg.HudMarginRight;
		s_dbgBottom = cfg.HudMarginBottom;

		Vector3 pos = s_root == null ? Vector3.zero : s_root.transform.localPosition;
		Vector3 text = s_label == null ? Vector3.zero : s_label.transform.localPosition;

		ModLog.Msg($"[挂腰/布局] 设置 宽={cfg.HudBarWidth:F1} 粗={cfg.HudBarHeight:F1}"
			+ $" 右={cfg.HudMarginRight:F1} 底={cfg.HudMarginBottom:F1}"
			+ $" → 条={s_appliedWidth:F0}x{s_appliedHeight:F0}"
			+ $" 实测={s_barBounds.x:F0}x{s_barBounds.y:F0}"
			+ $"（模板 {s_baseWidth:F0}x{s_baseHeight:F0}）"
			+ $" 整块={s_blockWidth:F0}x{s_blockHeight:F0}"
			+ $" 容器={pos.x:F1},{pos.y:F1}"
			+ $" 文字={text.y:F1}"
			+ $" 角={s_cornerPos.x:F1},{s_cornerPos.y:F1}"
			+ $"（{s_cornerSource}）");
	}

	// ────────────────────────── 建立 ──────────────────────────

	private static bool Build(Panel_HUD hud, Settings cfg, string text)
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

			// 容器是个**空物体**，只负责承载位置；文字必须是它的子物体。
			// 早先把 UILabel 直接加在容器身上，于是 s_label.transform 就是 s_root.transform，
			// 每帧排文字那一句会把容器的锚定位置整个覆盖掉 ——
			// 表现就是「边距怎么调都不动」，日志里的容器坐标永远是同一个数。
			GameObject root = new GameObject(LabelName);
			root.transform.SetParent(hudRoot, false);
			root.transform.localScale = Vector3.one;
			root.transform.localPosition = CornerLocal(hudRoot);

			GameObject labelGo = new GameObject(TextName);
			labelGo.transform.SetParent(root.transform, false);
			labelGo.transform.localScale = Vector3.one;
			labelGo.transform.localPosition = Vector3.zero;

			UILabel label = labelGo.AddComponent<UILabel>();
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

			// 先把句柄挂上，再量宽度——MeasureLabel 读的是 s_label
			s_root = root;
			s_label = label;
			s_lastText = text;
			s_barRetryAt = 0f;
			s_hudRoot = hudRoot;
			s_uiRoot = FindUIRoot(hudRoot);
			s_langChinese = Loc.UseChineseText;
			s_appliedFontSize = label.fontSize;
			s_appliedWidth = -1f;
			s_appliedHeight = -1f;

			// 先给真值再量宽度：条要按文字宽度决定要不要加长
			label.text = text;
			MeasureLabel();

			TryBuildBar(hud, cfg);

			ApplyLayout(cfg);

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				ModLog.Msg($"[挂腰] HUD 提示已建立：锚点={root.transform.localPosition}"
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
	/// 屏幕右下角在 HUD 面板本地空间的坐标（不含任何边距）。
	///
	/// 主路径是**让 UI 相机把视口右下角投到世界空间**再转本地——单位天然正确，
	/// 不受 UIRoot 缩放风格、面板偏移的影响。拿不到相机时退回
	/// <c>UIRoot.activeHeight</c> 乘屏幕宽高比（2.55 的 <c>UIRoot</c> **没有 <c>activeWidth</c>**）。
	/// </summary>
	private static Vector3 CornerLocal(Transform hudRoot)
	{
		if (TryScreenCornerLocal(hudRoot, out Vector3 corner))
		{
			return corner;
		}

		return FallbackCornerLocal(hudRoot);
	}

	/// <summary>视口右下角 → 世界 → 面板本地。</summary>
	private static bool TryScreenCornerLocal(Transform hudRoot, out Vector3 local)
	{
		local = Vector3.zero;

		Camera? cam = null;

		try
		{
			cam = UICamera.currentCamera;
		}
		catch
		{
			// 下面再找
		}

		if (cam == null)
		{
			try
			{
				cam = UICamera.mainCamera;
			}
			catch
			{
				// 继续
			}
		}

		// 不用 Camera.main 兜底：那是玩家视角的世界相机，视口坐标和 UI 面板不在一个空间里，
		// 用它算出来的位置会把整块提示扔到看不见的地方。拿不到 UI 相机就交给 UIRoot 兜底。
		if (cam == null)
		{
			return false;
		}

		try
		{
			float z = Mathf.Max(0.01f, cam.nearClipPlane);
			Vector3 world = cam.ViewportToWorldPoint(new Vector3(1f, 0f, z));
			local = hudRoot.InverseTransformPoint(world);

			if (float.IsNaN(local.x) || float.IsNaN(local.y))
			{
				return false;
			}

			s_cornerSource = "相机投影";
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static Vector3 FallbackCornerLocal(Transform hudRoot)
	{
		float w = 0f;
		float h = 0f;
		Vector3 center = Vector3.zero;
		string source = "兜底 1920x1080";

		try
		{
			UIRoot? root = s_uiRoot;
			if (root == null)
			{
				root = FindUIRoot(hudRoot);
				s_uiRoot = root;
			}

			if (root != null)
			{
				h = root.activeHeight;
				float aspect = (float)Screen.width / Mathf.Max(1, Screen.height);
				w = h * aspect;
				center = hudRoot.InverseTransformPoint(root.transform.position);
				source = $"UIRoot {w:F0}x{h:F0}";
			}
		}
		catch
		{
			w = 0f;
			h = 0f;
		}

		if (w <= 1f || h <= 1f)
		{
			// UIRoot 拿不到就退到面板自身尺寸
			try
			{
				UIPanel? panel = hudRoot.GetComponentInParent<UIPanel>();
				if (panel != null)
				{
					w = panel.width;
					h = panel.height;
					center = Vector3.zero;
					source = $"面板 {w:F0}x{h:F0}";
				}
			}
			catch
			{
				// 继续走下面的兜底
			}
		}

		if (w <= 1f || h <= 1f)
		{
			w = 1920f;
			h = 1080f;
			center = Vector3.zero;
			source = "兜底 1920x1080";
		}

		s_cornerSource = source;
		return center + new Vector3(w * 0.5f, -h * 0.5f, 0f);
	}

	private static UIRoot? FindUIRoot(Transform hudRoot)
	{
		try
		{
			return hudRoot.GetComponentInParent<UIRoot>();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 克隆一条「像原版耐久条」的东西当油量条。
	///
	/// 取模板的顺序：<c>m_ObjectDuration</c> → popup 子树里最扁的 UISprite →
	/// HUD 层级里名字含 Duration 的物体。模板缺失不是致命的，文字仍在，隔几秒再试。
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
			EquipItemPopup? popup = FindPopup(hud);
			GameObject? template = FindBarTemplate(hud, popup);

			if (template == null)
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
			bar.transform.localRotation = Quaternion.identity;
			bar.SetActive(true);

			// 模板自带百分比标签（量的是耐久）与点击区域，留着会误导 / 挡鼠标
			foreach (UILabel stray in bar.GetComponentsInChildren<UILabel>(true))
			{
				if (stray != null)
				{
					stray.gameObject.SetActive(false);
				}
			}

			foreach (Collider pick in bar.GetComponentsInChildren<Collider>(true))
			{
				if (pick != null)
				{
					Object.Destroy(pick);
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

			s_bar = bar;
			s_fill = FindFill(popup, template, bar);
			s_lastFill = -1f;
			s_missingBarLogged = false;

			// 记下每个控件的原始尺寸/位置，之后所有缩放都以这份基线换算，
			// 反复调滑条也不会累积误差
			CaptureBaseline(bar);

			if (s_fill == null)
			{
				// 条本身看得见，但油量填不进去：只报一次，别再重建
				ModLog.Warning("油量条建好了但没认出填充 sprite，条会保持静态；请把这一行连同「油量条已建立」发作者");
			}

			s_appliedWidth = -1f;
			s_appliedHeight = -1f;

			ApplyBarSize(cfg);
			PlaceBarAndLabel(cfg);

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				UIWidget? w = BarWidget();
				ModLog.Msg("[挂腰] 油量条已建立：模板=" + template.name
					+ $" 尺寸={(w == null ? 0 : w.width)}x{(w == null ? 0 : w.height)}"
					+ $" 填充={(s_fill == null ? "未找到" : s_fill.name)}"
					+ $" sprite={(s_fill == null ? "-" : s_fill.spriteName)}");
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
	/// 克隆完成后把每个控件的原始尺寸与位置记下来，作为缩放基线。
	///
	/// **必须跳过标签与已隐藏的控件**：原版耐久条模板里带着「100%」那行字，
	/// 它比条本身宽得多也高得多，早先按「所有控件的最大宽高」当基线，
	/// 于是基线被撑成 200x61（其实是文字撑出来的），条永远按错误的比例缩，
	/// 粗细怎么拖都只是在 3~11 像素之间打转。
	/// </summary>
	private static void CaptureBaseline(GameObject bar)
	{
		List<BarBase> list = new List<BarBase>();
		float maxWidth = 0f;
		float maxHeight = 0f;

		List<string> names = new List<string>();

		foreach (UIWidget widget in bar.GetComponentsInChildren<UIWidget>(true))
		{
			if (widget == null)
			{
				continue;
			}

			if (names.Count < 10)
			{
				names.Add($"{widget.name}={widget.width}x{widget.height}");
			}

			if (!IsDrawable(widget))
			{
				continue;
			}

			list.Add(new BarBase
			{
				Widget = widget,
				Width = widget.width,
				Height = widget.height,
				Position = widget.transform.localPosition,
			});

			maxWidth = Mathf.Max(maxWidth, widget.width);
			maxHeight = Mathf.Max(maxHeight, widget.height);
		}

		s_widgets = list.ToArray();
		s_baseWidth = maxWidth;
		s_baseHeight = maxHeight;

		// 一次性把模板构成写进日志：条缩得对不对，全靠这行比对
		ModLog.Msg("[挂腰/模板] " + string.Join(" ", names.ToArray()));
	}

	/// <summary>
	/// 这个控件算不算「条的一部分」。
	///
	/// 标签一律不算（克隆后已经被禁用，算进去只会污染包围盒），
	/// 隐藏物体上的控件也不算。
	/// </summary>
	private static bool IsDrawable(UIWidget widget)
	{
		if (widget is UILabel)
		{
			return false;
		}

		try
		{
			return widget.gameObject.activeInHierarchy;
		}
		catch
		{
			return true;
		}
	}

	/// <summary>
	/// 按设置把条拉成「目标宽度 × 目标粗细」。
	///
	/// 不是「乘一个比例」，而是**按基线包围盒绝对映射**：
	/// 每个控件的 `width` / `height` / `localPosition` 都按
	/// `目标 / 基线` 独立换算。这样最终包围盒一定等于设置里的数字，
	/// 反复拖滑条也不会累积误差，也不怕模板本身是个又大又怪的白块。
	///
	/// 目标宽度还会被抬到「比下面那行文字更长」，免得文字顶到条两头。
	/// </summary>
	private static void ApplyBarSize(Settings cfg)
	{
		if (s_bar == null || s_widgets == null || s_widgets.Length == 0)
		{
			return;
		}

		if (s_baseWidth <= 1f || s_baseHeight <= 1f)
		{
			return;
		}

		try
		{
			float wanted = Mathf.Max(cfg.HudBarWidth, MeasureLabel() + BarTextPadding);
			float wantedHeight = cfg.HudBarHeight > 0.5f
				? cfg.HudBarHeight
				: Mathf.Clamp(wanted / 16f, 6f, 28f);

			// 只在目标尺寸真的变了才写：改 width/height 会 MarkAsChanged 重建网格
			if (Mathf.Approximately(wanted, s_appliedWidth) && Mathf.Approximately(wantedHeight, s_appliedHeight))
			{
				return;
			}

			float sx = wanted / s_baseWidth;
			float sy = wantedHeight / s_baseHeight;

			foreach (BarBase baseWidget in s_widgets)
			{
				UIWidget? widget = baseWidget.Widget;
				if (widget == null)
				{
					continue;
				}

				widget.width = Mathf.Max(1, Mathf.RoundToInt(baseWidget.Width * sx));
				widget.height = Mathf.Max(1, Mathf.RoundToInt(baseWidget.Height * sy));
				widget.transform.localPosition = new Vector3(
					baseWidget.Position.x * sx,
					baseWidget.Position.y * sy,
					baseWidget.Position.z);

				// 尺寸是「写进去了但没重画」时最容易出问题的地方：显式标脏，
				// 免得条在隐藏 / 重显之后仍然沿用旧的网格
				widget.MarkAsChanged();
			}

			s_appliedWidth = wanted;
			s_appliedHeight = wantedHeight;

			if (LanternBeltMod.settings?.Verbose ?? false)
			{
				// 玩家反馈「拖了没反应」时，这一行能直接证明有没有写进去
				ModLog.Msg($"[挂腰] 油量条 {Mathf.RoundToInt(wanted)}x{Mathf.RoundToInt(wantedHeight)}"
					+ $"（模板原始 {Mathf.RoundToInt(s_baseWidth)}x{Mathf.RoundToInt(s_baseHeight)}）");
			}
		}
		catch (Exception ex)
		{
			ModLog.Warning("调整油量条尺寸失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 条居中于容器、文字放在条**正下方**，两者不重叠；顺带量出整块尺寸供锚定用。
	///
	/// 布局按「整块」算：块高 = 条高 + 间隙 + 文字高，块顶边贴容器 +H/2、块底边贴 -H/2。
	/// 条占块的上半段，文字占下半段。早先两边都以容器原点为基准，
	/// 结果文字的上半截压在条上（实机截图就是这个样子）。
	/// </summary>
	private static void PlaceBarAndLabel(Settings cfg)
	{
		if (s_label == null)
		{
			return;
		}

		Vector2 center = Vector2.zero;
		float barHeight = 0f;
		float barWidth = 0f;

		if (s_bar != null && cfg.ShowHudBar && TryBarBounds(out center, out Vector2 size))
		{
			barHeight = size.y;
			barWidth = size.x;
		}

		// 量文字会顺带让 NGUI 排一次版，高度也就跟着有值了
		float textWidth = MeasureLabel();
		float textHeight = s_label.height;
		if (textHeight <= 1f)
		{
			textHeight = Mathf.Max(8f, cfg.HudFontSize) * 1.2f;
		}

		float gap = barHeight > 0.5f ? Mathf.Max(2f, cfg.HudFontSize * 0.2f) : 0f;

		float blockHeight = barHeight + gap + textHeight;
		float blockWidth = Mathf.Max(barWidth, textWidth);
		float top = blockHeight * 0.5f;

		if (s_bar != null && barHeight > 0.5f)
		{
			float barY = top - barHeight * 0.5f;
			SetLocalPosition(s_bar.transform, new Vector3(-center.x, -center.y + barY, 0f));
		}

		float labelY = top - barHeight - gap - textHeight * 0.5f;
		SetLocalPosition(s_label.transform, new Vector3(0f, labelY, 0f));

		s_blockWidth = blockWidth;
		s_blockHeight = blockHeight;
	}

	/// <summary>条内所有控件的并集包围盒（相对 <c>s_bar</c> 的 localPosition 为 0 时）。</summary>
	private static bool TryBarBounds(out Vector2 center, out Vector2 size)
	{
		center = Vector2.zero;
		size = Vector2.zero;

		if (s_bar == null || s_widgets == null)
		{
			return false;
		}

		float minX = float.MaxValue;
		float maxX = float.MinValue;
		float minY = float.MaxValue;
		float maxY = float.MinValue;
		bool any = false;

		Vector3 scale = s_bar.transform.localScale;

		foreach (BarBase baseWidget in s_widgets)
		{
			UIWidget? widget = baseWidget.Widget;
			if (widget == null || !IsDrawable(widget))
			{
				continue;
			}

			// pivotOffset 是 0~1 的比例：x=0 贴左边、y=0 贴底边
			Vector2 po = widget.pivotOffset;
			float w = widget.width * Mathf.Abs(scale.x);
			float h = widget.height * Mathf.Abs(scale.y);
			Vector3 p = widget.transform.localPosition;

			float left = p.x - po.x * w;
			float bottom = p.y - po.y * h;

			minX = Mathf.Min(minX, left);
			maxX = Mathf.Max(maxX, left + w);
			minY = Mathf.Min(minY, bottom);
			maxY = Mathf.Max(maxY, bottom + h);
			any = true;
		}

		if (!any)
		{
			return false;
		}

		center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
		size = new Vector2(maxX - minX, maxY - minY);
		s_barBounds = size;

		return true;
	}


	private static EquipItemPopup? FindPopup(Panel_HUD hud)
	{
		try
		{
			EquipItemPopup? direct = hud.m_EquipItemPopup;
			if (direct != null)
			{
				return direct;
			}
		}
		catch
		{
			// 字段缺失，往下走
		}

		try
		{
			return hud.GetComponentInChildren<EquipItemPopup>(true);
		}
		catch
		{
			return null;
		}
	}

	private static GameObject? FindBarTemplate(Panel_HUD hud, EquipItemPopup? popup)
	{
		try
		{
			if (popup != null && (Object)popup.m_ObjectDuration != null)
			{
				return popup.m_ObjectDuration;
			}
		}
		catch
		{
			// 字段缺失，往下走
		}

		// 模板字段为空时，从 popup 子树里挑「最扁的那个 sprite」——耐久条就是这个形状
		if (popup != null)
		{
			GameObject? flat = FlattestSprite(popup.gameObject);
			if (flat != null)
			{
				return flat;
			}
		}

		try
		{
			foreach (Transform t in hud.GetComponentsInChildren<Transform>(true))
			{
				if (t.name.IndexOf("ObjectDuration", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return t.gameObject;
				}
			}
		}
		catch
		{
			// 找不到就算了
		}

		return null;
	}

	private static GameObject? FlattestSprite(GameObject root)
	{
		GameObject? best = null;
		float bestRatio = 0f;

		foreach (UISprite sprite in root.GetComponentsInChildren<UISprite>(true))
		{
			if (sprite == null || sprite.width < 40 || sprite.height < 2)
			{
				continue;
			}

			float ratio = sprite.width / (float)sprite.height;
			if (ratio > bestRatio)
			{
				bestRatio = ratio;
				best = sprite.gameObject;
			}
		}

		return best;
	}

	/// <summary>
	/// 在克隆体里找出与 <c>m_SpriteFillDuration</c> 对应的那个 sprite。
	///
	/// 先按层级路径对位（最准），对不上再取层数最高的 sprite 兜底——
	/// 填充条画在背景之上，深度通常就是最高的那个。
	/// </summary>
	private static UISprite? FindFill(EquipItemPopup? popup, GameObject template, GameObject clone)
	{
		try
		{
			UISprite? original = popup == null ? null : popup.m_SpriteFillDuration;
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

	/// <summary>量当前文字的宽度；量不出来按字号估一个够用的值。</summary>
	private static float MeasureLabel()
	{
		Settings? cfg = LanternBeltMod.settings;
		float fallback = (cfg == null ? 20f : cfg.HudFontSize) * 11f;

		if (s_label == null)
		{
			return fallback;
		}

		if (s_textWidth > 0f)
		{
			return s_textWidth;
		}

		try
		{
			s_label.ProcessText();
			Vector2 size = s_label.printedSize;
			if (size.x > 1f)
			{
				s_textWidth = size.x;
				return s_textWidth;
			}
		}
		catch
		{
			// 走 fallback
		}

		s_textWidth = fallback;
		return s_textWidth;
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
		s_widgets = null;
		s_baseWidth = 0f;
		s_baseHeight = 0f;
		s_appliedWidth = -1f;
		s_appliedHeight = -1f;
		s_appliedFontSize = -1f;
		s_textWidth = -1f;
		s_hudRoot = null;
		s_lastText = string.Empty;
		s_lastFill = -1f;
		s_barRetryAt = 0f;
		s_blockWidth = 0f;
		s_blockHeight = 0f;
		s_barBounds = Vector2.zero;
		s_cornerPos = Vector3.zero;
		s_cornerSource = "未算";
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
