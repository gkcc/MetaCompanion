using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MetaCompanion
{
	internal static class LocalMetaDataService
	{
		internal static LocalMetaDataActionResult ClearLocalSamples(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			config = config ?? new PluginConfig();
			config.LocalRecommendationHistoryClearedAt = now;
			Directory.CreateDirectory(dataDirectory);
			File.WriteAllText(
				QuickDashboardRefresher.GetLocalHistoryClearMarkerPath(dataDirectory),
				now.ToString("o", CultureInfo.InvariantCulture));
			DeleteDerivedFiles(dataDirectory);
			var refresh = QuickDashboardRefresher.Refresh(config, dataDirectory, now);
			return new LocalMetaDataActionResult
			{
				LocalMatchCount = refresh.LocalMatchCount,
				RecommendationCount = refresh.RecommendationCount,
				Message = "本地推荐样本已清空；HDT 原始对战历史仍保留，可随时恢复。"
			};
		}

		internal static LocalMetaDataActionResult RestoreCurrentPatchHistory(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			config = config ?? new PluginConfig();
			// “恢复当前补丁全部历史”与普通的筛选重建是两个不同动作。
			// 恢复时必须先移除天数和场数窗口，否则界面虽然提示恢复完成，
			// 实际参与本地加权的仍可能只是最近几天或几场。
			config.LocalRecommendationHistoryDays = 0;
			config.LocalRecommendationHistoryMatches = 0;
			config.LocalRecommendationHistoryClearedAt = DateTime.MinValue;
			var clearMarkerPath = QuickDashboardRefresher.GetLocalHistoryClearMarkerPath(dataDirectory);
			if (File.Exists(clearMarkerPath))
			{
				File.Delete(clearMarkerPath);
			}
			DeleteDerivedFiles(dataDirectory);
			var refresh = QuickDashboardRefresher.Refresh(config, dataDirectory, now);
			return new LocalMetaDataActionResult
			{
				LocalMatchCount = refresh.LocalMatchCount,
				RecommendationCount = refresh.RecommendationCount,
				Message = "已从 HDT 对战历史恢复当前补丁全部可用数据，共 " +
					refresh.LocalMatchCount.ToString(CultureInfo.InvariantCulture) +
					" 局；本地筛选已设为不限天数、不限场数。"
			};
		}

		internal static LocalMetaDataActionResult RebuildWithCurrentFilters(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			config = config ?? new PluginConfig();
			DeleteDerivedFiles(dataDirectory);
			var refresh = QuickDashboardRefresher.Refresh(config, dataDirectory, now);
			return new LocalMetaDataActionResult
			{
				LocalMatchCount = refresh.LocalMatchCount,
				RecommendationCount = refresh.RecommendationCount,
				Message = "本地样本筛选已应用，当前采用 " +
					refresh.LocalMatchCount.ToString(CultureInfo.InvariantCulture) + " 局。"
			};
		}

		internal static IReadOnlyList<string> GetDerivedFilePaths(string dataDirectory)
		{
			var root = dataDirectory ?? "";
			var recommendationPath = QuickDashboardRefresher.GetRecommendationsPath(root);
			return new[]
			{
				Path.Combine(root, "local_meta_environment.tsv"),
				Path.Combine(root, "local_meta_archetypes.tsv"),
				Path.Combine(root, "local_meta_summary.json"),
				Path.Combine(root, "hdt_opponent_history.tsv"),
				recommendationPath,
				Path.ChangeExtension(recommendationPath, ".json")
			};
		}

		private static void DeleteDerivedFiles(string dataDirectory)
		{
			foreach (var path in GetDerivedFilePaths(dataDirectory))
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}
	}

	internal class LocalMetaDataActionResult
	{
		public int LocalMatchCount { get; set; }
		public int RecommendationCount { get; set; }
		public string Message { get; set; } = "";
	}
}
