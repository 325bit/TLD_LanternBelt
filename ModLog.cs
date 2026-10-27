using System;
using MelonLoader;

namespace LanternBelt;

/// <summary>统一日志出口：静态工具类拿不到实例 Logger，由入口类绑定一次。</summary>
internal static class ModLog
{
	private static MelonLogger.Instance? s_instance;

	internal static void Bind(MelonLogger.Instance instance)
	{
		s_instance = instance;
	}

	internal static void Msg(string message)
	{
		try
		{
			s_instance?.Msg(message);
		}
		catch
		{
			// 退出流程里 Logger 可能已失效
		}
	}

	internal static void Warning(string message)
	{
		try
		{
			s_instance?.Warning(message);
		}
		catch
		{
		}
	}

	internal static void Error(string message)
	{
		try
		{
			s_instance?.Error(message);
		}
		catch
		{
		}
	}
}
