using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LanternBelt.Core;

/// <summary>
/// 自定义点火音：绕开 Wwise，也绕开 Unity 音频。
/// 两条 Unity 路线在这个 il2cpp 版本上都断了：<c>AudioClip.Create</c> + <c>SetData</c> 只会得到
/// length / frequency / channels 全 0 的空壳；<c>UnityWebRequestMultimedia</c> 要构造
/// <c>DownloadHandlerAudioClip</c>，而该构造函数被剥离，直接抛 Method not found。
/// 所以直接调 <c>winmm!PlaySound</c>：WAV 由 Windows 解码，与游戏的 Wwise 混音互不干扰；
/// 音量靠预缩放采样实现（系统接口没有音量参数）。
/// </summary>
internal static class LampAudio
{
	/// <summary>嵌入资源名 = 根命名空间 + 路径。</summary>
	private const string ResourceName = "LanternBelt.Assets.ignite.wav";

	/// <summary>解包出来的文件名；放在 dll 同一个目录，玩家可以自己替换。</summary>
	private const string FileName = "LanternBelt_ignite.wav";

	private const uint SndAsync = 0x0001;

	private const uint SndNoDefault = 0x0002;

	private const uint SndFilename = 0x00020000;

	private const uint SndPurge = 0x0040;

	[DllImport("winmm.dll", CharSet = CharSet.Unicode)]
	private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

	private static string? s_source;

	private static string? s_scaled;

	private static float s_scaledVolume = -1f;

	private static bool s_ready;

	private static bool s_failed;

	private static bool Verbose => LanternBeltMod.settings?.Verbose ?? false;

	/// <summary>点火音有多长（秒）；还没准备好时为 0。</summary>
	internal static float Length { get; private set; }

	/// <summary>音频是否已经可用。</summary>
	internal static bool Ready => s_ready;

	/// <summary>推进初始化：解包 WAV、读出时长。只做一次，之后每帧只剩一次布尔判断。</summary>
	internal static void Tick()
	{
		if (s_ready || s_failed)
		{
			return;
		}

		Prepare();
	}

	/// <summary>
	/// 播点火音。返回假表示没放出来（资源缺失 / 系统接口失败），调用方应退回原版音效。
	/// </summary>
	internal static bool Play(float volume)
	{
		if (!s_ready || s_source == null)
		{
			return false;
		}

		try
		{
			float v = Math.Clamp(volume, 0f, 1f);
			string path = s_source;

			// 系统接口没有音量参数：按当前音量另存一份，音量变了才重新生成
			if (v < 0.999f)
			{
				path = ScaleTo(v);
				if (path.Length == 0)
				{
					return false;
				}
			}

			if (!PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault))
			{
				ModLog.Warning("系统播放接口拒绝了这个 WAV：" + path);
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			ModLog.Warning("自定义点火音播放失败：" + ex.Message);
			return false;
		}
	}

	/// <summary>掐断正在播的点火音（关灯时别让「呲」还在响）。</summary>
	internal static void Stop()
	{
		try
		{
			PlaySound(null, IntPtr.Zero, SndPurge);
		}
		catch
		{
			// 系统接口不可用，没有要停的东西
		}
	}

	private static void Prepare()
	{
		try
		{
			string? path = EnsureFile();
			if (path == null)
			{
				Fail("解不出内嵌的点火音文件");
				return;
			}

			byte[] wav = File.ReadAllBytes(path);
			if (!TryParse(wav, out WavInfo info))
			{
				Fail("点火音不是可用的 PCM16 WAV");
				return;
			}

			s_source = path;
			Length = info.Seconds;
			s_ready = true;

			ModLog.Msg($"[挂腰] 自定义点火音已就绪 {Length:F2}s"
				+ $" {info.SampleRate}Hz {info.Channels}ch（系统播放）");
		}
		catch (Exception ex)
		{
			Fail("准备点火音失败：" + ex.Message);
		}
	}

	/// <summary>按音量缩放采样，另存一份；同一音量只生成一次。</summary>
	private static string ScaleTo(float volume)
	{
		if (s_scaled != null && Math.Abs(s_scaledVolume - volume) < 0.01f && File.Exists(s_scaled))
		{
			return s_scaled;
		}

		try
		{
			string source = s_source!;
			byte[] wav = File.ReadAllBytes(source);

			if (!TryParse(wav, out WavInfo info))
			{
				return "";
			}

			var copy = (byte[])wav.Clone();

			for (int i = info.DataAt; i + 1 < info.DataAt + info.DataSize; i += 2)
			{
				short sample = (short)(copy[i] | (copy[i + 1] << 8));
				int scaled = (int)(sample * volume);
				scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
				copy[i] = (byte)(scaled & 0xFF);
				copy[i + 1] = (byte)((scaled >> 8) & 0xFF);
			}

			string target = Path.Combine(
				Path.GetDirectoryName(source) ?? "",
				Path.GetFileNameWithoutExtension(source) + "_v.wav");

			File.WriteAllBytes(target, copy);

			s_scaled = target;
			s_scaledVolume = volume;

			return target;
		}
		catch (Exception ex)
		{
			ModLog.Warning("生成音量副本失败：" + ex.Message);
			return "";
		}
	}

