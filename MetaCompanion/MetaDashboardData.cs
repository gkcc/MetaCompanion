using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanion
{
	internal class MetaDashboardSnapshot
	{
		private const int RecommendationLimit = 3;
		private const int EnvironmentListLimit = 5;
		private const string RecommendationToolTip =
			"\u63a8\u8350\u6765\u81ea HSReplay \u5bf9\u9635\u77e9\u9635\u4e0e\u672c\u5730\u5f53\u524d\u8865\u4e01\u5bf9\u624b\u5206\u5e03\uff1b\u9ed8\u8ba4\u672c\u5730\u6743\u91cd 35%\uff0c\u672c\u5730\u6837\u672c\u6309\u65f6\u95f4\u8870\u51cf\u3002\u4f20\u7edf\u5bf9\u6218\u5165\u53e3\u663e\u793a\u7684\u662f\u672c\u5730\u7f13\u5b58\uff0c\u4e0d\u662f\u5f53\u524d\u5bf9\u624b\u5b9e\u65f6\u8bc6\u522b\u3002";
		private const string EnvironmentToolTip =
			"\u8fd1\u671f\u5bf9\u624b\u5206\u5e03\u6765\u81ea HDT \u672c\u5730\u5386\u53f2\uff0c\u6309\u539f\u59cb\u5c40\u6570\u7edf\u8ba1\u804c\u4e1a\u548c\u5f62\u6001\u9891\u6b21\u3002";
		private const string LastGameToolTip =
			"\u6700\u8fd1\u4e00\u5c40\u6765\u81ea\u672c\u5730\u5bf9\u5c40\u5386\u53f2\uff1b\u5f62\u6001\u7f6e\u4fe1\u5ea6\u7531\u5df2\u89c1\u539f\u59cb\u724c\u4e0e\u5019\u9009\u5206\u652f\u5339\u914d\u5ea6\u8ba1\u7b97\u3002";

		public List<MetaDashboardItem> Recommendations { get; private set; } =
			new List<MetaDashboardItem>();
		public List<MetaDashboardItem> Environment { get; private set; } =
			new List<MetaDashboardItem>();
		public List<MetaDashboardClassDistribution> EnvironmentClasses { get; private set; } =
			new List<MetaDashboardClassDistribution>();
		public MetaDashboardItem LastGame { get; private set; }
		public DateTime? UpdatedAt { get; private set; }

		public bool HasContent =>
			Recommendations.Count > 0 || Environment.Count > 0 || EnvironmentClasses.Count > 0;

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
			var environmentRows = LoadEnvironmentRows(environmentPath);
			snapshot.Environment = BuildEnvironmentItems(environmentRows);
			snapshot.EnvironmentClasses = BuildEnvironmentClasses(environmentRows);
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
				.Take(RecommendationLimit)
				.Select(row =>
					{
						var winRate = Get(row, "expected_win_rate");
						var coverage = Get(row, "coverage_pct");
						var detail = string.IsNullOrWhiteSpace(winRate)
							? Get(row, "player_class")
							: "\u9884\u671f " + winRate + "% / \u8986\u76d6 " + coverage + "%";
						var toolTip = RecommendationToolTip;
						if (!string.IsNullOrWhiteSpace(winRate))
						{
							toolTip += " \u9884\u671f\u80dc\u7387 " + winRate + "%\uff0c\u8986\u76d6 " + coverage + "%\u3002";
						}
						return new MetaDashboardItem(Get(row, "name"), detail, toolTip: toolTip);
					})
				.Where(item => !string.IsNullOrWhiteSpace(item.Title))
				.ToList();
		}

		private static List<MetaDashboardEnvironmentRow> LoadEnvironmentRows(string path)
		{
			return ReadTsv(path)
				.Select(row => new MetaDashboardEnvironmentRow
					{
						Rank = ParseInt(Get(row, "rank")),
						ArchetypeId = ParseInt(Get(row, "archetype_id")),
						Name = Get(row, "name"),
						PlayerClass = Get(row, "player_class"),
						Games = ParseInt(Get(row, "games")),
						WeightedGames = ParseDouble(Get(row, "weighted_games")),
						LocalPct = ParseDouble(Get(row, "local_pct")),
						AvgConfidence = ParseDouble(Get(row, "avg_confidence")),
						Wins = ParseInt(Get(row, "wins")),
						Losses = ParseInt(Get(row, "losses")),
						WinRate = ParseNullableDouble(Get(row, "win_rate"))
					})
				.Where(row => !string.IsNullOrWhiteSpace(row.Name) && row.Games > 0)
				.OrderByDescending(row => row.Games)
				.ThenBy(row => row.Name)
				.ToList();
		}

		private static List<MetaDashboardItem> BuildEnvironmentItems(
			List<MetaDashboardEnvironmentRow> rows)
		{
			var totalGames = rows == null ? 0 : rows.Sum(row => row.Games);
			return rows
				.OrderByDescending(row => row.Games)
				.ThenBy(row => row.Name)
				.Take(EnvironmentListLimit)
				.Select(row => new MetaDashboardItem(
					row.Name,
					row.Games.ToString(CultureInfo.InvariantCulture) + " \u5c40 / " +
						FormatPercent(GetSamplePercent(row.Games, totalGames), 1) + "%",
					toolTip: BuildEnvironmentItemToolTip(row, totalGames)))
				.ToList();
		}

		private static List<MetaDashboardClassDistribution> BuildEnvironmentClasses(
			List<MetaDashboardEnvironmentRow> rows)
		{
			var totalGames = rows.Sum(row => row.Games);
			return rows
				.GroupBy(row => NormalizeClassKey(row.PlayerClass))
				.Select(group =>
					{
						var classPct = group.Sum(row => row.LocalPct);
						var games = group.Sum(row => row.Games);
						var className = GetClassDisplayName(group.Key);
						var segments = group
							.OrderByDescending(row => row.Games)
							.ThenBy(row => row.Name)
							.Select(row => new MetaDashboardArchetypeSegment
							{
								Title = row.Name,
								PlayerClass = group.Key,
								ClassName = className,
								GlobalPct = row.LocalPct,
								SamplePct = GetSamplePercent(row.Games, totalGames),
								ClassPct = classPct > 0 ? row.LocalPct / classPct * 100.0 : 0.0,
								ClassSamplePct = games > 0 ? row.Games / (double)games * 100.0 : 0.0,
								Games = row.Games,
								Wins = row.Wins,
								Losses = row.Losses,
								AvgConfidence = row.AvgConfidence,
								WinRate = row.WinRate,
								ToolTip = BuildEnvironmentSegmentToolTip(
									row, className, totalGames, games)
							})
							.Where(segment => segment.ClassSamplePct > 0)
							.ToList();
						return new MetaDashboardClassDistribution
						{
							PlayerClass = group.Key,
							ClassName = className,
							GlobalPct = classPct,
							SamplePct = totalGames > 0 ? games / (double)totalGames * 100.0 : 0.0,
							Games = games,
							Segments = segments,
							ToolTip = BuildEnvironmentClassToolTip(
								className, totalGames, games, segments)
						};
					})
				.Where(row => row.Games > 0 && row.Segments.Count > 0)
				.OrderByDescending(row => row.Games)
				.ThenBy(row => row.PlayerClass)
				.ToList();
		}

		private static string BuildEnvironmentItemToolTip(
			MetaDashboardEnvironmentRow row,
			int totalGames)
		{
			return row.Name + " (" + GetClassDisplayName(row.PlayerClass) + ")\n" +
				"\u6d41\u6d3e\u6392\u884c\uff1a\u5168\u6837\u672c\u5360\u6bd4 " +
				FormatPercent(GetSamplePercent(row.Games, totalGames), 1) + "% / " +
				row.Games.ToString(CultureInfo.InvariantCulture) + " \u5c40\n" +
				"\u80dc\u8d1f " + row.Wins.ToString(CultureInfo.InvariantCulture) + "-" +
				row.Losses.ToString(CultureInfo.InvariantCulture) +
				(row.WinRate.HasValue ? " / " + FormatPercent(row.WinRate.Value, 2) + "%" : "");
		}

		private static string BuildEnvironmentSegmentToolTip(
			MetaDashboardEnvironmentRow row, string className, int totalGames, int classGames)
		{
			var classShare = classGames > 0 ? row.Games / (double)classGames * 100.0 : 0.0;
			return row.Name + " (" + className + ")\n" +
				"\u6837\u672c " + row.Games.ToString(CultureInfo.InvariantCulture) + " \u5c40\n" +
				"\u804c\u4e1a\u5185\u9891\u6b21 " + FormatPercent(classShare, 1) + "%\n" +
				"\u5168\u6837\u672c\u5360\u6bd4 " + FormatPercent(GetSamplePercent(row.Games, totalGames), 1) + "%\n" +
				"\u80dc\u8d1f " + row.Wins.ToString(CultureInfo.InvariantCulture) + "-" +
				row.Losses.ToString(CultureInfo.InvariantCulture) +
				(row.WinRate.HasValue ? " / " + FormatPercent(row.WinRate.Value, 2) + "%" : "");
		}

		private static string BuildEnvironmentClassToolTip(
			string className,
			int totalGames,
			int games,
			List<MetaDashboardArchetypeSegment> segments)
		{
			var lines = new List<string>
			{
				"\u804c\u4e1a\u5408\u8ba1\uff1a" + className + " " +
					games.ToString(CultureInfo.InvariantCulture) + " \u5c40 / " +
					FormatPercent(GetSamplePercent(games, totalGames), 1) + "%"
			};
			lines.AddRange(segments.Select(segment =>
				segment.Title + ": " +
				segment.Games.ToString(CultureInfo.InvariantCulture) + " \u5c40 / " +
				FormatPercent(segment.ClassSamplePct, 1) + "% \u804c\u4e1a\u5185 / " +
				FormatPercent(segment.SamplePct, 1) + "% \u5168\u6837\u672c"));
			return string.Join("\n", lines);
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
			var title = string.IsNullOrWhiteSpace(archetype) ? "\u6700\u8fd1\u4e00\u5c40" : archetype;
			var detailParts = new List<string>();
			if (!string.IsNullOrWhiteSpace(result) || !string.IsNullOrWhiteSpace(opponent))
			{
				detailParts.Add((result + " vs " + opponent).Trim());
			}
			if (!string.IsNullOrWhiteSpace(confidence))
			{
				detailParts.Add("\u7f6e\u4fe1 " + confidence + "%");
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

		private static int ParseInt(string value)
		{
			int parsed;
			return int.TryParse(
				value,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out parsed)
				? parsed
				: 0;
		}

		private static double ParseDouble(string value)
		{
			double parsed;
			return double.TryParse(
				value,
				NumberStyles.Float,
				CultureInfo.InvariantCulture,
				out parsed)
				? parsed
				: 0.0;
		}

		private static double? ParseNullableDouble(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}
			double parsed;
			return double.TryParse(
				value,
				NumberStyles.Float,
				CultureInfo.InvariantCulture,
				out parsed)
				? (double?)parsed
				: null;
		}

		private static string FormatPercent(double value, int digits)
		{
			return Math.Round(value, digits, MidpointRounding.AwayFromZero)
				.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
		}

		private static double GetSamplePercent(int games, int totalGames)
		{
			return totalGames > 0 ? games / (double)totalGames * 100.0 : 0.0;
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values == null
				? ""
				: values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
		}

		private static string NormalizeClassKey(string playerClass)
		{
			var classKey = (playerClass ?? "")
				.Replace(" ", "")
				.Replace("_", "")
				.Replace("-", "")
				.ToUpperInvariant();
			return string.IsNullOrWhiteSpace(classKey) ? "\u672a\u77e5" : classKey;
		}

		private static string GetClassDisplayName(string playerClass)
		{
			switch (NormalizeClassKey(playerClass))
			{
				case "DEATHKNIGHT":
					return "\u6b7b\u4ea1\u9a91\u58eb";
				case "DEMONHUNTER":
					return "\u6076\u9b54\u730e\u624b";
				case "DRUID":
					return "\u5fb7\u9c81\u4f0a";
				case "HUNTER":
					return "\u730e\u4eba";
				case "MAGE":
					return "\u6cd5\u5e08";
				case "PALADIN":
					return "\u5723\u9a91\u58eb";
				case "PRIEST":
					return "\u7267\u5e08";
				case "ROGUE":
					return "\u6f5c\u884c\u8005";
				case "SHAMAN":
					return "\u8428\u6ee1";
				case "WARLOCK":
					return "\u672f\u58eb";
				case "WARRIOR":
					return "\u6218\u58eb";
				case "\u672a\u77e5":
					return "\u672a\u77e5";
				default:
					return string.IsNullOrWhiteSpace(playerClass) ? "\u672a\u77e5" : playerClass;
			}
		}
	}

	internal class MetaDashboardEnvironmentRow
	{
		public int Rank { get; set; }
		public int ArchetypeId { get; set; }
		public string Name { get; set; } = "";
		public string PlayerClass { get; set; } = "";
		public int Games { get; set; }
		public double WeightedGames { get; set; }
		public double LocalPct { get; set; }
		public double AvgConfidence { get; set; }
		public int Wins { get; set; }
		public int Losses { get; set; }
		public double? WinRate { get; set; }
	}

	internal class MetaDashboardClassDistribution
	{
		public string PlayerClass { get; set; } = "";
		public string ClassName { get; set; } = "";
		public double GlobalPct { get; set; }
		public double SamplePct { get; set; }
		public int Games { get; set; }
		public List<MetaDashboardArchetypeSegment> Segments { get; set; } =
			new List<MetaDashboardArchetypeSegment>();
		public string ToolTip { get; set; } = "";
	}

	internal class MetaDashboardArchetypeSegment
	{
		public string Title { get; set; } = "";
		public string PlayerClass { get; set; } = "";
		public string ClassName { get; set; } = "";
		public double GlobalPct { get; set; }
		public double SamplePct { get; set; }
		public double ClassPct { get; set; }
		public double ClassSamplePct { get; set; }
		public int Games { get; set; }
		public int Wins { get; set; }
		public int Losses { get; set; }
		public double AvgConfidence { get; set; }
		public double? WinRate { get; set; }
		public string ToolTip { get; set; } = "";
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
