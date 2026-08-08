using MahApps.Metro.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
	using System.Threading.Tasks;
using System.Windows;

namespace MetaCompanion
{
	public partial class SettingsWindow : MetroWindow
	{
		private readonly PluginConfig _config;
		private readonly RefreshTaskService _refreshTaskService;
		private MetaDataHealthSnapshot _dataHealthSnapshot;
		private RefreshTaskSnapshot _refreshTaskSnapshot;
		private TrainingLogSummary _trainingLogSummary;
		private bool _isLocalMetaActionRunning;
		private string _localMetaActionStatus = "";

		public SettingsWindow(PluginConfig config)
		{
			_config = config;
			_refreshTaskService = new RefreshTaskService(MetaCompanionPlugin.DataDirectory);
			InitializeComponent();
			MaxHeight = SettingsDiagnostics.CalculateSettingsMaxHeight(
				SystemParameters.WorkArea.Height);
			DataContext = this;
		}

		private void ButtonReadme_Click(object sender, RoutedEventArgs e)
		{
			var readme = Path.Combine(
				Path.GetDirectoryName(typeof(SettingsWindow).Assembly.Location) ?? "",
				"README.md");
			TryRunUserAction(
				() => System.Diagnostics.Process.Start(File.Exists(readme)
					? readme
					: "https://github.com/"),
				"Open settings help failed",
				"打开使用说明",
				"请稍后重试；若仍失败，请检查系统默认浏览器或文件打开方式");
		}

		private void ButtonOpenData_Click(object sender, RoutedEventArgs e)
		{
			TryRunUserAction(
				() =>
				{
					Directory.CreateDirectory(MetaCompanionPlugin.DataDirectory);
					System.Diagnostics.Process.Start(MetaCompanionPlugin.DataDirectory);
				},
				"Open settings data directory failed",
				"打开数据目录",
				"请稍后重试；若仍失败，请检查系统文件管理器和目录访问权限");
		}

		private void ButtonOpenHistory_Click(object sender, RoutedEventArgs e)
		{
			TryRunUserAction(
				() => OpenLocalFile(
					MatchHistoryRecorder.GetHistoryPath(MetaCompanionPlugin.DataDirectory),
					MatchHistoryRecorder.HistoryHeader),
				"Open match history failed",
				"打开对局历史",
				"请稍后重试；若仍失败，请检查数据目录的读写权限");
		}

		private void ButtonOpenTimeline_Click(object sender, RoutedEventArgs e)
		{
			TryRunUserAction(
				() => OpenLocalFile(
					MatchHistoryRecorder.GetTimelinePath(MetaCompanionPlugin.DataDirectory),
					MatchHistoryRecorder.TimelineHeader),
				"Open prediction timeline failed",
				"打开预测时间线",
				"请稍后重试；若仍失败，请检查数据目录的读写权限");
		}

		private void ButtonOpenCorrections_Click(object sender, RoutedEventArgs e)
		{
			TryRunUserAction(
				() => OpenLocalFile(
					MatchHistoryRecorder.GetCorrectionsPath(MetaCompanionPlugin.DataDirectory),
					"match_id\tcorrected_archetype\tcorrected_result\tnotes"),
				"Open match corrections failed",
				"打开对局修正记录",
				"请稍后重试；若仍失败，请检查数据目录的读写权限");
		}