	/// <summary>解析出来的 WAV 关键信息。</summary>
	private readonly struct WavInfo
	{
		internal WavInfo(int dataAt, int dataSize, int channels, int sampleRate)
		{
			DataAt = dataAt;
			DataSize = dataSize;
			Channels = channels;
			SampleRate = sampleRate;
		}

		internal int DataAt { get; }

		internal int DataSize { get; }

		internal int Channels { get; }

		internal int SampleRate { get; }

		internal float Seconds => SampleRate > 0 && Channels > 0
			? (float)DataSize / 2f / Channels / SampleRate
			: 0f;
	}

	/// <summary>解析 RIFF/WAVE 头，只认 16bit PCM。</summary>
	private static bool TryParse(byte[] wav, out WavInfo info)
	{
		info = default;

		if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
		{
			return false;
		}

		int channels = 0;
		int sampleRate = 0;
		int bits = 0;
		int pos = 12;

		while (pos + 8 <= wav.Length)
		{
			int size = BitConverter.ToInt32(wav, pos + 4);

			if (wav[pos] == 'f' && wav[pos + 1] == 'm' && wav[pos + 2] == 't' && wav[pos + 3] == ' ')
			{
				channels = BitConverter.ToInt16(wav, pos + 10);
				sampleRate = BitConverter.ToInt32(wav, pos + 12);
				bits = BitConverter.ToInt16(wav, pos + 22);
			}
			else if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
			{
				int at = pos + 8;
				int len = size > 0 && at + size <= wav.Length ? size : wav.Length - at;

				if (channels <= 0 || sampleRate <= 0 || bits != 16 || len <= 0)
				{
					return false;
				}

				info = new WavInfo(at, len, channels, sampleRate);
				return true;
			}

			// 块长度按偶数对齐，否则会解析到错误偏移
			pos += 8 + size + (size & 1);
		}

		return false;
	}

	/// <summary>
	/// 把内嵌的 WAV 落到 dll 旁边。已存在且不比 dll 旧就跳过——玩家替换过自己的音频会被尊重；
	/// mod 一更新（dll 变新），旧的解包文件就会被覆盖成随包的那一版。
	/// </summary>
	private static string? EnsureFile()
	{
		string? path = ResolvePath();
		if (path == null)
		{
			return null;
		}

		try
		{
			bool present = File.Exists(path) && new FileInfo(path).Length > 44;

			if (present && !IsStale(path))
			{
				return path;
			}

			using Stream? stream = typeof(LampAudio).Assembly.GetManifestResourceStream(ResourceName);
			if (stream == null)
			{
				ModLog.Warning("没找到内嵌的点火音资源：" + ResourceName);
				return present ? path : null;
			}

			using var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			File.WriteAllBytes(path, buffer.ToArray());

			if (Verbose)
			{
				ModLog.Msg($"[挂腰] 点火音已解包到 {path}（{buffer.Length} 字节）");
			}

			return path;
		}
		catch (Exception ex)
		{
			ModLog.Warning("解包点火音失败：" + ex.Message);
			return File.Exists(path) ? path : null;
		}
	}

	/// <summary>磁盘上的那份是不是比 dll 还旧（= 上一个版本解出来的，该换成随包的新版）。</summary>
	private static bool IsStale(string path)
	{
		try
		{
			string dll = typeof(LampAudio).Assembly.Location;
			if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
			{
				return false;
			}

			return File.GetLastWriteTime(path) < File.GetLastWriteTime(dll);
		}
		catch
		{
			return false;
		}
	}

	private static string? ResolvePath()
	{
		try
		{
			string dir = Path.GetDirectoryName(typeof(LampAudio).Assembly.Location) ?? "";

			if (string.IsNullOrEmpty(dir))
			{
				dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods");
			}

			return Path.Combine(dir, FileName);
		}
		catch (Exception ex)
		{
			ModLog.Warning("定位点火音目录失败：" + ex.Message);
			return null;
		}
	}

	private static void Fail(string reason)
	{
		if (s_failed)
		{
			return;
		}

		s_failed = true;
		ModLog.Warning("自定义点火音不可用，退回原版音效：" + reason);
	}
}
