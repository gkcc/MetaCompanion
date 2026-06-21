using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanion
{
	public class MatchHistoryRecorder
	{
		private const string Unknown = "unknown";
		public const string HistoryHeader =
			"match_id\tstarted_at\tended_at\tformat\tmode\tresult\topponent_class\t" +
			"predicted_archetype\tconfidence_pct\tconfidence_label\tpossible_decks\t" +
			"evidence_cards\tremaining_deck_cards\tclosest_deck\tcandidate_archetypes\tend_reason\t" +
			"replay_file\treplay_path\thsreplay_upload_id\thsreplay_url\tkey_evidence_cards";
		public const string TimelineHeader =
			"match_id\ttimestamp\topponent_class\ttop_archetype\tconfidence_pct\t" +
			"confidence_label\tpossible_decks\tevidence_cards\tremaining_deck_cards\t" +
			"closest_deck\tcandidate_archetypes\tkey_evidence_cards";
		public const string CorrectionsHeader =
			"match_id\tcorrected_archetype\tcorrected_result\tnotes";
		private readonly PluginConfig _config;
		private readonly string _dataDirectory;
		private readonly Func<DateTime, string, HdtReplayInfo> _replayInfoProvider;
		private bool _completed;
		private string _matchId;
		private DateTime _startedAt;
		private string _format;
		private string _mode;
		private string _result = Unknown;
		private string _opponentClass = "";
		private PredictionInfo _lastPrediction;

		public MatchHistoryRecorder(PluginConfig config)
			: this(config, MetaCompanionPlugin.DataDirectory)
		{
		}

		internal MatchHistoryRecorder(
			PluginConfig config, string dataDirectory,
			Func<DateTime, string, HdtReplayInfo> replayInfoProvider = null)
		{
			_config = config ?? new PluginConfig();
			_dataDirectory = dataDirectory;
			_replayInfoProvider = replayInfoProvider ?? HdtReplayInfo.FindLatestReplay;
		}

		public static string GetHistoryPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "match_history.tsv");
		}

		public static string GetTimelinePath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "prediction_timeline.tsv");
		}

		public static string GetCorrectionsPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "match_corrections.tsv");
		}

		public static void AppendCorrection(
			string dataDirectory,
			string matchId,
			string correctedArchetype,
			string correctedResult = "",
			string notes = "")
		{
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				throw new ArgumentException("Data directory is required.", "dataDirectory");
			}
			if (string.IsNullOrWhiteSpace(matchId))
			{
				throw new ArgumentException("Match id is required.", "matchId");
			}
			if (string.IsNullOrWhiteSpace(correctedArchetype))
			{
				throw new ArgumentException("Corrected archetype is required.", "correctedArchetype");
			}

			Directory.CreateDirectory(dataDirectory);
			var path = GetCorrectionsPath(dataDirectory);
			EnsureFile(path, CorrectionsHeader);
			AppendLine(path, JoinTsv(new[]
			{
				matchId.Trim(),
				correctedArchetype.Trim(),
				(correctedResult ?? "").Trim().ToLowerInvariant(),
				notes ?? ""
			}));
		}

		public void Start(string format, string mode)
		{
			if (!_config.EnableMatchHistory)
			{
				return;
			}

			Directory.CreateDirectory(_dataDirectory);
			EnsureHistoryFile();
			EnsureTimelineFile();
			EnsureCorrectionsFile();
			_completed = false;
			_startedAt = DateTime.Now;
			_matchId = _startedAt.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
			_format = format ?? "";
			_mode = mode ?? "";
			_result = Unknown;
			_opponentClass = "";
			_lastPrediction = null;
		}

		public void SetResult(string result)
		{
			if (!_config.EnableMatchHistory || _completed || string.IsNullOrWhiteSpace(_matchId))
			{
				return;
			}

			_result = string.IsNullOrWhiteSpace(result) ? Unknown : result.Trim().ToLowerInvariant();
		}

		public void RecordPrediction(PredictionInfo prediction, string opponentClass)
		{
			if (!_config.EnableMatchHistory || _completed || string.IsNullOrWhiteSpace(_matchId) ||
				prediction == null)
			{
				return;
			}

			_lastPrediction = prediction;
			if (!string.IsNullOrWhiteSpace(opponentClass))
			{
				_opponentClass = opponentClass;
			}

			if (_config.EnablePredictionTimeline)
			{
				AppendLine(GetTimelinePath(_dataDirectory), BuildTimelineRow(DateTime.Now, prediction));
			}
		}

		public void Complete(string reason)
		{
			if (!_config.EnableMatchHistory || _completed || string.IsNullOrWhiteSpace(_matchId))
			{
				return;
			}

			_completed = true;
			if (_lastPrediction == null)
			{
				return;
			}

			AppendLine(GetHistoryPath(_dataDirectory), BuildHistoryRow(DateTime.Now, reason ?? ""));
		}

		private string BuildTimelineRow(DateTime now, PredictionInfo prediction)
		{
			return JoinTsv(new[]
			{
				_matchId,
				ToTimestamp(now),
				_opponentClass,
				prediction.CandidateArchetypes.Count > 0 ? prediction.CandidateArchetypes[0].Name : "",
				prediction.ConfidencePercent.ToString(CultureInfo.InvariantCulture),
				prediction.ConfidenceLabel,
				prediction.NumPossibleDecks.ToString(CultureInfo.InvariantCulture),
				prediction.EvidenceCards.ToString(CultureInfo.InvariantCulture),
				prediction.RemainingDeckCards.HasValue
					? prediction.RemainingDeckCards.Value.ToString(CultureInfo.InvariantCulture)
					: "",
				prediction.ClosestDeckName ?? "",
				FormatCandidates(prediction),
				prediction.FormatKeyEvidence(6)
			});
		}

		private string BuildHistoryRow(DateTime endedAt, string reason)
		{
			var replay = _replayInfoProvider(_startedAt, _opponentClass) ?? HdtReplayInfo.Empty;
			return JoinTsv(new[]
			{
				_matchId,
				ToTimestamp(_startedAt),
				ToTimestamp(endedAt),
				_format,
				_mode,
				_result,
				_opponentClass,
				_lastPrediction.CandidateArchetypes.Count > 0
					? _lastPrediction.CandidateArchetypes[0].Name
					: "",
				_lastPrediction.ConfidencePercent.ToString(CultureInfo.InvariantCulture),
				_lastPrediction.ConfidenceLabel,
				_lastPrediction.NumPossibleDecks.ToString(CultureInfo.InvariantCulture),
				_lastPrediction.EvidenceCards.ToString(CultureInfo.InvariantCulture),
				_lastPrediction.RemainingDeckCards.HasValue
					? _lastPrediction.RemainingDeckCards.Value.ToString(CultureInfo.InvariantCulture)
					: "",
				_lastPrediction.ClosestDeckName ?? "",
				FormatCandidates(_lastPrediction),
				reason,
				replay.ReplayFile,
				replay.ReplayPath,
				replay.UploadId,
				replay.ReplayUrl,
				_lastPrediction.FormatKeyEvidence(6)
			});
		}

		private void EnsureHistoryFile()
		{
			EnsureFile(
				GetHistoryPath(_dataDirectory),
				HistoryHeader);
		}

		private void EnsureTimelineFile()
		{
			EnsureFile(
				GetTimelinePath(_dataDirectory),
				TimelineHeader);
		}

		private void EnsureCorrectionsFile()
		{
			EnsureFile(
				GetCorrectionsPath(_dataDirectory),
				CorrectionsHeader);
		}

		private static void EnsureFile(string path, string header)
		{
			if (File.Exists(path))
			{
				EnsureCurrentHeader(path, header);
				return;
			}

			File.WriteAllText(path, header + Environment.NewLine, Encoding.UTF8);
		}

		private static void EnsureCurrentHeader(string path, string header)
		{
			var lines = File.ReadAllLines(path, Encoding.UTF8);
			if (lines.Length == 0)
			{
				File.WriteAllText(path, header + Environment.NewLine, Encoding.UTF8);
				return;
			}

			if (string.Equals(lines[0], header, StringComparison.Ordinal))
			{
				return;
			}

			if (!IsKnownLegacyHeader(lines[0], header))
			{
				return;
			}

			lines[0] = header;
			File.WriteAllLines(path, lines, Encoding.UTF8);
		}

		private static bool IsKnownLegacyHeader(string existingHeader, string currentHeader)
		{
			if (string.IsNullOrWhiteSpace(existingHeader))
			{
				return true;
			}

			if (!currentHeader.StartsWith(existingHeader + "\t", StringComparison.Ordinal))
			{
				return false;
			}

			return existingHeader.StartsWith("match_id\t", StringComparison.Ordinal) ||
				existingHeader.StartsWith("timestamp\t", StringComparison.Ordinal);
		}

		private static void AppendLine(string path, string line)
		{
			File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
		}

		private static string FormatCandidates(PredictionInfo prediction)
		{
			return string.Join(" / ", prediction.CandidateArchetypes
				.Take(3)
				.Select(candidate => candidate.Name + ":" +
					candidate.ConfidencePercent.ToString(CultureInfo.InvariantCulture) + "% score=" +
					candidate.Score.ToString(CultureInfo.InvariantCulture) + " branchCount=" +
					candidate.BranchCount.ToString(CultureInfo.InvariantCulture)));
		}

		private static string ToTimestamp(DateTime value)
		{
			return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		}

		private static string JoinTsv(IEnumerable<string> values)
		{
			return string.Join("\t", values.Select(EscapeTsv));
		}

		private static string EscapeTsv(string value)
		{
			return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
		}
	}
}
