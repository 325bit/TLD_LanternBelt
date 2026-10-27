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

	[Name("挂起 / 收起提灯")]
	[Description("按一下把背包里的提灯挂到腰上，再按一下收起。提灯在手上时按下也等于「挂腰」。点一下输入框再按想要的键即可重新绑定。")]
	public KeyCode HolsterKey = KeyCode.H;

	[Name("开关提灯（仅挂腰时）")]
	[Description("只有在提灯处于挂腰状态时这个键才生效，避免和游戏原有按键打架。收起状态下按它什么都不会发生。")]
	public KeyCode LampKey = KeyCode.L;

	[Section("装备栏")]

	[Name("允许在装备栏中切换")]
	[Description("开启后，在物品检视界面选中提灯并点「装备」，改为挂腰 / 收起。关闭则装备栏完全保持原版行为，只能用快捷键挂腰。")]
	public bool PanelToggle = true;

	[Name("装备栏保留原版「装备」")]
	[Description("开启后装备栏的「装备」仍然是原版动作（拿在手上），挂腰只能靠快捷键。与上一项同时开启时以本项为准。")]
	public bool PanelKeepsVanilla = false;

	[Section("挂腰行为")]

	[Name("挂上去就点亮")]
	[Description("挂腰的瞬间如果提灯是灭的，自动点燃。关闭则挂上去保持熄灭，需要按开关灯键点亮。")]
	public bool LightOnHang = true;

	[Name("收起时熄灯")]
	[Description("把提灯从腰上收回背包时吹灭它，符合原版「放进背包的灯不会一直烧油」的规则。")]
	public bool ExtinguishOnUnhang = true;

	[Name("读档后自动恢复")]
	[Description("读档或进入新场景时，如果背包里有一盏点着的提灯，自动重新挂回腰上。开着它就不需要额外的存档标记。")]
	public bool AutoRestore = true;

	[Name("显示屏幕提示")]
	[Description("挂上 / 收起 / 没油 时在屏幕上提示一行字。嫌吵可以关掉，日志里仍然有记录。")]
	public bool HudMessages = true;

	[Section("挂点位置")]

	[Name("挂点基准")]
	[Description("「玩家身体」以玩家根节点为准，偏移量按站立时的米数算；「相机」跟随视角，走动时晃动更明显。默认身体。")]
	[Choice("玩家身体", "相机")]
	public int AnchorMode = 0;

	[Name("左右偏移（米）")]
	[Description("负值在左侧（默认 -0.20 即玩家左胯），正值在右侧。")]
	[Slider(-1f, 1f)]
	public float OffsetX = -0.2f;

	[Name("上下偏移（米）")]
	[Description("相对玩家根节点（脚底）的高度。默认 0.90 大约在腰胯位置。")]
	[Slider(0f, 2f)]
	public float OffsetY = 0.9f;

	[Name("前后偏移（米）")]
	[Description("正值朝前。默认 0.10 略微前置，避免灯体插进身体模型。")]
	[Slider(-1f, 1f)]
	public float OffsetZ = 0.1f;

	[Name("整体缩放")]
	[Description("提灯模型按世界比例显示，一般保持 1。挂点基准选「相机」时因为离眼睛很近，通常要压到 0.5 左右。")]
	[Slider(0.1f, 2f)]
	public float Scale = 1f;

	[Name("每帧对齐挂点")]
	[Description("每帧把提灯钉回挂点。关闭后只在挂腰瞬间摆一次位置，走动时灯会留在原地——除排查问题外不要关。")]
	public bool FollowAnchor = true;

	[Section("界面")]

	[Name("提示语言")]
	[Description("只影响本 mod 打印的屏幕提示文字。自动 = 跟随游戏系统语言。")]
	[Choice("自动", "中文", "English")]
	public int Language = 0;

	[Section("调试")]

	[Name("输出详细日志")]
	[Description("把挂点解析、灯光状态、原始父节点等信息写进 MelonLoader 日志，排查挂不上 / 看不见时打开。")]
	public bool Verbose = false;
}
