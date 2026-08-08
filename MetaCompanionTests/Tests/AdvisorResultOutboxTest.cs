using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MetaCompanion.Tests
{
	[TestClass]
	public class AdvisorResultOutboxTest
	{
		[TestMethod]
		public async Task LostResponse_RetriesExactPayloadInFifoOrderAndAcceptsDuplicateAck()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorResultOutbox(directory.Path))
			{
				var first = Result("game-one", "state-one", "win");
				var second = Result("game-two", "state-two", "loss");
				outbox.Enqueue(first);
				outbox.Enqueue(second);
				var failedClient = new ThrowingResultClient();

				var failed = await outbox.FlushAsync(failedClient, CancellationToken.None);

				Assert.AreEqual("result_transport_failed", failed.ErrorCode);
				Assert.AreEqual(2, outbox.CountPending());
				var retry = new RecordingResultClient(duplicate: true);
				var completed = await outbox.FlushAsync(retry, CancellationToken.None);

				Assert.IsTrue(completed.Succeeded, completed.ErrorCode);
				Assert.AreEqual(2, completed.AcknowledgedCount);
				Assert.AreEqual(0, outbox.CountPending());
				CollectionAssert.AreEqual(
					new[] { "state-one", "state-two" },
					retry.Payloads.Select(
						json => AdvisorWireProtocol.DeserializeResultObservationIdentity(json).StateId)
						.ToArray());
				Assert.AreEqual(failedClient.Payload, retry.Payloads[0]);
			}
		}

		[TestMethod]
		public async Task MismatchedExactAcknowledgement_KeepsDurableFile()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorResultOutbox(directory.Path))
			{
				var observation = Result("game-one", "state-one", "win");
				outbox.Enqueue(observation);
				var mismatch = Acknowledgement(observation, logged: true, duplicate: false);
				mismatch.StateId = "different-state";

				var result = await outbox.FlushAsync(
					new QueueResultClient(new[] { mismatch }),
					CancellationToken.None);

				Assert.AreEqual("result_acknowledgement_mismatch", result.ErrorCode);
				Assert.AreEqual(1, outbox.CountPending());
			}
		}

		[TestMethod]
		public async Task PendingFile_SurvivesOutboxRestart()
		{
			using (var directory = new TemporaryDirectory())
			{
				var observation = Result("game-one", "state-one", "tie");
				using (var first = new AdvisorResultOutbox(directory.Path))
					first.Enqueue(observation);

				using (var restarted = new AdvisorResultOutbox(directory.Path))
				{
					Assert.AreEqual(1, restarted.CountPending());
					var result = await restarted.FlushAsync(
						new RecordingResultClient(duplicate: true),
						CancellationToken.None);
					Assert.IsTrue(result.Succeeded, result.ErrorCode);
					Assert.AreEqual(0, restarted.CountPending());
				}
			}
		}

		[TestMethod]
		public void SameGameDifferentTerminalContent_FailsClosedWithoutOverwrite()
		{
			using (var directory = new TemporaryDirectory())
			using (var outbox = new AdvisorResultOutbox(directory.Path))
			{
				outbox.Enqueue(Result("game-one", "state-one", "win"));

				Assert.ThrowsException<InvalidOperationException>(() =>
					outbox.Enqueue(Result("game-one", "state-two", "loss")));
				Assert.AreEqual(1, outbox.CountPending());
			}
		}

		[TestMethod]
		public void ResultAndBehaviorOutboxes_ArePhysicallyIsolated()
		{
			using (var directory = new TemporaryDirectory())
			using (var resultOutbox = new AdvisorResultOutbox(
				System.IO.Path.Combine(directory.Path, "result-outbox-v1")))
			using (var behaviorOutbox = new AdvisorBehaviorOutbox(
				System.IO.Path.Combine(directory.Path, "behavior-outbox-v1")))
			{
				resultOutbox.Enqueue(Result("game-one", "state-one", "win"));

				Assert.AreNotEqual(resultOutbox.RootPath, behaviorOutbox.RootPath);
				Assert.AreEqual(1, Directory.GetFiles(
					resultOutbox.RootPath, "*.json", SearchOption.AllDirectories).Length);
				Assert.AreEqual(0, behaviorOutbox.CountPending());
				Assert.IsFalse(
					File.ReadAllText(Directory.GetFiles(resultOutbox.RootPath, "*.json").Single())
						.Contains(AdvisorBehaviorContract.Schema));
			}
		}

		private static AdvisorObservation Result(string gameId, string stateId, string result)
		{
			return new AdvisorObservation
			{
				Kind = "result",
				GameId = gameId,
				StateId = stateId,
				ObservedAtUtc = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
				Result = result,
				Metadata = new Dictionary<string, string>
				{
					{ "capture_contract", "terminal_result_v1" },
					{ "completeness", "terminal_result" },
					{ "training_eligible", "true" }
				}
			};
		}

		private static AdvisorResultAppendResult Acknowledgement(
			AdvisorObservation observation,
			bool logged,
			bool duplicate)
		{
			return new AdvisorResultAppendResult
			{
				Status = duplicate ? "duplicate" : "ok",
				Kind = "result",
				Logged = logged,
				Duplicate = duplicate,
				ResultId = "result-" + new string('1', 64),
				GameId = AnonymousGameId(observation.GameId),
				StateId = observation.StateId,
				Result = observation.Result
			};
		}

		private static string AnonymousGameId(string value)
		{
			using (var algorithm = SHA256.Create())
			{
				var digest = algorithm.ComputeHash(Encoding.UTF8.GetBytes((value ?? "").Trim()));
				return "anon-" + string.Concat(digest.Select(item => item.ToString("x2")))
					.Substring(0, 16);
			}
		}

		private sealed class RecordingResultClient : IAdvisorResultClient
		{
			private readonly bool _duplicate;
			internal List<string> Payloads { get; } = new List<string>();

			internal RecordingResultClient(bool duplicate)
			{
				_duplicate = duplicate;
			}

			public Task<AdvisorResultAppendResult> AppendResultJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				Payloads.Add(json);
				var identity = AdvisorWireProtocol.DeserializeResultObservationIdentity(json);
				return Task.FromResult(Acknowledgement(
					Result(identity.GameId, identity.StateId, identity.Result),
					!_duplicate,
					_duplicate));
			}
		}

		private sealed class QueueResultClient : IAdvisorResultClient
		{
			private readonly Queue<AdvisorResultAppendResult> _results;

			internal QueueResultClient(IEnumerable<AdvisorResultAppendResult> results)
			{
				_results = new Queue<AdvisorResultAppendResult>(results);
			}

			public Task<AdvisorResultAppendResult> AppendResultJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				return Task.FromResult(_results.Dequeue());
			}
		}

		private sealed class ThrowingResultClient : IAdvisorResultClient
		{
			internal string Payload { get; private set; }

			public Task<AdvisorResultAppendResult> AppendResultJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				Payload = json;
				throw new IOException("simulated lost response");
			}
		}

		private sealed class TemporaryDirectory : IDisposable
		{
			internal string Path { get; }

			internal TemporaryDirectory()
			{
				Path = System.IO.Path.Combine(
					System.IO.Path.GetTempPath(),
					"metacompanion-result-outbox-" + Guid.NewGuid().ToString("N"));
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
