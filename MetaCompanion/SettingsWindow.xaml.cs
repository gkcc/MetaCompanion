using MahApps.Metro.Controls;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace MetaCompanion
{
	public partial class SettingsWindow : MetroWindow
	{
		private readonly PluginConfig _config;

		public SettingsWindow(PluginConfig config)
		{
			_config = config;
			InitializeComponent();
			DataContext = this;
		}

		private void ButtonReadme_Click(object sender, RoutedEventArgs e)
		{
			var readme = Path.Combine(
				Path.GetDirectoryName(typeof(SettingsWindow).Assembly.Location) ?? "",
				"README.md");
			System.Diagnostics.Process.Start(File.Exists(readme)
				? readme
				: "https://github.com/");
		}

		private void ButtonOpenData_Click(object sender, RoutedEventArgs e)
		{
			Directory.CreateDirectory(MetaCompanionPlugin.DataDirectory);
			System.Diagnostics.Process.Start(MetaCompanionPlugin.DataDirectory);
		}

		private void ButtonOpenHistory_Click(object sender, RoutedEventArgs e)
		{
			OpenLocalFile(
				MatchHistoryRecorder.GetHistoryPath(MetaCompanionPlugin.DataDirectory),
				MatchHistoryRecorder.HistoryHeader);
		}

		private void ButtonOpenTimeline_Click(object sender, RoutedEventArgs e)
		{
			OpenLocalFile(
				MatchHistoryRecorder.GetTimelinePath(MetaCompanionPlugin.DataDirectory),
				MatchHistoryRecorder.TimelineHeader);
		}

		private void ButtonOpenCorrections_Click(object sender, RoutedEventArgs e)
		{
			OpenLocalFile(
				MatchHistoryRecorder.GetCorrectionsPath(MetaCompanionPlugin.DataDirectory),
				"match_id\tcorrected_archetype\tcorrected_result\tnotes");
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

			System.Diagnostics.Process.Start(recommendationsPath);
		}

		private void ButtonResetLayout_Click(object sender, RoutedEventArgs e)
		{
			_config.ResetOverlayPositions();
			MessageBox.Show(
				"浮窗位置已重置。下一次显示时会回到默认位置。",
				"Meta Companion",
				MessageBoxButton.OK,
				MessageBoxImage.Information);
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
					return deckStatus + " | " + branchStatus;
				}
				if (deckStatus != null)
				{
					return deckStatus;
				}
				if (branchStatus != null)
				{
					return "未找到 HSReplay 牌组库，当前使用 " + branchStatus;
				}
				return "未找到 HSReplay 牌组快照。当前仅显示已有本地缓存。";
			}
		}

		public string RecommendationStatus
		{
			get
			{
				var personalPath = GetPersonalRecommendationsPath();
				var recommendationsPath = File.Exists(personalPath)
					? personalPath
					: GetRecommendationsPath();
				if (!File.Exists(recommendationsPath))
				{
					return "推荐结果: 未生成";
				}

				var count = Math.Max(0, File.ReadLines(recommendationsPath).Count() - 1);
				var source = recommendationsPath == personalPath ? "个人" : "HSReplay";
				return "推荐结果: " + source + " Top " + count + " | 更新于 " +
					File.GetLastWriteTime(recommendationsPath).ToString("yyyy-MM-dd HH:mm");
			}
		}

		public string PremiumStatus
		{
			get
			{
				var matrixPath = PostGameMetaRefresher.GetMetaMatrixPath(MetaCompanionPlugin.DataDirectory);
				var summaryPath = PostGameMetaRefresher.GetMetaSummaryPath(MetaCompanionPlugin.DataDirectory);
				if (!File.Exists(matrixPath) && !File.Exists(summaryPath))
				{
					return "对阵矩阵: 未同步";
				}

				var newest = new[] {matrixPath, summaryPath}
					.Where(File.Exists)
					.Select(File.GetLastWriteTime)
					.OrderByDescending(time => time)
					.First();
				return "对阵矩阵: 更新于 " + newest.ToString("yyyy-MM-dd HH:mm");
			}
		}

		public bool EnableMetaDashboard
		{
			get { return _config.EnableMetaDashboard; }
			set
			{
				_config.EnableMetaDashboard = value;
				_config.Save();
			}
		}

		public bool EnableMatchHistory
		{
			get { return _config.EnableMatchHistory; }
			set
			{
				_config.EnableMatchHistory = value;
				_config.Save();
			}
		}

		public bool EnablePredictionTimeline
		{
			get { return _config.EnablePredictionTimeline; }
			set
			{
				_config.EnablePredictionTimeline = value;
				_config.Save();
			}
		}

		public bool EnablePostGameMetaRefresh
		{
			get { return _config.EnablePostGameMetaRefresh; }
			set
			{
				_config.EnablePostGameMetaRefresh = value;
				_config.Save();
			}
		}

		public bool EnablePostGameDataRefresh
		{
			get { return _config.EnablePostGameDataRefresh; }
			set
			{
				_config.EnablePostGameDataRefresh = value;
				_config.Save();
			}
		}

		public bool PostGameDashboardPersistent
		{
			get { return _config.PostGameDashboardPersistent; }
			set
			{
				_config.PostGameDashboardPersistent = value;
				_config.Save();
			}
		}

		public bool LateGamePanelRightSide
		{
			get { return _config.LateGamePanelRightSide; }
			set
			{
				_config.LateGamePanelRightSide = value;
				_config.Save();
			}
		}

		public bool FitDeckListToDisplay
		{
			get { return _config.FitDeckListToDisplay; }
			set
			{
				_config.FitDeckListToDisplay = value;
				_config.Save();
			}
		}

		public bool EnableLateGamePanel
		{
			get { return _config.EnableLateGamePanel; }
			set
			{
				_config.EnableLateGamePanel = value;
				_config.Save();
			}
		}

		public bool EnableNativeHdtOpponentPredictions
		{
			get { return _config.EnableNativeHdtOpponentPredictions; }
			set
			{
				_config.EnableNativeHdtOpponentPredictions = value;
				_config.Save();
			}
		}

		public int LateGameEvidenceThreshold
		{
			get { return _config.LateGameEvidenceThreshold; }
			set
			{
				_config.LateGameEvidenceThreshold = ClampThreshold(value, 1, 30);
				_config.Save();
			}
		}

		public int LateGameRemainingDeckThreshold
		{
			get { return _config.LateGameRemainingDeckThreshold; }
			set
			{
				_config.LateGameRemainingDeckThreshold = ClampThreshold(value, 0, 30);
				_config.Save();
			}
		}

		public int LateGamePanelCardLimit
		{
			get { return _config.LateGamePanelCardLimit; }
			set
			{
				_config.LateGamePanelCardLimit = ClampThreshold(value, 4, 30);
				_config.Save();
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

			return label + ": " + countText + " 更新于 " +
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

