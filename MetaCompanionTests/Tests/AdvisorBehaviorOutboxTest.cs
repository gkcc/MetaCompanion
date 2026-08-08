using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MetaCompanion.Tests
{
	[TestClass]
	public class AdvisorBehaviorOutboxTest
	{
		[TestMethod]
		public async Task EnqueueAndFlush_DeletesOnlyAfterMatchingWorkerAcknowledgement()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorBehaviorOutbox(directory.Path))
			{
				var record = CreateRecord(1, "turn_passed_to_opponent");
				Assert.IsTrue(outbox.Enqueue(record));
				Assert.AreEqual(1, outbox.CountPending());
				var client = new RecordingBehaviorClient(record, false);

				var result = await outbox.FlushAsync(client, CancellationToken.None);

				Assert.IsTrue(result.Succeeded, result.ErrorCode);
				Assert.AreEqual(1, result.AcknowledgedCount);
				Assert.AreEqual(0, result.PendingCount);
				Assert.AreEqual(0, outbox.CountPending());
				Assert.AreEqual(1, client.Payloads.Count);
				Assert.AreEqual(
					AdvisorWireProtocol.Serialize(record.ToWireValue()),
					client.Payloads[0]);
				Assert.IsFalse(client.Payloads[0].Contains("behavior_id"));
				Assert.IsFalse(client.Payloads[0].Contains("content_sha256"));
				Assert.IsFalse(client.Payloads[0].Contains("controller_id"));
			}
		}

		[TestMethod]
		public async Task LostResponse_RetriesSameContentAndAcceptsDuplicateAck()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorBehaviorOutbox(directory.Path))
			{
				var first = CreateRecord(1, "turn_passed_to_opponent");
				var second = CreateRecord(2, "turn_passed_to_opponent");
				outbox.Enqueue(first);
				outbox.Enqueue(second);
				var unavailable = new ThrowingBehaviorClient();

				var failed = await outbox.FlushAsync(unavailable, CancellationToken.None);

				Assert.AreEqual("behavior_transport_failed", failed.ErrorCode);
				Assert.AreEqual(2, outbox.CountPending());

				var retry = new QueueBehaviorClient(new[]
				{
					Acknowledgement(first, false, true),
					Acknowledgement(second, true, false)
				});
				var completed = await outbox.FlushAsync(retry, CancellationToken.None);

				Assert.IsTrue(completed.Succeeded, completed.ErrorCode);
				Assert.AreEqual(2, completed.AcknowledgedCount);
				Assert.AreEqual(0, outbox.CountPending());
				CollectionAssert.AreEqual(
					new long[] { 1, 2 },
					retry.Payloads.Select(ReadSequence).ToArray());
			}
		}

		[TestMethod]
		public async Task MismatchedAck_KeepsFileForLaterRetry()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorBehaviorOutbox(directory.Path))
			{
				var record = CreateRecord(1, "turn_passed_to_opponent");
				outbox.Enqueue(record);
				var mismatch = Acknowledgement(record, true, false);
				mismatch.GameId = "anon-" + new string('0', 16);
				var client = new QueueBehaviorClient(new[] { mismatch });

				var result = await outbox.FlushAsync(client, CancellationToken.None);

				Assert.AreEqual("behavior_acknowledgement_mismatch", result.ErrorCode);
				Assert.AreEqual(1, outbox.CountPending());
			}
		}

		[TestMethod]
		public async Task DurablePendingRename_IsRecoveredAfterRestart()
		{
			using (var directory = new TemporaryDirectory())
			{
				var record = CreateRecord(1, "turn_passed_to_opponent");
				string queuedPath;
				using (var first = new AdvisorBehaviorOutbox(directory.Path))
				{
					first.Enqueue(record);
					queuedPath = Directory.GetFiles(directory.Path, "*.json", SearchOption.AllDirectories).Single();
					File.Move(queuedPath, queuedPath + ".pending");
				}

				using (var restarted = new AdvisorBehaviorOutbox(directory.Path))
				{
					Assert.AreEqual(1, restarted.CountPending());
					Assert.IsTrue(File.Exists(queuedPath));
					Assert.IsFalse(File.Exists(queuedPath + ".pending"));
					var result = await restarted.FlushAsync(
						new RecordingBehaviorClient(record, false),
						CancellationToken.None);
					Assert.IsTrue(result.Succeeded, result.ErrorCode);
					Assert.AreEqual(0, restarted.CountPending());
				}
			}
		}

		[TestMethod]
		public void SameSequenceConflict_FailsClosedWithoutOverwriting()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorBehaviorOutbox(directory.Path))
			{
				outbox.Enqueue(CreateRecord(1, "turn_passed_to_opponent"));
				var conflict = CreateRecord(1, "turn_passed_to_player");

				Assert.ThrowsException<InvalidOperationException>(() => outbox.Enqueue(conflict));
				Assert.AreEqual(1, outbox.CountPending());
			}
		}

		[TestMethod]
		public async Task ConcurrentDuplicateEnqueue_ProducesOneDurableItem()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorBehaviorOutbox(directory.Path))
			{
				var record = CreateRecord(1, "turn_passed_to_opponent");
				var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(
					_ => Task.Run(() => outbox.Enqueue(record))));

				Assert.AreEqual(1, attempts.Count(value => value));
				Assert.AreEqual(1, outbox.CountPending());
			}
		}

		private static AdvisorBehaviorRecord CreateRecord(long sequence, string sourceEvent)
		{
			var gameId = "session-0123456789abcdef";
			var state = new AdvisorBehaviorPublicState
			{
				StateId = "state-" + sequence,
				Turn = 4,
				ActivePlayerId = sourceEvent == "turn_passed_to_player" ? "opponent" : "friendly",
				PerspectivePlayerId = "friendly",
				Friendly = Player("friendly", "1"),
				Opponent = Player("opponent", "2"),
				Patch = "33.2",
				Mode = "standard"
			};
			var record = new AdvisorBehaviorRecord
			{
				GameId = gameId,
				BehaviorSequence = sequence,
				ObservedAtUtc = "2026-07-31T12:00:00.0000000Z",
				ActorSide = sourceEvent == "turn_passed_to_player" ? "opponent" : "local",
				ActorPlayerId = state.ActivePlayerId,
				ActorEvidence = "active_player",
				IdentityStatus = "event_only",
				VisibilityStatus = "public_pre_state",
				BoundaryStatus = "isolated",
				SourceEvent = sourceEvent,
				Action = new AdvisorBehaviorAction { Kind = "end_turn" },
				PreState = state,
				PostState = null,
				BehaviorEligible = true
			};
			return record;
		}

		private static AdvisorBehaviorPublicPlayer Player(string role, string heroId)
		{
			return new AdvisorBehaviorPublicPlayer
			{
				PlayerId = role,
				Hero = new AdvisorBehaviorPublicEntity
				{
					EntityId = heroId,
					CardId = role == "friendly" ? "HERO_01" : "HERO_02",
					CardType = "HERO",
					Health = 30,
					CurrentHealth = 30
				},
				Hand = new List<AdvisorBehaviorPublicEntity>(),
				Board = new List<AdvisorBehaviorPublicEntity>(),
				Mana = 4,
				MaxMana = 4,
				DeckSize = 20
			};
		}

		private static AdvisorBehaviorAppendResult Acknowledgement(
			AdvisorBehaviorRecord record,
			bool logged,
			bool duplicate)
		{
			return new AdvisorBehaviorAppendResult
			{
				Status = "ok",
				Logged = logged,
				Duplicate = duplicate,
				BehaviorId = "behavior-" + new string('1', 64),
				GameId = AnonymousGameId(record.GameId),
				BehaviorSequence = record.BehaviorSequence
			};
		}

		private static long ReadSequence(string json)
		{
			var match = Regex.Match(
				json ?? "",
				"\\\"behavior_sequence\\\"\\s*:\\s*(?<value>[0-9]+)",
				RegexOptions.CultureInvariant);
			Assert.IsTrue(match.Success, "Behavior payload did not contain a sequence.");
			return Int64.Parse(match.Groups["value"].Value);
		}

		private static string AnonymousGameId(string gameId)
		{
			using (var algorithm = SHA256.Create())
			{
				var digest = algorithm.ComputeHash(Encoding.UTF8.GetBytes((gameId ?? "").Trim()));
				return "anon-" + string.Concat(digest.Select(item => item.ToString("x2")))
					.Substring(0, 16);
			}
		}

		private sealed class RecordingBehaviorClient : IAdvisorBehaviorClient
		{
			private readonly AdvisorBehaviorRecord _record;
			private readonly bool _duplicate;
			internal List<string> Payloads { get; } = new List<string>();

			internal RecordingBehaviorClient(AdvisorBehaviorRecord record, bool duplicate)
			{
				_record = record;
				_duplicate = duplicate;
			}

			public Task<AdvisorBehaviorAppendResult> AppendBehaviorJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				Payloads.Add(json);
				return Task.FromResult(Acknowledgement(_record, !_duplicate, _duplicate));
			}
		}

		private sealed class QueueBehaviorClient : IAdvisorBehaviorClient
		{
			private readonly Queue<AdvisorBehaviorAppendResult> _results;
			internal List<string> Payloads { get; } = new List<string>();

			internal QueueBehaviorClient(IEnumerable<AdvisorBehaviorAppendResult> results)
			{
				_results = new Queue<AdvisorBehaviorAppendResult>(results);
			}

			public Task<AdvisorBehaviorAppendResult> AppendBehaviorJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				Payloads.Add(json);
				return Task.FromResult(_results.Dequeue());
			}
		}

		private sealed class ThrowingBehaviorClient : IAdvisorBehaviorClient
		{
			public Task<AdvisorBehaviorAppendResult> AppendBehaviorJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				throw new IOException("simulated transport loss");
			}
		}

		private sealed class TemporaryDirectory : IDisposable
		{
			internal string Path { get; }

			internal TemporaryDirectory()
			{
				Path = System.IO.Path.Combine(
					System.IO.Path.GetTempPath(),
					"metacompanion-behavior-outbox-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(Path);
			}

			public void Dispose()
			{
				if (Directory.Exists(Path))
					Directory.Delete(Path, true);
			}
		}
	}
}
