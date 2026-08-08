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
		public bool LatestLogDeferred { get; set; }
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

		public bool OptionalRefreshComponentsNotInstalled
		{
			get
			{
				return !RefreshScriptExists &&
					!InstallScriptExists &&
					!ScheduledTaskInstalled;
			}
		}

		public bool RefreshScriptsComplete
		{
			get { return RefreshScriptExists && InstallScriptExists; }
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
		private const int ScheduledTaskNotFoundHResult = unchecked((int)0x80070002);

		private static readonly Regex CredentialValueRegex = new Regex(
			@"(?i)\b(cookie|set-cookie|cookiepath|token|access[_-]?token|refresh[_-]?token|authorization|api[_-]?key)\b\s*[:=]\s*("".*?""|'.*?'|Bearer\s+\S+|\S+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex BearerValueRegex = new Regex(
			@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex HtmlTitleRegex = new Regex(
			@"(?is)<title>\s*(.*?)\s*</title>",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex RefreshFailureRegex = new Regex(
			@"(?i)(TerminatingError|returned HTTP [45]\d\d|返回 HTTP [45]\d\d|curl\.exe[^\r\n]*(?:failed|失败)|No HSReplay cookie found|未找到 HSReplay Cookie|Cookie file is empty|Cookie 文件为空|Exception calling)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex RefreshOutcomeRegex = new Regex(
			@"META_COMPANION_REFRESH_OUTCOME=(?<outcome>[A-Z_]+)",
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
				if (!IsScheduledTaskNotFound(ex))
				{
					snapshot.ScheduledTaskError = SummarizeException(ex);
					Log.Warn("Refresh scheduled task status check failed: " + snapshot.ScheduledTaskError);
				}
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
					Message = "自动刷新组件不完整。请重新安装插件后重试。"
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
					Message = "立即刷新不可用。请重新安装插件以补全刷新组件。"
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
						? actionName + "已在刷新窗口中启动；完成后可关闭窗口。"
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
					Message = SettingsDiagnostics.BuildUserFacingFailure(
						actionName,
						"请确认 Windows 脚本组件可用后重试")
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
			snapshot.LatestLogSummaryLines = BuildLatestLogSummary(snapshot);
		}

		private static void ApplyLatestLogOutcome(RefreshTaskSnapshot snapshot, string path)
		{
			try
			{
				var text = ReadAllTextShared(path);
				var transcriptEnded =
					text.IndexOf("Windows PowerShell 脚本结束", StringComparison.OrdinalIgnoreCase) >= 0 ||
					text.IndexOf("Windows PowerShell transcript end", StringComparison.OrdinalIgnoreCase) >= 0;
				var outcomeMatches = RefreshOutcomeRegex.Matches(text);
				if (outcomeMatches.Count > 0)
				{
					var outcome = outcomeMatches[outcomeMatches.Count - 1]
						.Groups["outcome"].Value;
					snapshot.LatestLogFailed = string.Equals(
						outcome, "FAILED", StringComparison.OrdinalIgnoreCase);
					snapshot.LatestLogDeferred = string.Equals(
						outcome, "DEFERRED", StringComparison.OrdinalIgnoreCase);
					snapshot.LatestLogSucceeded = !snapshot.LatestLogFailed &&
						!snapshot.LatestLogDeferred;
					return;
				}
				if (transcriptEnded && text.IndexOf(
						"本次停止分支与推荐刷新，请稍后重试",
						StringComparison.OrdinalIgnoreCase) >= 0)
				{
					snapshot.LatestLogDeferred = true;
					return;
				}
				snapshot.LatestLogFailed = RefreshFailureRegex.IsMatch(text);
				snapshot.LatestLogSucceeded = !snapshot.LatestLogFailed && transcriptEnded;
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

		private static List<string> BuildLatestLogSummary(RefreshTaskSnapshot snapshot)
		{
			if (snapshot.LatestLogBusy)
			{
				return new List<string>
				{
					"数据刷新正在进行，请稍后点击“刷新状态”。"
				};
			}
			if (snapshot.LatestLogFailed)
			{
				return new List<string>
				{
					"最近一次数据刷新失败。请先重试；仍失败时可打开刷新日志（开发者用，其中可能含英文技术信息）。"
				};
			}
			if (snapshot.LatestLogDeferred)
			{
				return new List<string>
				{
					"HSReplay 当前补丁数据仍在生成；现有缓存已保留，请稍后重试。"
				};
			}
			if (snapshot.LatestLogSucceeded)
			{
				return new List<string>
				{
					"最近一次数据刷新已完成。"
				};
			}
			return new List<string>
			{
				"尚未确认刷新结果。请稍后点击“刷新状态”；刷新日志仅供排查，可能含英文技术信息。"
			};
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

		internal static string SanitizeDiagnosticText(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			var sanitized = CredentialValueRegex.Replace(text, "$1=[redacted]");
			sanitized = BearerValueRegex.Replace(sanitized, "Bearer [redacted]");
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
			foreach (var responsePrefix in new[] { "Body:", "Response:", "响应：", "响应:" })
			{
				if (prefix.EndsWith(responsePrefix, StringComparison.OrdinalIgnoreCase))
				{
					prefix = prefix.Substring(0, prefix.Length - responsePrefix.Length).TrimEnd();
					break;
				}
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
			if (snapshot.OptionalRefreshComponentsNotInstalled)
			{
				return "可选自动刷新组件未安装；不影响现有数据、预测和实战建议。";
			}

			if (!snapshot.RefreshScriptsComplete)
			{
				return "高级自动刷新组件不完整，请安装完整组件。";
			}

			return "高级刷新脚本已安装";
		}

		private static string BuildScheduledTaskStatus(RefreshTaskSnapshot snapshot)
		{
			if (!string.IsNullOrWhiteSpace(snapshot.ScheduledTaskError))
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"读取自动刷新状态",
					"请点击“刷新状态”重试");
			}

			if (snapshot.OptionalRefreshComponentsNotInstalled)
			{
				return "可选自动刷新组件未安装；不影响现有数据、预测和实战建议。";
			}

			if (!snapshot.RefreshScriptsComplete)
			{
				if (snapshot.ScheduledTaskInstalled)
				{
					return "自动刷新计划仍存在，但高级刷新组件不完整。";
				}
				return "高级自动刷新组件不完整，暂时无法安装计划。";
			}

			if (snapshot.ScheduledTaskInstalled &&
				snapshot.ScheduledTaskActionKnown &&
				!snapshot.ScheduledTaskUsesInstalledScript)
			{
				return "自动刷新已安装，但指向旧脚本路径；请重新安装";
			}

			return snapshot.ScheduledTaskInstalled
				? "自动刷新已安装"
				: "自动刷新未安装";
		}

		private static string BuildLatestLogStatus(RefreshTaskSnapshot snapshot)
		{
			if (!snapshot.LatestLogTime.HasValue)
			{
				return snapshot.OptionalRefreshComponentsNotInstalled
					? "最近刷新日志: 未找到（可选自动刷新组件未安装）"
					: "最近刷新日志: 未找到";
			}

			var outcome = snapshot.LatestLogBusy
				? "刷新中"
				: snapshot.LatestLogFailed
				? "失败"
				: snapshot.LatestLogDeferred
					? "等待上游数据"
				: snapshot.LatestLogSucceeded
					? "完成"
					: "状态未知";
			var scriptState = snapshot.OptionalRefreshComponentsNotInstalled
				? "；可选自动刷新组件未安装"
				: snapshot.RefreshScriptsComplete
					? ""
					: "；高级自动刷新组件不完整";
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
			catch (Exception ex)
			{
				if (IsScheduledTaskNotFound(ex))
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

		internal static bool IsScheduledTaskNotFound(Exception ex)
		{
			return ex != null && ex.HResult == ScheduledTaskNotFoundHResult;
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
