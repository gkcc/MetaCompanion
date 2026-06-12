using MetaCompanion;
using Hearthstone_Deck_Tracker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MatchHistoryRecorderTest
	{
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = Path.Combine(
				Path.GetTempPath(), "MetaCompanionTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_tempDirectory);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (Directory.Exists(_tempDirectory))
			{
				Directory.Delete(_tempDirectory, true);
			}
		}

		[TestMethod]
		public void Complete_WritesMatchHistoryAndTimeline()
		{
			var recorder = new MatchHistoryRecorder(
				new PluginConfig(),
				_tempDirectory,
				(startedAt, opponentClass) => new HdtReplayInfo
				{
					ReplayFile = "game.hdtreplay",
					ReplayPath = @"C:\HDT\Replays\game.hdtreplay",
					UploadId = "abc123",
					ReplayUrl = "https://hsreplay.net/uploads/upload/abc123/"
				});
			var info = BuildPredictionInfo("Spell Hunter", 71);

			recorder.Start("Standard", "Ranked");
			recorder.RecordPrediction(info, "Hunter");
			recorder.SetResult("win");
			recorder.Complete("game_end");

			var historyLines = File.ReadAllLines(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory));
			var timelineLines = File.ReadAllLines(
				MatchHistoryRecorder.GetTimelinePath(_tempDirectory));
			var correctionsLines = File.ReadAllLines(
				MatchHistoryRecorder.GetCorrectionsPath(_tempDirectory));

			Assert.AreEqual(2, historyLines.Length);
			Assert.AreEqual(2, timelineLines.Length);
			Assert.AreEqual(1, correctionsLines.Length);
			StringAssert.Contains(historyLines[1], "win");
			StringAssert.Contains(historyLines[1], "Hunter");
			StringAssert.Contains(historyLines[1], "Spell Hunter");
			StringAssert.Contains(historyLines[1], "71");
			StringAssert.Contains(historyLines[0], "replay_file");
			StringAssert.Contains(historyLines[0], "hsreplay_url");
			StringAssert.Contains(historyLines[1], "game.hdtreplay");
			StringAssert.Contains(historyLines[1], "abc123");
		}

		[TestMethod]
		public void Disabled_DoesNotWriteFiles()
		{
			var recorder = new MatchHistoryRecorder(
				new PluginConfig {EnableMatchHistory = false}, _tempDirectory);

			recorder.Start("Standard", "Ranked");
			recorder.RecordPrediction(BuildPredictionInfo("Spell Hunter", 71), "Hunter");
			recorder.Complete("game_end");

			Assert.IsFalse(File.Exists(MatchHistoryRecorder.GetHistoryPath(_tempDirectory)));
		}

		[TestMethod]
		public void Start_MigratesLegacyHistoryHeader()
		{
			var path = MatchHistoryRecorder.GetHistoryPath(_tempDirectory);
			var legacyHeader = "match_id\tstarted_at\tended_at\tformat\tmode\tresult\topponent_class\t" +
				"predicted_archetype\tconfidence_pct\tconfidence_label\tpossible_decks\t" +
				"evidence_cards\tremaining_deck_cards\tclosest_deck\tcandidate_archetypes\tend_reason";
			File.WriteAllLines(path, new[] {legacyHeader, "old-match\told-row"});

			var recorder = new MatchHistoryRecorder(new PluginConfig(), _tempDirectory);

			recorder.Start("Standard", "Ranked");

			var lines = File.ReadAllLines(path);
			Assert.AreEqual(MatchHistoryRecorder.HistoryHeader, lines[0]);
			Assert.AreEqual("old-match\told-row", lines[1]);
		}

		private static PredictionInfo BuildPredictionInfo(string archetypeName, int confidence)
		{
			return new PredictionInfo(
				1,
				1,
				1,
				1,
				new List<PredictionInfo.CardInfo>(),
				new List<PredictionInfo.CardInfo>(),
				new List<string> {archetypeName},
				evidenceCards: 3,
				candidateArchetypes: new List<PredictionInfo.ArchetypeCandidate>
				{
					new PredictionInfo.ArchetypeCandidate(archetypeName, confidence, 300, 2)
				});
		}
	}
}
