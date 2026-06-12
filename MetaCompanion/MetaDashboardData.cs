using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanion
{
	internal class MetaDashboardSnapshot
	{
		private const string RecommendationToolTip =
			"推荐来自 HSReplay 对阵矩阵与本地近 3 天对手分布；默认本地权重 35%。开局显示的是本地缓存，不是当前对手实时识别。";
		private const string EnvironmentToolTip =
			"近期对手分布来自 HDT 本地历史，默认统计近 3 天，并按识别置信度计权。";
		private const string LastGameToolTip =
			"最近一局来自本地对局历史；形态置信度由已见原始牌与候选分支匹配度计算。";

		public List<MetaDashboardItem> Recommendations { get; private set; } =
			new List<MetaDashboardItem>();
		public List<MetaDashboardItem> Environment { get; private set; } =
			new List<MetaDashboardItem>();
		public MetaDashboardItem LastGame { get; private set; }
		public DateTime? UpdatedAt { get; private set; }

		public bool HasContent =>
			Recommendations.Count > 0 || Environment.Count > 0 || LastGame != null;

		public static MetaDashboardSnapshot Load(string dataDirectory)
		{
			var snapshot = new MetaDashboardSnapshot();
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return snapshot;
			}

			var recommendationPath = Path.Combine(
				dataDirectory, "Premium", "Meta", "latest", "personal_recommendations.tsv");
			var environmentPath = Path.Combine(dataDirectory, "local_meta_environment.tsv");
			var gamesPath = Path.Combine(dataDirectory, "local_meta_archetypes.tsv");
			var hdtHistoryPath = Path.Combine(dataDirectory, "hdt_opponent_history.tsv");

			snapshot.Recommendations = LoadRecommendations(recommendationPath);
			snapshot.Environment = LoadEnvironment(environmentPath);
			snapshot.LastGame = LoadLastGame(gamesPath, hdtHistoryPath);
			snapshot.UpdatedAt = new[] { recommendationPath, environmentPath, gamesPath, hdtHistoryPath }
				.Where(File.Exists)
				.Select(File.GetLastWriteTime)
				.OrderByDescending(value => value)
				.Cast<DateTime?>()
				.FirstOrDefault();
			return snapshot;
		}

		private static List<MetaDashboardItem> LoadRecommendations(string path)
		{
			return ReadTsv(path)
				.Take(3)
				.Select(row =>
					{
						var winRate = Get(row, "expected_win_rate");
						var coverage = Get(row, "coverage_pct");
						var detail = string.IsNullOrWhiteSpace(winRate)
							? Get(row, "player_class")
							: "预期 " + winRate + "% / 覆盖 " + coverage + "%";
						var toolTip = RecommendationToolTip;
						if (!string.IsNullOrWhiteSpace(winRate))
						{
							toolTip += " 预期胜率 " + winRate + "%，覆盖 " + coverage + "%。";
						}
						return new MetaDashboardItem(Get(row, "name"), detail, toolTip: toolTip);
					})
				.Where(item => !string.IsNullOrWhiteSpace(item.Title))
				.ToList();
		}

		private static List<MetaDashboardItem> LoadEnvironment(string path)
		{
			return ReadTsv(path)
				.Take(4)
				.Select(row =>
					{
						var pct = Get(row, "local_pct");
						var games = Get(row, "games");
						return new MetaDashboardItem(
							Get(row, "name"),
							(string.IsNullOrWhiteSpace(pct) ? "" : pct + "%") +
							(string.IsNullOrWhiteSpace(games) ? "" : " / " + games + " 局"),
							toolTip: EnvironmentToolTip + " 当前行表示该形态在近期样本中的占比和局数。");
					})
				.Where(item => !string.IsNullOrWhiteSpace(item.Title))
				.ToList();
		}

		private static MetaDashboardItem LoadLastGame(string localMetaPath, string hdtHistoryPath)
		{
			var localRow = ReadTsv(localMetaPath).LastOrDefault();
			var hdtRow = FindMatchingHdtRow(hdtHistoryPath, localRow);
			if (localRow == null && hdtRow == null)
			{
				return null;
			}

			var archetype = Get(localRow, "predicted_archetype");
			var result = FirstNonEmpty(Get(localRow, "result"), Get(hdtRow, "result"));
			var opponent = FirstNonEmpty(Get(localRow, "opponent_hero"), Get(hdtRow, "opponent_hero"));
			var confidence = Get(localRow, "confidence_pct");
			var title = string.IsNullOrWhiteSpace(archetype) ? "最近一局" : archetype;
			var detailParts = new List<string>();
			if (!string.IsNullOrWhiteSpace(result) || !string.IsNullOrWhiteSpace(opponent))
			{
				detailParts.Add((result + " vs " + opponent).Trim());
			}
			if (!string.IsNullOrWhiteSpace(confidence))
			{
				detailParts.Add("置信 " + confidence + "%");
			}

			return new MetaDashboardItem(
				title,
				string.Join(" / ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part))),
				Get(hdtRow, "hsreplay_url"),
				FirstNonEmpty(Get(hdtRow, "replay_path"), ResolveReplayPath(Get(hdtRow, "replay_file"))),
				LastGameToolTip);
		}

		private static Dictionary<string, string> FindMatchingHdtRow(
			string hdtHistoryPath, Dictionary<string, string> localRow)
		{
			var rows = ReadTsv(hdtHistoryPath);
			if (rows.Count == 0)
			{
				return null;
			}

			var gameId = Get(localRow, "game_id");
			if (!string.IsNullOrWhiteSpace(gameId))
			{
				var match = rows.LastOrDefault(row => Get(row, "game_id") == gameId);
				if (match != null)
				{
					return match;
				}
			}

			return rows.LastOrDefault();
		}

		private static string ResolveReplayPath(string replayFile)
		{
			if (string.IsNullOrWhiteSpace(replayFile))
			{
				return "";
			}

			var path = Path.Combine(
				Hearthstone_Deck_Tracker.Config.AppDataPath,
				"Replays",
				replayFile);
			return File.Exists(path) ? path : "";
		}

		private static List<Dictionary<string, string>> ReadTsv(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return new List<Dictionary<string, string>>();
			}

			var lines = File.ReadAllLines(path, Encoding.UTF8)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.ToList();
			if (lines.Count < 2)
			{
				return new List<Dictionary<string, string>>();
			}

			var headers = lines[0].Split('\t');
			return lines
				.Skip(1)
				.Select(line =>
					{
						var values = line.Split('\t');
						var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
						for (var index = 0; index < headers.Length; index++)
						{
							row[headers[index]] = index < values.Length ? values[index] : "";
						}
						return row;
					})
				.ToList();
		}

		private static string Get(Dictionary<string, string> row, string key)
		{
			if (row == null || key == null || !row.ContainsKey(key))
			{
				return "";
			}
			return row[key] ?? "";
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values == null
				? ""
				: values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
		}
	}

	internal class MetaDashboardItem
	{
		public MetaDashboardItem(
			string title, string detail, string hsReplayUrl = "", string replayPath = "",
			string toolTip = "")
		{
			Title = title ?? "";
			Detail = detail ?? "";
			HsReplayUrl = hsReplayUrl ?? "";
			ReplayPath = replayPath ?? "";
			ToolTip = toolTip ?? "";
		}

		public string Title { get; private set; }
		public string Detail { get; private set; }
		public string HsReplayUrl { get; private set; }
		public string ReplayPath { get; private set; }
		public string ToolTip { get; private set; }
	}
}
