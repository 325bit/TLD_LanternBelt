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

	[Name("每帧对齐挂点")]
	[Description("每帧把提灯钉回挂点。关闭后只在挂腰瞬间摆一次位置，走动时灯会留在原地——除排查问题外不要关。")]
	public bool FollowAnchor = true;

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