		private void ButtonOpenRecommendations_Click(object sender, RoutedEventArgs e)
		{
			var personalPath = GetPersonalRecommendationsPath();
			var recommendationsPath = File.Exists(personalPath)
				? personalPath
				: GetRecommendationsPath();
			if (!File.Exists(recommendationsPath))
			{
				MessageBox.Show(
					"未找到推荐结果。插件会使用随包或本地已有的数据快照；高级数据同步请在源码工具中手动执行。",
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				return;
			}

			TryRunUserAction(
				() => System.Diagnostics.Process.Start(recommendationsPath),
				"Open recommendation result failed",
				"打开推荐结果",
				"请稍后重试；若仍失败，请检查系统文件打开方式");
		}

		private void ButtonResetLayout_Click(object sender, RoutedEventArgs e)
		{
			if (!TryRunUserAction(
				() => _config.ResetOverlayPositions(),
				"Reset overlay layout failed",
				"重置浮窗位置",
				"请稍后重试；若仍失败，请检查 HDT 配置目录的写入权限"))
			{
				return;
			}
			MessageBox.Show(
				"浮窗位置已重置。下一次显示时会回到默认位置。",
				"Meta Companion",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
		}

		private void ButtonInstallRefreshTask_Click(object sender, RoutedEventArgs e)
		{
			ShowRefreshLaunchResult(_refreshTaskService.StartInstallTask());
			RefreshStatusBindings();
		}

		private void ButtonRunRefreshNow_Click(object sender, RoutedEventArgs e)
		{
			ShowRefreshLaunchResult(_refreshTaskService.StartRefreshNow());
			RefreshStatusBindings();
		}

		private void ButtonRefreshStatus_Click(object sender, RoutedEventArgs e)
		{
			RefreshStatusBindings();
		}

		private async void ButtonApplyLocalFilters_Click(object sender, RoutedEventArgs e)
		{
			await RunLocalMetaActionAsync(() => LocalMetaDataService.RebuildWithCurrentFilters(
				_config,
				MetaCompanionPlugin.DataDirectory,
				DateTime.Now));
		}

		private async void ButtonClearLocalSamples_Click(object sender, RoutedEventArgs e)
		{
			var confirmation = MessageBox.Show(
				"将清空插件当前用于推荐加权的本地对战数据和派生统计。HDT 原始对战历史不会删除，之后可以一键恢复。是否继续？",
				"Meta Companion",
				MessageBoxButton.YesNo,
				MessageBoxImage.Question,
				MessageBoxResult.No);
			if (confirmation != MessageBoxResult.Yes)
			{
				return;
			}

			await RunLocalMetaActionAsync(() => LocalMetaDataService.ClearLocalSamples(
				_config,
				MetaCompanionPlugin.DataDirectory,
				DateTime.Now));
		}

		private async void ButtonRestoreLocalSamples_Click(object sender, RoutedEventArgs e)
		{
			await RunLocalMetaActionAsync(() => LocalMetaDataService.RestoreCurrentPatchHistory(
				_config,
				MetaCompanionPlugin.DataDirectory,
				DateTime.Now));
		}

		private async Task RunLocalMetaActionAsync(Func<LocalMetaDataActionResult> action)
		{
			if (_isLocalMetaActionRunning || action == null)
			{
				return;
			}

			_isLocalMetaActionRunning = true;
			_localMetaActionStatus = "正在重建本地样本，请稍候……";
			RefreshStatusBindings();
			try
			{
				var result = await Task.Run(action);
				SaveConfig();
				_localMetaActionStatus = result == null ? "本地样本处理完成。" : result.Message;
				MessageBox.Show(
					_localMetaActionStatus,
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				SaveConfig();
				Log.Warn("Local meta data action failed: " + ex);
				_localMetaActionStatus = SettingsDiagnostics.BuildUserFacingFailure(
					"处理本地样本",
					"HDT 原始历史未被删除；请稍后重试");
				MessageBox.Show(
					_localMetaActionStatus,
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
			finally
			{
				_isLocalMetaActionRunning = false;
				RefreshStatusBindings();
			}
		}

		private void ButtonCopyDiagnostics_Click(object sender, RoutedEventArgs e)
		{
			var diagnostics = SettingsDiagnostics.BuildDiagnosticText(
				DateTime.Now,
				MetaCompanionPlugin.DataDirectory,
				_refreshTaskService.LogDirectory,
				DataStatus,
				RecommendationStatus,
				PremiumStatus,
				DataHealthSnapshot,
				RefreshTaskSnapshot);
			try
			{
				Clipboard.SetText(diagnostics);
				MessageBox.Show(
					"诊断信息已复制到剪贴板。内容不包含登录凭据。",
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
			}
			catch (Exception ex)
			{
				Log.Warn("Copy diagnostics failed: " + ex.Message);
				MessageBox.Show(
					SettingsDiagnostics.BuildUserFacingFailure(
						"复制诊断信息",
						"请稍后重试；若仍失败，请打开插件日志查看原因"),
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
		}

		private void ButtonOpenLogDirectory_Click(object sender, RoutedEventArgs e)
		{
			if (!ConfirmOpenDeveloperLog())
			{
				return;
			}

			try
			{
				Directory.CreateDirectory(_refreshTaskService.LogDirectory);
				System.Diagnostics.Process.Start(_refreshTaskService.LogDirectory);
			}
			catch (Exception ex)
			{
				Log.Warn("Open developer log directory failed: " + ex);
				ShowUserFailure(
					"打开开发者日志目录",
					"请稍后重试；若仍失败，可先使用“复制诊断信息”");
			}
		}

		private void ButtonOpenRefreshLog_Click(object sender, RoutedEventArgs e)
		{
			var logPath = RefreshTaskSnapshot.LatestLogPath;
			if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
			{
				MessageBox.Show(
					"未找到刷新日志。运行一次“立即刷新”后，日志目录中会生成新的刷新记录。",
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				return;
			}

			if (!ConfirmOpenDeveloperLog())
			{
				return;
			}

			try
			{
				System.Diagnostics.Process.Start(logPath);
			}
			catch (Exception ex)
			{
				Log.Warn("Open refresh developer log failed: " + ex);
				ShowUserFailure(
					"打开开发者刷新日志",
					"请稍后重试；若仍失败，可先使用“复制诊断信息”");
			}
		}

		private void ShowRefreshLaunchResult(RefreshTaskLaunchResult result)
		{
			result = result ?? new RefreshTaskLaunchResult
			{
				Started = false,
				Message = "脚本启动失败。"
			};
			var message = result.Started
				? SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Normal,
					result.Message)
				: EnsureActionRequiredStatus(
					result.Message,
					"刷新操作失败",
					"请稍后重试；若仍失败，请重新安装插件");

			MessageBox.Show(
				message,
				"Meta Companion",
				MessageBoxButton.OK,
				result.Started ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}

		private static bool ConfirmOpenDeveloperLog()
		{
			return MessageBox.Show(
				SettingsDiagnostics.DeveloperLogConfirmation,
				"打开开发者日志",
				MessageBoxButton.YesNo,
				MessageBoxImage.Warning,
				MessageBoxResult.No) == MessageBoxResult.Yes;
		}

		private static void ShowUserFailure(string action, string nextStep)
		{
			MessageBox.Show(
				SettingsDiagnostics.BuildUserFacingFailure(action, nextStep),
				"Meta Companion",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}

		private static bool TryRunUserAction(
			Action action,
			string logContext,
			string userAction,
			string nextStep)
		{
			return TryRunSettingsAction(action, ex =>
			{
				Log.Warn(logContext + ": " + ex);
				ShowUserFailure(userAction, nextStep);
			});
		}

		internal static bool TryRunSettingsAction(
			Action action, Action<Exception> failureHandler)
		{
			if (action == null)
				throw new ArgumentNullException(nameof(action));
			try
			{
				action();
				return true;
			}
			catch (Exception ex)
			{
				failureHandler?.Invoke(ex);
				return false;
			}
		}

		private void SaveConfig()
		{
			TryRunUserAction(
				() => _config.Save(),
				"Save settings failed",
				"保存插件设置",
				"当前修改可能未保存；请检查 HDT 配置目录的写入权限后重试");
		}

		private static string EnsureActionRequiredStatus(
			string message,
			string fallbackMessage,
			string nextStep)
		{
			var safeMessage = SettingsDiagnostics.HideTechnicalDetails(message, "");
			if (safeMessage.StartsWith("需处理：", StringComparison.Ordinal))
			{
				return safeMessage;
			}

			return SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.ActionRequired,
				string.IsNullOrWhiteSpace(safeMessage) ? fallbackMessage : safeMessage,
				nextStep);
		}

		private void RefreshStatusBindings()
		{
			_dataHealthSnapshot = null;
			_refreshTaskSnapshot = null;
			_trainingLogSummary = null;
			DataContext = null;
			DataContext = this;
		}

		private static void OpenLocalFile(string path, string header)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			if (!File.Exists(path))
			{
				File.WriteAllText(path, header + Environment.NewLine, System.Text.Encoding.UTF8);
			}
			System.Diagnostics.Process.Start(path);
		}

		public string DataStatus
		{
			get
			{
				try
				{
					return BuildDataStatus();
				}
				catch (Exception ex)
				{
					Log.Warn("Settings data status check failed: " + ex);
					return SettingsDiagnostics.BuildUserFacingFailure(
						"读取数据源状态",
						"请点击“刷新状态”重试；若仍失败，请重新生成数据快照");
				}
			}
		}

		private string BuildDataStatus()
		{
			var deckStatus = DescribeDeckDataFile(
				"HSReplay 牌组库",
				PostGameMetaRefresher.GetDeckSnapshotPath(MetaCompanionPlugin.DataDirectory),
				false);
			var branchStatus = DescribeDeckDataFile(
				"代表分支兜底",
				PostGameMetaRefresher.GetBranchSnapshotPath(MetaCompanionPlugin.DataDirectory),
				true);
			if (deckStatus != null && branchStatus != null)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Normal,
					deckStatus + "；" + branchStatus);
			}
			if (deckStatus != null)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Normal,
					deckStatus);
			}
			if (branchStatus != null)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Notice,
					"未找到 HSReplay 牌组库，当前使用" + branchStatus,
					"需要完整牌组库时请运行一次数据更新");
			}
			return SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Notice,
				"尚未找到 HSReplay 牌组快照，当前仅显示已有本地缓存",
				"请运行一次数据更新");
		}

		public string RecommendationStatus
		{
			get
			{
				try
				{
					return BuildRecommendationStatus();
				}
				catch (Exception ex)
				{
					Log.Warn("Settings recommendation status check failed: " + ex);
					return SettingsDiagnostics.BuildUserFacingFailure(
						"读取推荐数据状态",
						"请点击“刷新状态”重试；若仍失败，请重新生成推荐数据");
				}
			}
		}

		private string BuildRecommendationStatus()
		{
			var personalPath = GetPersonalRecommendationsPath();
			var recommendationsPath = File.Exists(personalPath)
				? personalPath
				: GetRecommendationsPath();
			if (!File.Exists(recommendationsPath))
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Notice,
					"尚未生成推荐结果",
					"请先运行一次数据更新");
			}

