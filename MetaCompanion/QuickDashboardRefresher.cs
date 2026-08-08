using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	internal class QuickDashboardRefresher
	{
		private const int LegacyMinMatchupGames = 200;
		private const double MinCoveragePct = 50.0;
		private const double PrePatchWeight = 0.0;
		private const double MinimumRecencyHalfLifeDays = 0.1;
		private const string RecommendationModelVersion = "beta_dirichlet_soft_v2";
		private static readonly Regex CandidateProbabilityRegex =
			new Regex(@"(?<probability>\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
		private readonly object _lock = new object();
		private DateTime _lastProcessedSourceWriteTimeUtc = DateTime.MinValue;
		private bool _isRunning;
		private bool _pendingForcedRefresh;
		private PluginConfig _pendingConfig;
		private Action _pendingOnCompleted;

		public bool TryRefreshAfterGame(PluginConfig config, Action onCompleted)
		{
			return RequestRefresh(config, onCompleted, false);
		}

		public bool ForceRefresh(PluginConfig config, Action onCompleted)
		{
			return RequestRefresh(config, onCompleted, true);
		}

		private bool RequestRefresh(PluginConfig config, Action onCompleted, bool force)
		{
			var dataDirectory = MetaCompanionPlugin.DataDirectory;
			var sourceWriteTimeUtc = GetLatestSourceWriteTimeUtc(dataDirectory);
			if (!force && !sourceWriteTimeUtc.HasValue)
			{
				return false;
			}
			var effectiveSourceWriteTimeUtc = sourceWriteTimeUtc ?? DateTime.UtcNow;

			lock (_lock)
			{
				if (_isRunning)
				{
					if (force)
					{
						_pendingForcedRefresh = true;
						_pendingConfig = config ?? new PluginConfig();
						_pendingOnCompleted += onCompleted;
						return true;
					}
					return false;
				}
				if (!force && effectiveSourceWriteTimeUtc <= _lastProcessedSourceWriteTimeUtc)
				{
					return false;
				}
				_isRunning = true;
			}

			Task.Run(() => RunRefreshQueue(
				config ?? new PluginConfig(),
				onCompleted,
				effectiveSourceWriteTimeUtc,
				force,
				dataDirectory));
			return true;
		}

		private void RunRefreshQueue(
			PluginConfig config,
			Action onCompleted,
			DateTime sourceWriteTimeUtc,
			bool force,
			string dataDirectory)
		{
			while (true)
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

				if (refreshed)
				{
					try
					{
						onCompleted?.Invoke();
					}
					catch (Exception ex)
					{
						Log.Warn("Quick dashboard completion callback failed: " + ex.Message);
					}
				}

				lock (_lock)
				{
					if (refreshed)
					{
						_lastProcessedSourceWriteTimeUtc = sourceWriteTimeUtc;
					}
					if (!_pendingForcedRefresh)
					{
						_isRunning = false;
						return;
					}

					config = _pendingConfig ?? config;
					onCompleted = _pendingOnCompleted;
					_pendingConfig = null;
					_pendingOnCompleted = null;
					_pendingForcedRefresh = false;
					force = true;
					sourceWriteTimeUtc = GetLatestSourceWriteTimeUtc(dataDirectory) ?? DateTime.UtcNow;
				}
			}
		}

		private static DateTime? GetLatestSourceWriteTimeUtc(string dataDirectory)
		{
			var paths = new[] { MatchHistoryRecorder.GetHistoryPath(dataDirectory) }
				.Concat(ResolveDeckStatsPaths(null));
			var times = paths
				.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
				.Select(File.GetLastWriteTimeUtc)
				.ToList();
			return times.Count == 0
				? (DateTime?)null
				: times.Max();
		}

		internal static QuickDashboardRefreshResult Refresh(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			return Refresh(config, dataDirectory, now, null, null);
		}

		internal static QuickDashboardRefreshResult Refresh(
			PluginConfig config,
			string dataDirectory,
			DateTime now,
			string deckStatsPath,
			IReadOnlyList<Deck> metaDecks)
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
			var sampleWindow = BuildLocalSampleWindow(config, dataDirectory, now);
			var localRows = LoadLocalRows(
				config, dataDirectory, archetypes, corrections, now, deckStatsPath, metaDecks,
				sampleWindow);
			if (localRows.Count > 0)
			{
				var environmentRows = BuildEnvironmentRows(localRows, archetypes);
				WriteEnvironment(dataDirectory, environmentRows, localRows, config, now, sampleWindow);
				result.EnvironmentUpdated = true;
				result.LocalMatchCount = localRows.Count;
			}

			RecommendationModelDiagnostics recommendationDiagnostics;
			var recommendations = BuildRecommendations(
				config,
				dataDirectory,
				metaDirectory,
				archetypes,
				localRows,
				sampleWindow,
				out recommendationDiagnostics);
			if (recommendations.Count > 0)
			{
				var expectedTimeRange = ResolveRemoteSnapshotTimeRange(metaDirectory, config);
				var expectedRankRange = string.IsNullOrWhiteSpace(config.PostGameRankRange)
					? "DIAMOND_THROUGH_LEGEND"
					: config.PostGameRankRange.Trim();
				AttachRepresentativeDecks(
					recommendations,
					LoadRepresentativeDecks(
						Path.Combine(dataDirectory, "archetype_deck_branches.tsv"),
						expectedTimeRange,
						expectedRankRange));
				WriteRecommendations(
					metaDirectory, recommendations, config, localRows.Count, now, sampleWindow,
					recommendationDiagnostics);
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

		private static string GetPatchMarkerPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "patch_marker.txt");
		}

		internal static string GetLocalHistoryClearMarkerPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "local_history_cleared_at.txt");
		}

		private static LocalSampleWindow BuildLocalSampleWindow(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			var historyDays = Math.Max(0, config.LocalRecommendationHistoryDays);
			var historyMatches = Math.Max(0, config.LocalRecommendationHistoryMatches);
			var defaultStart = historyDays > 0
				? now.AddDays(-historyDays)
				: DateTime.MinValue;
			var patchTime = ResolvePatchTime(dataDirectory);
			var sampleWindow = historyDays > 0
				? "last_" + historyDays.ToString(CultureInfo.InvariantCulture) + "_days"
				: "all_available_history";
			var start = defaultStart;

			if (patchTime.HasValue)
			{
				if (patchTime.Value > start)
				{
					start = patchTime.Value;
					sampleWindow = "current_patch";
				}
				else
				{
					sampleWindow += "_within_current_patch";
				}
			}

			var clearedAt = config.LocalRecommendationHistoryClearedAt;
			var markerClearedAt = ResolveLocalHistoryClearTime(dataDirectory);
			if (markerClearedAt.HasValue && markerClearedAt.Value > clearedAt)
			{
				clearedAt = markerClearedAt.Value;
			}
			if (clearedAt > DateTime.MinValue && clearedAt > start)
			{
				start = clearedAt;
				sampleWindow = "after_local_clear";
			}

			return new LocalSampleWindow
			{
				HistoryDays = historyDays,
				HistoryMatches = historyMatches,
				Start = start,
				Name = sampleWindow,
				PatchTime = patchTime,
				ClearedAt = clearedAt > DateTime.MinValue ? (DateTime?)clearedAt : null,
				PrePatchWeight = PrePatchWeight,
				RecencyHalfLifeDays = Math.Max(
					MinimumRecencyHalfLifeDays,
					historyDays > 0 ? historyDays : 3)
			};
		}

		private static DateTime? ResolveLocalHistoryClearTime(string dataDirectory)
		{
			var markerPath = GetLocalHistoryClearMarkerPath(dataDirectory);
			if (!File.Exists(markerPath))
			{
				return null;
			}
			try
			{
				return ParseDate(File.ReadAllText(markerPath, Encoding.UTF8).Trim());
			}
			catch
			{
				return null;
			}
		}

		private static DateTime? ResolvePatchTime(string dataDirectory)
		{
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return null;
			}

			var markerPath = GetPatchMarkerPath(dataDirectory);
			if (!File.Exists(markerPath))
			{
				return null;
			}

			try
			{
				return ParseDate(File.ReadAllText(markerPath, Encoding.UTF8).Trim());
			}
			catch
			{
				return null;
			}
		}

		private static double GetPatchWeight(DateTime value, LocalSampleWindow sampleWindow)
		{
			return sampleWindow.PatchTime.HasValue && value < sampleWindow.PatchTime.Value
				? 0.0
				: 1.0;
		}

		private static double GetRecencyWeight(
			DateTime value,
			DateTime now,
			LocalSampleWindow sampleWindow)
		{
			var ageDays = Math.Max(0.0, (now - value).TotalDays);
			return Math.Pow(
				0.5,
				ageDays / Math.Max(MinimumRecencyHalfLifeDays, sampleWindow.RecencyHalfLifeDays));
		}

		private static List<LocalMatchRow> LoadLocalRows(
			PluginConfig config,
			string dataDirectory,
			ArchetypeLookup archetypes,
			Dictionary<string, MatchCorrection> corrections,
			DateTime now,
			string deckStatsPath,
			IReadOnlyList<Deck> metaDecks,
			LocalSampleWindow sampleWindow)
		{
			var minConfidence = Math.Max(0, config.LocalMetaMinConfidence);
			var localRows = new List<LocalMatchRow>();
			localRows.AddRange(LoadHdtDeckStatsRows(
				config, dataDirectory, archetypes, corrections, now, deckStatsPath, metaDecks,
				sampleWindow));
			localRows.AddRange(LoadPluginHistoryRows(
				config, dataDirectory, archetypes, corrections, now, sampleWindow));
			var deduplicated = DeduplicateLocalRows(localRows);
			if (sampleWindow.HistoryMatches > 0)
			{
				deduplicated = deduplicated
					.OrderByDescending(row => row.EndedAt)
					.Take(sampleWindow.HistoryMatches)
					.OrderBy(row => row.EndedAt)
					.ToList();
			}
			return deduplicated;
		}

		private static List<LocalMatchRow> LoadPluginHistoryRows(
			PluginConfig config,
			string dataDirectory,
			ArchetypeLookup archetypes,
			Dictionary<string, MatchCorrection> corrections,
			DateTime now,
			LocalSampleWindow sampleWindow)
		{
			var historyPath = MatchHistoryRecorder.GetHistoryPath(dataDirectory);
			var rows = ReadTsv(historyPath);
			if (rows.Count == 0)
			{
				return new List<LocalMatchRow>();
			}

			var minConfidence = Math.Max(0, config.LocalMetaMinConfidence);
			var localRows = new List<LocalMatchRow>();
			foreach (var row in rows)
			{
				var endedAt = ParseDate(FirstNonEmpty(Get(row, "ended_at"), Get(row, "started_at")));
				var startedAt = ParseDate(Get(row, "started_at"));
				var patchReferenceTime = startedAt ?? endedAt;
				if (!endedAt.HasValue || !patchReferenceTime.HasValue ||
					patchReferenceTime.Value < sampleWindow.Start)
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
				var hasCorrection = correction != null &&
					!string.IsNullOrWhiteSpace(correction.CorrectedArchetype);
				var archetypeName = correction != null &&
					!string.IsNullOrWhiteSpace(correction.CorrectedArchetype)
					? correction.CorrectedArchetype
					: Get(row, "predicted_archetype");
				if (string.IsNullOrWhiteSpace(archetypeName) ||
					!archetypes.NameToId.ContainsKey(archetypeName))
				{
					var fallbackRow = BuildUnidentifiedPluginHistoryRow(
						row, correction, matchId, endedAt.Value, now, sampleWindow);
					if (fallbackRow != null)
					{
						localRows.Add(fallbackRow);
					}
					continue;
				}

				var confidencePct = ParseDouble(Get(row, "confidence_pct"), 0.0);
				if (correction != null && !string.IsNullOrWhiteSpace(correction.CorrectedArchetype))
				{
					confidencePct = 100.0;
				}
				var archetypeId = archetypes.NameToId[archetypeName];
				var archetype = archetypes.ById.ContainsKey(archetypeId)
					? archetypes.ById[archetypeId]
					: new ArchetypeInfo { Id = archetypeId, Name = archetypeName };
				var effectivePatchReferenceTime = patchReferenceTime.Value;
				var ageDays = Math.Max(0.0, (now - endedAt.Value).TotalDays);
				var patchWeight = GetPatchWeight(effectivePatchReferenceTime, sampleWindow);
				var recencyWeight = GetRecencyWeight(endedAt.Value, now, sampleWindow);
				var result = correction != null && !string.IsNullOrWhiteSpace(correction.CorrectedResult)
					? correction.CorrectedResult
					: Get(row, "result");
				var localRow = new LocalMatchRow
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
					Weight = confidencePct >= minConfidence
						? Clamp(confidencePct / 100.0, 0.25, 1.0) * patchWeight * recencyWeight
						: 0.0,
					PatchWeight = patchWeight,
					RecencyWeight = recencyWeight,
					EvidenceWeight = patchWeight * recencyWeight,
					AgeDays = ageDays,
					EvidenceCount = ParseInt(Get(row, "evidence_cards"), 0),
					EvidenceCards = Get(row, "evidence_cards"),
					CandidateArchetypes = Get(row, "candidate_archetypes"),
					KeyEvidenceCards = Get(row, "key_evidence_cards"),
					ReplayFile = Get(row, "replay_file"),
					ReplayPath = Get(row, "replay_path"),
					HsReplayUploadId = Get(row, "hsreplay_upload_id"),
					HsReplayUrl = Get(row, "hsreplay_url"),
					HasCorrection = hasCorrection,
					Source = "plugin_match_history",
					RecognitionModel = Get(row, "recognition_model")
				};
				ConfigureSoftEvidence(
					localRow, archetypes, hasCorrection, archetypeId, confidencePct,
					localRow.CandidateArchetypes,
					Get(row, "archetype_distribution_json"));
				localRows.Add(localRow);
			}

			return localRows;
		}

		private static LocalMatchRow BuildUnidentifiedPluginHistoryRow(
			Dictionary<string, string> row,
			MatchCorrection correction,
			string matchId,
			DateTime endedAt,
			DateTime now,
			LocalSampleWindow sampleWindow)
		{
			var opponentClass = MetaRetriever.NormalizeClass(Get(row, "opponent_class"));
			if (string.IsNullOrWhiteSpace(opponentClass))
			{
				return null;
			}

			var startedAt = ParseDate(Get(row, "started_at"));
			var patchReferenceTime = startedAt ?? endedAt;
			var result = correction != null && !string.IsNullOrWhiteSpace(correction.CorrectedResult)
				? correction.CorrectedResult
				: Get(row, "result");
			var patchWeight = GetPatchWeight(patchReferenceTime, sampleWindow);
			var recencyWeight = GetRecencyWeight(endedAt, now, sampleWindow);
			return new LocalMatchRow
			{
				MatchId = matchId,
				StartedAt = FirstNonEmpty(
					Get(row, "started_at"),
					endedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
				EndedAt = endedAt,
				Format = Get(row, "format"),
				Mode = Get(row, "mode"),
				Result = result,
				OpponentClass = opponentClass,
				ArchetypeId = 0,
				ArchetypeName = "",
				PlayerClass = opponentClass,
				ConfidencePct = 0.0,
				Weight = 0.0,
				PatchWeight = patchWeight,
				RecencyWeight = recencyWeight,
				EvidenceWeight = patchWeight * recencyWeight,
				UnknownProbability = 1.0,
				SoftUnknownWeight = patchWeight * recencyWeight,
				RecognitionModel = "prediction_softmax_v1",
				RecognitionTier = "unknown",
				AgeDays = Math.Max(0.0, (now - endedAt).TotalDays),
				EvidenceCount = ParseInt(Get(row, "evidence_cards"), 0),
				EvidenceCards = Get(row, "evidence_cards"),
				CandidateArchetypes = Get(row, "candidate_archetypes"),
				KeyEvidenceCards = Get(row, "key_evidence_cards"),
				ReplayFile = Get(row, "replay_file"),
				ReplayPath = Get(row, "replay_path"),
				HsReplayUploadId = Get(row, "hsreplay_upload_id"),
				HsReplayUrl = Get(row, "hsreplay_url"),
				HasCorrection = correction != null,
				Source = "plugin_match_history_unidentified"
			};
		}

		private static List<LocalMatchRow> LoadHdtDeckStatsRows(
			PluginConfig config,
			string dataDirectory,
			ArchetypeLookup archetypes,
			Dictionary<string, MatchCorrection> corrections,
			DateTime now,
			string deckStatsPath,
			IReadOnlyList<Deck> metaDecks,
			LocalSampleWindow sampleWindow)
		{
			var paths = ResolveDeckStatsPaths(deckStatsPath)
				.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
				.ToList();
			if (paths.Count == 0 || archetypes.NameToId.Count == 0)
			{
				return new List<LocalMatchRow>();
			}

			var decks = metaDecks == null
				? MetaRetriever.LoadDeckCodeDecks(dataDirectory)
				: metaDecks.ToList();
			if (decks.Count == 0)
			{
				return new List<LocalMatchRow>();
			}

			var minConfidence = Math.Max(0, config.LocalMetaMinConfidence);
			var localRows = new List<LocalMatchRow>();
			foreach (var path in paths)
			{
				try
				{
					var doc = new XmlDocument();
					doc.Load(path);
					foreach (XmlNode game in doc.SelectNodes("//Game"))
					{
						var startedAt = ParseDate(GetNodeText(game, "StartTime"));
						var endedAt = ParseDate(FirstNonEmpty(
							GetNodeText(game, "EndTime"),
							GetNodeText(game, "StartTime")));
						if (!startedAt.HasValue || !endedAt.HasValue ||
							startedAt.Value < sampleWindow.Start)
						{
							continue;
						}

						var format = GetNodeText(game, "Format");
						var mode = GetNodeText(game, "GameMode");
						if (!IsStandardMatch(format, mode))
						{
							continue;
						}

						var matchId = GetNodeText(game, "GameId");
						var correction = !string.IsNullOrWhiteSpace(matchId) &&
							corrections != null &&
							corrections.ContainsKey(matchId)
							? corrections[matchId]
							: null;
						var hasCorrection = correction != null &&
							!string.IsNullOrWhiteSpace(correction.CorrectedArchetype);
						var opponentClass = MetaRetriever.NormalizeClass(GetNodeText(game, "OpponentHero"));
						if (string.IsNullOrWhiteSpace(opponentClass))
						{
							continue;
						}

						var knownOriginalCards = ParseDeckStatsKnownCards(game);
						var evidenceCards = knownOriginalCards.Values.Sum();
						var classDecks = decks
							.Where(deck => MetaRetriever.NormalizeClass(deck.Class) == opponentClass)
							.ToList();
						var candidateDistribution = PredictionController.BuildCandidateArchetypeDistribution(
							classDecks, knownOriginalCards, evidenceCards);
						var candidates = candidateDistribution.Take(3).ToList();
						var bestCandidate = candidateDistribution
							.FirstOrDefault(candidate => candidate.Probability > 0.0 &&
								archetypes.NameToId.ContainsKey(candidate.Name));
						var archetypeName = hasCorrection
							? correction.CorrectedArchetype
							: bestCandidate == null ? "" : bestCandidate.Name;
						var archetypeId = !string.IsNullOrWhiteSpace(archetypeName) &&
							archetypes.NameToId.ContainsKey(archetypeName)
							? archetypes.NameToId[archetypeName]
							: 0;
						var archetype = archetypeId > 0 && archetypes.ById.ContainsKey(archetypeId)
							? archetypes.ById[archetypeId]
							: new ArchetypeInfo
							{
								Id = archetypeId,
								Name = archetypeId > 0 ? archetypeName : "",
								PlayerClass = opponentClass
							};
						var confidencePct = hasCorrection
							? 100
							: bestCandidate == null ? 0 : bestCandidate.ConfidencePercent;
						var ageDays = Math.Max(0.0, (now - endedAt.Value).TotalDays);
						var patchWeight = GetPatchWeight(startedAt.Value, sampleWindow);
						var recencyWeight = GetRecencyWeight(endedAt.Value, now, sampleWindow);
						var result = hasCorrection &&
							!string.IsNullOrWhiteSpace(correction.CorrectedResult)
							? correction.CorrectedResult
							: NormalizeResult(GetNodeText(game, "Result"));

						var localRow = new LocalMatchRow
						{
							MatchId = matchId,
							StartedAt = startedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
							EndedAt = endedAt.Value,
							Format = format,
							Mode = mode,
							Result = result,
							OpponentClass = opponentClass,
							ArchetypeId = archetypeId,
							ArchetypeName = archetype.Name,
							PlayerClass = archetype.PlayerClass,
							ConfidencePct = confidencePct,
							Weight = confidencePct >= minConfidence
								? Clamp(confidencePct / 100.0, 0.25, 1.0) * patchWeight * recencyWeight
								: 0.0,
							PatchWeight = patchWeight,
							RecencyWeight = recencyWeight,
							EvidenceWeight = patchWeight * recencyWeight,
							AgeDays = ageDays,
							EvidenceCount = evidenceCards,
							EvidenceCards = evidenceCards.ToString(CultureInfo.InvariantCulture),
							CandidateArchetypes = FormatCandidateArchetypes(candidates),
							KeyEvidenceCards = FormatKnownCardEvidence(knownOriginalCards),
							ReplayFile = GetNodeText(game, "ReplayFile"),
							ReplayPath = ResolveReplayPath(GetNodeText(game, "ReplayFile")),
							HsReplayUploadId = GetNestedNodeText(game, "HsReplay/UploadId"),
							HsReplayUrl = GetNestedNodeText(game, "HsReplay/ReplayUrl"),
							HasCorrection = hasCorrection,
							Source = archetypeId > 0
								? "hdt_deckstats"
								: "hdt_deckstats_unidentified"
						};
						ConfigureSoftEvidence(
							localRow, archetypes, hasCorrection, archetypeId, confidencePct,
							localRow.CandidateArchetypes, null, candidateDistribution);
						localRows.Add(localRow);
					}
				}
				catch (Exception ex)
				{
					Log.Warn("Failed to load HDT DeckStats local meta from " + path + ": " + ex.Message);
				}
			}
			return localRows;
		}

		private static string ResolveDeckStatsPath(string deckStatsPath)
		{
			if (!string.IsNullOrWhiteSpace(deckStatsPath))
			{
				return deckStatsPath;
			}

			return ResolveDeckStatsPaths(null).FirstOrDefault() ?? "";
		}

		internal static IReadOnlyList<string> ResolveDeckStatsPaths(string deckStatsPath)
		{
			if (!string.IsNullOrWhiteSpace(deckStatsPath))
			{
				return new[] { deckStatsPath };
			}

			return new[]
			{
				Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "DeckStats.xml"),
				Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "DefaultDeckStats.xml")
			}
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		}

		private static Dictionary<string, int> ParseDeckStatsKnownCards(XmlNode game)
		{
			var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			foreach (XmlNode cardNode in game.SelectNodes("OpponentCards/Card"))
			{
				var cardId = GetAttribute(cardNode, "Id");
				if (string.IsNullOrWhiteSpace(cardId))
				{
					continue;
				}

				var card = Database.GetCardFromId(cardId);
				if (!KnownOriginalCardCounter.IsOriginalConstructedCard(card))
				{
					continue;
				}

				var count = Math.Max(1, ParseInt(GetAttribute(cardNode, "Count"), 1));
				int current;
				counts.TryGetValue(card.Id, out current);
				counts[card.Id] = Math.Min(
					KnownOriginalCardCounter.GetConstructedCopyLimit(card),
					current + count);
			}
			return counts;
		}

		private static List<LocalMatchRow> DeduplicateLocalRows(IEnumerable<LocalMatchRow> rows)
		{
			var groups = new List<List<LocalMatchRow>>();
			foreach (var row in rows
				.Where(row => row != null)
				.OrderBy(row => row.EndedAt)
				.ThenBy(row => ParseDate(row.StartedAt) ?? row.EndedAt))
			{
				var group = groups.FirstOrDefault(existing =>
					IsSameLocalMatch(existing[0], row));
				if (group == null)
				{
					group = new List<LocalMatchRow>();
					groups.Add(group);
				}
				group.Add(row);
			}

			return groups
				.Select(SelectPreferredLocalRow)
				.OrderBy(row => row.EndedAt)
				.ToList();
		}

		private static bool IsSameLocalMatch(LocalMatchRow left, LocalMatchRow right)
		{
			if (!string.IsNullOrWhiteSpace(left.MatchId) &&
				string.Equals(left.MatchId, right.MatchId, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (!string.Equals(
				MetaRetriever.NormalizeClass(left.OpponentClass),
				MetaRetriever.NormalizeClass(right.OpponentClass),
				StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (!string.Equals(
				NormalizeResult(left.Result),
				NormalizeResult(right.Result),
				StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (Math.Abs((left.EndedAt - right.EndedAt).TotalSeconds) <= 90)
			{
				return true;
			}

			var leftStartedAt = ParseDate(left.StartedAt);
			var rightStartedAt = ParseDate(right.StartedAt);
			return leftStartedAt.HasValue &&
				rightStartedAt.HasValue &&
				Math.Abs((leftStartedAt.Value - rightStartedAt.Value).TotalSeconds) <= 90;
		}

		private static LocalMatchRow SelectPreferredLocalRow(IEnumerable<LocalMatchRow> group)
		{
			var rows = group.ToList();
			var selected = rows
				.OrderBy(row => row.HasCorrection ? 0 : 1)
				.ThenBy(row => row.Source == "hdt_deckstats" ? 0 : 1)
				.ThenByDescending(row => row.ConfidencePct)
				.ThenByDescending(row => row.EvidenceCount)
				.First();
			foreach (var donor in rows.Where(row => !ReferenceEquals(row, selected)))
			{
				if (string.IsNullOrWhiteSpace(selected.ReplayFile))
				{
					selected.ReplayFile = donor.ReplayFile;
				}
				if (string.IsNullOrWhiteSpace(selected.ReplayPath))
				{
					selected.ReplayPath = donor.ReplayPath;
				}
				if (string.IsNullOrWhiteSpace(selected.HsReplayUploadId))
				{
					selected.HsReplayUploadId = donor.HsReplayUploadId;
				}
				if (string.IsNullOrWhiteSpace(selected.HsReplayUrl))
				{
					selected.HsReplayUrl = donor.HsReplayUrl;
				}
				if (string.IsNullOrWhiteSpace(selected.KeyEvidenceCards))
				{
					selected.KeyEvidenceCards = donor.KeyEvidenceCards;
				}
			}
			return selected;
		}

		private static string FormatCandidateArchetypes(
			IEnumerable<PredictionInfo.ArchetypeCandidate> candidates)
		{
			return string.Join(" / ", candidates
				.Take(3)
				.Select(candidate => candidate.Name + ":" +
					candidate.ConfidencePercent.ToString(CultureInfo.InvariantCulture) + "% score=" +
					candidate.Score.ToString(CultureInfo.InvariantCulture) + " branchCount=" +
					candidate.BranchCount.ToString(CultureInfo.InvariantCulture)));
		}

		private static void ConfigureSoftEvidence(
			LocalMatchRow row,
			ArchetypeLookup archetypes,
			bool hasCorrection,
			int fallbackArchetypeId,
			double fallbackProbabilityPct,
			string candidateText,
			string distributionJson = null,
			IEnumerable<PredictionInfo.ArchetypeCandidate> exactCandidates = null)
		{
			if (row == null)
			{
				return;
			}

			var assignments = new Dictionary<int, double>();
			if (hasCorrection && fallbackArchetypeId > 0)
			{
				assignments[fallbackArchetypeId] = 1.0;
				row.RecognitionModel = "manual_correction";
			}
			else
			{
				var hasPreciseDistribution = TryAddSerializedDistribution(
					assignments, distributionJson, archetypes);
				if (exactCandidates != null)
				{
					hasPreciseDistribution = true;
					foreach (var candidate in exactCandidates)
					{
						if (candidate == null || candidate.Probability <= 0.0 ||
							string.IsNullOrWhiteSpace(candidate.Name) ||
							!archetypes.NameToId.ContainsKey(candidate.Name))
						{
							continue;
						}
						AddWeight(
							assignments,
							archetypes.NameToId[candidate.Name],
							candidate.Probability);
					}
				}

				if (!hasPreciseDistribution)
				{
					foreach (var part in (candidateText ?? "")
						.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
					{
						var match = CandidateProbabilityRegex.Match(part);
						if (!match.Success)
						{
							continue;
						}
						var separator = part.IndexOf(':');
						var nameEnd = separator >= 0 ? separator : match.Index;
						var name = part.Substring(0, Math.Max(0, nameEnd)).Trim();
						if (string.IsNullOrWhiteSpace(name) || !archetypes.NameToId.ContainsKey(name))
						{
							continue;
						}
						var probabilityPct = ParseDouble(match.Groups["probability"].Value, 0.0);
						AddWeight(
							assignments,
							archetypes.NameToId[name],
							Clamp(probabilityPct / 100.0, 0.0, 1.0));
					}
				}

				if (assignments.Count == 0 && fallbackArchetypeId > 0)
				{
					assignments[fallbackArchetypeId] =
						Clamp(fallbackProbabilityPct / 100.0, 0.0, 1.0);
				}
				if (string.IsNullOrWhiteSpace(row.RecognitionModel))
				{
					row.RecognitionModel = hasPreciseDistribution
						? "prediction_softmax_v2_fit"
						: "prediction_softmax_v1";
				}
			}

			var total = assignments.Values.Sum();
			if (total > 1.0)
			{
				foreach (var id in assignments.Keys.ToList())
				{
					assignments[id] /= total;
				}
				total = 1.0;
			}

			row.ArchetypeProbabilities = assignments
				.Where(pair => pair.Key > 0 && pair.Value > 0.0)
				.Select(pair => new ArchetypeProbability
				{
					ArchetypeId = pair.Key,
					Name = archetypes.ById.ContainsKey(pair.Key)
						? archetypes.ById[pair.Key].Name
						: "",
					Probability = pair.Value
				})
				.OrderByDescending(value => value.Probability)
				.ThenBy(value => value.ArchetypeId)
				.ToList();
			row.KnownProbability = Clamp(total, 0.0, 1.0);
			row.UnknownProbability = 1.0 - row.KnownProbability;
			row.SoftKnownWeight = row.EvidenceWeight * row.KnownProbability;
			row.SoftUnknownWeight = row.EvidenceWeight * row.UnknownProbability;
			row.TopProbability = row.ArchetypeProbabilities.Count == 0
				? 0.0
				: row.ArchetypeProbabilities[0].Probability;
			row.RecognitionTier = GetRecognitionTier(row, hasCorrection);
		}

		private static bool TryAddSerializedDistribution(
			Dictionary<int, double> assignments,
			string distributionJson,
			ArchetypeLookup archetypes)
		{
			if (string.IsNullOrWhiteSpace(distributionJson))
			{
				return false;
			}

			try
			{
				var parsed = CreateSerializer().DeserializeObject(distributionJson);
				var values = AsEnumerable(parsed).Select(AsObject).ToList();
				if (values.Count == 0)
				{
					return false;
				}
				foreach (var value in values)
				{
					var probability = ParseDouble(GetObject(value, "probability"), 0.0);
					if (probability <= 0.0)
					{
						continue;
					}
					var id = ParseInt(GetObject(value, "id"), 0);
					if (id <= 0)
					{
						var name = Convert.ToString(
							GetObject(value, "name"), CultureInfo.InvariantCulture) ?? "";
						if (string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase) ||
							!archetypes.NameToId.ContainsKey(name))
						{
							continue;
						}
						id = archetypes.NameToId[name];
					}
					if (archetypes.ById.ContainsKey(id))
					{
						AddWeight(assignments, id, Clamp(probability, 0.0, 1.0));
					}
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static string GetRecognitionTier(LocalMatchRow row, bool hasCorrection)
		{
			if (hasCorrection)
			{
				return "corrected";
			}
			var first = row.ArchetypeProbabilities.Count > 0
				? row.ArchetypeProbabilities[0].Probability
				: 0.0;
			var second = row.ArchetypeProbabilities.Count > 1
				? row.ArchetypeProbabilities[1].Probability
				: 0.0;
			if (row.UnknownProbability >= first || row.KnownProbability <= 0.0)
			{
				return "unknown";
			}
			if (first >= 0.70 && first - second >= 0.25 && row.UnknownProbability < 0.20)
			{
				return "confirmed";
			}
			if (first >= 0.40 && first - second >= 0.10 && row.UnknownProbability < 0.50)
			{
				return "likely";
			}
			return "mixed";
		}

		private static string SerializeArchetypeDistribution(LocalMatchRow row)
		{
			var values = row.ArchetypeProbabilities.Select(value =>
				{
					return new Dictionary<string, object>
					{
						{ "id", value.ArchetypeId },
						{ "name", value.Name },
						{ "probability", Round(value.Probability, 6) }
					};
				}).ToList();
			values.Add(new Dictionary<string, object>
			{
				{ "id", 0 },
				{ "name", "Unknown" },
				{ "probability", Round(row.UnknownProbability, 6) }
			});
			return CreateSerializer().Serialize(values);
		}

		private static string FormatKnownCardEvidence(IDictionary<string, int> knownOriginalCards)
		{
			if (knownOriginalCards == null || knownOriginalCards.Count == 0)
			{
				return "";
			}

			return string.Join(", ", knownOriginalCards
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key)
				.Take(6)
				.Select(pair =>
					{
						var card = Database.GetCardFromId(pair.Key);
						var name = card == null
							? pair.Key
							: !string.IsNullOrWhiteSpace(card.LocalizedName)
								? card.LocalizedName
								: !string.IsNullOrWhiteSpace(card.Name) ? card.Name : pair.Key;
						return name + (pair.Value > 1
							? "×" + pair.Value.ToString(CultureInfo.InvariantCulture)
							: "");
					}));
		}

		private static string NormalizeResult(string value)
		{
			if (string.Equals(value, "Win", StringComparison.OrdinalIgnoreCase))
			{
				return "win";
			}
			if (string.Equals(value, "Loss", StringComparison.OrdinalIgnoreCase))
			{
				return "loss";
			}
			return (value ?? "").Trim().ToLowerInvariant();
		}

		private static string ResolveReplayPath(string replayFile)
		{
			if (string.IsNullOrWhiteSpace(replayFile))
			{
				return "";
			}

			var candidate = Path.Combine(
				Hearthstone_Deck_Tracker.Config.AppDataPath,
				"Replays",
				replayFile);
			return File.Exists(candidate) ? candidate : "";
		}

		private static string GetNodeText(XmlNode node, string name)
		{
			return GetNestedNodeText(node, name);
		}

		private static string GetNestedNodeText(XmlNode node, string xpath)
		{
			var child = node == null || string.IsNullOrWhiteSpace(xpath)
				? null
				: node.SelectSingleNode(xpath);
			return child == null ? "" : child.InnerText ?? "";
		}

		private static string GetAttribute(XmlNode node, string name)
		{
			return node?.Attributes?[name]?.Value ?? "";
		}

		private static bool IsStandardMatch(Dictionary<string, string> row)
		{
			return IsStandardMatch(Get(row, "format"), Get(row, "mode"));
		}

		private static bool IsStandardMatch(string format, string mode)
		{
			if (!string.IsNullOrWhiteSpace(format) &&
				!string.Equals(format, "Standard", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			return string.IsNullOrWhiteSpace(mode) ||
				string.Equals(mode, "Ranked", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(mode, "Casual", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(mode, "Friendly", StringComparison.OrdinalIgnoreCase);
		}

		private static List<EnvironmentRow> BuildEnvironmentRows(
			List<LocalMatchRow> localRows,
			ArchetypeLookup archetypes)
		{
			var identifiedRows = localRows
				.Where(IsIdentifiedLocalRow)
				.ToList();
			var totalWeight = identifiedRows.Sum(row => row.Weight);
			var rank = 1;
			return identifiedRows
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
			string dataDirectory,
			string metaDirectory,
			ArchetypeLookup archetypes,
			List<LocalMatchRow> localRows,
			LocalSampleWindow sampleWindow,
			out RecommendationModelDiagnostics diagnostics)
		{
			diagnostics = new RecommendationModelDiagnostics
			{
				ModelVersion = RecommendationModelVersion,
				MatchupPriorGames = ResolvePositiveSetting(
					config.RecommendationMatchupPriorGames,
					RecommendationStatistics.DefaultMatchupPriorGames),
				RemotePriorGames = ResolvePositiveSetting(
					config.RecommendationRemotePriorGames,
					RecommendationStatistics.DefaultRemotePriorGames),
				PosteriorDraws = Math.Max(200, Math.Min(5000, config.RecommendationPosteriorDraws))
			};
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
			if (!RemoteSnapshotMatchesScope(
				config, summary, matrix, sampleWindow, dataDirectory))
			{
				return new List<RecommendationRow>();
			}

			var environment = BuildRecommendationEnvironment(
				config,
				summary,
				archetypes,
				localRows);
			var environmentRows = environment.Rows;
			diagnostics.RemotePriorGames = environment.RemotePriorGames;
			diagnostics.LocalKnownEvidence = environment.LocalKnownEvidence;
			diagnostics.LocalUnknownEvidence = environment.LocalUnknownEvidence;
			diagnostics.LocalEffectiveSampleSize = environment.LocalEffectiveSampleSize;
			diagnostics.EffectiveLocalWeight = environment.EffectiveLocalWeight;
			diagnostics.RecommendationLocalMatchCount = environment.RecommendationLocalMatchCount;
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
				var fallbackWinRate = ParseValidPercent(GetObject(candidateMeta, "win_rate"), 50.0);
				var weightedWinRate = 0.0;
				var coverageWeight = 0.0;
				var legacyCoverageWeight = 0.0;
				var weightedGames = 0.0;
				var matchupsUsed = 0;
				var legacyMatchupsUsed = 0;
				var matchupVariance = 0.0;
				var alphaWeightedMeanSquare = 0.0;
				foreach (var opponent in environmentRows)
				{
					var observedGames = 0.0;
					var observedWinRate = fallbackWinRate;
					var hasObservedCell = false;
					var cell = GetMatchupCell(data, candidateId, opponent.ArchetypeId);
					if (cell != null)
					{
						var totalGames = ParseDouble(GetObject(cell, "total_games"), 0.0);
						double parsedWinRate;
						if (totalGames > 0.0 &&
							TryParseValidPercent(GetObject(cell, "win_rate"), out parsedWinRate))
						{
							observedGames = totalGames;
							observedWinRate = parsedWinRate;
							hasObservedCell = true;
							matchupsUsed++;
							weightedGames += opponent.Weight * observedGames;
							if (observedGames >= LegacyMinMatchupGames)
							{
								legacyCoverageWeight += opponent.Weight;
								legacyMatchupsUsed++;
							}
						}
					}

					var posteriorMean = RecommendationStatistics.CalculatePosteriorMeanPercent(
						fallbackWinRate,
						observedWinRate,
						observedGames,
						diagnostics.MatchupPriorGames);
					var posteriorVariance = RecommendationStatistics
						.CalculatePosteriorVariancePercentSquared(
							fallbackWinRate,
							observedWinRate,
							observedGames,
							diagnostics.MatchupPriorGames);
					var dataShare = hasObservedCell
						? RecommendationStatistics.CalculateDataShare(
							observedGames, diagnostics.MatchupPriorGames)
						: 0.0;
					weightedWinRate += opponent.Weight * posteriorMean;
					coverageWeight += opponent.Weight * dataShare;
					alphaWeightedMeanSquare += opponent.Alpha * posteriorMean * posteriorMean;
					matchupVariance += opponent.Alpha * (opponent.Alpha + 1.0) /
						(environment.TotalAlpha * (environment.TotalAlpha + 1.0)) *
						posteriorVariance;
				}
				var environmentVariance = Math.Max(
					0.0,
					(alphaWeightedMeanSquare / environment.TotalAlpha -
						weightedWinRate * weightedWinRate) /
					(environment.TotalAlpha + 1.0));

				recommendations.Add(new RecommendationRow
				{
					ArchetypeId = candidateId,
					Name = candidate.Name,
					PlayerClass = candidate.PlayerClass,
					ExpectedWinRate = weightedWinRate,
					CoveragePct = coverageWeight * 100.0,
					LegacyCoveragePct = legacyCoverageWeight * 100.0,
					WeightedSampleGames = weightedGames,
					MatchupsUsed = matchupsUsed,
					LegacyMatchupsUsed = legacyMatchupsUsed,
					FallbackWinRate = fallbackWinRate,
					PosteriorVariance = environmentVariance + matchupVariance
				});
			}

			var eligible = recommendations
				.Where(row => row.CoveragePct >= MinCoveragePct)
				.ToList();
			var distributions = eligible.Select(row => new RecommendationDistribution
				{
					ArchetypeId = row.ArchetypeId,
					Mean = row.ExpectedWinRate,
					Variance = row.PosteriorVariance
				})
				.ToList();
			RecommendationStatistics.PopulateApproximateRanking(
				distributions,
				diagnostics.PosteriorDraws);
			var distributionById = distributions.ToDictionary(row => row.ArchetypeId);
			foreach (var row in eligible)
			{
				var distribution = distributionById[row.ArchetypeId];
				row.ExpectedWinRateLow90 = distribution.Lower90;
				row.ExpectedWinRateHigh90 = distribution.Upper90;
				row.ProbabilityBest = distribution.ProbabilityBest;
				row.Tier = distribution.Tier;
			}

			var rank = 1;
			return eligible
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

		private static bool RemoteSnapshotMatchesScope(
			PluginConfig config,
			Dictionary<string, object> summary,
			Dictionary<string, object> matrix,
			LocalSampleWindow sampleWindow,
			string dataDirectory)
		{
			config = config ?? new PluginConfig();
			var timeRange = Convert.ToString(
				GetObject(summary, "time_range"), CultureInfo.InvariantCulture) ?? "";
			var rankRange = Convert.ToString(
				GetObject(summary, "rank_range"), CultureInfo.InvariantCulture) ?? "";
			var expectedRankRange = string.IsNullOrWhiteSpace(config.PostGameRankRange)
				? "DIAMOND_THROUGH_LEGEND"
				: config.PostGameRankRange.Trim();
			if (!GetCompatibleRemoteTimeRanges(config).Contains(timeRange) ||
				!string.Equals(rankRange, expectedRankRange, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			// CURRENT_PATCH is a server-owned tag. The local patch marker records when
			// this client noticed/installed the patch, which may be later than the
			// server's CURRENT_PATCH as_of and therefore cannot be its lower bound.
			if (string.Equals(timeRange, "CURRENT_PATCH", StringComparison.OrdinalIgnoreCase))
			{
				var remotePatchVersion = NormalizePublicPatchVersion(Convert.ToString(
					GetObject(summary, "patch_version"), CultureInfo.InvariantCulture));
				var localPatchVersion = ReadLocalPublicPatchVersion(dataDirectory);
				return !string.IsNullOrWhiteSpace(remotePatchVersion) &&
					string.Equals(
						remotePatchVersion,
						localPatchVersion,
						StringComparison.OrdinalIgnoreCase);
			}

			if (sampleWindow == null || !sampleWindow.PatchTime.HasValue)
			{
				return true;
			}

			var summaryAsOf = ParseDate(Convert.ToString(
				GetObject(summary, "as_of"), CultureInfo.InvariantCulture));
			var matrixAsOf = ParseDate(Convert.ToString(
				GetObject(matrix, "as_of"), CultureInfo.InvariantCulture));
			return summaryAsOf.HasValue && matrixAsOf.HasValue &&
				summaryAsOf.Value >= sampleWindow.PatchTime.Value &&
				matrixAsOf.Value >= sampleWindow.PatchTime.Value;
		}

		private static HashSet<string> GetCompatibleRemoteTimeRanges(PluginConfig config)
		{
			config = config ?? new PluginConfig();
			return new HashSet<string>(new[]
				{
					string.IsNullOrWhiteSpace(config.PostGamePrimaryTimeRange)
						? "LAST_7_DAYS"
						: config.PostGamePrimaryTimeRange.Trim(),
					string.IsNullOrWhiteSpace(config.PostGameMetaFallbackTimeRange)
						? "LAST_1_DAY"
						: config.PostGameMetaFallbackTimeRange.Trim(),
					"CURRENT_PATCH"
				},
				StringComparer.OrdinalIgnoreCase);
		}

		private static string ReadLocalPublicPatchVersion(string dataDirectory)
		{
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return "";
			}
			var path = Path.Combine(dataDirectory, "patch_version.txt");
			try
			{
				return File.Exists(path)
					? NormalizePublicPatchVersion(File.ReadAllText(path, Encoding.UTF8))
					: "";
			}
			catch
			{
				return "";
			}
		}

		private static string NormalizePublicPatchVersion(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}
			var match = Regex.Match(
				value,
				@"\b(?<version>\d+\.\d+\.\d+)(?:\.\d+)?\b",
				RegexOptions.CultureInvariant);
			return match.Success ? match.Groups["version"].Value : "";
		}

		private static string ResolveRemoteSnapshotTimeRange(
			string metaDirectory,
			PluginConfig config)
		{
			try
			{
				var summary = AsObject(ReadJson(GetSummaryPath(metaDirectory)));
				var actual = Convert.ToString(
					GetObject(summary, "time_range"), CultureInfo.InvariantCulture);
				if (!string.IsNullOrWhiteSpace(actual))
				{
					return actual.Trim();
				}
			}
			catch
			{
				// BuildRecommendations reports an unusable snapshot by returning no rows.
			}

			return config == null || string.IsNullOrWhiteSpace(config.PostGamePrimaryTimeRange)
				? "LAST_7_DAYS"
				: config.PostGamePrimaryTimeRange.Trim();
		}

		private static RecommendationEnvironmentModel BuildRecommendationEnvironment(
			PluginConfig config,
			Dictionary<string, object> summary,
			ArchetypeLookup archetypes,
			List<LocalMatchRow> localRows)
		{
			var recommendationLocalRows = (localRows ?? new List<LocalMatchRow>())
				.Where(IsRankedRecommendationRow)
				.ToList();
			var alpha = new Dictionary<int, double>();
			var remotePriorGames = ResolvePositiveSetting(
				config.RecommendationRemotePriorGames,
				RecommendationStatistics.DefaultRemotePriorGames);
			var remoteRows = ReadRemoteEnvironment(summary)
				.Where(row => archetypes.ById.ContainsKey(row.ArchetypeId))
				.ToList();
			var remoteTotal = remoteRows.Sum(row => row.Weight);
			if (remoteTotal > 0.0)
			{
				foreach (var row in remoteRows)
				{
					AddWeight(alpha, row.ArchetypeId, row.Weight / remoteTotal * remotePriorGames);
				}
			}

			foreach (var row in recommendationLocalRows)
			{
				foreach (var probability in row.ArchetypeProbabilities)
				{
					if (probability.ArchetypeId > 0 &&
						archetypes.ById.ContainsKey(probability.ArchetypeId))
					{
						AddWeight(
							alpha,
							probability.ArchetypeId,
							row.EvidenceWeight * probability.Probability);
					}
				}
			}

			var localKnownEvidence = recommendationLocalRows
				.Sum(row => row.SoftKnownWeight);
			var localUnknownEvidence = recommendationLocalRows
				.Sum(row => row.SoftUnknownWeight);
			var totalAlpha = alpha.Values.Sum();
			var model = new RecommendationEnvironmentModel
			{
				RemotePriorGames = remotePriorGames,
				LocalKnownEvidence = localKnownEvidence,
				LocalUnknownEvidence = localUnknownEvidence,
				LocalEffectiveSampleSize = RecommendationStatistics.CalculateKishEffectiveSampleSize(
					recommendationLocalRows.Select(row => row.SoftKnownWeight)),
				EffectiveLocalWeight = RecommendationStatistics.CalculateAdaptiveLocalWeight(
					localKnownEvidence, remotePriorGames),
				RecommendationLocalMatchCount = recommendationLocalRows.Count,
				TotalAlpha = totalAlpha
			};
			if (totalAlpha <= 0.0)
			{
				return model;
			}

			model.Rows = alpha
				.Where(pair => pair.Key > 0 && pair.Value > 0.0 && archetypes.ById.ContainsKey(pair.Key))
				.Select(pair => new RecommendationEnvironmentRow
				{
					ArchetypeId = pair.Key,
					Alpha = pair.Value,
					Weight = pair.Value / totalAlpha
				})
				.OrderByDescending(row => row.Weight)
				.ToList();
			return model;
		}

		private static double ResolvePositiveSetting(double value, double fallback)
		{
			return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
				? value
				: fallback;
		}

		private static bool TryParseValidPercent(object value, out double result)
		{
			result = ParseDouble(value, double.NaN);
			return !double.IsNaN(result) && !double.IsInfinity(result) &&
				result >= 0.0 && result <= 100.0;
		}

		private static double ParseValidPercent(object value, double fallback)
		{
			double result;
			return TryParseValidPercent(value, out result) ? result : fallback;
		}

		private static bool IsIdentifiedLocalRow(LocalMatchRow row)
		{
			return row != null &&
				row.ArchetypeId > 0 &&
				row.Weight > 0.0 &&
				!string.IsNullOrWhiteSpace(row.ArchetypeName);
		}

		private static bool IsRankedRecommendationRow(LocalMatchRow row)
		{
			return row != null &&
				(string.IsNullOrWhiteSpace(row.Mode) ||
					string.Equals(row.Mode, "Ranked", StringComparison.OrdinalIgnoreCase));
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
			var corrections = new Dictionary<string, MatchCorrection>(StringComparer.OrdinalIgnoreCase);
			foreach (var row in ReadTsv(path)
				.Where(row => !string.IsNullOrWhiteSpace(Get(row, "match_id"))))
			{
				corrections[Get(row, "match_id")] = new MatchCorrection
					{
						CorrectedArchetype = Get(row, "corrected_archetype"),
						CorrectedResult = Get(row, "corrected_result")
					};
			}
			return corrections;
		}

		private static Dictionary<int, RepresentativeDecks> LoadRepresentativeDecks(
			string path,
			string expectedTimeRange,
			string expectedRankRange)
		{
			var result = new Dictionary<int, RepresentativeDecks>();
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return result;
			}

			var comments = File.ReadLines(path, Encoding.UTF8)
				.Take(24)
				.Where(line => line.StartsWith("#", StringComparison.Ordinal))
				.ToList();
			var timeRange = ReadCommentValue(comments, "# CandidateTimeRange:");
			var rankRange = ReadCommentValue(comments, "# RankRange:");
			if (!string.Equals(timeRange, expectedTimeRange, StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(rankRange, expectedRankRange, StringComparison.OrdinalIgnoreCase))
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

				var archetypeId = ParseInt(values[3], 0);
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
					Games = Math.Max(0, ParseInt(values[6], 0)),
					WinRate = ParseDouble(values[7], 0.0)
				});
			}

			foreach (var group in candidates.GroupBy(candidate => candidate.ArchetypeId))
			{
				var highestWinRate = group
					.OrderByDescending(candidate => candidate.WinRate)
					.ThenByDescending(candidate => candidate.Games)
					.ThenBy(candidate => candidate.DeckId)
					.First();
				var mostPopular = group
					.OrderByDescending(candidate => candidate.Games)
					.ThenByDescending(candidate => candidate.WinRate)
					.ThenBy(candidate => candidate.DeckId)
					.First();
				result[group.Key] = new RepresentativeDecks
				{
					HighestWinRate = highestWinRate,
					MostPopular = mostPopular
				};
			}

			return result;
		}

		private static string ReadCommentValue(
			IEnumerable<string> lines,
			string prefix)
		{
			var line = (lines ?? Enumerable.Empty<string>()).FirstOrDefault(value =>
				value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
			return line == null ? "" : line.Substring(prefix.Length).Trim();
		}

		private static void AttachRepresentativeDecks(
			IEnumerable<RecommendationRow> recommendations,
			Dictionary<int, RepresentativeDecks> representativeDecks)
		{
			if (recommendations == null || representativeDecks == null)
			{
				return;
			}

			foreach (var recommendation in recommendations)
			{
				RepresentativeDecks decks;
				if (!representativeDecks.TryGetValue(recommendation.ArchetypeId, out decks))
				{
					continue;
				}
				recommendation.HighestWinRateDeck = decks.HighestWinRate;
				recommendation.MostPopularDeck = decks.MostPopular;
			}
		}

		private static void WriteEnvironment(
			string dataDirectory,
			List<EnvironmentRow> environmentRows,
			List<LocalMatchRow> localRows,
			PluginConfig config,
			DateTime now,
			LocalSampleWindow sampleWindow)
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
				"game_id\tstart_time\tend_time\tresult\tplayer_deck_name\tplayer_hero\topponent_hero\topponent_class\topponent_card_count\trelevant_cards\tmatched_cards\tpredicted_archetype_id\tpredicted_archetype\tconfidence_pct\tweight\tpatch_weight\trecency_weight\tage_days\tcoverage_pct\tbest_branch_rank\tbest_branch_deck_id\tcandidate_archetypes\treplay_file\treplay_path\thsreplay_upload_id\thsreplay_url\tsource\tkey_evidence_cards\trecognition_model\ttop_probability_pct\tunknown_probability_pct\trecognition_tier\tarchetype_distribution_json\tevidence_weight\tsoft_known_weight\tsoft_unknown_weight\tformat\tmode"
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
					FormatDouble(row.PatchWeight, 4),
					FormatDouble(row.RecencyWeight, 4),
					FormatDouble(row.AgeDays, 3),
					"",
					"",
					"",
					row.CandidateArchetypes,
					row.ReplayFile,
					row.ReplayPath,
					row.HsReplayUploadId,
					row.HsReplayUrl,
					row.Source,
					row.KeyEvidenceCards,
					row.RecognitionModel,
					FormatDouble(row.TopProbability * 100.0, 2),
					FormatDouble(row.UnknownProbability * 100.0, 2),
					row.RecognitionTier,
					SerializeArchetypeDistribution(row),
					FormatDouble(row.EvidenceWeight, 4),
					FormatDouble(row.SoftKnownWeight, 4),
					FormatDouble(row.SoftUnknownWeight, 4),
					row.Format,
					row.Mode
				})));
			WriteAllLinesAtomic(GetLocalGamesPath(dataDirectory), gameLines);

			var serializer = CreateSerializer();
			var sourceCounts = localRows
				.GroupBy(row => string.IsNullOrWhiteSpace(row.Source) ? "unknown" : row.Source)
				.ToDictionary(group => group.Key, group => (object)group.Count());
			var payload = new Dictionary<string, object>
			{
				{ "schema_version", 2 },
				{ "generated_at", now.ToString("o", CultureInfo.InvariantCulture) },
				{ "history_path", MatchHistoryRecorder.GetHistoryPath(dataDirectory) },
				{ "hdt_deck_stats_path", ResolveDeckStatsPath(null) },
				{ "hdt_deck_stats_paths", ResolveDeckStatsPaths(null).ToArray() },
				{ "hdt_deck_stats_existing_paths", ResolveDeckStatsPaths(null)
					.Where(File.Exists)
					.ToArray() },
				{ "local_source", "hdt_deckstats_plus_plugin_match_history" },
				{ "source_counts", sourceCounts },
				{ "history_days", Math.Max(0, config.LocalRecommendationHistoryDays) },
				{ "history_matches", Math.Max(0, config.LocalRecommendationHistoryMatches) },
				{ "sample_window", sampleWindow.Name },
				{ "sample_window_start", sampleWindow.Start.ToString("o", CultureInfo.InvariantCulture) },
				{ "local_history_cleared_at", sampleWindow.ClearedAt.HasValue
					? (object)sampleWindow.ClearedAt.Value.ToString("o", CultureInfo.InvariantCulture)
					: null },
				{ "min_confidence", Math.Max(0, config.LocalMetaMinConfidence) },
				{ "patch_time", sampleWindow.PatchTime.HasValue
					? (object)sampleWindow.PatchTime.Value.ToString("o", CultureInfo.InvariantCulture)
					: null },
				{ "pre_patch_weight", sampleWindow.PrePatchWeight },
				{ "recency_half_life_days", sampleWindow.RecencyHalfLifeDays },
				{ "patch_marker_path", GetPatchMarkerPath(dataDirectory) },
				{ "game_count", localRows.Count },
				{ "weighted_game_count", Round(localRows.Sum(row => row.Weight), 3) },
				{ "soft_known_evidence", Round(localRows.Sum(row => row.SoftKnownWeight), 3) },
				{ "soft_unknown_evidence", Round(localRows.Sum(row => row.SoftUnknownWeight), 3) },
				{ "soft_unknown_pct", localRows.Sum(row => row.EvidenceWeight) > 0.0
					? (object)Round(localRows.Sum(row => row.SoftUnknownWeight) /
						localRows.Sum(row => row.EvidenceWeight) * 100.0, 2)
					: 0.0 },
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
			DateTime now,
			LocalSampleWindow sampleWindow,
			RecommendationModelDiagnostics diagnostics)
		{
			Directory.CreateDirectory(metaDirectory);
			var lines = new List<string>
			{
				"rank\tarchetype_id\tname\tplayer_class\texpected_win_rate\tcoverage_pct\tweighted_sample_games\tmatchups_used\tfallback_win_rate\texpected_win_rate_low_90\texpected_win_rate_high_90\tprobability_best_pct\ttier\tmodel_version\tlegacy_coverage_pct\tlegacy_matchups_used\thighest_winrate_deck_code\thighest_winrate_deck_id\thighest_winrate_deck_win_rate\thighest_winrate_deck_games\tmost_popular_deck_code\tmost_popular_deck_id\tmost_popular_deck_win_rate\tmost_popular_deck_games"
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
				FormatDouble(row.FallbackWinRate, 2),
				FormatDouble(row.ExpectedWinRateLow90, 2),
				FormatDouble(row.ExpectedWinRateHigh90, 2),
				FormatDouble(row.ProbabilityBest * 100.0, 2),
				row.Tier.ToString(CultureInfo.InvariantCulture),
				diagnostics.ModelVersion,
				FormatDouble(row.LegacyCoveragePct, 2),
				row.LegacyMatchupsUsed.ToString(CultureInfo.InvariantCulture),
				row.HighestWinRateDeck == null ? "" : row.HighestWinRateDeck.DeckCode,
				row.HighestWinRateDeck == null ? "" : row.HighestWinRateDeck.DeckId,
				row.HighestWinRateDeck == null ? "" : FormatDouble(row.HighestWinRateDeck.WinRate, 2),
				row.HighestWinRateDeck == null ? "" : row.HighestWinRateDeck.Games.ToString(CultureInfo.InvariantCulture),
				row.MostPopularDeck == null ? "" : row.MostPopularDeck.DeckCode,
				row.MostPopularDeck == null ? "" : row.MostPopularDeck.DeckId,
				row.MostPopularDeck == null ? "" : FormatDouble(row.MostPopularDeck.WinRate, 2),
				row.MostPopularDeck == null ? "" : row.MostPopularDeck.Games.ToString(CultureInfo.InvariantCulture)
			})));
			WriteAllLinesAtomic(Path.Combine(metaDirectory, "personal_recommendations.tsv"), lines);

			var serializer = CreateSerializer();
			var payload = new Dictionary<string, object>
			{
				{ "schema_version", 2 },
				{ "model_version", diagnostics.ModelVersion },
				{ "generated_at", now.ToString("o", CultureInfo.InvariantCulture) },
				{ "meta_directory", metaDirectory },
				{ "history_days", Math.Max(0, config.LocalRecommendationHistoryDays) },
				{ "history_matches", Math.Max(0, config.LocalRecommendationHistoryMatches) },
				{ "local_weight", diagnostics.EffectiveLocalWeight },
				{ "remote_weight", 1.0 - diagnostics.EffectiveLocalWeight },
				{ "legacy_local_weight_setting", Clamp(config.LocalRecommendationWeight, 0.0, 1.0) },
				{ "remote_prior_games", diagnostics.RemotePriorGames },
				{ "matchup_prior_games", diagnostics.MatchupPriorGames },
				{ "local_known_evidence", diagnostics.LocalKnownEvidence },
				{ "local_unknown_evidence", diagnostics.LocalUnknownEvidence },
				{ "local_effective_sample_size", diagnostics.LocalEffectiveSampleSize },
				{ "recommendation_local_match_count", diagnostics.RecommendationLocalMatchCount },
				{ "recommendation_mode", "Standard Ranked" },
				{ "posterior_draws", diagnostics.PosteriorDraws },
				{ "uncertainty_method", "dirichlet_beta_moments_with_normal_rank_draws" },
				{ "coverage_model", "posterior_data_share" },
				{ "local_source", "hdt_deckstats_plus_plugin_match_history" },
				{ "local_match_count", localMatchCount },
				{ "sample_window", sampleWindow.Name },
				{ "sample_window_start", sampleWindow.Start.ToString("o", CultureInfo.InvariantCulture) },
				{ "local_history_cleared_at", sampleWindow.ClearedAt.HasValue
					? (object)sampleWindow.ClearedAt.Value.ToString("o", CultureInfo.InvariantCulture)
					: null },
				{ "patch_time", sampleWindow.PatchTime.HasValue
					? (object)sampleWindow.PatchTime.Value.ToString("o", CultureInfo.InvariantCulture)
					: null },
				{ "recency_half_life_days", sampleWindow.RecencyHalfLifeDays },
				{ "min_matchup_games", LegacyMinMatchupGames },
				{ "min_matchup_games_mode", "legacy_diagnostic_only" },
				{ "min_coverage_pct", MinCoveragePct },
				{ "recommendations", recommendations.Select(row => new Dictionary<string, object>
					{
						{ "rank", row.Rank },
						{ "archetype_id", row.ArchetypeId },
						{ "name", row.Name },
						{ "player_class", row.PlayerClass },
						{ "expected_win_rate", Round(row.ExpectedWinRate, 2) },
						{ "expected_win_rate_low_90", Round(row.ExpectedWinRateLow90, 2) },
						{ "expected_win_rate_high_90", Round(row.ExpectedWinRateHigh90, 2) },
						{ "probability_best_pct", Round(row.ProbabilityBest * 100.0, 2) },
						{ "tier", row.Tier },
						{ "coverage_pct", Round(row.CoveragePct, 2) },
						{ "legacy_coverage_pct", Round(row.LegacyCoveragePct, 2) },
						{ "weighted_sample_games", Round(row.WeightedSampleGames, 1) },
						{ "matchups_used", row.MatchupsUsed },
						{ "legacy_matchups_used", row.LegacyMatchupsUsed },
						{ "fallback_win_rate", Round(row.FallbackWinRate, 2) },
						{ "highest_winrate_deck_code", row.HighestWinRateDeck == null ? "" : row.HighestWinRateDeck.DeckCode },
						{ "highest_winrate_deck_id", row.HighestWinRateDeck == null ? "" : row.HighestWinRateDeck.DeckId },
						{ "highest_winrate_deck_win_rate", row.HighestWinRateDeck == null ? (object)"" : Round(row.HighestWinRateDeck.WinRate, 2) },
						{ "highest_winrate_deck_games", row.HighestWinRateDeck == null ? (object)0 : row.HighestWinRateDeck.Games },
						{ "most_popular_deck_code", row.MostPopularDeck == null ? "" : row.MostPopularDeck.DeckCode },
						{ "most_popular_deck_id", row.MostPopularDeck == null ? "" : row.MostPopularDeck.DeckId },
						{ "most_popular_deck_win_rate", row.MostPopularDeck == null ? (object)"" : Round(row.MostPopularDeck.WinRate, 2) },
						{ "most_popular_deck_games", row.MostPopularDeck == null ? (object)0 : row.MostPopularDeck.Games }
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

		private class LocalSampleWindow
		{
			public int HistoryDays { get; set; }
			public int HistoryMatches { get; set; }
			public string Name { get; set; } = "";
			public DateTime Start { get; set; }
			public DateTime? PatchTime { get; set; }
			public DateTime? ClearedAt { get; set; }
			public double PrePatchWeight { get; set; }
			public double RecencyHalfLifeDays { get; set; }
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
			public double EvidenceWeight { get; set; }
			public double SoftKnownWeight { get; set; }
			public double SoftUnknownWeight { get; set; }
			public double KnownProbability { get; set; }
			public double UnknownProbability { get; set; } = 1.0;
			public double TopProbability { get; set; }
			public double PatchWeight { get; set; } = 1.0;
			public double RecencyWeight { get; set; }
			public double AgeDays { get; set; }
			public int EvidenceCount { get; set; }
			public string EvidenceCards { get; set; } = "";
			public string CandidateArchetypes { get; set; } = "";
			public string KeyEvidenceCards { get; set; } = "";
			public string ReplayFile { get; set; } = "";
			public string ReplayPath { get; set; } = "";
			public string HsReplayUploadId { get; set; } = "";
			public string HsReplayUrl { get; set; } = "";
			public bool HasCorrection { get; set; }
			public string Source { get; set; } = "";
			public string RecognitionModel { get; set; } = "";
			public string RecognitionTier { get; set; } = "unknown";
			public List<ArchetypeProbability> ArchetypeProbabilities { get; set; } =
				new List<ArchetypeProbability>();
		}

		private class ArchetypeProbability
		{
			public int ArchetypeId { get; set; }
			public string Name { get; set; } = "";
			public double Probability { get; set; }
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
			public double Alpha { get; set; }
			public double Weight { get; set; }
		}

		private class RecommendationEnvironmentModel
		{
			public List<RecommendationEnvironmentRow> Rows { get; set; } =
				new List<RecommendationEnvironmentRow>();
			public double RemotePriorGames { get; set; }
			public double LocalKnownEvidence { get; set; }
			public double LocalUnknownEvidence { get; set; }
			public double LocalEffectiveSampleSize { get; set; }
			public double EffectiveLocalWeight { get; set; }
			public int RecommendationLocalMatchCount { get; set; }
			public double TotalAlpha { get; set; }
		}

		private class RecommendationRow
		{
			public int Rank { get; set; }
			public int ArchetypeId { get; set; }
			public string Name { get; set; } = "";
			public string PlayerClass { get; set; } = "";
			public double ExpectedWinRate { get; set; }
			public double ExpectedWinRateLow90 { get; set; }
			public double ExpectedWinRateHigh90 { get; set; }
			public double ProbabilityBest { get; set; }
			public int Tier { get; set; }
			public double CoveragePct { get; set; }
			public double LegacyCoveragePct { get; set; }
			public double WeightedSampleGames { get; set; }
			public int MatchupsUsed { get; set; }
			public int LegacyMatchupsUsed { get; set; }
			public double FallbackWinRate { get; set; }
			public double PosteriorVariance { get; set; }
			public RepresentativeDeckCandidate HighestWinRateDeck { get; set; }
			public RepresentativeDeckCandidate MostPopularDeck { get; set; }
		}

		private class RepresentativeDecks
		{
			public RepresentativeDeckCandidate HighestWinRate { get; set; }
			public RepresentativeDeckCandidate MostPopular { get; set; }
		}

		private class RepresentativeDeckCandidate
		{
			public int ArchetypeId { get; set; }
			public string DeckCode { get; set; } = "";
			public string DeckId { get; set; } = "";
			public int Games { get; set; }
			public double WinRate { get; set; }
		}

		private class RecommendationModelDiagnostics
		{
			public string ModelVersion { get; set; } = "";
			public double MatchupPriorGames { get; set; }
			public double RemotePriorGames { get; set; }
			public double LocalKnownEvidence { get; set; }
			public double LocalUnknownEvidence { get; set; }
			public double LocalEffectiveSampleSize { get; set; }
			public double EffectiveLocalWeight { get; set; }
			public int RecommendationLocalMatchCount { get; set; }
			public int PosteriorDraws { get; set; }
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
