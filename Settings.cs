using ModSettings;
using UnityEngine;

namespace LanternBelt;

/// <summary>
/// 设置面板（选项 → Mod Settings → Lantern Belt）。
/// 落盘位置：Mods\LanternBelt.json。
/// </summary>
internal class Settings : JsonModSettings
{
	[Section("快捷键")]

	[Name("开关提灯（仅挂腰时）")]
	[Description("提灯挂在腰上时，按这个键点亮 / 熄灭。没挂腰时按它不会有任何反应，也不会和游戏原有按键打架。点一下输入框再按想要的键即可重新绑定。")]
	public KeyCode LampKey = KeyCode.L;

	[Section("挂腰行为")]

	[Name("挂上去就点亮")]
	[Description("挂腰的瞬间如果提灯是灭的，自动点燃。关闭则挂上去保持熄灭，需要按开关灯键点亮。")]
	public bool LightOnHang = true;

	[Name("收起时熄灯")]
	[Description("把提灯从腰上收回背包时吹灭它，符合原版「放进背包的灯不会一直烧油」的规则。")]
	public bool ExtinguishOnUnhang = true;

	[Name("开关灯音效")]
	[Description("挂腰时开灯 / 关灯播放原版的点火音、燃烧循环音与熄灭音。关掉后这两个动作完全静音，但点火时灯光闪一下的效果仍然保留。")]
	public bool PlayLampAudio = true;

	[Name("点火声音量")]
	[Description("点火那一段（擦火、燃起）的音量。这一段用的是本 mod 自带的音频文件，和游戏整体的音效音量不是同一套，觉得吵或者听不清就调这里。调到 0 等于不播点火音。")]
	[Slider(0f, 1f)]
	public float IgniteAudioVolume = 1f;

	[Name("关灯延迟（秒）")]
	[Description("按下关灯后熄灭声先响，隔这么久灯才真正灭掉并响第二声。默认 2.8 秒；调小更干脆，调到 0 就是声音一响立刻灭。")]
	[Slider(0f, 5f)]
	public float TurnOffHoldSeconds = 2.8f;

	[Name("悬挂 / 收起的装备声")]
	[Description("悬挂提灯、收起提灯时播放原版的「拿出 / 收回」金属声。关掉后这两个动作静音，开灯音效与闪灭不受影响。")]
	public bool PlayGearAudio = true;

	[Name("点火闪灭")]
	[Description("按开关灯键点火时，灯光跟着声效走「短亮 — 短亮 — 长亮」，像擦火柴。关掉后点火即长亮；读档恢复本来就是直接长亮。")]
	public bool PlayIgniteFlicker = true;

	[Name("读档后自动恢复")]
	[Description("读档或进入新场景时，如果背包里有一盏点着的提灯，自动重新挂回腰上。")]
	public bool AutoRestore = true;

	[Name("保留原版「装备」行为")]
	[Description("开启后，物品栏里点提灯仍然是原版的「拿到手上」，挂腰功能整体失效。只在排查其它 mod 冲突时使用。")]
	public bool PanelKeepsVanilla = false;

	[Section("挂点位置")]

	[Name("挂点基准")]
	[Description("「玩家身体」以玩家根节点为准，偏移量按站立时的米数算；「相机」跟随视角，走动时晃动更明显。默认身体。")]
	[Choice("玩家身体", "相机")]
	public int AnchorMode = 0;

	[Name("左右偏移（米）")]
	[Description("负值在左侧（默认 -0.22 即玩家左胯），正值在右侧。")]
	[Slider(-1f, 1f)]
	public float OffsetX = -0.22f;

	[Name("上下偏移（米）")]
	[Description("相对玩家根节点（脚底）的高度。默认 0.95 大约在腰胯位置。")]
	[Slider(0f, 2f)]
	public float OffsetY = 0.95f;

	[Name("前后偏移（米）")]
	[Description("正值朝前。默认 0.12 略微前置，避免灯体插进身体模型而被挡住。")]
	[Slider(-1f, 1f)]
	public float OffsetZ = 0.12f;

	[Name("整体缩放")]
	[Description("提灯模型按世界比例显示，一般保持 1。挂点基准选「相机」时因为离眼睛很近，通常要压到 0.5 左右。")]
	[Slider(0.1f, 2f)]
	public float Scale = 1f;

	[Name("蹲下时跟着下移")]
	[Description("蹲下时把提灯一起往下挪，别让它悬在站立时的腰位上。关闭则挂点高度固定。")]
	public bool FollowCrouch = true;

	[Name("蹲下下移量（米）")]
	[Description("蹲下时挂点降低多少米。默认 0.35 大约跟着腰胯一起落；觉得灯还偏高就调大，压进腿里就调小。")]
	[Slider(0f, 1.2f)]
	public float CrouchDropAmount = 0.35f;

	[Name("每帧对齐挂点")]
	[Description("每帧把提灯钉回挂点。关闭后只在挂腰瞬间摆一次位置，走动时灯会留在原地——除排查问题外不要关。")]
	public bool FollowAnchor = true;

	[Section("HUD 提示")]

	[Name("显示灯油与耐久")]
	[Description("总开关：关掉后条和文字一起消失（原版只在物品拿在手上时才显示耐久条）。只想关掉条、保留文字，请用下面那项「显示油量条」。")]
	public bool ShowHudReadout = true;

	[Name("显示油量条")]
	[Description("只控制那根条：关掉后只留百分比文字。上面的总开关关掉时，这一项无论开不开都不显示。")]
	public bool ShowHudBar = true;

	[Name("油量条宽度")]
	[Description("油量条有多长（界面单位，1080p 下约等于像素）。会自动加长到比下面那行文字更长，所以这里设小了也不会比文字短。")]
	[Slider(60f, 600f)]
	public float HudBarWidth = 200f;

	[Name("油量条粗细")]
	[Description("油量条有多厚（界面单位，1080p 下约等于像素）。0 = 按宽度自动压细（大约宽度的十六分之一，接近原版耐久条的比例）；嫌细填 12~20。")]
	[Slider(0f, 40f)]
	public float HudBarHeight = 0f;

	[Name("距屏幕右边缘")]
	[Description("油量条**右端**离屏幕右边有多远。数值越大整块越往左，填 0 会贴边。")]
	[Slider(0f, 600f)]
	public float HudMarginRight = 150f;

	[Name("距屏幕底边缘")]
	[Description("那行文字**底边**离屏幕底边有多高。数值越大整块越往上，觉得挡住底部状态图标就调大。")]
	[Slider(0f, 600f)]
	public float HudMarginBottom = 110f;

	[Name("提示字号")]
	[Description("HUD 提示的字号。分辨率高觉得太小、或嫌它挡画面就调。")]
	[Slider(10f, 40f)]
	public float HudFontSize = 20f;

	[Section("界面")]

	[Name("提示语言")]
	[Description("只影响本 mod 打印的屏幕提示与物品栏按钮文字。自动 = 跟随游戏系统语言。")]
	[Choice("自动", "中文", "English")]
	public int Language = 0;

	[Section("调试")]

	[Name("输出详细日志")]
	[Description("把挂点、渲染器、层、热量源等状态写进 MelonLoader 日志。提灯看不见 / 没有热度时打开它，日志里会有一行「[挂腰] 诊断」。")]
	public bool Verbose = false;
}
