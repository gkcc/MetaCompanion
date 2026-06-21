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
		public string ScheduledTaskError { get; set; } = "";
		public string LatestLogPath { get; set; } = "";
		public DateTime? LatestLogTime { get; set; }
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

	internal class RefreshTaskService
	{
		public const string ScheduledTaskName = "Meta Companion Remote Cache Refresh";
		public const string RefreshScriptFileName = "Run-MetaCompanionRefresh.ps1";
		public const string InstallScriptFileName = "Install-MetaCompanionRefreshTask.ps1";

		private const int LogTailLineCount = 8;
		private static readonly Regex CookieValueRegex = new Regex(
			@"(?i)\b(cookie|set-cookie|cookiepath)\b\s*[:=]\s*("".*?""|'.*?'|\S+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private readonly string _dataDirectory;
		private readonly Func<string, bool> _scheduledTaskExists;
		private readonly Func<ProcessStartInfo, int> _startProcess;

		public RefreshTaskService(string dataDirectory)
			: this(dataDirectory, null, null)
		{
		}

		internal RefreshTaskService(
			string dataDirectory,
			Func<string, bool> scheduledTaskExists,
			Func<ProcessStartInfo, int> startProcess)
		{
			_dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
				? MetaCompanionPlugin.DataDirectory
				: Path.GetFullPath(dataDirectory);
			_scheduledTaskExists = scheduledTaskExists ?? WindowsScheduledTaskExists;
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
				snapshot.ScheduledTaskInstalled = _scheduledTaskExists(ScheduledTaskName);
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

			return StartPowerShellScript(InstallScriptPath, "安装自动刷新");
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

			return StartPowerShellScript(RefreshScriptPath, "立即刷新");
		}

		private RefreshTaskLaunchResult StartPowerShellScript(string scriptPath, string actionName)
		{
			try
			{
				var startInfo = BuildPowerShellStartInfo(scriptPath);
				Log.Info("Starting refresh task PowerShell process: " + actionName + " (" +
					Path.GetFileName(scriptPath) + ")");
				var processId = _startProcess(startInfo);
				return new RefreshTaskLaunchResult
				{
					Started = true,
					ProcessId = processId,
					Message = actionName + "脚本已启动。"
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

		private ProcessStartInfo BuildPowerShellStartInfo(string scriptPath)
		{
			return new ProcessStartInfo
			{
				FileName = FindPowerShellPath(),
				Arguments = "-NoProfile -ExecutionPolicy Bypass -File " +
					QuoteArgument(scriptPath) +
					" -DataDirectory " +
					QuoteArgument(_dataDirectory),
				WorkingDirectory = Directory.Exists(ToolsDirectory)
					? ToolsDirectory
					: _dataDirectory,
				UseShellExecute = false,
				CreateNoWindow = true
			};
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
			snapshot.LatestLogSummaryLines = ReadLogTail(latest.FullName, LogTailLineCount);
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
				var lines = File.ReadAllLines(path, Encoding.UTF8)
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
					"日志摘要读取失败: " + SummarizeException(ex)
				};
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

			return CookieValueRegex.Replace(text, "$1=[redacted]");
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
				return "自动刷新不可用: 缺少高级刷新脚本";
			}

			return snapshot.ScheduledTaskInstalled
				? "自动刷新已安装: " + ScheduledTaskName
				: "自动刷新未安装";
		}

		private static string BuildLatestLogStatus(RefreshTaskSnapshot snapshot)
		{
			return snapshot.LatestLogTime.HasValue
				? "最近刷新日志: " +
					snapshot.LatestLogTime.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
				: "最近刷新日志: 未找到";
		}

		private static bool WindowsScheduledTaskExists(string taskName)
		{
			var serviceType = Type.GetTypeFromProgID("Schedule.Service");
			if (serviceType == null)
			{
				return false;
			}

			object service = null;
			object rootFolder = null;
			try
			{
				service = Activator.CreateInstance(serviceType);
				dynamic scheduler = service;
				scheduler.Connect();
				rootFolder = scheduler.GetFolder("\\");
				dynamic root = rootFolder;
				root.GetTask(taskName);
				return true;
			}
			catch (COMException ex)
			{
				const int FileNotFoundHResult = unchecked((int)0x80070002);
				if (ex.ErrorCode == FileNotFoundHResult)
				{
					return false;
				}
				throw;
			}
			finally
			{
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
