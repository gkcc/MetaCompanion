using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	internal class MetaDashboardSnapshot
	{
		private const int RecommendationLimit = 3;
		private const int EnvironmentListLimit = 5;
		private const string ReadyStatus =
			"\u6b63\u5e38\uff1a\u63a8\u8350\u6570\u636e\u5df2\u52a0\u8f7d\u3002";
		private const string EmptyStatus =
			"\u63d0\u793a\uff1a\u6682\u65f6\u6ca1\u6709\u53ef\u7528\u63a8\u8350\u6570\u636e\u3002\u8bf7\u5148\u8fd0\u884c\u4e00\u6b21\u6570\u636e\u66f4\u65b0\u3002";
		private const string ReadIssueStatus =
			"\u9700\u5904\u7406\uff1a\u90e8\u5206\u63a8\u8350\u6570\u636e\u8bfb\u53d6\u5931\u8d25\u3002\u8bf7\u5728\u8bbe\u7f6e\u9875\u5237\u65b0\u6216\u91cd\u65b0\u751f\u6210\u6570\u636e\u3002";
		private const string RecommendationToolTip =
			"\u63a8\u8350\u6765\u81ea HSReplay \u5bf9\u9635\u77e9\u9635\u4e0e\u672c\u5730\u5f53\u524d\u8865\u4e01\u5bf9\u624b\u5206\u5e03\uff1b\u5bf9\u9635\u7387\u4f7f\u7528\u8fde\u7eed\u8d1d\u53f6\u65af\u6536\u7f29\uff0c\u672c\u5730\u5f71\u54cd\u4f1a\u968f\u6709\u6548\u6837\u672c\u81ea\u52a8\u589e\u957f\u3002\u4f20\u7edf\u5bf9\u6218\u5165\u53e3\u663e\u793a\u7684\u662f\u672c\u5730\u7f13\u5b58\uff0c\u4e0d\u662f\u5f53\u524d\u5bf9\u624b\u5b9e\u65f6\u8bc6\u522b\u3002";
		private const string EnvironmentToolTip =
			"\u8fd1\u671f\u5bf9\u624b\u5206\u5e03\u6765\u81ea HDT \u672c\u5730\u5386\u53f2\uff1b\u6837\u672c\u7a97\u53e3\u4f18\u5148\u4f7f\u7528\u5f53\u524d\u8865\u4e01\uff0c\u8fd9\u91cc\u6309\u539f\u59cb\u5c40\u6570\u7edf\u8ba1\u804c\u4e1a\u548c\u5f62\u6001\u9891\u6b21\u3002";
		private const string LastGameToolTip =
			"\u6700\u8fd1\u4e00\u5c40\u6765\u81ea\u672c\u5730\u5bf9\u5c40\u5386\u53f2\uff1b\u5f62\u6001\u7f6e\u4fe1\u5ea6\u7531\u5df2\u89c1\u539f\u59cb\u724c\u4e0e\u5019\u9009\u5206\u652f\u5339\u914d\u5ea6\u8ba1\u7b97\u3002";
		private static readonly Regex CandidateConfidenceRegex =
			new Regex(@"(?<confidence>\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
		private static readonly Regex CandidateScoreRegex =
			new Regex(@"\bscore=(?<score>-?\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex CandidateBranchCountRegex =
			new Regex(@"\b(?:branchCount|branches)=(?<branches>-?\d+)",
				RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex EvidenceCardCopyRegex =
			new Regex(@"^(?<name>.+?)(?:\s*[x×](?<count>\d+))?$",
				RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public List<MetaDashboardItem> Recommendations { get; private set; } =
			new List<MetaDashboardItem>();
		public List<MetaDashboardItem> Environment { get; private set; } =
			new List<MetaDashboardItem>();
		public List<MetaDashboardClassDistribution> EnvironmentClasses { get; private set; } =
			new List<MetaDashboardClassDistribution>();
		public MetaDashboardLastGame LastGame { get; private set; }
		public DateTime? UpdatedAt { get; private set; }
		public MetaDashboardRemoteSource RemoteSource { get; private set; } =
			MetaDashboardRemoteSource.Empty;
		public bool HasReadIssue { get; private set; }
		public string UserStatusMessage { get; private set; } = EmptyStatus;

		public bool HasContent =>
			Recommendations.Count > 0 || Environment.Count > 0 || EnvironmentClasses.Count > 0;

		public static MetaDashboardSnapshot Load(string dataDirectory)
		{
			var snapshot = new MetaDashboardSnapshot();
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return snapshot;
			}

			string recommendationPath;
			string environmentPath;
			string gamesPath;
			string hdtHistoryPath;
			string branchPath;
			string remoteSummaryPath;
			string remoteManifestPath;
			try
			{
				recommendationPath = Path.Combine(
					dataDirectory, "Premium", "Meta", "latest", "personal_recommendations.tsv");
				environmentPath = Path.Combine(dataDirectory, "local_meta_environment.tsv");
				gamesPath = Path.Combine(dataDirectory, "local_meta_archetypes.tsv");
				hdtHistoryPath = Path.Combine(dataDirectory, "hdt_opponent_history.tsv");
				branchPath = Path.Combine(dataDirectory, "archetype_deck_branches.tsv");
				remoteSummaryPath = Path.Combine(
					dataDirectory, "Premium", "Meta", "latest", "summary.json");
				remoteManifestPath = Path.Combine(
					dataDirectory, "Premium", "Meta", "latest", "manifest.json");
			}
			catch (Exception ex)
			{
				RecordReadIssue(snapshot, "dashboard data paths", ex);
				snapshot.UpdateUserStatus();
				return snapshot;
			}

			TryLoadSection(snapshot, "recommendations", () =>
				snapshot.Recommendations = LoadRecommendations(
					recommendationPath, branchPath, remoteSummaryPath));
			TryLoadSection(snapshot, "local environment", () =>
				{
					var environmentRows = LoadEnvironmentRows(environmentPath);
					snapshot.Environment = BuildEnvironmentItems(environmentRows);
					snapshot.EnvironmentClasses = BuildEnvironmentClasses(environmentRows);
				});
			TryLoadSection(snapshot, "last game", () =>
				snapshot.LastGame = LoadLastGame(gamesPath, hdtHistoryPath));
			TryLoadSection(snapshot, "update time", () =>
				snapshot.UpdatedAt = new[]
					{
						recommendationPath, environmentPath, gamesPath, hdtHistoryPath
					}
					.Where(File.Exists)
					.Select(File.GetLastWriteTime)
					.OrderByDescending(value => value)
					.Cast<DateTime?>()
					.FirstOrDefault());
			TryLoadSection(snapshot, "remote source summary", () =>
				snapshot.RemoteSource = MetaDashboardRemoteSource.Load(
					remoteSummaryPath, remoteManifestPath));
			snapshot.UpdateUserStatus();
			return snapshot;
		}

		private static void TryLoadSection(
			MetaDashboardSnapshot snapshot, string section, Action loadAction)
		{
			try
			{
				loadAction();
			}
			catch (Exception ex)
			{
				RecordReadIssue(snapshot, section, ex);
			}
		}

		private static void RecordReadIssue(
			MetaDashboardSnapshot snapshot, string section, Exception exception)
		{
			snapshot.HasReadIssue = true;
			Log.Warn("Meta dashboard " + section + " read failed: " + exception);
		}

		private void UpdateUserStatus()
		{
			UserStatusMessage = HasReadIssue
				? ReadIssueStatus
				: HasContent
					? ReadyStatus
					: EmptyStatus;
		}

		private static List<MetaDashboardItem> LoadRecommendations(
			string path,
			string branchPath,
			string remoteSummaryPath)
		{
			var representativeDecks = LoadRepresentativeDecks(branchPath, remoteSummaryPath);
			return ReadTsv(path)
				.Take(RecommendationLimit)
				.Select(row =>
					{
						var archetypeId = ParseInt(Get(row, "archetype_id"));
						var winRate = Get(row, "expected_win_rate");
						var coverage = Get(row, "coverage_pct");
						var low = Get(row, "expected_win_rate_low_90");
						var high = Get(row, "expected_win_rate_high_90");
						var probabilityBest = Get(row, "probability_best_pct");
						var tier = Get(row, "tier");
						var hasPosterior = !string.IsNullOrWhiteSpace(low) &&
							!string.IsNullOrWhiteSpace(high);
						var detail = string.IsNullOrWhiteSpace(winRate)
							? GetClassDisplayName(Get(row, "player_class"))
							: hasPosterior
								? "\u9884\u671f " + winRate + "% / \u6700\u4f18\u6982\u7387 " + probabilityBest + "% / T" + tier
								: "\u9884\u671f " + winRate + "% / \u8986\u76d6 " + coverage + "%";
						var toolTip = RecommendationToolTip;
						if (!string.IsNullOrWhiteSpace(winRate))
						{
							toolTip += " \u9884\u671f\u80dc\u7387 " + winRate + "%\uff0c\u6570\u636e\u8d21\u732e\u8986\u76d6 " + coverage + "%\u3002";
						}
						if (hasPosterior)
						{
							toolTip += " 90%\u533a\u95f4 " + low + "%\u2013" + high + "%\uff1b\u8fd1\u4f3c\u6700\u4f18\u6982\u7387 " +
								probabilityBest + "%\uff0c\u63a8\u8350\u68af\u961f T" + tier + "\u3002";
						}

						var highestWinRateDeckCode = Get(row, "highest_winrate_deck_code");
						var highestWinRateDeckRate = Get(row, "highest_winrate_deck_win_rate");
						var highestWinRateDeckGames = ParseInt(Get(row, "highest_winrate_deck_games"));
						var mostPopularDeckCode = Get(row, "most_popular_deck_code");
						var mostPopularDeckRate = Get(row, "most_popular_deck_win_rate");
						var mostPopularDeckGames = ParseInt(Get(row, "most_popular_deck_games"));
						RepresentativeDeckPair pair;
						if (archetypeId > 0 && representativeDecks.TryGetValue(archetypeId, out pair))
						{
							if (string.IsNullOrWhiteSpace(highestWinRateDeckCode))
							{
								highestWinRateDeckCode = pair.HighestWinRate.DeckCode;
								highestWinRateDeckRate = FormatPercent(pair.HighestWinRate.WinRate, 2);
								highestWinRateDeckGames = pair.HighestWinRate.Games;
							}
							if (string.IsNullOrWhiteSpace(mostPopularDeckCode))
							{
								mostPopularDeckCode = pair.MostPopular.DeckCode;
								mostPopularDeckRate = FormatPercent(pair.MostPopular.WinRate, 2);
								mostPopularDeckGames = pair.MostPopular.Games;
							}
						}

						if (!string.IsNullOrWhiteSpace(highestWinRateDeckCode))
						{
							toolTip += " 最高胜率牌组：" + highestWinRateDeckRate + "% / " +
								highestWinRateDeckGames.ToString(CultureInfo.InvariantCulture) + " 局。";
						}
						if (!string.IsNullOrWhiteSpace(mostPopularDeckCode))
						{
							toolTip += " 最高使用量牌组：" + mostPopularDeckGames.ToString(CultureInfo.InvariantCulture) +
								" 局 / 胜率 " + mostPopularDeckRate + "%。";
						}

						return new MetaDashboardItem(
							Get(row, "name"),
							detail,
							toolTip: toolTip,
							highestWinRateDeckCode: highestWinRateDeckCode,
							mostPopularDeckCode: mostPopularDeckCode);
					})
				.Where(item => !string.IsNullOrWhiteSpace(item.Title))
				.ToList();
		}

		private static Dictionary<int, RepresentativeDeckPair> LoadRepresentativeDecks(
			string path,
			string remoteSummaryPath)
		{
			var result = new Dictionary<int, RepresentativeDeckPair>();
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return result;
			}

			Dictionary<string, object> summary = null;
			if (!string.IsNullOrWhiteSpace(remoteSummaryPath) && File.Exists(remoteSummaryPath))
			{
				try
				{
					summary = new JavaScriptSerializer().DeserializeObject(
						File.ReadAllText(remoteSummaryPath, Encoding.UTF8)) as
						Dictionary<string, object>;
				}
				catch
				{
					summary = null;
				}
			}
			object summaryTimeRange;
			object summaryRankRange;
			var expectedTimeRange = summary != null && summary.TryGetValue(
				"time_range", out summaryTimeRange)
				? Convert.ToString(summaryTimeRange, CultureInfo.InvariantCulture)
				: "";
			var expectedRankRange = summary != null && summary.TryGetValue(
				"rank_range", out summaryRankRange)
				? Convert.ToString(summaryRankRange, CultureInfo.InvariantCulture)
				: "";
			var comments = File.ReadLines(path, Encoding.UTF8)
				.Take(24)
				.Where(line => line.StartsWith("#", StringComparison.Ordinal))
				.ToList();
			var branchTimeRange = ReadCommentValue(comments, "# CandidateTimeRange:");
			var branchRankRange = ReadCommentValue(comments, "# RankRange:");
			if (string.IsNullOrWhiteSpace(expectedTimeRange) ||
				string.IsNullOrWhiteSpace(expectedRankRange) ||
				!string.Equals(branchTimeRange, expectedTimeRange, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(branchRankRange, expectedRankRange, StringComparison.OrdinalIgnoreCase))
			{
				return result;
			}

			var candidates = new List<RepresentativeDeckCandidate>();
			foreach (var line in File.ReadLines(path, Encoding.UTF8))
			{
				if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
				{
					continue;
				}
				var values = line.Split('\t');
				if (values.Length < 8)
				{
					continue;
				}
				var archetypeId = ParseInt(values[3]);
				var deckCode = values[1] == null ? "" : values[1].Trim();
				if (archetypeId <= 0 || string.IsNullOrWhiteSpace(deckCode))
				{
					continue;
				}
				candidates.Add(new RepresentativeDeckCandidate
				{
					ArchetypeId = archetypeId,
					DeckCode = deckCode,
					DeckId = values[2] == null ? "" : values[2].Trim(),
					Games = Math.Max(0, ParseInt(values[6])),
					WinRate = ParseDouble(values[7])
				});
			}

			foreach (var group in candidates.GroupBy(candidate => candidate.ArchetypeId))
			{
				result[group.Key] = new RepresentativeDeckPair
				{
					HighestWinRate = group
						.OrderByDescending(candidate => candidate.WinRate)
						.ThenByDescending(candidate => candidate.Games)
						.ThenBy(candidate => candidate.DeckId)
						.First(),
					MostPopular = group
						.OrderByDescending(candidate => candidate.Games)
						.ThenByDescending(candidate => candidate.WinRate)
						.ThenBy(candidate => candidate.DeckId)
						.First()
				};
			}
			return result;
		}

		private static string ReadCommentValue(IEnumerable<string> lines, string prefix)
		{
			var line = (lines ?? Enumerable.Empty<string>()).FirstOrDefault(value =>
				value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			return line == null ? "" : line.Substring(prefix.Length).Trim();
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

		private static MetaDashboardLastGame LoadLastGame(string localMetaPath, string hdtHistoryPath)
		{
			var localRow = GetLatestMatchRow(ReadTsv(localMetaPath));
			var hdtRows = ReadTsv(hdtHistoryPath);
			var latestHdtRow = GetLatestMatchRow(hdtRows);
			var hdtRow = FindMatchingHdtRow(hdtRows, localRow);
			if (latestHdtRow != null && hdtRow == null && !IsLocalRowNewer(localRow, latestHdtRow))
			{
				// The derived local-meta cache can lag behind HDT immediately after a game. Never
				// combine the previous prediction with the latest HDT opponent in that window.
				localRow = null;
				hdtRow = latestHdtRow;
			}
			if (localRow == null && hdtRow == null)
			{
				return null;
			}

			var archetype = Get(localRow, "predicted_archetype");
			var result = FirstNonEmpty(Get(localRow, "result"), Get(hdtRow, "result"));
			var opponent = FirstNonEmpty(
				Get(localRow, "opponent_class"),
				Get(localRow, "opponent_hero"),
				Get(hdtRow, "opponent_class"),
				Get(hdtRow, "opponent_hero"));
			var confidence = Get(localRow, "confidence_pct");
			var legacyCandidates = ParseCandidateArchetypes(Get(localRow, "candidate_archetypes"));
			List<MetaDashboardCandidate> preciseCandidates;
			double unknownProbabilityPct;
			var hasPreciseRecognition = TryParseRecognitionDistribution(
				Get(localRow, "archetype_distribution_json"),
				legacyCandidates,
				out preciseCandidates,
				out unknownProbabilityPct);
			var candidates = hasPreciseRecognition ? preciseCandidates : legacyCandidates;
			var recognitionTier = hasPreciseRecognition
				? Get(localRow, "recognition_tier")
				: "";
			if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(archetype))
			{
				candidates.Add(new MetaDashboardCandidate
				{
					Name = archetype,
					ConfidencePercent = ParseInt(confidence)
				});
			}
			var keyEvidenceCards = ParseEvidenceCardList(Get(localRow, "key_evidence_cards"));
			var opponentClassName = GetClassDisplayName(opponent);
			if (hasPreciseRecognition)
			{
				archetype = candidates.Count > 0 ? candidates[0].Name : "";
			}
			var title = hasPreciseRecognition &&
				string.Equals(recognitionTier, "mixed", StringComparison.OrdinalIgnoreCase)
				? opponentClassName + "\u00b7\u6d41\u6d3e\u5f85\u5b9a"
				: hasPreciseRecognition &&
					(string.Equals(recognitionTier, "unknown", StringComparison.OrdinalIgnoreCase) ||
						candidates.Count == 0)
					? string.IsNullOrWhiteSpace(opponent)
						? "\u6700\u8fd1\u4e00\u5c40"
						: opponentClassName + " \u672a\u8bc6\u522b"
					: string.IsNullOrWhiteSpace(archetype)
						? string.IsNullOrWhiteSpace(opponent)
							? "\u6700\u8fd1\u4e00\u5c40"
							: opponentClassName + " \u672a\u8bc6\u522b"
						: FormatArchetypeDisplayName(archetype);
			var detailParts = new List<string>();
			var matchSummary = BuildMatchSummary(result, opponentClassName,
				!string.IsNullOrWhiteSpace(opponent));
			if (!string.IsNullOrWhiteSpace(matchSummary))
			{
				detailParts.Add(matchSummary);
			}
			if (hasPreciseRecognition)
			{
				var topProbability = candidates.Count == 0 ? 0 : candidates[0].ConfidencePercent;
				detailParts.Add("\u6700\u9ad8 " + topProbability.ToString(CultureInfo.InvariantCulture) +
					"% / \u672a\u77e5 " + FormatPercent(unknownProbabilityPct, 0) + "%" +
					FormatRecognitionTier(recognitionTier));
			}
			else if (!string.IsNullOrWhiteSpace(confidence))
			{
				detailParts.Add("\u7f6e\u4fe1 " + confidence + "%");
			}

			var confidenceValue = hasPreciseRecognition
				? candidates.Count == 0 ? 0 : candidates[0].ConfidencePercent
				: ParseInt(confidence);
			var toolTip = BuildLastGameToolTip(
				candidates,
				keyEvidenceCards,
				confidenceValue,
				string.IsNullOrWhiteSpace(archetype) && !string.IsNullOrWhiteSpace(opponent),
				recognitionTier,
				unknownProbabilityPct);
			return new MetaDashboardLastGame(
				title,
				string.Join(" / ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part))),
				Get(hdtRow, "hsreplay_url"),
				FirstNonEmpty(Get(hdtRow, "replay_path"), ResolveReplayPath(Get(hdtRow, "replay_file"))),
				toolTip)
			{
				MatchId = Get(localRow, "game_id"),
				ConfidencePercent = confidenceValue,
				RecognitionTier = recognitionTier,
				UnknownProbabilityPercent = unknownProbabilityPct,
				Candidates = candidates,
				KeyEvidenceCards = keyEvidenceCards
			};
		}

		private static bool TryParseRecognitionDistribution(
			string value,
			IEnumerable<MetaDashboardCandidate> legacyCandidates,
			out List<MetaDashboardCandidate> candidates,
			out double unknownProbabilityPct)
		{
			candidates = new List<MetaDashboardCandidate>();
			unknownProbabilityPct = 0.0;
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}

			try
			{
				var parsed = new JavaScriptSerializer().DeserializeObject(value) as IEnumerable;
				if (parsed == null || parsed is string)
				{
					return false;
				}
				var legacyByName = (legacyCandidates ?? Enumerable.Empty<MetaDashboardCandidate>())
					.GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(group => group.Key, group => group.First(),
						StringComparer.OrdinalIgnoreCase);
				var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
				var parsedAny = false;
				foreach (var item in parsed)
				{
					var row = item as Dictionary<string, object>;
					if (row == null)
					{
						continue;
					}
					parsedAny = true;
					object nameValue;
					row.TryGetValue("name", out nameValue);
					var name = nameValue == null
						? ""
						: Convert.ToString(nameValue, CultureInfo.InvariantCulture) ?? "";
					object probabilityValue;
					row.TryGetValue("probability", out probabilityValue);
					var probability = Math.Max(0.0, Math.Min(1.0,
						ParseDouble(probabilityValue == null
							? ""
							: Convert.ToString(probabilityValue, CultureInfo.InvariantCulture))));
					object idValue;
					row.TryGetValue("id", out idValue);
					var id = ParseInt(idValue == null
						? ""
						: Convert.ToString(idValue, CultureInfo.InvariantCulture));
					if ((row.ContainsKey("id") && id == 0) ||
						string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
					{
						unknownProbabilityPct += probability * 100.0;
						continue;
					}
					if (string.IsNullOrWhiteSpace(name) || probability <= 0.0)
					{
						continue;
					}
					double current;
					probabilities.TryGetValue(name, out current);
					probabilities[name] = current + probability;
				}
				if (!parsedAny)
				{
					return false;
				}

				candidates = probabilities
					.Select(pair =>
						{
							MetaDashboardCandidate legacy;
							legacyByName.TryGetValue(pair.Key, out legacy);
							return new MetaDashboardCandidate
							{
								Name = pair.Key,
								ConfidencePercent = (int)Math.Round(
									Math.Max(0.0, Math.Min(1.0, pair.Value)) * 100.0),
								Score = legacy == null ? 0 : legacy.Score,
								BranchCount = legacy == null ? 0 : legacy.BranchCount,
								IsProbability = true
							};
						})
					.OrderByDescending(candidate => candidate.ConfidencePercent)
					.ThenBy(candidate => candidate.Name)
					.Take(3)
					.ToList();
				unknownProbabilityPct = Math.Max(0.0, Math.Min(100.0, unknownProbabilityPct));
				return true;
			}
			catch
			{
				candidates = new List<MetaDashboardCandidate>();
				unknownProbabilityPct = 0.0;
				return false;
			}
		}

		private static string FormatRecognitionTier(string tier)
		{
			var displayName = GetRecognitionTierDisplayName(tier);
			return string.IsNullOrWhiteSpace(displayName) ? "" : " / " + displayName;
		}

		private static string GetRecognitionTierDisplayName(string tier)
		{
			switch ((tier ?? "").Trim().ToLowerInvariant())
			{
				case "corrected": return "\u5df2\u4eba\u5de5\u4fee\u6b63";
				case "confirmed": return "\u7a33\u5b9a";
				case "likely": return "\u8f83\u53ef\u80fd";
				case "mixed": return "\u591a\u79cd\u6d41\u6d3e\u5747\u6709\u53ef\u80fd";
				case "unknown": return "\u672a\u8bc6\u522b";
				default: return string.IsNullOrWhiteSpace(tier) ? "" : "\u672a\u77e5\u72b6\u6001";
			}
		}

		private static string BuildMatchSummary(
			string result, string opponentClassName, bool hasOpponent)
		{
			var resultText = FormatMatchResult(result);
			var opponentText = hasOpponent
				? "\u5bf9\u9635 " + opponentClassName
				: "";
			return string.Join(" / ", new[] { resultText, opponentText }
				.Where(part => !string.IsNullOrWhiteSpace(part))
				.ToArray());
		}

		private static string FormatMatchResult(string result)
		{
			switch ((result ?? "").Trim().ToLowerInvariant())
			{
				case "win":
				case "won":
				case "victory":
					return "\u80dc\u5229";
				case "loss":
				case "lost":
				case "defeat":
					return "\u5931\u8d25";
				case "tie":
				case "draw":
					return "\u5e73\u5c40";
				case "unknown":
					return "\u7ed3\u679c\u672a\u77e5";
				default:
					return string.IsNullOrWhiteSpace(result) ? "" : "\u7ed3\u679c\u672a\u77e5";
			}
		}

		internal static bool IsUnknownArchetypePlaceholder(string value)
		{
			return string.Equals((value ?? "").Trim(), "unknown",
				StringComparison.OrdinalIgnoreCase);
		}

		internal static string FormatArchetypeDisplayName(string value)
		{
			return IsUnknownArchetypePlaceholder(value)
				? "\u672a\u8bc6\u522b\u6d41\u6d3e"
				: value ?? "";
		}

		internal static List<MetaDashboardCandidate> ParseCandidateArchetypes(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return new List<MetaDashboardCandidate>();
			}

			return value
				.Split(new[] {" / "}, StringSplitOptions.RemoveEmptyEntries)
				.Select(ParseCandidateArchetype)
				.Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Name))
				.Take(3)
				.ToList();
		}

		private static MetaDashboardCandidate ParseCandidateArchetype(string value)
		{
			value = value == null ? "" : value.Trim();
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			var confidenceMatch = CandidateConfidenceRegex.Match(value);
			var confidence = confidenceMatch.Success
				? (int)Math.Round(ParseDouble(confidenceMatch.Groups["confidence"].Value))
				: 0;
			var scoreMatch = CandidateScoreRegex.Match(value);
			var branchMatch = CandidateBranchCountRegex.Match(value);
			var name = value;
			var colon = value.IndexOf(':');
			if (colon >= 0)
			{
				name = value.Substring(0, colon);
			}
			else if (confidenceMatch.Success)
			{
				name = value.Substring(0, confidenceMatch.Index);
			}

			return new MetaDashboardCandidate
			{
				Name = name.Trim(),
				ConfidencePercent = confidence,
				Score = scoreMatch.Success ? ParseInt(scoreMatch.Groups["score"].Value) : 0,
				BranchCount = branchMatch.Success ? ParseInt(branchMatch.Groups["branches"].Value) : 0
			};
		}

		private static List<string> ParseList(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return new List<string>();
			}

			return value
				.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
				.Select(item => item.Trim())
				.Where(item => !string.IsNullOrWhiteSpace(item))
				.Take(6)
				.ToList();
		}

		private static List<string> ParseEvidenceCardList(string value)
		{
			var raw = ParseList(value);
			var localized = raw
				.Select(item => LocalizeEvidenceCardName(item, ResolveLocalizedEvidenceCardName))
				.Where(item => !string.IsNullOrWhiteSpace(item))
				.Distinct(StringComparer.Ordinal)
				.Take(6)
				.ToList();
			if (localized.Count == 0 && raw.Count > 0)
				localized.Add("\u5df2\u8bb0\u5f55\u5361\u724c\u8bc1\u636e\uff08\u540d\u79f0\u6682\u4e0d\u53ef\u7528\uff09");
			return localized;
		}

		internal static string LocalizeEvidenceCardName(
			string value,
			Func<string, string> localizedNameResolver)
		{
			var normalized = (value ?? "").Trim();
			if (string.IsNullOrWhiteSpace(normalized))
				return "";

			var match = EvidenceCardCopyRegex.Match(normalized);
			var name = match.Success ? match.Groups["name"].Value.Trim() : normalized;
			var count = match.Success ? match.Groups["count"].Value : "";
			var displayName = ContainsChinese(name) ? name : "";
			if (string.IsNullOrWhiteSpace(displayName) && localizedNameResolver != null)
			{
				try
				{
					var candidate = (localizedNameResolver(name) ?? "").Trim();
					if (ContainsChinese(candidate))
						displayName = candidate;
				}
				catch
				{
					// Card definitions can still be loading; never expose the raw English name.
				}
			}
			if (string.IsNullOrWhiteSpace(displayName))
				return "";
			return displayName + (string.IsNullOrWhiteSpace(count) ? "" : " ×" + count);
		}

		private static string ResolveLocalizedEvidenceCardName(string englishName)
		{
			if (string.IsNullOrWhiteSpace(englishName))
				return "";
			try
			{
				var card = Database.GetCardFromName(englishName, false, false, false);
				return card == null ? "" : card.LocalizedName ?? "";
			}
			catch
			{
				return "";
			}
		}

		private static bool ContainsChinese(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Any(character =>
				(character >= '\u3400' && character <= '\u4DBF') ||
				(character >= '\u4E00' && character <= '\u9FFF'));
		}

		private static string BuildLastGameToolTip(
			List<MetaDashboardCandidate> candidates,
			List<string> keyEvidenceCards,
			int confidencePercent,
			bool unidentified = false,
			string recognitionTier = "",
			double unknownProbabilityPct = 0.0)
		{
			var lines = new List<string> { LastGameToolTip };
			if (!string.IsNullOrWhiteSpace(recognitionTier))
			{
				lines.Add("\u8bc6\u522b\u72b6\u6001\uff1a" +
					GetRecognitionTierDisplayName(recognitionTier) +
					"\uff1b\u672a\u8bc6\u522b\u6982\u7387 " +
					FormatPercent(unknownProbabilityPct, 1) + "%\u3002");
			}
			if (unidentified)
			{
				lines.Add("\u672a\u8bc6\u522b\u5230\u7a33\u5b9a\u5f62\u6001\uff0c\u4ec5\u663e\u793a\u5bf9\u624b\u804c\u4e1a\u3002");
			}
			if (confidencePercent > 0 && confidencePercent < 40)
			{
				lines.Add("\u4f4e\u7f6e\u4fe1\uff0c\u4ec5\u4f9b\u53c2\u8003\u3002");
			}
			if (candidates != null && candidates.Count > 0)
			{
				lines.Add((candidates.Any(candidate => candidate.IsProbability)
					? "\u5019\u9009\u6982\u7387\uff1a"
					: "\u5019\u9009\u5f62\u6001\uff1a") + string.Join("\uff1b", candidates
					.Select(candidate => candidate.IsProbability
						? FormatArchetypeDisplayName(candidate.Name) + " " +
							candidate.ConfidencePercent.ToString(CultureInfo.InvariantCulture) + "%"
						: FormatArchetypeDisplayName(candidate.Name) + " " +
							candidate.ConfidencePercent.ToString(CultureInfo.InvariantCulture) +
							"% / \u5339\u914d\u5206 " + candidate.Score.ToString(CultureInfo.InvariantCulture) +
							" / \u5206\u652f " + candidate.BranchCount.ToString(CultureInfo.InvariantCulture))
					.ToArray()));
			}
			if (keyEvidenceCards != null && keyEvidenceCards.Count > 0)
			{
				lines.Add("\u5173\u952e\u8bc1\u636e\u724c\uff1a" +
					string.Join("\u3001", keyEvidenceCards.ToArray()));
			}
			return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
		}

		private static Dictionary<string, string> FindMatchingHdtRow(
			List<Dictionary<string, string>> rows, Dictionary<string, string> localRow)
		{
			if (rows == null || rows.Count == 0 || localRow == null)
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

			return null;
		}

		private static bool IsLocalRowNewer(
			Dictionary<string, string> localRow,
			Dictionary<string, string> hdtRow)
		{
			if (localRow == null)
			{
				return false;
			}

			DateTime localTime;
			DateTime hdtTime;
			var hasLocalTime = DateTime.TryParse(
				FirstNonEmpty(Get(localRow, "end_time"), Get(localRow, "ended_at"),
					Get(localRow, "start_time"), Get(localRow, "started_at")),
				out localTime);
			var hasHdtTime = DateTime.TryParse(
				FirstNonEmpty(Get(hdtRow, "end_time"), Get(hdtRow, "ended_at"),
					Get(hdtRow, "start_time"), Get(hdtRow, "started_at")),
				out hdtTime);
			return hasLocalTime && hasHdtTime && localTime > hdtTime;
		}

		private static Dictionary<string, string> GetLatestMatchRow(
			List<Dictionary<string, string>> rows)
		{
			if (rows == null || rows.Count == 0)
			{
				return null;
			}

			var datedRows = rows
				.Select((row, index) => new
				{
					Row = row,
					Index = index,
					Timestamp = ParseMatchTimestamp(row)
				})
				.Where(item => item.Timestamp.HasValue)
				.OrderBy(item => item.Timestamp.Value)
				.ThenBy(item => item.Index)
				.ToList();
			return datedRows.Count > 0 ? datedRows.Last().Row : rows.Last();
		}

		private static DateTime? ParseMatchTimestamp(Dictionary<string, string> row)
		{
			DateTime timestamp;
			return DateTime.TryParse(
				FirstNonEmpty(Get(row, "end_time"), Get(row, "ended_at"),
					Get(row, "start_time"), Get(row, "started_at")),
				out timestamp)
				? (DateTime?)timestamp
				: null;
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
				case "EVOKER":
					return "\u5524\u9b54\u5e08";
				case "HUNTER":
					return "\u730e\u4eba";
				case "MAGE":
					return "\u6cd5\u5e08";
				case "MONK":
					return "\u6b66\u50e7";
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
				case "UNKNOWN":
				case "\u672a\u77e5":
					return "\u672a\u77e5";
				default:
					return string.IsNullOrWhiteSpace(playerClass) ||
						!playerClass.Any(character => character >= '\u3400' && character <= '\u9fff')
						? "\u672a\u77e5\u804c\u4e1a"
						: playerClass.Trim();
			}
		}
	}

	internal class MetaDashboardRemoteSource
	{
		public static readonly MetaDashboardRemoteSource Empty = new MetaDashboardRemoteSource();

		public string TimeRange { get; private set; } = "";
		public string SelectedTimeRange { get; private set; } = "";
		public string AutoTimeRangePolicy { get; private set; } = "";
		public string GameType { get; private set; } = "";
		public string RankRange { get; private set; } = "";
		public string Region { get; private set; } = "";
		public string PatchVersion { get; private set; } = "";
		public DateTime? GeneratedAt { get; private set; }
		public DateTime? AsOf { get; private set; }
		public List<MetaDashboardRemoteCandidate> Candidates { get; private set; } =
			new List<MetaDashboardRemoteCandidate>();

		public bool HasData
		{
			get
			{
				return !string.IsNullOrWhiteSpace(TimeRange) ||
					!CreatingTimeIsEmpty() ||
					!string.IsNullOrWhiteSpace(SelectedTimeRange);
			}
		}

		public string EffectiveTimeRange
		{
			get
			{
				return string.IsNullOrWhiteSpace(SelectedTimeRange)
					? TimeRange
					: SelectedTimeRange;
			}
		}

		public string ShortText
		{
			get
			{
				if (!HasData)
				{
					return "";
				}

				var time = FormatShortTime(AsOf ?? GeneratedAt);
				var range = FormatTimeRangeShort(EffectiveTimeRange, PatchVersion);
				if (string.IsNullOrWhiteSpace(time))
				{
					return range;
				}
				return string.IsNullOrWhiteSpace(range)
					? time
					: time + " " + range;
			}
		}

		public string SettingsText
		{
			get
			{
				if (!HasData)
				{
					return "";
				}

				var parts = new List<string>
				{
					"HSReplay " + FormatTimeRangeLong(EffectiveTimeRange, PatchVersion)
				};
				if (!string.IsNullOrWhiteSpace(RankRange))
				{
					parts.Add(FormatRankRange(RankRange));
				}
				if (AsOf.HasValue)
				{
					parts.Add("\u622a\u81f3 " + FormatFullTime(AsOf));
				}
				return string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray());
			}
		}

		public string ToolTip
		{
			get
			{
				if (!HasData)
				{
					return "";
				}

				var lines = new List<string>
				{
					"HSReplay \u8fdc\u7a0b\u6570\u636e\u6e90",
					"\u65f6\u95f4\u8303\u56f4\uff1a" + FormatTimeRangeLong(EffectiveTimeRange, PatchVersion)
				};
				if (!string.IsNullOrWhiteSpace(SelectedTimeRange) &&
					!string.Equals(SelectedTimeRange, TimeRange, StringComparison.OrdinalIgnoreCase))
				{
					lines[1] += "\uff08\u81ea\u52a8\u9009\u62e9\uff09";
				}
				AddLine(lines, "\u5206\u6bb5\uff1a", FormatRankRange(RankRange));
				AddLine(lines, "\u6a21\u5f0f\uff1a", FormatGameType(GameType));
				AddLine(lines, "\u5730\u533a\uff1a", FormatRegion(Region));
				AddLine(lines, "\u6570\u636e\u622a\u81f3\uff1a", FormatFullTime(AsOf));
				AddLine(lines, "\u7f13\u5b58\u751f\u6210\uff1a", FormatFullTime(GeneratedAt));
				if (Candidates.Count > 0)
				{
					lines.Add("\u5019\u9009\u6837\u672c\uff1a" + string.Join("\uff1b", Candidates
						.Select(candidate => FormatTimeRangeLong(candidate.TimeRange, PatchVersion) + " " +
							candidate.SampleGames.ToString(CultureInfo.InvariantCulture) + "\u5c40")
						.ToArray()));
				}
				return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
			}
		}

		public static MetaDashboardRemoteSource Load(string summaryPath, string manifestPath)
		{
			var source = new MetaDashboardRemoteSource();
			var summary = ReadJsonObject(summaryPath);
			if (summary != null)
			{
				source.GeneratedAt = ParseDate(StringValue(summary, "generated_at"));
				source.AsOf = ParseDate(StringValue(summary, "as_of"));
				source.TimeRange = StringValue(summary, "time_range");
				source.GameType = StringValue(summary, "game_type");
				source.RankRange = StringValue(summary, "rank_range");
				source.Region = StringValue(summary, "region");
				source.PatchVersion = StringValue(summary, "patch_version");
			}

			var manifest = ReadJsonObject(manifestPath);
			if (manifest != null)
			{
				source.SelectedTimeRange = StringValue(manifest, "selected_time_range");
				source.AutoTimeRangePolicy = StringValue(manifest, "auto_time_range_policy");
				source.PatchVersion = FirstNonEmpty(
					source.PatchVersion,
					StringValue(manifest, "patch_version"));
				var candidates = ObjectValue(manifest, "candidate_sample_games") as IEnumerable;
				if (candidates != null && !(candidates is string))
				{
					foreach (var candidateObject in candidates)
					{
						var candidate = candidateObject as Dictionary<string, object>;
						if (candidate == null)
						{
							continue;
						}

						source.Candidates.Add(new MetaDashboardRemoteCandidate
						{
							TimeRange = StringValue(candidate, "time_range"),
							SampleGames = LongValue(candidate, "sample_games"),
							SummaryAsOf = ParseDate(StringValue(candidate, "summary_as_of"))
						});
					}
				}
			}

			return source.HasData ? source : Empty;
		}

		private bool CreatingTimeIsEmpty()
		{
			return !GeneratedAt.HasValue && !AsOf.HasValue;
		}

		private static void AddLine(List<string> lines, string label, string value)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				lines.Add(label + value);
			}
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values == null
				? ""
				: values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
		}

		private static Dictionary<string, object> ReadJsonObject(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return null;
			}

			try
			{
				return new JavaScriptSerializer().DeserializeObject(
					File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
			}
			catch
			{
				return null;
			}
		}

		private static object ObjectValue(Dictionary<string, object> values, string key)
		{
			if (values == null || key == null || !values.ContainsKey(key))
			{
				return null;
			}
			return values[key];
		}

		private static string StringValue(Dictionary<string, object> values, string key)
		{
			var value = ObjectValue(values, key);
			return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
		}

		private static long LongValue(Dictionary<string, object> values, string key)
		{
			var value = ObjectValue(values, key);
			if (value == null)
			{
				return 0;
			}

			long parsed;
			return long.TryParse(
				Convert.ToString(value, CultureInfo.InvariantCulture),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out parsed)
				? parsed
				: 0;
		}

		private static DateTime? ParseDate(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			DateTime parsed;
			return DateTime.TryParse(
				value,
				CultureInfo.InvariantCulture,
				DateTimeStyles.RoundtripKind,
				out parsed)
				? (DateTime?)parsed.ToLocalTime()
				: null;
		}

		private static string FormatShortTime(DateTime? value)
		{
			return value.HasValue
				? value.Value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
				: "";
		}

		private static string FormatFullTime(DateTime? value)
		{
			return value.HasValue
				? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
				: "";
		}

		private static string FormatTimeRangeShort(string timeRange, string patchVersion)
		{
			switch ((timeRange ?? "").Trim().ToUpperInvariant())
			{
				case "CURRENT_PATCH":
					return FormatPatchLabel(patchVersion);
				case "LAST_1_DAY":
					return "1\u5929";
				case "LAST_3_DAYS":
					return "3\u5929";
				case "LAST_7_DAYS":
					return "7\u5929";
				case "LAST_30_DAYS":
					return "30\u5929";
				case "CURRENT_EXPANSION":
					return "\u5f53\u524d\u6269\u5c55\u5305";
				case "CURRENT_SEASON":
					return "\u5f53\u524d\u8d5b\u5b63";
				default:
					return string.IsNullOrWhiteSpace(timeRange) ? "" : "\u5176\u4ed6\u65f6\u95f4\u8303\u56f4";
			}
		}

		private static string FormatTimeRangeLong(string timeRange, string patchVersion)
		{
			switch ((timeRange ?? "").Trim().ToUpperInvariant())
			{
				case "CURRENT_PATCH":
					return FormatPatchLabel(patchVersion);
				case "LAST_1_DAY":
					return "\u6700\u8fd11\u5929";
				case "LAST_3_DAYS":
					return "\u6700\u8fd13\u5929";
				case "LAST_7_DAYS":
					return "\u6700\u8fd17\u5929";
				case "LAST_30_DAYS":
					return "\u6700\u8fd130\u5929";
				case "CURRENT_EXPANSION":
					return "\u5f53\u524d\u6269\u5c55\u5305";
				case "CURRENT_SEASON":
					return "\u5f53\u524d\u8d5b\u5b63";
				default:
					return string.IsNullOrWhiteSpace(timeRange) ? "" : "\u5176\u4ed6\u65f6\u95f4\u8303\u56f4";
			}
		}

		private static string FormatPatchLabel(string patchVersion)
		{
			return string.IsNullOrWhiteSpace(patchVersion)
				? "\u5f53\u524d\u8865\u4e01\u540e"
				: patchVersion.Trim() + "\u8865\u4e01\u540e";
		}

		private static string FormatRankRange(string rankRange)
		{
			switch ((rankRange ?? "").Trim().ToUpperInvariant())
			{
				case "ALL":
					return "\u5168\u90e8\u6bb5\u4f4d";
				case "TOP_1000_LEGEND":
					return "\u4f20\u8bf4\u524d1000";
				case "LEGEND":
					return "\u4f20\u8bf4";
				case "DIAMOND_THROUGH_LEGEND":
					return "\u94bb\u77f3-\u4f20\u8bf4";
				case "DIAMOND_FOUR_THROUGH_DIAMOND_ONE":
					return "\u94bb4-\u94bb1";
				case "DIAMOND":
					return "\u94bb\u77f3";
				case "PLATINUM":
					return "\u767d\u91d1";
				case "GOLD":
					return "\u9ec4\u91d1";
				case "SILVER":
					return "\u767d\u94f6";
				case "BRONZE":
					return "\u9752\u94dc";
				case "BRONZE_THROUGH_GOLD":
					return "\u9752\u94dc-\u9ec4\u91d1";
				default:
					return string.IsNullOrWhiteSpace(rankRange) ? "" : "\u5176\u4ed6\u6bb5\u4f4d";
			}
		}

		private static string FormatGameType(string gameType)
		{
			switch ((gameType ?? "").Trim().ToUpperInvariant())
			{
				case "RANKED_STANDARD": return "\u6807\u51c6\u6a21\u5f0f\uff08\u5929\u68af\uff09";
				case "RANKED_WILD": return "\u72c2\u91ce\u6a21\u5f0f\uff08\u5929\u68af\uff09";
				case "RANKED_TWIST": return "\u5947\u8da3\u6a21\u5f0f\uff08\u5929\u68af\uff09";
				case "CASUAL_STANDARD": return "\u6807\u51c6\u6a21\u5f0f\uff08\u4f11\u95f2\uff09";
				case "CASUAL_WILD": return "\u72c2\u91ce\u6a21\u5f0f\uff08\u4f11\u95f2\uff09";
				case "ARENA": return "\u7ade\u6280\u573a";
				case "BATTLEGROUNDS": return "\u9152\u9986\u6218\u68cb";
				case "DUELS": return "\u5bf9\u51b3\u6a21\u5f0f";
				case "UNKNOWN": return "\u672a\u77e5\u6a21\u5f0f";
				default: return string.IsNullOrWhiteSpace(gameType) ? "" : "\u5176\u4ed6\u6a21\u5f0f";
			}
		}

		private static string FormatRegion(string region)
		{
			switch ((region ?? "").Trim().ToUpperInvariant())
			{
				case "ALL": return "\u5168\u90e8\u5730\u533a";
				case "AMERICAS": return "\u7f8e\u6d32";
				case "EUROPE": return "\u6b27\u6d32";
				case "ASIA": return "\u4e9a\u6d32";
				case "CHINA":
				case "CN": return "\u4e2d\u56fd";
				case "UNKNOWN": return "\u672a\u77e5\u5730\u533a";
				default: return string.IsNullOrWhiteSpace(region) ? "" : "\u5176\u4ed6\u5730\u533a";
			}
		}
	}

	internal class MetaDashboardRemoteCandidate
	{
		public string TimeRange { get; set; } = "";
		public long SampleGames { get; set; }
		public DateTime? SummaryAsOf { get; set; }
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

	internal class MetaDashboardCandidate
	{
		public string Name { get; set; } = "";
		public int ConfidencePercent { get; set; }
		public int Score { get; set; }
		public int BranchCount { get; set; }
		public bool IsProbability { get; set; }
	}

	internal class MetaDashboardLastGame : MetaDashboardItem
	{
		public MetaDashboardLastGame(
			string title, string detail, string hsReplayUrl = "", string replayPath = "",
			string toolTip = "")
			: base(title, detail, hsReplayUrl, replayPath, toolTip)
		{
		}

		public string MatchId { get; set; } = "";
		public int ConfidencePercent { get; set; }
		public string RecognitionTier { get; set; } = "";
		public double UnknownProbabilityPercent { get; set; }
		public List<MetaDashboardCandidate> Candidates { get; set; } =
			new List<MetaDashboardCandidate>();
		public List<string> KeyEvidenceCards { get; set; } = new List<string>();

		public bool IsLowConfidence
		{
			get { return ConfidencePercent > 0 && ConfidencePercent < 40; }
		}
	}

	internal class MetaDashboardItem
	{
		public MetaDashboardItem(
			string title, string detail, string hsReplayUrl = "", string replayPath = "",
			string toolTip = "", string highestWinRateDeckCode = "",
			string mostPopularDeckCode = "")
		{
			Title = title ?? "";
			Detail = detail ?? "";
			HsReplayUrl = hsReplayUrl ?? "";
			ReplayPath = replayPath ?? "";
			ToolTip = toolTip ?? "";
			HighestWinRateDeckCode = highestWinRateDeckCode ?? "";
			MostPopularDeckCode = mostPopularDeckCode ?? "";
		}

		public string Title { get; private set; }
		public string Detail { get; private set; }
		public string HsReplayUrl { get; private set; }
		public string ReplayPath { get; private set; }
		public string ToolTip { get; private set; }
		public string HighestWinRateDeckCode { get; private set; }
		public string MostPopularDeckCode { get; private set; }
	}

	internal class RepresentativeDeckPair
	{
		public RepresentativeDeckCandidate HighestWinRate { get; set; }
		public RepresentativeDeckCandidate MostPopular { get; set; }
	}

	internal class RepresentativeDeckCandidate
	{
		public int ArchetypeId { get; set; }
		public string DeckCode { get; set; } = "";
		public string DeckId { get; set; } = "";
		public int Games { get; set; }
		public double WinRate { get; set; }
	}
}
