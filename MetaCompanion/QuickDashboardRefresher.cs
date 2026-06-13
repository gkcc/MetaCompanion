using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MetaCompanion
{
	internal class QuickDashboardRefresher
	{
		private const int MinMatchupGames = 200;
		private const double MinCoveragePct = 50.0;
		private readonly object _lock = new object();
		private DateTime _lastProcessedHistoryWriteTimeUtc = DateTime.MinValue;
		private bool _isRunning;

		public bool TryRefreshAfterGame(PluginConfig config, Action onCompleted)
		{
			var dataDirectory = MetaCompanionPlugin.DataDirectory;
			var historyPath = MatchHistoryRecorder.GetHistoryPath(dataDirectory);
			if (!File.Exists(historyPath))
			{
				return false;
			}

			var historyWriteTimeUtc = File.GetLastWriteTimeUtc(historyPath);
			lock (_lock)
			{
				if (_isRunning || historyWriteTimeUtc <= _lastProcessedHistoryWriteTimeUtc)
				{
					return false;
				}
				_isRunning = true;
			}

			Task.Run(() =>
				{
					var refreshed = false;
					try
					{
						var result = Refresh(config, dataDirectory, DateTime.Now);
						refreshed = result.EnvironmentUpdated || result.RecommendationsUpdated;
						if (result.EnvironmentUpdated)
						{
							Log.Info("Quick dashboard local meta refreshed from " +
								result.LocalMatchCount.ToString(CultureInfo.InvariantCulture) + " matches.");
						}
						if (result.RecommendationsUpdated)
						{
							Log.Info("Quick dashboard recommendations refreshed: " +
								result.RecommendationCount.ToString(CultureInfo.InvariantCulture) + " rows.");
						}
					}
					catch (Exception ex)
					{
						Log.Warn("Quick dashboard refresh failed: " + ex.Message);
					}
					finally
					{
						lock (_lock)
						{
							if (refreshed)
							{
								_lastProcessedHistoryWriteTimeUtc = historyWriteTimeUtc;
							}
							_isRunning = false;
						}
					}

					if (refreshed)
					{
						onCompleted?.Invoke();
					}
				});
			return true;
		}

		internal static QuickDashboardRefreshResult Refresh(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			config = config ?? new PluginConfig();
			var result = new QuickDashboardRefreshResult();
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return result;
			}

			var metaDirectory = GetPremiumMetaDirectory(dataDirectory);
			var archetypes = LoadArchetypes(GetArchetypesPath(metaDirectory));
			var corrections = LoadCorrections(MatchHistoryRecorder.GetCorrectionsPath(dataDirectory));
			var localRows = LoadLocalRows(config, dataDirectory, archetypes, corrections, now);
			if (localRows.Count > 0)
			{
				var environmentRows = BuildEnvironmentRows(localRows, archetypes);
				WriteEnvironment(dataDirectory, environmentRows, localRows, config, now);
				result.EnvironmentUpdated = true;
				result.LocalMatchCount = localRows.Count;
			}

			var recommendations = BuildRecommendations(
				config,
				metaDirectory,
				archetypes,
				localRows);
			if (recommendations.Count > 0)
			{
				WriteRecommendations(metaDirectory, recommendations, config, localRows.Count, now);
				result.RecommendationsUpdated = true;
				result.RecommendationCount = recommendations.Count;
			}

			return result;
		}

		internal static string GetPremiumMetaDirectory(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "Premium", "Meta", "latest");
		}

		internal static string GetRecommendationsPath(string dataDirectory)
		{
			return Path.Combine(GetPremiumMetaDirectory(dataDirectory), "personal_recommendations.tsv");
		}

		private static string GetRecommendationsJsonPath(string metaDirectory)
		{
			return Path.Combine(metaDirectory, "personal_recommendations.json");
		}

		private static string GetLocalEnvironmentPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "local_meta_environment.tsv");
		}

		private static string GetLocalGamesPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "local_meta_archetypes.tsv");
		}

		private static string GetLocalSummaryPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "local_meta_summary.json");
		}

		private static string GetArchetypesPath(string metaDirectory)
		{
			return Path.Combine(metaDirectory, "archetypes.zh-hans.json");
		}

		private static string GetSummaryPath(string metaDirectory)
		{
			return Path.Combine(metaDirectory, "summary.json");
		}

		private static string GetMatrixPath(string metaDirectory)
		{
			return Path.Combine(metaDirectory, "head_to_head_archetype_matchups_v2.json");
		}

		private static List<LocalMatchRow> LoadLocalRows(
			PluginConfig config,
			string dataDirectory,
			ArchetypeLookup archetypes,
			Dictionary<string, MatchCorrection> corrections,
			DateTime now)
		{
			var historyPath = MatchHistoryRecorder.GetHistoryPath(dataDirectory);
			var rows = ReadTsv(historyPath);
			if (rows.Count == 0)
			{
				return new List<LocalMatchRow>();
			}

			var historyDays = Math.Max(1, config.LocalRecommendationHistoryDays);
			var cutoff = now.AddDays(-historyDays);
			var minConfidence = Math.Max(0, config.LocalMetaMinConfidence);
			var localRows = new List<LocalMatchRow>();
			foreach (var row in rows)
			{
				var endedAt = ParseDate(FirstNonEmpty(Get(row, "ended_at"), Get(row, "started_at")));
				if (!endedAt.HasValue || endedAt.Value < cutoff)
				{
					continue;
				}

				if (!IsStandardMatch(row))
				{
					continue;
				}

				var matchId = Get(row, "match_id");
				var correction = !string.IsNullOrWhiteSpace(matchId) && corrections.ContainsKey(matchId)
					? corrections[matchId]
					: null;
				var archetypeName = correction != null &&
					!string.IsNullOrWhiteSpace(correction.CorrectedArchetype)
					? correction.CorrectedArchetype
					: Get(row, "predicted_archetype");
				if (string.IsNullOrWhiteSpace(archetypeName) ||
					!archetypes.NameToId.ContainsKey(archetypeName))
				{
					continue;
				}

				var confidencePct = ParseDouble(Get(row, "confidence_pct"), 0.0);
				if (correction != null && !string.IsNullOrWhiteSpace(correction.CorrectedArchetype))
				{
					confidencePct = 100.0;
				}
				if (confidencePct < minConfidence)
				{
					continue;
				}

				var archetypeId = archetypes.NameToId[archetypeName];
				var archetype = archetypes.ById.ContainsKey(archetypeId)
					? archetypes.ById[archetypeId]
					: new ArchetypeInfo { Id = archetypeId, Name = archetypeName };
				var ageDays = Math.Max(0.0, (now - endedAt.Value).TotalDays);
				var confidenceWeight = Clamp(confidencePct / 100.0, 0.25, 1.0);
				var recencyWeight = Math.Pow(0.5, ageDays / historyDays);
				var result = correction != null && !string.IsNullOrWhiteSpace(correction.CorrectedResult)
					? correction.CorrectedResult
					: Get(row, "result");
				localRows.Add(new LocalMatchRow
				{
					MatchId = matchId,
					StartedAt = FirstNonEmpty(Get(row, "started_at"), endedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
					EndedAt = endedAt.Value,
					Format = Get(row, "format"),
					Mode = Get(row, "mode"),
					Result = result,
					OpponentClass = Get(row, "opponent_class"),
					ArchetypeId = archetypeId,
					ArchetypeName = archetype.Name,
					PlayerClass = archetype.PlayerClass,
					ConfidencePct = confidencePct,
					Weight = confidenceWeight * recencyWeight,
					RecencyWeight = recencyWeight,
					AgeDays = ageDays,
					EvidenceCards = Get(row, "evidence_cards"),
					CandidateArchetypes = Get(row, "candidate_archetypes"),
					ReplayFile = Get(row, "replay_file"),
					ReplayPath = Get(row, "replay_path"),
					HsReplayUploadId = Get(row, "hsreplay_upload_id"),
					HsReplayUrl = Get(row, "hsreplay_url")
				});
			}

			return localRows;
		}

		private static bool IsStandardMatch(Dictionary<string, string> row)
		{
			var format = Get(row, "format");
			if (!string.IsNullOrWhiteSpace(format) &&
				!string.Equals(format, "Standard", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var mode = Get(row, "mode");
			return string.IsNullOrWhiteSpace(mode) ||
				string.Equals(mode, "Ranked", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(mode, "Casual", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(mode, "Friendly", StringComparison.OrdinalIgnoreCase);
		}

		private static List<EnvironmentRow> BuildEnvironmentRows(
			List<LocalMatchRow> localRows,
			ArchetypeLookup archetypes)
		{
			var totalWeight = localRows.Sum(row => row.Weight);
			var rank = 1;
			return localRows
				.GroupBy(row => row.ArchetypeId)
				.Select(group =>
					{
						var first = group.First();
						var wins = group.Count(row => IsResult(row.Result, "win"));
						var losses = group.Count(row => IsResult(row.Result, "loss"));
						var finished = wins + losses;
						var weightedGames = group.Sum(row => row.Weight);
						var archetype = archetypes.ById.ContainsKey(first.ArchetypeId)
							? archetypes.ById[first.ArchetypeId]
							: new ArchetypeInfo
							{
								Id = first.ArchetypeId,
								Name = first.ArchetypeName,
								PlayerClass = first.PlayerClass
							};
						return new EnvironmentRow
						{
							ArchetypeId = first.ArchetypeId,
							Name = archetype.Name,
							PlayerClass = archetype.PlayerClass,
							Games = group.Count(),
							WeightedGames = weightedGames,
							LocalPct = totalWeight > 0.0 ? weightedGames / totalWeight * 100.0 : 0.0,
							AvgConfidence = group.Average(row => row.ConfidencePct),
							Wins = wins,
							Losses = losses,
							WinRate = finished > 0 ? (double?)wins / finished * 100.0 : null
						};
					})
				.OrderByDescending(row => row.WeightedGames)
				.ThenByDescending(row => row.Games)
				.Select(row =>
					{
						row.Rank = rank++;
						return row;
					})
				.ToList();
		}

		private static List<RecommendationRow> BuildRecommendations(
			PluginConfig config,
			string metaDirectory,
			ArchetypeLookup archetypes,
			List<LocalMatchRow> localRows)
		{
			var summaryPath = GetSummaryPath(metaDirectory);
			var matrixPath = GetMatrixPath(metaDirectory);
			if (!File.Exists(summaryPath) || !File.Exists(matrixPath) || archetypes.ById.Count == 0)
			{
				return new List<RecommendationRow>();
			}

			var summary = AsObject(ReadJson(summaryPath));
			var matrix = AsObject(ReadJson(matrixPath));
			var series = AsObject(GetObject(matrix, "series"));
			var metadata = AsObject(GetObject(series, "metadata"));
			var data = AsObject(GetObject(series, "data"));
			if (metadata.Count == 0 || data.Count == 0)
			{
				return new List<RecommendationRow>();
			}

			var environmentRows = BuildRecommendationEnvironment(
				config,
				summary,
				archetypes,
				localRows);
			if (environmentRows.Count == 0)
			{
				return new List<RecommendationRow>();
			}

			var recommendations = new List<RecommendationRow>();
			foreach (var candidateKey in metadata.Keys)
			{
				int candidateId;
				if (!int.TryParse(candidateKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out candidateId) ||
					candidateId <= 0 || !archetypes.ById.ContainsKey(candidateId))
				{
					continue;
				}

				var candidate = archetypes.ById[candidateId];
				if (string.IsNullOrWhiteSpace(candidate.Name))
				{
					continue;
				}

				var candidateMeta = AsObject(metadata[candidateKey]);
				var fallbackWinRate = ParseDouble(GetObject(candidateMeta, "win_rate"), 50.0);
				var weightedWinRate = 0.0;
				var coverageWeight = 0.0;
				var weightedGames = 0.0;
				var matchupsUsed = 0;
				foreach (var opponent in environmentRows)
				{
					var winRate = fallbackWinRate;
					var cell = GetMatchupCell(data, candidateId, opponent.ArchetypeId);
					if (cell != null)
					{
						var totalGames = ParseInt(GetObject(cell, "total_games"), 0);
						if (totalGames >= MinMatchupGames)
						{
							winRate = ParseDouble(GetObject(cell, "win_rate"), fallbackWinRate);
							coverageWeight += opponent.Weight;
							weightedGames += opponent.Weight * totalGames;
							matchupsUsed++;
						}
					}
					weightedWinRate += opponent.Weight * winRate;
				}

				recommendations.Add(new RecommendationRow
				{
					ArchetypeId = candidateId,
					Name = candidate.Name,
					PlayerClass = candidate.PlayerClass,
					ExpectedWinRate = weightedWinRate,
					CoveragePct = coverageWeight * 100.0,
					WeightedSampleGames = weightedGames,
					MatchupsUsed = matchupsUsed,
					FallbackWinRate = fallbackWinRate
				});
			}

			var rank = 1;
			return recommendations
				.Where(row => row.CoveragePct >= MinCoveragePct)
				.OrderByDescending(row => row.ExpectedWinRate)
				.ThenByDescending(row => row.CoveragePct)
				.ThenByDescending(row => row.WeightedSampleGames)
				.Take(Math.Max(1, config.LocalRecommendationTop))
				.Select(row =>
					{
						row.Rank = rank++;
						return row;
					})
				.ToList();
		}

		private static List<RecommendationEnvironmentRow> BuildRecommendationEnvironment(
			PluginConfig config,
			Dictionary<string, object> summary,
			ArchetypeLookup archetypes,
			List<LocalMatchRow> localRows)
		{
			var weights = new Dictionary<int, double>();
			var localFactor = localRows.Count > 0
				? Clamp(config.LocalRecommendationWeight, 0.0, 1.0)
				: 0.0;
			var remoteFactor = localRows.Count > 0 ? 1.0 - localFactor : 1.0;
			var remoteRows = ReadRemoteEnvironment(summary);
			var remoteTotal = remoteRows.Sum(row => row.Weight);
			if (remoteTotal > 0.0)
			{
				foreach (var row in remoteRows)
				{
					AddWeight(weights, row.ArchetypeId, row.Weight / remoteTotal * remoteFactor);
				}
			}

			var localTotal = localRows.Sum(row => row.Weight);
			if (localTotal > 0.0)
			{
				foreach (var row in localRows)
				{
					AddWeight(weights, row.ArchetypeId, row.Weight / localTotal * localFactor);
				}
			}

			return weights
				.Where(pair => pair.Key > 0 && pair.Value > 0.0 && archetypes.ById.ContainsKey(pair.Key))
				.Select(pair => new RecommendationEnvironmentRow
				{
					ArchetypeId = pair.Key,
					Weight = pair.Value
				})
				.OrderByDescending(row => row.Weight)
				.ToList();
		}

		private static List<RecommendationEnvironmentRow> ReadRemoteEnvironment(
			Dictionary<string, object> summary)
		{
			var rows = new List<RecommendationEnvironmentRow>();
			var sourceRows = AsEnumerable(GetObject(summary, "all")).ToList();
			if (sourceRows.Count == 0)
			{
				sourceRows = AsEnumerable(GetObject(summary, "top_overall")).ToList();
			}

			foreach (var row in sourceRows.Select(AsObject))
			{
				var id = ParseInt(GetObject(row, "archetype_id"), 0);
				var weight = ParseDouble(GetObject(row, "pct_of_total"), 0.0);
				if (id > 0 && weight > 0.0)
				{
					rows.Add(new RecommendationEnvironmentRow
					{
						ArchetypeId = id,
						Weight = weight
					});
				}
			}
			return rows;
		}

		private static Dictionary<string, object> GetMatchupCell(
			Dictionary<string, object> data,
			int candidateId,
			int opponentId)
		{
			object rowObject;
			if (!data.TryGetValue(candidateId.ToString(CultureInfo.InvariantCulture), out rowObject))
			{
				return null;
			}

			var row = AsObject(rowObject);
			object cellObject;
			return row.TryGetValue(opponentId.ToString(CultureInfo.InvariantCulture), out cellObject)
				? AsObject(cellObject)
				: null;
		}

		private static ArchetypeLookup LoadArchetypes(string path)
		{
			var lookup = new ArchetypeLookup();
			if (!File.Exists(path))
			{
				return lookup;
			}

			var root = ReadJson(path);
			var rows = AsEnumerable(root).ToList();
			if (rows.Count == 0)
			{
				rows = AsEnumerable(GetObject(AsObject(root), "results")).ToList();
			}

			foreach (var row in rows.Select(AsObject))
			{
				var id = ParseInt(GetObject(row, "id"), 0);
				var name = GetString(row, "name");
				if (id <= 0 || string.IsNullOrWhiteSpace(name))
				{
					continue;
				}
				var info = new ArchetypeInfo
				{
					Id = id,
					Name = name,
					PlayerClass = GetString(row, "player_class_name")
				};
				lookup.ById[id] = info;
				lookup.NameToId[name] = id;
			}
			return lookup;
		}

		private static Dictionary<string, MatchCorrection> LoadCorrections(string path)
		{
			return ReadTsv(path)
				.Where(row => !string.IsNullOrWhiteSpace(Get(row, "match_id")))
				.ToDictionary(
					row => Get(row, "match_id"),
					row => new MatchCorrection
					{
						CorrectedArchetype = Get(row, "corrected_archetype"),
						CorrectedResult = Get(row, "corrected_result")
					},
					StringComparer.OrdinalIgnoreCase);
		}

		private static void WriteEnvironment(
			string dataDirectory,
			List<EnvironmentRow> environmentRows,
			List<LocalMatchRow> localRows,
			PluginConfig config,
			DateTime now)
		{
			Directory.CreateDirectory(dataDirectory);
			var environmentLines = new List<string>
			{
				"rank\tarchetype_id\tname\tplayer_class\tgames\tweighted_games\tlocal_pct\tavg_confidence\twins\tlosses\twin_rate"
			};
			environmentLines.AddRange(environmentRows.Select(row => JoinTsv(new[]
			{
				row.Rank.ToString(CultureInfo.InvariantCulture),
				row.ArchetypeId.ToString(CultureInfo.InvariantCulture),
				row.Name,
				row.PlayerClass,
				row.Games.ToString(CultureInfo.InvariantCulture),
				FormatDouble(row.WeightedGames, 3),
				FormatDouble(row.LocalPct, 2),
				FormatDouble(row.AvgConfidence, 1),
				row.Wins.ToString(CultureInfo.InvariantCulture),
				row.Losses.ToString(CultureInfo.InvariantCulture),
				row.WinRate.HasValue ? FormatDouble(row.WinRate.Value, 2) : ""
			})));
			WriteAllLinesAtomic(GetLocalEnvironmentPath(dataDirectory), environmentLines);

			var gameLines = new List<string>
			{
				"game_id\tstart_time\tend_time\tresult\tplayer_deck_name\tplayer_hero\topponent_hero\topponent_class\topponent_card_count\trelevant_cards\tmatched_cards\tpredicted_archetype_id\tpredicted_archetype\tconfidence_pct\tweight\tpatch_weight\trecency_weight\tage_days\tcoverage_pct\tbest_branch_rank\tbest_branch_deck_id\tcandidate_archetypes\treplay_file\treplay_path\thsreplay_upload_id\thsreplay_url"
			};
			gameLines.AddRange(localRows
				.OrderBy(row => row.EndedAt)
				.Select(row => JoinTsv(new[]
				{
					row.MatchId,
					row.StartedAt,
					row.EndedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
					row.Result,
					"",
					"",
					row.OpponentClass,
					row.OpponentClass,
					"",
					row.EvidenceCards,
					row.EvidenceCards,
					row.ArchetypeId.ToString(CultureInfo.InvariantCulture),
					row.ArchetypeName,
					FormatDouble(row.ConfidencePct, 1),
					FormatDouble(row.Weight, 4),
					"1",
					FormatDouble(row.RecencyWeight, 4),
					FormatDouble(row.AgeDays, 3),
					"",
					"",
					"",
					row.CandidateArchetypes,
					row.ReplayFile,
					row.ReplayPath,
					row.HsReplayUploadId,
					row.HsReplayUrl
				})));
			WriteAllLinesAtomic(GetLocalGamesPath(dataDirectory), gameLines);

			var serializer = CreateSerializer();
			var payload = new Dictionary<string, object>
			{
				{ "generated_at", now.ToString("o", CultureInfo.InvariantCulture) },
				{ "history_path", MatchHistoryRecorder.GetHistoryPath(dataDirectory) },
				{ "history_days", Math.Max(1, config.LocalRecommendationHistoryDays) },
				{ "sample_window_start", now.AddDays(-Math.Max(1, config.LocalRecommendationHistoryDays)).ToString("o", CultureInfo.InvariantCulture) },
				{ "min_confidence", Math.Max(0, config.LocalMetaMinConfidence) },
				{ "game_count", localRows.Count },
				{ "weighted_game_count", Round(localRows.Sum(row => row.Weight), 3) },
				{ "games_path", GetLocalGamesPath(dataDirectory) },
				{ "environment_path", GetLocalEnvironmentPath(dataDirectory) },
				{ "environment", environmentRows.Select(row => new Dictionary<string, object>
					{
						{ "rank", row.Rank },
						{ "archetype_id", row.ArchetypeId },
						{ "name", row.Name },
						{ "player_class", row.PlayerClass },
						{ "games", row.Games },
						{ "weighted_games", Round(row.WeightedGames, 3) },
						{ "local_pct", Round(row.LocalPct, 2) },
						{ "avg_confidence", Round(row.AvgConfidence, 1) },
						{ "wins", row.Wins },
						{ "losses", row.Losses },
						{ "win_rate", row.WinRate.HasValue ? (object)Round(row.WinRate.Value, 2) : "" }
					}).ToArray() }
			};
			WriteAllTextAtomic(GetLocalSummaryPath(dataDirectory), serializer.Serialize(payload));
		}

		private static void WriteRecommendations(
			string metaDirectory,
			List<RecommendationRow> recommendations,
			PluginConfig config,
			int localMatchCount,
			DateTime now)
		{
			Directory.CreateDirectory(metaDirectory);
			var lines = new List<string>
			{
				"rank\tarchetype_id\tname\tplayer_class\texpected_win_rate\tcoverage_pct\tweighted_sample_games\tmatchups_used\tfallback_win_rate"
			};
			lines.AddRange(recommendations.Select(row => JoinTsv(new[]
			{
				row.Rank.ToString(CultureInfo.InvariantCulture),
				row.ArchetypeId.ToString(CultureInfo.InvariantCulture),
				row.Name,
				row.PlayerClass,
				FormatDouble(row.ExpectedWinRate, 2),
				FormatDouble(row.CoveragePct, 2),
				FormatDouble(row.WeightedSampleGames, 1),
				row.MatchupsUsed.ToString(CultureInfo.InvariantCulture),
				FormatDouble(row.FallbackWinRate, 2)
			})));
			WriteAllLinesAtomic(Path.Combine(metaDirectory, "personal_recommendations.tsv"), lines);

			var serializer = CreateSerializer();
			var payload = new Dictionary<string, object>
			{
				{ "generated_at", now.ToString("o", CultureInfo.InvariantCulture) },
				{ "meta_directory", metaDirectory },
				{ "history_days", Math.Max(1, config.LocalRecommendationHistoryDays) },
				{ "local_weight", Clamp(config.LocalRecommendationWeight, 0.0, 1.0) },
				{ "local_source", "plugin_match_history_quick" },
				{ "local_match_count", localMatchCount },
				{ "min_matchup_games", MinMatchupGames },
				{ "min_coverage_pct", MinCoveragePct },
				{ "recommendations", recommendations.Select(row => new Dictionary<string, object>
					{
						{ "rank", row.Rank },
						{ "archetype_id", row.ArchetypeId },
						{ "name", row.Name },
						{ "player_class", row.PlayerClass },
						{ "expected_win_rate", Round(row.ExpectedWinRate, 2) },
						{ "coverage_pct", Round(row.CoveragePct, 2) },
						{ "weighted_sample_games", Round(row.WeightedSampleGames, 1) },
						{ "matchups_used", row.MatchupsUsed },
						{ "fallback_win_rate", Round(row.FallbackWinRate, 2) }
					}).ToArray() }
			};
			WriteAllTextAtomic(GetRecommendationsJsonPath(metaDirectory), serializer.Serialize(payload));
		}

		private static object ReadJson(string path)
		{
			var serializer = CreateSerializer();
			return serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
		}

		private static JavaScriptSerializer CreateSerializer()
		{
			return new JavaScriptSerializer
			{
				MaxJsonLength = int.MaxValue,
				RecursionLimit = 100
			};
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

		private static DateTime? ParseDate(string value)
		{
			DateTime parsed;
			if (DateTime.TryParse(
				value,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeLocal,
				out parsed))
			{
				return parsed;
			}

			if (DateTime.TryParse(value, out parsed))
			{
				return parsed;
			}

			return null;
		}

		private static int ParseInt(object value, int fallback)
		{
			if (value == null)
			{
				return fallback;
			}
			int parsed;
			return int.TryParse(
				Convert.ToString(value, CultureInfo.InvariantCulture),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out parsed)
				? parsed
				: fallback;
		}

		private static double ParseDouble(object value, double fallback)
		{
			if (value == null)
			{
				return fallback;
			}
			double parsed;
			return double.TryParse(
				Convert.ToString(value, CultureInfo.InvariantCulture),
				NumberStyles.Float,
				CultureInfo.InvariantCulture,
				out parsed)
				? parsed
				: fallback;
		}

		private static string Get(Dictionary<string, string> row, string key)
		{
			return row != null && key != null && row.ContainsKey(key) ? row[key] ?? "" : "";
		}

		private static string GetString(Dictionary<string, object> row, string key)
		{
			var value = GetObject(row, key);
			return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
		}

		private static object GetObject(Dictionary<string, object> row, string key)
		{
			if (row == null || key == null)
			{
				return null;
			}
			object value;
			return row.TryGetValue(key, out value) ? value : null;
		}

		private static Dictionary<string, object> AsObject(object value)
		{
			return value as Dictionary<string, object> ?? new Dictionary<string, object>();
		}

		private static IEnumerable<object> AsEnumerable(object value)
		{
			var array = value as object[];
			if (array != null)
			{
				return array;
			}
			var list = value as IEnumerable<object>;
			return list ?? Enumerable.Empty<object>();
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values == null
				? ""
				: values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
		}

		private static bool IsResult(string value, string expected)
		{
			return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
		}

		private static double Clamp(double value, double min, double max)
		{
			return Math.Max(min, Math.Min(max, value));
		}

		private static void AddWeight(Dictionary<int, double> weights, int id, double weight)
		{
			double current;
			weights.TryGetValue(id, out current);
			weights[id] = current + weight;
		}

		private static double Round(double value, int digits)
		{
			return Math.Round(value, digits, MidpointRounding.AwayFromZero);
		}

		private static string FormatDouble(double value, int digits)
		{
			return Round(value, digits).ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
		}

		private static string JoinTsv(IEnumerable<string> values)
		{
			return string.Join("\t", values.Select(EscapeTsv));
		}

		private static string EscapeTsv(string value)
		{
			return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
		}

		private static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
		{
			WriteAllTextAtomic(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
		}

		private static void WriteAllTextAtomic(string path, string content)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			var tempPath = path + ".tmp";
			File.WriteAllText(tempPath, content ?? "", Encoding.UTF8);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			File.Move(tempPath, path);
		}

		private class ArchetypeLookup
		{
			public Dictionary<int, ArchetypeInfo> ById { get; } =
				new Dictionary<int, ArchetypeInfo>();
			public Dictionary<string, int> NameToId { get; } =
				new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		}

		private class ArchetypeInfo
		{
			public int Id { get; set; }
			public string Name { get; set; } = "";
			public string PlayerClass { get; set; } = "";
		}

		private class MatchCorrection
		{
			public string CorrectedArchetype { get; set; } = "";
			public string CorrectedResult { get; set; } = "";
		}

		private class LocalMatchRow
		{
			public string MatchId { get; set; } = "";
			public string StartedAt { get; set; } = "";
			public DateTime EndedAt { get; set; }
			public string Format { get; set; } = "";
			public string Mode { get; set; } = "";
			public string Result { get; set; } = "";
			public string OpponentClass { get; set; } = "";
			public int ArchetypeId { get; set; }
			public string ArchetypeName { get; set; } = "";
			public string PlayerClass { get; set; } = "";
			public double ConfidencePct { get; set; }
			public double Weight { get; set; }
			public double RecencyWeight { get; set; }
			public double AgeDays { get; set; }
			public string EvidenceCards { get; set; } = "";
			public string CandidateArchetypes { get; set; } = "";
			public string ReplayFile { get; set; } = "";
			public string ReplayPath { get; set; } = "";
			public string HsReplayUploadId { get; set; } = "";
			public string HsReplayUrl { get; set; } = "";
		}

		private class EnvironmentRow
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

		private class RecommendationEnvironmentRow
		{
			public int ArchetypeId { get; set; }
			public double Weight { get; set; }
		}

		private class RecommendationRow
		{
			public int Rank { get; set; }
			public int ArchetypeId { get; set; }
			public string Name { get; set; } = "";
			public string PlayerClass { get; set; } = "";
			public double ExpectedWinRate { get; set; }
			public double CoveragePct { get; set; }
			public double WeightedSampleGames { get; set; }
			public int MatchupsUsed { get; set; }
			public double FallbackWinRate { get; set; }
		}
	}

	internal class QuickDashboardRefreshResult
	{
		public bool EnvironmentUpdated { get; set; }
		public bool RecommendationsUpdated { get; set; }
		public int LocalMatchCount { get; set; }
		public int RecommendationCount { get; set; }
	}
}
