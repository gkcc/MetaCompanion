using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Hearthstone_Deck_Tracker.Utility.Extensions;

namespace MetaCompanion
{
	public class Log
	{
		private static readonly string LogDirectory = Path.Combine(MetaCompanionPlugin.DataDirectory, "Logs");
		private static readonly string LogPrefix = "log";
		private static readonly string LogSuffix = ".txt";
		private static readonly string LogFile = Path.Combine(LogDirectory, LogPrefix + LogSuffix);

		private const int MaxLogFileAge = 2;
		private const int KeepOldLogs = 5;
		private const int MaxOperationalMessageLength = 320;
		private const int MaxDiagnosticMessageLength = 2000;
		private const string HiddenCredential = "凭据=[已隐藏]";

		private static readonly Regex CredentialHeaderPattern = new Regex(
			@"(?im)\b(?:authorization|proxy-authorization|cookie|set-cookie|x-[a-z0-9-]*token)\s*:\s*[^\r\n]+",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex CredentialValuePattern = new Regex(
			@"(?ix)(?:[""']?(?:(?<![A-Za-z0-9_])(?:session[_\s-]?token|access[_\s-]?token|refresh[_\s-]?token|auth[_\s-]?token|token|password|passwd|pwd|secret|client[_\s-]?secret|api[_\s-]?key|private[_\s-]?key|account[_\s-]?key|shared[_\s-]?access[_\s-]?signature|authorization|cookie|set-cookie)|(?:会话|访问|刷新|认证)?令牌|密码|口令|密钥|凭据|授权|认证)[""']?\s*[:=：]\s*)(?:""[^""]*""|'[^']*'|[^\s,;，；]+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex AuthorizationValuePattern = new Regex(
			@"(?i)\b(?:basic|bearer)\s+[a-z0-9._~+/=-]+",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex UriCredentialPattern = new Regex(
			@"(?i)(://)[^/@\s:]+:[^/@\s]+@",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex LongSecretPattern = new Regex(
			@"(?<![A-Za-z0-9])[A-Za-z0-9_\-+/=]{48,}(?![A-Za-z0-9])",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex UrlPattern = new Regex(
			@"(?i)(?:\b(?:https?|ftp|file)://[^\s，；;]+|\bwww\.[^\s，；;]+|\blocalhost(?::\d+)?(?:/[^\s，；;]*)?|\b(?:127\.0\.0\.1|\[::1\])(?::\d+)?(?:/[^\s，；;]*)?)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex WindowsPathPattern = new Regex(
			@"(?i)(?:\b[a-z]:[\\/]|\\\\)[^\r\n，；;]*",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex UnixPathPattern = new Regex(
			@"(?i)(?<![:\p{L}\p{N}_])/(?:[^\\/\s，；;]+[\\/])*[^\\/\s，；;]*",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex RelativePathPattern = new Regex(
			@"(?i)(?<![\p{L}\p{N}_])(?:[^\\/\s，；;]+[\\/])+(?:[^\\/\s，；;]+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex FileLocationPattern = new Regex(
			@"(?i)(?<![\p{L}\p{N}_])(?:\.env(?:\.[\w.-]+)?|[\w.-]+\.(?:cs|rs|py|ps1|cmd|bat|exe|dll|pdb|log|txt|jsonl?|csv|tsv|ya?ml|toml|xml|zip|config|ini|db|sqlite|env))(?::\d+)?(?![\p{L}\p{N}_])",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex TechnicalTailPattern = new Regex(
			@"(?is)(?:\b(?:System\.)?[A-Za-z_][A-Za-z0-9_.+`]*(?:Exception|Error)\b|\bTraceback\s*\(|\bStack\s*trace\b|\bInner\s+Exception\b|\bat\s+[A-Za-z_][A-Za-z0-9_.<>+`]*\s*\(|(?:^|\s)在\s+[A-Za-z_][A-Za-z0-9_.<>+`]*\s*\().*$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex ErrorCodePattern = new Regex(
			@"(?i)\b(?:HRESULT\s*[:=]?\s*)?0x[0-9a-f]{8}\b",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex ChineseTextPattern = new Regex(
			@"[\u3400-\u9fff]",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex WhitespacePattern = new Regex(
			@"\s+",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex SafeCallerPattern = new Regex(
			@"^[A-Za-z_\u3400-\u9fff<][A-Za-z0-9_\u3400-\u9fff.<>+`]{0,79}$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly KeyValuePair<string, string>[] KnownOperationalPrefixes =
		{
			Pair("Starting Meta Companion", "插件已启动。"),
			Pair("Plugin assembly", "插件组件已加载。"),
			Pair("Plugin data directory", "插件数据目录已就绪。"),
			Pair("Skipping upstream auto-update", "当前为本地数据源版本，已跳过上游自动更新。"),
			Pair("Meta deck library loading started", "牌组库正在后台加载。"),
			Pair("Meta deck library loaded no decks", "牌组库没有加载到可用牌组；预测暂不可用。"),
			Pair("Meta deck library loaded", "牌组库加载完成。"),
			Pair("No deck code file found", "未找到牌组代码数据源。"),
			Pair("Reading deck codes from preferred sources", "正在读取首选牌组代码数据源。"),
			Pair("Meta retrieved from deck code file", "已从牌组代码快照加载牌组。"),
			Pair("Meta retrieved", "已从旧版环境数据加载牌组。"),
			Pair("Meta deck class counts", "牌组库职业分布已统计。"),
			Pair("Downloading new Meta Companion version", "正在下载插件更新。"),
			Pair("Downloaded to", "插件更新下载完成。"),
			Pair("Extracting", "正在解压插件更新。"),
			Pair("Copying over new Meta Companion files", "正在安装插件更新。"),
			Pair("Creating", "正在初始化插件日志文件。"),
			Pair("Resetting legacy dashboard panel position", "已重置旧版数据面板位置。"),
			Pair("Post-game full HSReplay data refresh is due", "赛后完整数据刷新已到期。"),
			Pair("Post-game local meta refresh complete", "赛后本地环境刷新完成。"),
			Pair("Starting post-game meta refresh", "正在启动赛后环境刷新。"),
			Pair("Starting refresh task PowerShell process", "正在启动自动刷新任务。"),
			Pair("Refresh log is still being written", "刷新日志仍在写入，稍后再检查结果。"),
			Pair("Quick dashboard local meta refreshed", "快捷面板的本地环境数据已刷新。"),
			Pair("Quick dashboard recommendations refreshed", "快捷面板的推荐数据已刷新。"),
			Pair("Download failed", "插件更新下载失败；请检查网络后重试。"),
			Pair("Exception while installing update", "插件更新安装失败；当前版本保持不变。"),
			Pair("Unable to read HDT replay info", "无法读取 HDT 对局回放信息。"),
			Pair("Meta dashboard panel update failed", "数据面板更新失败。"),
			Pair("Unable to open dashboard target", "无法打开数据面板目标。"),
			Pair("Meta dashboard display scheduling failed", "数据面板显示调度失败。"),
			Pair("Meta dashboard failure status display failed", "数据面板无法显示失败状态。"),
			Pair("Meta dashboard display failed", "数据面板显示失败。"),
			Pair("Meta dashboard", "数据面板读取失败。"),
			Pair("Manual match correction refresh failed", "手动修正对局记录后的刷新失败。"),
			Pair("Manual match correction write failed", "手动修正对局记录写入失败。"),
			Pair("Data health inspection failed", "数据健康检查失败。"),
			Pair("No deck code snapshot found", "未找到牌组代码快照；预测数据暂不可用。"),
			Pair("No valid decks were imported", "牌组代码快照中没有可用牌组，正在尝试备用数据。"),
			Pair("Ignoring invalid deck code", "已忽略无效牌组代码。"),
			Pair("Skipped", "已跳过无法识别的卡牌数据。"),
			Pair("Ignoring current-patch branch snapshot preference", "当前版本的分支快照不可用，已改用安全回退。"),
			Pair("Post-game local meta refresh script not found", "未找到赛后本地环境刷新脚本。"),
			Pair("Post-game local meta refresh failed", "赛后本地环境刷新失败。"),
			Pair("Post-game full data refresh failed", "赛后完整数据刷新失败，正在尝试安全回退。"),
			Pair("Opponent class changed from", "检测到对手职业发生变化，正在重建预测候选。"),
			Pair("Cannot CheckOpponentCards", "尚未识别对手职业，无法检查对手卡牌。"),
			Pair("Unable to update HDT native opponent predictions", "无法更新 HDT 原生对手预测。"),
			Pair("Unable to clear HDT native opponent predictions", "无法清空 HDT 原生对手预测。"),
			Pair("Unable to refresh HDT opponent cards", "无法刷新 HDT 对手卡牌预测。"),
			Pair("Quick dashboard completion callback failed", "快捷面板刷新后的状态更新失败。"),
			Pair("Quick dashboard refresh failed", "快捷面板刷新失败。"),
			Pair("Failed to load HDT DeckStats local meta", "无法加载 HDT 本地对局统计。"),
			Pair("Refresh scheduled task status check failed", "自动刷新计划任务状态检查失败。"),
			Pair("Refresh scheduled task action check failed", "自动刷新计划任务动作检查失败。"),
			Pair("Failed to start refresh task PowerShell process", "无法启动自动刷新任务。"),
			Pair("Refresh log outcome check failed", "无法检查自动刷新日志结果。"),
			Pair("Training log summary read failed", "无法读取训练日志摘要；原始记录保持不变。"),
			Pair("Copy diagnostics failed", "复制诊断信息失败。"),
			Pair("Open developer log directory failed", "无法打开开发日志目录。"),
			Pair("Open refresh developer log failed", "无法打开刷新日志。"),
			Pair("Open settings help failed", "无法打开设置帮助。"),
			Pair("Open data directory failed", "无法打开插件数据目录。"),
			Pair("Open match history failed", "无法打开对局历史。"),
			Pair("Open prediction timeline failed", "无法打开预测时间线。"),
			Pair("Open match corrections failed", "无法打开对局修正记录。"),
			Pair("Open recommendation result failed", "无法打开推荐结果。"),
			Pair("Reset overlay layout failed", "重置浮窗位置失败。"),
			Pair("Save settings failed", "保存插件设置失败。"),
			Pair("Settings data status check failed", "设置页读取数据源状态失败。"),
			Pair("Settings recommendation status check failed", "设置页读取推荐数据状态失败。"),
			Pair("Settings premium status check failed", "设置页读取对阵数据状态失败。"),
			Pair("Settings data health snapshot failed", "设置页读取数据健康状态失败。"),
			Pair("Settings refresh task snapshot failed", "设置页读取自动刷新状态失败。"),
			Pair("Settings training log snapshot failed", "设置页读取训练日志状态失败。"),
			Pair("No deck predictions for", "当前对局没有可用的牌组预测。"),
			Pair("Meta deck library load failed", "牌组库加载失败；预测暂不可用。"),
			Pair("Unable to update patch state", "无法更新游戏版本边界状态。"),
			Pair("Unable to write meta deck load status", "无法写入牌组库加载状态。")
		};

		public static string PrevLine { get; private set; }
		public static bool Initialized { get; private set; }

		public static void Initialize()
		{
			if (Initialized)
				return;
			Trace.AutoFlush = true;
			if (!Directory.Exists(LogDirectory))
				Directory.CreateDirectory(LogDirectory);
			else
			{
				try
				{
					var fileInfo = new FileInfo(LogFile);
					if (fileInfo.Exists)
					{
						using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.None))
						{
							//can access log file => no other instance of same installation running
						}
						File.Move(LogFile, LogFile.Replace(LogSuffix, "_" + DateTimeOffset.Now.ToUnixTimeSeconds() + LogSuffix));
						//keep logs from the last 2 days plus 5 before that
						foreach (var file in
							new DirectoryInfo(LogDirectory).GetFiles(LogPrefix + "*")
													 .Where(x => x.LastWriteTime < DateTime.Now.AddDays(-MaxLogFileAge))
													 .OrderByDescending(x => x.LastWriteTime)
													 .Skip(KeepOldLogs))
						{
							try
							{
								File.Delete(file.FullName);
							}
							catch
							{
							}
						}
					}
					else
						File.Create(LogFile).Dispose();
				}
				catch (Exception)
				{
					return;
				}
			}
			Initialized = true;
		}

		public static void WriteLine(string msg, LogType type, [CallerMemberName] string memberName = "",
									 [CallerFilePath] string sourceFilePath = "")
		{
#if (!DEBUG)
			if (type == LogType.Debug && Config.Instance.LogLevel == 0)
				return;
#endif
			var file = GetSafeCallerFile(sourceFilePath);
			var member = GetSafeCallerMember(memberName);
			var safeMessage = PrepareMessage(msg, type);
			var line = $"{type}|{file}.{member} >> {safeMessage}";

			PrevLine = line;
			Write(line);
		}

		private static void Write(string line)
		{
			line = $"{DateTime.Now.ToLongTimeString()}|{NormalizeSingleLine(line)}";
			if (!Initialized)
			{
				return;
			}
			using (StreamWriter sw = new StreamWriter(LogFile, true))
			{
				sw.WriteLine(line);
			}
		}

		public static void Debug(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "")
			=> WriteLine(msg, LogType.Debug, memberName, sourceFilePath);

		public static void Info(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "")
			=> WriteLine(msg, LogType.Info, memberName, sourceFilePath);

		public static void Warn(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "")
			=> WriteLine(msg, LogType.Warning, memberName, sourceFilePath);

		public static void Error(string msg, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "")
			=> WriteLine(msg, LogType.Error, memberName, sourceFilePath);

		public static void Error(Exception ex, [CallerMemberName] string memberName = "", [CallerFilePath] string sourceFilePath = "")
			=> WriteLine(BuildExceptionSummary(ex), LogType.Error, memberName, sourceFilePath);

		internal static string PrepareMessage(string message, LogType type)
		{
			var original = message ?? "";
			var redacted = RedactCredentials(original);
			if (type == LogType.Debug)
				return Limit(NormalizeSingleLine(redacted), MaxDiagnosticMessageLength);

			return PrepareOperationalMessage(
				redacted, type, ChineseTextPattern.IsMatch(original));
		}

		internal static string BuildExceptionSummary(Exception error)
		{
			var current = Unwrap(error);
			var category = "未分类运行异常";
			if (current is UnauthorizedAccessException ||
				string.Equals(current?.GetType().FullName,
					"System.Security.SecurityException", StringComparison.Ordinal))
				category = "权限访问异常";
			else if (current is FileNotFoundException || current is DirectoryNotFoundException)
				category = "文件缺失异常";
			else if (current is IOException)
				category = "文件访问异常";
			else if (current is TimeoutException)
				category = "等待超时异常";
			else if (current is OperationCanceledException)
				category = "操作取消异常";
			else if (current is ArgumentException)
				category = "输入参数异常";
			else if (current is InvalidOperationException || current is ObjectDisposedException)
				category = "运行状态异常";
			else if (current is NotSupportedException || current is PlatformNotSupportedException)
				category = "功能兼容异常";
			else if (current is OutOfMemoryException)
				category = "资源不足异常";
			else if (IsNetworkException(current))
				category = "网络通信异常";

			return "检测到" + category + "；调用来源见日志定位，技术细节已隐藏。";
		}

		private static string PrepareOperationalMessage(
			string redacted, LogType type, bool originalContainedChinese)
		{
			var firstLine = FirstLine(redacted).Trim();
			foreach (var item in KnownOperationalPrefixes)
			{
				if (firstLine.StartsWith(item.Key, StringComparison.OrdinalIgnoreCase))
					return item.Value;
			}

			if (string.IsNullOrWhiteSpace(firstLine))
				return FallbackOperationalMessage(type);
			if (!originalContainedChinese)
				return FallbackOperationalMessage(type);

			var withoutTail = TechnicalTailPattern.Replace(firstLine, "").Trim();
			var technicalTailRemoved = !string.Equals(
				withoutTail, firstLine, StringComparison.Ordinal);
			var safe = UrlPattern.Replace(withoutTail, "[地址已隐藏]");
			safe = WindowsPathPattern.Replace(safe, "[路径已隐藏]");
			safe = UnixPathPattern.Replace(safe, "[路径已隐藏]");
			safe = RelativePathPattern.Replace(safe, "[路径已隐藏]");
			safe = FileLocationPattern.Replace(safe, "[文件已隐藏]");
			safe = ErrorCodePattern.Replace(safe, "[错误代码已隐藏]");
			safe = NormalizeSingleLine(safe).Trim(' ', ':', '：', ';', '；', ',', '，');

			if (!ChineseTextPattern.IsMatch(safe))
				return FallbackOperationalMessage(type);
			if (technicalTailRemoved)
				safe += "；技术细节已隐藏。";
			return Limit(safe, MaxOperationalMessageLength);
		}

		private static string RedactCredentials(string value)
		{
			var redacted = CredentialHeaderPattern.Replace(value ?? "", HiddenCredential);
			redacted = CredentialValuePattern.Replace(redacted, HiddenCredential);
			redacted = AuthorizationValuePattern.Replace(redacted, HiddenCredential);
			redacted = UriCredentialPattern.Replace(redacted, "$1[已隐藏]@");
			redacted = LongSecretPattern.Replace(redacted, "[疑似凭据已隐藏]");
			return redacted;
		}

		private static string NormalizeSingleLine(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return "";
			return WhitespacePattern.Replace(value, " ").Trim();
		}

		private static string FirstLine(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "";
			var index = value.IndexOfAny(new[] { '\r', '\n' });
			return index < 0 ? value : value.Substring(0, index);
		}

		private static string FallbackOperationalMessage(LogType type)
		{
			if (type == LogType.Error)
				return "运行错误：无法安全展示原始技术信息；请根据调用来源排查。";
			if (type == LogType.Warning)
				return "运行警告：无法安全展示原始技术信息；请根据调用来源排查。";
			return "运行信息：原始技术内容已隐藏。";
		}

		private static Exception Unwrap(Exception error)
		{
			if (error == null)
				return null;
			var aggregate = error as AggregateException;
			if (aggregate != null)
			{
				var flattened = aggregate.Flatten();
				if (flattened.InnerExceptions.Count > 0)
					return Unwrap(flattened.InnerExceptions[0]);
			}
			return error.InnerException == null ? error : Unwrap(error.InnerException);
		}

		private static bool IsNetworkException(Exception error)
		{
			var fullName = error?.GetType().FullName ?? "";
			return fullName == "System.Net.WebException" ||
				fullName == "System.Net.Http.HttpRequestException" ||
				fullName == "System.Net.Sockets.SocketException";
		}

		private static string GetSafeCallerFile(string sourceFilePath)
		{
			try
			{
				return SafeCaller(Path.GetFileNameWithoutExtension(sourceFilePath), "未知来源");
			}
			catch
			{
				return "未知来源";
			}
		}

		private static string GetSafeCallerMember(string memberName)
		{
			return SafeCaller(memberName, "未知调用");
		}

		private static string SafeCaller(string value, string fallback)
		{
			return !string.IsNullOrWhiteSpace(value) && SafeCallerPattern.IsMatch(value)
				? value
				: fallback;
		}

		private static string Limit(string value, int maxLength)
		{
			if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
				return value ?? "";
			return value.Substring(0, Math.Max(0, maxLength - 1)).TrimEnd() + "…";
		}

		private static KeyValuePair<string, string> Pair(string prefix, string translation)
		{
			return new KeyValuePair<string, string>(prefix, translation);
		}
	}
}