			var count = Math.Max(0, File.ReadLines(recommendationsPath).Count() - 1);
			var source = recommendationsPath == personalPath ? "个人" : "HSReplay";
			return SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Normal,
				"推荐结果：" + source + "前 " + count + " 名，更新于 " +
					File.GetLastWriteTime(recommendationsPath).ToString("yyyy-MM-dd HH:mm"));
		}

		public string PremiumStatus
		{
			get
			{
				try
				{
					return BuildPremiumStatus();
				}
				catch (Exception ex)
				{
					Log.Warn("Settings premium status check failed: " + ex);
					return SettingsDiagnostics.BuildUserFacingFailure(
						"读取对阵数据状态",
						"请点击“刷新状态”重试；若仍失败，请重新生成对阵数据");
				}
			}
		}

		private string BuildPremiumStatus()
		{
			var matrixPath = PostGameMetaRefresher.GetMetaMatrixPath(MetaCompanionPlugin.DataDirectory);
			var summaryPath = PostGameMetaRefresher.GetMetaSummaryPath(MetaCompanionPlugin.DataDirectory);
			var manifestPath = Path.Combine(
				MetaCompanionPlugin.DataDirectory, "Premium", "Meta", "latest", "manifest.json");
			if (!File.Exists(matrixPath) && !File.Exists(summaryPath))
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Notice,
					"尚未同步对阵数据",
					"请先运行一次数据更新");
			}

			var newest = new[] {matrixPath, summaryPath}
				.Where(File.Exists)
				.Select(File.GetLastWriteTime)
				.OrderByDescending(time => time)
				.First();
			var remoteSource = MetaDashboardRemoteSource.Load(summaryPath, manifestPath);
			var sourceText = remoteSource.HasData
				? " | " + remoteSource.SettingsText
				: "";
			return SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Normal,
				"对阵数据更新于 " + newest.ToString("yyyy-MM-dd HH:mm") + sourceText);
		}

		public string DataHealthMessage
		{
			get { return BuildDataHealthStatus(DataHealthSnapshot); }
		}

		internal static string BuildDataHealthStatus(MetaDataHealthSnapshot snapshot)
		{
			if (snapshot == null)
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"读取数据健康状态",
					"请点击“刷新状态”重试");
			}

			var severity = snapshot.OverallStatus == MetaDataHealthOverallStatus.Ready
				? UserMessageSeverity.Normal
				: snapshot.OverallStatus == MetaDataHealthOverallStatus.Error
					? UserMessageSeverity.ActionRequired
					: UserMessageSeverity.Notice;
			var nextStep = snapshot.OverallStatus == MetaDataHealthOverallStatus.Error
				? "请点击“刷新状态”重试；若仍失败，请重新生成数据快照"
				: snapshot.OverallStatus == MetaDataHealthOverallStatus.Stale ||
					snapshot.OverallStatus == MetaDataHealthOverallStatus.Empty
					? "请运行一次数据更新"
					: "";
			return SettingsDiagnostics.BuildUserStatus(
				severity,
				snapshot.UserMessage,
				nextStep);
		}

		public string TrainingLogStatusMessage
		{
			get { return BuildTrainingLogStatus(CurrentTrainingLogSummary); }
		}

		public List<string> AdvisorModelStatusLines
		{
			get
			{
				return SettingsDiagnostics.BuildAdvisorModelStatusLines(
					MetaCompanionPlugin.GetAdvisorWorkerHealthSnapshot(),
					(_config?.EnableLiveAdvisor ?? false) ||
					(_config?.EnableAdvisorTrainingLog ?? false));
			}
		}

		internal static string BuildTrainingLogStatus(TrainingLogSummary summary)
		{
			if (summary == null)
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"读取训练记录摘要",
					"请点击“刷新状态”重试");
			}
			if (!string.IsNullOrWhiteSpace(summary.ReadIssueMessage))
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.ActionRequired,
					"训练记录摘要暂时不可用",
					"请点击“刷新状态”重试");
			}

			var hasCoverageNotice = summary.ModelCoverageLines != null &&
				summary.ModelCoverageLines.Count > 0;
			var severity = !summary.HasCurrentLog || hasCoverageNotice
				? UserMessageSeverity.Notice
				: UserMessageSeverity.Normal;
			return SettingsDiagnostics.BuildUserStatus(
				severity,
				summary.StatusMessage);
		}

		public string TrainingLogLastUpdatedMessage
		{
			get { return CurrentTrainingLogSummary.LastUpdatedMessage; }
		}

		public List<string> TrainingLogModelCoverageLines
		{
			get
			{
				return CurrentTrainingLogSummary.ModelCoverageLines
					.Select(line => SettingsDiagnostics.BuildUserStatus(
						UserMessageSeverity.Notice,
						line))
					.ToList();
			}
		}

		public string TrainingLogReadIssueMessage
		{
			get
			{
				var message = CurrentTrainingLogSummary.ReadIssueMessage;
				return string.IsNullOrWhiteSpace(message)
					? ""
					: SettingsDiagnostics.BuildUserStatus(
						UserMessageSeverity.ActionRequired,
						message,
						"请点击“刷新状态”重试");
			}
		}

		public string TrainingLogLegacyMessage
		{
			get { return CurrentTrainingLogSummary.LegacyMessage; }
		}

		public string TrainingLogDeveloperNote
		{
			get { return CurrentTrainingLogSummary.DeveloperNote; }
		}

		public List<string> DataHealthDetailLines
		{
			get
			{
				return SettingsDiagnostics.BuildUserVisibleLines(
					DataHealthSnapshot.DetailLines,
					"部分详细状态未在界面显示；需要排查时可查看开发者日志。");
			}
		}

		public string RefreshToolsStatus
		{
			get { return BuildRefreshToolsStatus(RefreshTaskSnapshot); }
		}

		public string RefreshScheduledTaskStatus
		{
			get { return BuildRefreshScheduledTaskStatus(RefreshTaskSnapshot); }
		}

		public string RefreshLatestLogStatus
		{
			get { return BuildRefreshLatestStatus(RefreshTaskSnapshot); }
		}

		internal static string BuildRefreshToolsStatus(RefreshTaskSnapshot snapshot)
		{
			if (snapshot == null)
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"检查自动刷新组件",
					"请点击“刷新状态”重试");
			}
			if (snapshot.OptionalRefreshComponentsNotInstalled)
			{
				return BuildOptionalRefreshNotInstalledStatus();
			}
			if (snapshot.RefreshScriptsComplete)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Normal,
					snapshot.ToolsStatus);
			}
			return EnsureActionRequiredStatus(
				snapshot.ToolsStatus,
				"高级自动刷新组件不完整",
				"请安装完整的高级自动刷新组件");
		}

		internal static string BuildRefreshScheduledTaskStatus(RefreshTaskSnapshot snapshot)
		{
			if (snapshot == null)
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"检查自动刷新计划",
					"请点击“刷新状态”重试");
			}
			if (!string.IsNullOrWhiteSpace(snapshot.ScheduledTaskError))
			{
				return EnsureActionRequiredStatus(
					snapshot.ScheduledTaskStatus,
					"自动刷新计划状态暂时无法确认",
					"请点击“刷新状态”重试");
			}
			if (snapshot.OptionalRefreshComponentsNotInstalled)
			{
				return BuildOptionalRefreshNotInstalledStatus();
			}
			if (!snapshot.RefreshScriptsComplete)
			{
				return EnsureActionRequiredStatus(
					snapshot.ScheduledTaskStatus,
					"高级自动刷新组件不完整",
					"请安装完整的高级自动刷新组件");
			}
			if (snapshot.ScheduledTaskInstalled &&
				snapshot.ScheduledTaskActionKnown &&
				!snapshot.ScheduledTaskUsesInstalledScript)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.ActionRequired,
					"自动刷新计划仍指向旧位置",
					"请重新安装自动刷新计划");
			}
			return SettingsDiagnostics.BuildUserStatus(
				snapshot.ScheduledTaskInstalled
					? UserMessageSeverity.Normal
					: UserMessageSeverity.Notice,
				snapshot.ScheduledTaskStatus,
				snapshot.ScheduledTaskInstalled
					? ""
					: "需要自动刷新时可点击“安装自动刷新”");
		}

		internal static string BuildRefreshLatestStatus(RefreshTaskSnapshot snapshot)
		{
			if (snapshot == null)
			{
				return SettingsDiagnostics.BuildUserFacingFailure(
					"检查最近刷新结果",
					"请点击“刷新状态”重试");
			}
			if (snapshot.LatestLogFailed)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.ActionRequired,
					"最近一次数据刷新失败",
					"请打开开发者刷新日志确认原因");
			}
			if (snapshot.OptionalRefreshComponentsNotInstalled)
			{
				return BuildOptionalRefreshNotInstalledStatus();
			}
			if (!snapshot.RefreshScriptsComplete)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.ActionRequired,
					"高级自动刷新组件不完整",
					"请安装完整组件后再查看自动刷新状态");
			}
			if (!snapshot.LatestLogTime.HasValue)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Notice,
					"尚未找到刷新记录",
					"运行一次“立即刷新”后再查看");
			}
			if (snapshot.LatestLogBusy)
			{
				return SettingsDiagnostics.BuildUserStatus(
					UserMessageSeverity.Notice,
					"数据刷新正在进行",
					"请稍后点击“刷新状态”");
			}
			return SettingsDiagnostics.BuildUserStatus(
				snapshot.LatestLogSucceeded
					? UserMessageSeverity.Normal
					: UserMessageSeverity.Notice,
				snapshot.LatestLogStatus,
				snapshot.LatestLogSucceeded
					? ""
					: "请稍后点击“刷新状态”确认结果");
		}

		private static string BuildOptionalRefreshNotInstalledStatus()
		{
			return SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Notice,
				"可选自动刷新组件未安装",
				"不影响现有数据、预测和实战建议");
		}

		public List<string> RefreshLogSummaryLines
		{
			get
			{
				return SettingsDiagnostics.BuildUserVisibleLines(
					RefreshTaskSnapshot.LatestLogSummaryLines,
					"日志详情未在界面显示；需要排查时可打开开发者刷新日志。");
			}
		}

		public bool CanInstallRefreshTask
		{
			get { return RefreshTaskSnapshot.CanInstallTask; }
		}

		public bool CanRunRefreshNow
		{
			get { return RefreshTaskSnapshot.CanRunRefresh; }
		}

		public bool CanOpenRefreshLog
		{
			get { return RefreshTaskSnapshot.CanOpenLatestLog; }
		}

		private MetaDataHealthSnapshot DataHealthSnapshot
		{
			get
			{
				if (_dataHealthSnapshot == null)
				{
					try
					{
						var staleAfter = TimeSpan.FromHours(Math.Max(
							1,
							_config == null ? 24 : _config.PostGameDataRefreshCooldownHours));
						_dataHealthSnapshot = new MetaDataHealthService(
							MetaCompanionPlugin.DataDirectory,
							DateTime.Now,
							staleAfter).Inspect();
					}
					catch (Exception ex)
					{
						Log.Warn("Settings data health snapshot failed: " + ex);
						_dataHealthSnapshot = new MetaDataHealthSnapshot
						{
							OverallStatus = MetaDataHealthOverallStatus.Error,
							UserMessage = "数据健康状态暂时无法确认",
							DetailLines = new List<string>
							{
								"详细原因已写入开发者日志。"
							}
						};
					}
				}
				return _dataHealthSnapshot;
			}
		}

		private RefreshTaskSnapshot RefreshTaskSnapshot
		{
			get
			{
				if (_refreshTaskSnapshot == null)
				{
					try
					{
						_refreshTaskSnapshot = _refreshTaskService.Inspect();
					}
					catch (Exception ex)
					{
						Log.Warn("Settings refresh task snapshot failed: " + ex);
						var failure = SettingsDiagnostics.BuildUserFacingFailure(
							"检查自动刷新状态",
							"请点击“刷新状态”重试");
						_refreshTaskSnapshot = new RefreshTaskSnapshot
						{
							ScheduledTaskError = ex.GetType().Name,
							ToolsStatus = failure,
							ScheduledTaskStatus = failure,
							LatestLogStatus = failure,
							LatestLogSummaryLines = new List<string>
							{
								"自动刷新状态暂时无法确认；详细原因已写入开发者日志。"
							}
						};
					}
				}
				return _refreshTaskSnapshot;
			}
		}

		private TrainingLogSummary CurrentTrainingLogSummary
		{
			get
			{
				if (_trainingLogSummary == null)
				{
					try
					{
						_trainingLogSummary = SettingsDiagnostics.InspectTrainingLogs(
							MetaCompanionPlugin.DataDirectory);
					}
					catch (Exception ex)
					{
						Log.Warn("Settings training log snapshot failed: " + ex);
						_trainingLogSummary = new TrainingLogSummary
						{
							StatusMessage = "训练记录摘要暂时不可用，原始记录保持不变。",
							ReadIssueMessage =
								"训练记录读取故障；详细原因已写入开发者日志。"
						};
					}
				}
				return _trainingLogSummary;
			}
		}

		public int LocalRecommendationHistoryDays
		{
			get { return Math.Max(0, _config.LocalRecommendationHistoryDays); }
			set
			{
				var normalized = ClampThreshold(value, 0, 365);
				if (_config.LocalRecommendationHistoryDays == normalized)
					return;
				_config.LocalRecommendationHistoryDays = normalized;
				SaveConfig();
			}
		}

		public int LocalRecommendationHistoryMatches
		{
			get { return Math.Max(0, _config.LocalRecommendationHistoryMatches); }
			set
			{
				var normalized = ClampThreshold(value, 0, 10000);
				if (_config.LocalRecommendationHistoryMatches == normalized)
					return;
				_config.LocalRecommendationHistoryMatches = normalized;
				SaveConfig();
			}
		}

		public int RemoteTimeRangeIndex
		{
			get { return RemoteTimeRangeToIndex(_config.PostGamePrimaryTimeRange); }
			set
			{
				var timeRange = RemoteTimeRangeFromIndex(value);
				if (string.Equals(_config.PostGamePrimaryTimeRange, timeRange,
					StringComparison.OrdinalIgnoreCase))
					return;
				_config.PostGamePrimaryTimeRange = timeRange;
				_config.RecommendationScopeSettingsVersion = 1;
				SaveConfig();
			}
		}

		public int RemoteRankRangeIndex
		{
			get { return RemoteRankRangeToIndex(_config.PostGameRankRange); }
			set
			{
				var rankRange = RemoteRankRangeFromIndex(value);
				if (string.Equals(_config.PostGameRankRange, rankRange,
					StringComparison.OrdinalIgnoreCase))
					return;
				_config.PostGameRankRange = rankRange;
				_config.RecommendationScopeSettingsVersion = 1;
				SaveConfig();
			}
		}

		public string RemoteScopeStatus
		{
			get
			{
				var target = "目标口径：HSReplay " +
					FormatRemoteTimeRange(_config.PostGamePrimaryTimeRange) + " / " +
					FormatRemoteRankRange(_config.PostGameRankRange) + "。";
				try
				{
					var source = MetaDashboardSnapshot.Load(MetaCompanionPlugin.DataDirectory).RemoteSource;
					if (source == null || !source.HasData)
						return target + " 当前还没有远端缓存。";
					var matchesTarget = string.Equals(source.EffectiveTimeRange,
						_config.PostGamePrimaryTimeRange, StringComparison.OrdinalIgnoreCase) &&
						string.Equals(source.RankRange, _config.PostGameRankRange,
							StringComparison.OrdinalIgnoreCase);
					return target + " 当前缓存：" + source.SettingsText +
						(matchesTarget ? "。" : "；需刷新后才会切换。");
				}
				catch (Exception ex)
				{
					Log.Warn("Remote scope status failed: " + ex.Message);
					return target + " 当前缓存口径暂时无法读取。";
				}
			}
		}

		public string LocalSampleStatus
		{
			get
			{
				var days = LocalRecommendationHistoryDays == 0
					? "不限天数"
					: "最近 " + LocalRecommendationHistoryDays + " 天";
				var matches = LocalRecommendationHistoryMatches == 0
					? "不限场数"
					: "最近 " + LocalRecommendationHistoryMatches + " 场";
				var cleared = _config.LocalRecommendationHistoryClearedAt > DateTime.MinValue
					? "；当前只统计 " + _config.LocalRecommendationHistoryClearedAt.ToString("MM-dd HH:mm") +
						" 之后的新对局"
					: "";
				return "本地口径：" + days + " + " + matches +
					"，同时生效，且不会越过当前补丁起点" + cleared + "。";
			}
		}

		public bool CanManageLocalMeta
		{
			get { return !_isLocalMetaActionRunning; }
		}

		public string LocalMetaActionStatus
		{
			get { return _localMetaActionStatus; }
		}

		internal static int RemoteTimeRangeToIndex(string value)
		{
			switch ((value ?? "").Trim().ToUpperInvariant())
			{
				case "LAST_1_DAY": return 1;
				case "LAST_3_DAYS": return 2;
				case "CURRENT_PATCH": return 3;
				default: return 0;
			}
		}

		internal static string RemoteTimeRangeFromIndex(int index)
		{
			switch (index)
			{
				case 1: return "LAST_1_DAY";
				case 2: return "LAST_3_DAYS";
				case 3: return "CURRENT_PATCH";
				default: return "LAST_7_DAYS";
			}
		}

		internal static int RemoteRankRangeToIndex(string value)
		{
			switch ((value ?? "").Trim().ToUpperInvariant())
			{
				case "LEGEND": return 1;
				case "TOP_1000_LEGEND": return 2;
				case "DIAMOND_FOUR_THROUGH_DIAMOND_ONE": return 3;
				case "DIAMOND": return 4;
				case "PLATINUM": return 5;
				case "GOLD": return 6;
				case "BRONZE_THROUGH_GOLD": return 7;
				default: return 0;
			}
		}

		internal static string RemoteRankRangeFromIndex(int index)
		{
			switch (index)
			{
				case 1: return "LEGEND";
				case 2: return "TOP_1000_LEGEND";
				case 3: return "DIAMOND_FOUR_THROUGH_DIAMOND_ONE";
				case 4: return "DIAMOND";
				case 5: return "PLATINUM";
				case 6: return "GOLD";
				case 7: return "BRONZE_THROUGH_GOLD";
				default: return "DIAMOND_THROUGH_LEGEND";
			}
		}

		private static string FormatRemoteTimeRange(string value)
		{
			switch ((value ?? "").Trim().ToUpperInvariant())
			{
				case "LAST_1_DAY": return "最近 1 天";
				case "LAST_3_DAYS": return "最近 3 天";
				case "LAST_7_DAYS": return "最近 7 天";
				case "CURRENT_PATCH": return "当前补丁";
				default: return "最近 7 天";
			}
		}

		private static string FormatRemoteRankRange(string value)
		{
			switch ((value ?? "").Trim().ToUpperInvariant())
			{
				case "LEGEND": return "传说";
				case "TOP_1000_LEGEND": return "传说前 1000";
				case "DIAMOND_FOUR_THROUGH_DIAMOND_ONE": return "钻 4—钻 1";
				case "DIAMOND": return "钻石";
				case "PLATINUM": return "白金";
				case "GOLD": return "黄金";
				case "BRONZE_THROUGH_GOLD": return "青铜—黄金";
				default: return "钻石—传说";
			}
		}

		public bool EnableMetaDashboard
		{
			get { return _config.EnableMetaDashboard; }
			set
			{
				_config.EnableMetaDashboard = value;
				SaveConfig();
			}
		}

		public bool EnableMatchHistory
		{
			get { return _config.EnableMatchHistory; }
			set
			{
				_config.EnableMatchHistory = value;
				SaveConfig();
			}
		}

		public bool EnablePredictionTimeline
		{
			get { return _config.EnablePredictionTimeline; }
			set
			{
				_config.EnablePredictionTimeline = value;
				SaveConfig();
			}
		}

		public bool EnablePostGameMetaRefresh
		{
			get { return _config.EnablePostGameMetaRefresh; }
			set
			{
				_config.EnablePostGameMetaRefresh = value;
				SaveConfig();
			}
		}

		public bool EnablePostGameDataRefresh
		{
			get { return _config.EnablePostGameDataRefresh; }
			set
			{
				_config.EnablePostGameDataRefresh = value;
				SaveConfig();
			}
		}

		public bool PostGameDashboardPersistent
		{
			get { return _config.PostGameDashboardPersistent; }
			set
			{
				_config.PostGameDashboardPersistent = value;
				SaveConfig();
			}
		}

		public bool LateGamePanelRightSide
		{
			get { return _config.LateGamePanelRightSide; }
			set
			{
				_config.LateGamePanelRightSide = value;
				SaveConfig();
			}
		}

		public bool FitDeckListToDisplay
		{
			get { return _config.FitDeckListToDisplay; }
			set
			{
				_config.FitDeckListToDisplay = value;
				SaveConfig();
			}
		}

		public bool EnableLateGamePanel
		{
			get { return _config.EnableLateGamePanel; }
			set
			{
				_config.EnableLateGamePanel = value;
				SaveConfig();
			}
		}

		public bool EnableNativeHdtOpponentPredictions
		{
			get { return _config.EnableNativeHdtOpponentPredictions; }
			set
			{
				_config.EnableNativeHdtOpponentPredictions = value;
				SaveConfig();
			}
		}

		public bool EnableLiveAdvisor
		{
			get { return _config.EnableLiveAdvisor; }
			set
			{
				_config.EnableLiveAdvisor = value;
				SaveConfig();
			}
		}

		public bool EnableAdvisorTrainingLog
		{
			get { return _config.EnableAdvisorTrainingLog; }
			set
			{
				_config.EnableAdvisorTrainingLog = value;
				SaveConfig();
			}
		}

		public int AdvisorWorkerBackendModeIndex
		{
			get { return BackendModeToIndex(_config.AdvisorWorkerBackendMode); }
			set
			{
				if (value < 0 || value > 2)
					return;
				var mode = BackendModeFromIndex(value);
				if (_config.AdvisorWorkerBackendMode == mode)
					return;
				_config.AdvisorWorkerBackendMode = mode;
				SaveConfig();
			}
		}

		internal static int BackendModeToIndex(AdvisorWorkerBackendMode mode)
		{
			switch (mode)
			{
				case AdvisorWorkerBackendMode.RustOnly:
					return 1;
				case AdvisorWorkerBackendMode.PythonOnly:
					return 2;
				default:
					return 0;
			}
		}

		internal static AdvisorWorkerBackendMode BackendModeFromIndex(int index)
		{
			switch (index)
			{
				case 1:
					return AdvisorWorkerBackendMode.RustOnly;
				case 2:
					return AdvisorWorkerBackendMode.PythonOnly;
				default:
					return AdvisorWorkerBackendMode.Auto;
			}
		}

		public int LateGameEvidenceThreshold
		{
			get { return _config.LateGameEvidenceThreshold; }
			set
			{
				_config.LateGameEvidenceThreshold = ClampThreshold(value, 1, 30);
				SaveConfig();
			}
		}

		public int LateGameRemainingDeckThreshold
		{
			get { return _config.LateGameRemainingDeckThreshold; }
			set
			{
				_config.LateGameRemainingDeckThreshold = ClampThreshold(value, 0, 30);
				SaveConfig();
			}
		}

		public int LateGamePanelCardLimit
		{
			get { return _config.LateGamePanelCardLimit; }
			set
			{
				_config.LateGamePanelCardLimit = ClampThreshold(value, 4, 30);
				SaveConfig();
			}
		}

		private static int ClampThreshold(int value, int min, int max)
		{
			return Math.Min(max, Math.Max(min, value));
		}

		private static string DescribeDeckDataFile(string label, string path, bool countRows)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			string countText;
			if (countRows)
			{
				var count = File.ReadLines(path)
					.Count(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"));
				countText = count + " 套";
			}
			else
			{
				var countLine = File.ReadLines(path)
					.FirstOrDefault(line => line.StartsWith("# Count:"));
				countText = countLine == null ? "数量未知" :
					countLine.Substring("# Count:".Length).Trim() + " 套";
			}

			return label + "：" + countText + "，更新于 " +
				File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
		}

		private static string GetRecommendationsPath()
		{
			return Path.Combine(
				MetaCompanionPlugin.DataDirectory,
				"Premium",
				"Meta",
				"latest",
				"recommendations.tsv");
		}

		private static string GetPersonalRecommendationsPath()
		{
			return Path.Combine(
				MetaCompanionPlugin.DataDirectory,
				"Premium",
				"Meta",
				"latest",
				"personal_recommendations.tsv");
		}
	}
}

