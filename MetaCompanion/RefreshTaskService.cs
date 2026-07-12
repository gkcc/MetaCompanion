using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCompanion
{
	internal class RefreshTaskSnapshot
	{
		public bool ToolsDirectoryExists { get; set; }
		public bool RefreshScriptExists { get; set; }
		public bool InstallScriptExists { get; set; }
		public bool ScheduledTaskInstalled { get; set; }
		public bool ScheduledTaskActionKnown { get; set; }
		public bool ScheduledTaskUsesInstalledScript { get; set; }
		public string ScheduledTaskActionPath { get; set; } = "";
		public string ScheduledTaskArguments { get; set; } = "";
		public string ScheduledTaskWorkingDirectory { get; set; } = "";
		public string ScheduledTaskError { get; set; } = "";
		public string LatestLogPath { get; set; } = "";
		public DateTime? LatestLogTime { get; set; }
		public bool LatestLogSucceeded { get; set; }
		public bool LatestLogFailed { get; set; }
		public bool LatestLogBusy { get; set; }
		public List<string> LatestLogSummaryLines { get; set; } = new List<string>();
		public string ToolsStatus { get; set; } = "";
		public string ScheduledTaskStatus { get; set; } = "";
		public string LatestLogStatus { get; set; } = "";

		public bool CanInstallTask
		{
			get { return RefreshScriptExists && InstallScriptExists; }
		}

		public bool CanRunRefresh
		{
			get { return RefreshScriptExists; }
		}

		public bool CanOpenLatestLog
		{
			get { return !string.IsNullOrWhiteSpace(LatestLogPath) && File.Exists(LatestLogPath); }
		}
	}

	internal class RefreshTaskLaunchResult
	{
		public bool Started { get; set; }
		public int ProcessId { get; set; }
		public string Message { get; set; } = "";
	}

	internal class RefreshScheduledTaskInfo
	{
		public bool Installed { get; set; }
		public string Path { get; set; } = "";
		public string Arguments { get; set; } = "";
		public string WorkingDirectory { get; set; } = "";
	}

	internal class RefreshTaskService
	{
		public const string ScheduledTaskName = "Meta Companion Remote Cache Refresh";
		public const string RefreshScriptFileName = "Run-MetaCompanionRefresh.ps1";
		public const string InstallScriptFileName = "Install-MetaCompanionRefreshTask.ps1";

		private const int LogTailLineCount = 8;
		private static readonly Regex CookieValueRegex = new Regex(
			@"(?i)\b(cookie|set-cookie|cookiepath)\b\s*[:=]\s*("".*?""|'.*?'|\S+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex HtmlTitleRegex = new Regex(
			@"(?is)<title>\s*(.*?)\s*</title>",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex RefreshFailureRegex = new Regex(
			@"(?i)(TerminatingError|returned HTTP [45]\d\d|curl\.exe failed|No HSReplay cookie found|Cookie file is empty|Exception calling)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private readonly string _dataDirectory;
		private readonly Func<string, RefreshScheduledTaskInfo> _scheduledTaskInspector;
		private readonly Func<ProcessStartInfo, int> _startProcess;

		public RefreshTaskService(string dataDirectory)
			: this(dataDirectory, (Func<string, RefreshScheduledTaskInfo>)null, null)
		{
		}

		internal RefreshTaskService(
			string dataDirectory,
			Func<string, bool> scheduledTaskExists,
			Func<ProcessStartInfo, int> startProcess)
			: this(
				dataDirectory,
				scheduledTaskExists == null
					? (Func<string, RefreshScheduledTaskInfo>)null
					: (name => new RefreshScheduledTaskInfo
					{
						Installed = scheduledTaskExists(name)
					}),
				startProcess)
		{
		}

		internal RefreshTaskService(
			string dataDirectory,
			Func<string, RefreshScheduledTaskInfo> scheduledTaskInspector,
			Func<ProcessStartInfo, int> startProcess)
		{
			_dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
				? MetaCompanionPlugin.DataDirectory
				: Path.GetFullPath(dataDirectory);
			_scheduledTaskInspector = scheduledTaskInspector ?? WindowsScheduledTaskInspect;
			_startProcess = startProcess ?? StartDetachedProcess;
		}

		public string DataDirectory
		{
			get { return _dataDirectory; }
		}

		public string ToolsDirectory
		{
			get { return Path.Combine(_dataDirectory, "Tools"); }
		}

		public string RefreshScriptPath
		{
			get { return Path.Combine(ToolsDirectory, RefreshScriptFileName); }
		}

		public string InstallScriptPath
		{
			get { return Path.Combine(ToolsDirectory, InstallScriptFileName); }
		}

		public string LogDirectory
		{
			get { return Path.Combine(_dataDirectory, "Logs"); }
		}

		public RefreshTaskSnapshot Inspect()
		{
			var snapshot = new RefreshTaskSnapshot
			{
				ToolsDirectoryExists = Directory.Exists(ToolsDirectory),
				RefreshScriptExists = File.Exists(RefreshScriptPath),
				InstallScriptExists = File.Exists(InstallScriptPath)
			};

			try
			{
				ApplyScheduledTaskInfo(snapshot, _scheduledTaskInspector(ScheduledTaskName));
			}
			catch (Exception ex)
			{
				snapshot.ScheduledTaskError = SummarizeException(ex);
				Log.Warn("Refresh scheduled task status check failed: " + snapshot.ScheduledTaskError);
			}

			ApplyLatestLog(snapshot);
			snapshot.ToolsStatus = BuildToolsStatus(snapshot);
			snapshot.ScheduledTaskStatus = BuildScheduledTaskStatus(snapshot);
			snapshot.LatestLogStatus = BuildLatestLogStatus(snapshot);
			return snapshot;
		}

		private void ApplyScheduledTaskInfo(
			RefreshTaskSnapshot snapshot,
			RefreshScheduledTaskInfo taskInfo)
		{
			if (taskInfo == null)
			{
				return;
			}

			snapshot.ScheduledTaskInstalled = taskInfo.Installed;
			snapshot.ScheduledTaskActionPath = taskInfo.Path ?? "";
			snapshot.ScheduledTaskArguments = taskInfo.Arguments ?? "";
			snapshot.ScheduledTaskWorkingDirectory = taskInfo.WorkingDirectory ?? "";
			snapshot.ScheduledTaskActionKnown = !string.IsNullOrWhiteSpace(
				snapshot.ScheduledTaskActionPath +
				snapshot.ScheduledTaskArguments +
				snapshot.ScheduledTaskWorkingDirectory);
			snapshot.ScheduledTaskUsesInstalledScript =
				snapshot.ScheduledTaskInstalled &&
				snapshot.ScheduledTaskActionKnown &&
				ContainsPath(
					snapshot.ScheduledTaskActionPath + " " + snapshot.ScheduledTaskArguments,
					RefreshScriptPath);
		}

		public RefreshTaskLaunchResult StartInstallTask()
		{
			if (!File.Exists(RefreshScriptPath) || !File.Exists(InstallScriptPath))
			{
				return new RefreshTaskLaunchResult
				{
					Started = false,
					Message = "高级刷新脚本未安装，无法启动自动刷新安装。"
				};
			}

			return StartPowerShellScript(InstallScriptPath, "安装自动刷新", true, false);
		}

		public RefreshTaskLaunchResult StartRefreshNow()
		{
			if (!File.Exists(RefreshScriptPath))
			{
				return new RefreshTaskLaunchResult
				{
					Started = false,
					Message = "高级刷新脚本未安装，无法立即刷新。"
				};
			}

			return StartPowerShellScript(RefreshScriptPath, "立即刷新", false, true);
		}

		private RefreshTaskLaunchResult StartPowerShellScript(
			string scriptPath,
			string actionName,
			bool requireElevation,
			bool keepWindowOpen)
		{
			try
			{
				var startInfo = BuildPowerShellStartInfo(scriptPath, requireElevation, keepWindowOpen);
				Log.Info("Starting refresh task PowerShell process: " + actionName + " (" +
					Path.GetFileName(scriptPath) + ")");
				var processId = _startProcess(startInfo);
				return new RefreshTaskLaunchResult
				{
					Started = true,
					ProcessId = processId,
					Message = keepWindowOpen
						? actionName + "已在 PowerShell 窗口中启动；完成后可关闭窗口。"
						: actionName + "脚本已启动。"
				};
			}
			catch (Exception ex)
			{
				var summary = SummarizeException(ex);
				Log.Warn("Failed to start refresh task PowerShell process: " + summary);
				return new RefreshTaskLaunchResult
				{
					Started = false,
					Message = actionName + "脚本启动失败: " + summary
				};
			}
		}

		private ProcessStartInfo BuildPowerShellStartInfo(
			string scriptPath,
			bool requireElevation,
			bool keepWindowOpen)
		{
			var arguments = (keepWindowOpen ? "-NoExit " : "") +
				"-NoProfile -ExecutionPolicy Bypass -File " +
				QuoteArgument(scriptPath) +
				" -DataDirectory " +
				QuoteArgument(_dataDirectory);
			var startInfo = new ProcessStartInfo
			{
				FileName = FindPowerShellPath(),
				Arguments = arguments,
				WorkingDirectory = Directory.Exists(ToolsDirectory)
					? ToolsDirectory
					: _dataDirectory,
				UseShellExecute = requireElevation || keepWindowOpen,
				CreateNoWindow = false
			};
			if (requireElevation)
			{
				startInfo.Verb = "runas";
				startInfo.WindowStyle = ProcessWindowStyle.Hidden;
			}
			else if (keepWindowOpen)
			{
				startInfo.WindowStyle = ProcessWindowStyle.Normal;
			}
			return startInfo;
		}

		private void ApplyLatestLog(RefreshTaskSnapshot snapshot)
		{
			var latest = FindLatestRefreshLog();
			if (latest == null)
			{
				return;
			}

			snapshot.LatestLogPath = latest.FullName;
			snapshot.LatestLogTime = latest.LastWriteTime;
			ApplyLatestLogOutcome(snapshot, latest.FullName);
			snapshot.LatestLogSummaryLines = ReadLogTail(latest.FullName, LogTailLineCount);
		}

		private static void ApplyLatestLogOutcome(RefreshTaskSnapshot snapshot, string path)
		{
			try
			{
				var text = ReadAllTextShared(path);
				snapshot.LatestLogFailed = RefreshFailureRegex.IsMatch(text);
				snapshot.LatestLogSucceeded = !snapshot.LatestLogFailed &&
					text.IndexOf("Windows PowerShell 脚本结束", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			catch (IOException ex)
			{
				snapshot.LatestLogBusy = true;
				Log.Info("Refresh log is still being written: " + SummarizeException(ex));
			}
			catch (Exception ex)
			{
				Log.Warn("Refresh log outcome check failed: " + SummarizeException(ex));
			}
		}

		private FileInfo FindLatestRefreshLog()
		{
			if (!Directory.Exists(LogDirectory))
			{
				return null;
			}

			return new DirectoryInfo(LogDirectory)
				.GetFiles("refresh-*.log")
				.OrderByDescending(file => file.LastWriteTime)
				.ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
		}

		private static List<string> ReadLogTail(string path, int lineCount)
		{
			try
			{
				var lines = ReadAllTextShared(path)
					.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
					.Where(line => !string.IsNullOrWhiteSpace(line))
					.ToList();

				return lines
					.Skip(Math.Max(0, lines.Count - lineCount))
					.Select(SanitizeLogLine)
					.ToList();
			}
			catch (Exception ex)
			{
				return new List<string>
				{
					ex is IOException
						? "刷新正在运行，日志仍在写入；请稍后刷新状态，或看弹出的 PowerShell 窗口。"
						: "日志摘要读取失败: " + SummarizeException(ex)
				};
			}
		}

		private static string ReadAllTextShared(string path)
		{
			using (var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete))
			using (var reader = new StreamReader(stream, Encoding.UTF8, true))
			{
				return reader.ReadToEnd();
			}
		}

		private static string SanitizeLogLine(string line)
		{
			return SanitizeDiagnosticText(line);
		}

		internal static string SanitizeDiagnosticText(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			var sanitized = CookieValueRegex.Replace(text, "$1=[redacted]");
			return CollapseHtmlDiagnosticText(sanitized);
		}

		private static string CollapseHtmlDiagnosticText(string text)
		{
			var htmlIndex = IndexOfHtmlDocument(text);
			if (htmlIndex < 0)
			{
				return text;
			}

			var prefix = text.Substring(0, htmlIndex).TrimEnd();
			if (prefix.EndsWith("Body:", StringComparison.OrdinalIgnoreCase))
			{
				prefix = prefix.Substring(0, prefix.Length - "Body:".Length).TrimEnd();
			}

			var html = text.Substring(htmlIndex);
			var title = ExtractHtmlTitle(html);
			var responseKind = IsCloudflareChallenge(html, title)
				? "Cloudflare 验证页"
				: string.IsNullOrWhiteSpace(title)
					? "HTML 响应"
					: "HTML 响应 (" + title + ")";
			var suffix = "响应正文已省略: " + responseKind + "。";
			return string.IsNullOrWhiteSpace(prefix) ? suffix : prefix + " " + suffix;
		}

		private static int IndexOfHtmlDocument(string text)
		{
			var doctypeIndex = text.IndexOf("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase);
			var htmlIndex = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
			if (doctypeIndex < 0)
			{
				return htmlIndex;
			}
			if (htmlIndex < 0)
			{
				return doctypeIndex;
			}
			return Math.Min(doctypeIndex, htmlIndex);
		}

		private static string ExtractHtmlTitle(string html)
		{
			var match = HtmlTitleRegex.Match(html ?? "");
			if (!match.Success)
			{
				return "";
			}

			return Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
		}

		private static bool IsCloudflareChallenge(string html, string title)
		{
			return string.Equals(title, "Just a moment...", StringComparison.OrdinalIgnoreCase) ||
				(html ?? "").IndexOf("cloudflare", StringComparison.OrdinalIgnoreCase) >= 0 ||
				(html ?? "").IndexOf("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string BuildToolsStatus(RefreshTaskSnapshot snapshot)
		{
			if (!snapshot.ToolsDirectoryExists || !snapshot.RefreshScriptExists)
			{
				return "高级刷新脚本未安装";
			}

			if (!snapshot.InstallScriptExists)
			{
				return "高级刷新脚本不完整: 缺少 " + InstallScriptFileName;
			}

			return "高级刷新脚本已安装";
		}

		private static string BuildScheduledTaskStatus(RefreshTaskSnapshot snapshot)
		{
			if (!string.IsNullOrWhiteSpace(snapshot.ScheduledTaskError))
			{
				return "自动刷新状态读取失败: " + snapshot.ScheduledTaskError;
			}

			if (!snapshot.RefreshScriptExists)
			{
				if (snapshot.ScheduledTaskInstalled)
				{
					return "自动刷新计划任务已安装，但设置页脚本未安装";
				}
				return "自动刷新不可用: 缺少高级刷新脚本";
			}

			if (snapshot.ScheduledTaskInstalled &&
				snapshot.ScheduledTaskActionKnown &&
				!snapshot.ScheduledTaskUsesInstalledScript)
			{
				return "自动刷新已安装，但指向旧脚本路径；请重新安装";
			}

			return snapshot.ScheduledTaskInstalled
				? "自动刷新已安装: " + ScheduledTaskName
				: "自动刷新未安装";
		}

		private static string BuildLatestLogStatus(RefreshTaskSnapshot snapshot)
		{
			if (!snapshot.LatestLogTime.HasValue)
			{
				return "最近刷新日志: 未找到";
			}

			var outcome = snapshot.LatestLogBusy
				? "刷新中"
				: snapshot.LatestLogFailed
				? "失败"
				: snapshot.LatestLogSucceeded
					? "完成"
					: "状态未知";
			var scriptState = snapshot.RefreshScriptExists
				? ""
				: "；设置页脚本未安装";
			return "最近刷新日志: " +
				snapshot.LatestLogTime.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
				"（" + outcome + scriptState + "）";
		}

		private static RefreshScheduledTaskInfo WindowsScheduledTaskInspect(string taskName)
		{
			var serviceType = Type.GetTypeFromProgID("Schedule.Service");
			if (serviceType == null)
			{
				return new RefreshScheduledTaskInfo();
			}

			object service = null;
			object rootFolder = null;
			object task = null;
			try
			{
				service = Activator.CreateInstance(serviceType);
				dynamic scheduler = service;
				scheduler.Connect();
				rootFolder = scheduler.GetFolder("\\");
				dynamic root = rootFolder;
				task = root.GetTask(taskName);
				dynamic scheduledTask = task;
				var info = new RefreshScheduledTaskInfo
				{
					Installed = true
				};
				try
				{
					dynamic actions = scheduledTask.Definition.Actions;
					if (actions != null && actions.Count > 0)
					{
						dynamic action = actions.Item(1);
						info.Path = Convert.ToString(action.Path, CultureInfo.InvariantCulture);
						info.Arguments = Convert.ToString(action.Arguments, CultureInfo.InvariantCulture);
						info.WorkingDirectory = Convert.ToString(
							action.WorkingDirectory,
							CultureInfo.InvariantCulture);
					}
				}
				catch (Exception ex)
				{
					Log.Warn("Refresh scheduled task action check failed: " + SummarizeException(ex));
				}
				return info;
			}
			catch (COMException ex)
			{
				const int FileNotFoundHResult = unchecked((int)0x80070002);
				if (ex.ErrorCode == FileNotFoundHResult)
				{
					return new RefreshScheduledTaskInfo();
				}
				throw;
			}
			finally
			{
				ReleaseComObject(task);
				ReleaseComObject(rootFolder);
				ReleaseComObject(service);
			}
		}

		private static void ReleaseComObject(object value)
		{
			if (value != null && Marshal.IsComObject(value))
			{
				Marshal.ReleaseComObject(value);
			}
		}

		private static int StartDetachedProcess(ProcessStartInfo startInfo)
		{
			using (var process = Process.Start(startInfo))
			{
				if (process == null)
				{
					throw new InvalidOperationException("Unable to start powershell.exe");
				}

				return process.Id;
			}
		}

		private static string FindPowerShellPath()
		{
			var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			var powerShell = Path.Combine(
				windowsDirectory,
				"System32",
				"WindowsPowerShell",
				"v1.0",
				"powershell.exe");
			return File.Exists(powerShell) ? powerShell : "powershell.exe";
		}

		private static string QuoteArgument(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
		}

		private static bool ContainsPath(string text, string path)
		{
			return !string.IsNullOrWhiteSpace(text) &&
				!string.IsNullOrWhiteSpace(path) &&
				text.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string SummarizeException(Exception ex)
		{
			if (ex == null)
			{
				return "未知错误";
			}

			var message = ex.Message ?? ex.GetType().Name;
			message = message.Replace(Environment.NewLine, " ").Trim();
			return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message;
		}
	}
}
