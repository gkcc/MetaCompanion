using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanion
{
	/// <summary>
	/// Narrow transport used by the durable behavior outbox. Keeping this separate from the
	/// solve client interface means existing solver test doubles do not accidentally acknowledge
	/// behavior records they never persisted.
	/// </summary>
	internal interface IAdvisorBehaviorClient
	{
		Task<AdvisorBehaviorAppendResult> AppendBehaviorJsonAsync(
			string json,
			CancellationToken cancellationToken);
	}

	internal sealed class AdvisorBehaviorAppendResult
	{
		internal string Status { get; set; } = "";
		internal bool Logged { get; set; }
		internal bool Duplicate { get; set; }
		internal string BehaviorId { get; set; } = "";
		internal string GameId { get; set; } = "";
		internal long BehaviorSequence { get; set; }
	}

	internal sealed class AdvisorBehaviorPumpResult
	{
		internal int AcknowledgedCount { get; set; }
		internal int PendingCount { get; set; }
		internal string ErrorCode { get; set; } = "";

		internal bool Succeeded
		{
			get { return string.IsNullOrWhiteSpace(ErrorCode); }
		}
	}

	/// <summary>
	/// Disk-backed, per-game FIFO queue for privacy-safe behavior records.
	///
	/// The local file name carries only a transport-integrity hash; it is not a corpus ID. A file is
	/// removed only after the worker returns a valid corpus ID for the matching anonymous game ID and
	/// sequence. If the HTTP response is lost after append, the exact payload is retried and the
	/// worker's idempotency key turns the retry into a safe acknowledgement.
	/// </summary>
	internal sealed class AdvisorBehaviorOutbox : IDisposable
	{
		// Must not exceed the authenticated worker HTTP request-body limit.
		internal const int MaximumRecordBytes = 2 * 1024 * 1024;
		private const string JsonSuffix = ".json";
		private const string PendingSuffix = ".pending";
		private readonly string _root;
		private readonly Action<string, string> _movePendingFile;
		private readonly object _fileLock = new object();
		private readonly SemaphoreSlim _pumpGate = new SemaphoreSlim(1, 1);
		private bool _disposed;

		internal AdvisorBehaviorOutbox(string root)
			: this(root, File.Move)
		{
		}

		internal AdvisorBehaviorOutbox(
			string root,
			Action<string, string> movePendingFile)
		{
			if (string.IsNullOrWhiteSpace(root))
				throw new ArgumentException("Behavior outbox path is required.", nameof(root));
			if (movePendingFile == null)
				throw new ArgumentNullException(nameof(movePendingFile));
			_root = Path.GetFullPath(root);
			_movePendingFile = movePendingFile;
		}

		internal string RootPath
		{
			get { return _root; }
		}

		/// <summary>
		/// Atomically stores a canonical behavior content object. A fully flushed pending file counts
		/// as durable even when its final rename is interrupted, because startup recovery completes the
		/// rename. Returns false when the exact item is already queued; conflicting content for the same
		/// per-game sequence fails closed.
		/// </summary>
		internal bool Enqueue(AdvisorBehaviorRecord record)
		{
			ThrowIfDisposed();
			if (record == null || record.BehaviorSequence <= 0 ||
				string.IsNullOrWhiteSpace(record.GameId))
			{
				throw new ArgumentException("Behavior record identity is incomplete.", nameof(record));
			}

			var json = AdvisorWireProtocol.Serialize(record.ToWireValue());
			var bytes = new UTF8Encoding(false).GetBytes(json);
			if (bytes.Length == 0 || bytes.Length > MaximumRecordBytes)
				throw new InvalidOperationException("Behavior outbox record exceeds its size limit.");
			var digest = Sha256(json);

			lock (_fileLock)
			{
				var gameDirectory = GetGameDirectory(record.GameId);
				Directory.CreateDirectory(gameDirectory);
				RecoverPendingFiles();
				var sequencePrefix = SequenceText(record.BehaviorSequence) + ".";
				var existing = Directory.GetFiles(gameDirectory, sequencePrefix + "*" + JsonSuffix);
				var target = Path.Combine(
					gameDirectory,
					sequencePrefix + digest + JsonSuffix);
				AssertWithinRoot(target);
				if (existing.Length > 0)
				{
					if (existing.Length == 1 && Path.GetFullPath(existing[0]).Equals(
						Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
					{
						ValidateQueuedFile(existing[0], digest);
						return false;
					}
					throw new InvalidOperationException(
						"Behavior outbox contains conflicting content for the same sequence.");
				}

				var temporary = target + PendingSuffix;
				AssertWithinRoot(temporary);
				var durablePendingWrite = false;
				try
				{
					using (var stream = new FileStream(
						temporary,
						FileMode.CreateNew,
						FileAccess.Write,
						FileShare.None,
						65536,
						FileOptions.WriteThrough))
					{
						stream.Write(bytes, 0, bytes.Length);
						stream.Flush(true);
					}
					durablePendingWrite = true;
					_movePendingFile(temporary, target);
				}
				catch
				{
					if (durablePendingWrite && HasExactDurableCopy(temporary, target, digest))
						return true;
					if (!durablePendingWrite && File.Exists(temporary))
						File.Delete(temporary);
					throw;
				}
				return true;
			}
		}

		internal int CountPending()
		{
			ThrowIfDisposed();
			lock (_fileLock)
				return EnumerateQueueFiles().Count;
		}

		/// <summary>
		/// Sends queued records one at a time. Any transport, schema, or acknowledgement failure keeps
		/// the current file and stops the pump, so a later retry cannot overtake it.
		/// </summary>
		internal async Task<AdvisorBehaviorPumpResult> FlushAsync(
			IAdvisorBehaviorClient client,
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			if (client == null)
			{
				return new AdvisorBehaviorPumpResult
				{
					PendingCount = CountPending(),
					ErrorCode = "behavior_worker_unavailable"
				};
			}

			await _pumpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var result = new AdvisorBehaviorPumpResult();
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					QueuedItem item;
					try
					{
						lock (_fileLock)
							item = ReadNextItem();
					}
					catch (Exception ex) when (IsOutboxIoFailure(ex))
					{
						result.ErrorCode = "behavior_outbox_read_failed";
						result.PendingCount = SafeCountPending();
						return result;
					}
					if (item == null)
					{
						result.PendingCount = 0;
						return result;
					}

					AdvisorBehaviorAppendResult acknowledgement;
					try
					{
						acknowledgement = await client.AppendBehaviorJsonAsync(
							item.Json,
							cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch
					{
						result.ErrorCode = "behavior_transport_failed";
						result.PendingCount = SafeCountPending();
						return result;
					}

					if (!IsExactAcknowledgement(item, acknowledgement))
					{
						result.ErrorCode = "behavior_acknowledgement_mismatch";
						result.PendingCount = SafeCountPending();
						return result;
					}

					try
					{
						lock (_fileLock)
						{
							ValidateQueuedFile(item.Path, item.TransportSha256);
							File.Delete(item.Path);
						}
						result.AcknowledgedCount++;
					}
					catch (Exception ex) when (IsOutboxIoFailure(ex))
					{
						result.ErrorCode = "behavior_outbox_ack_cleanup_failed";
						result.PendingCount = SafeCountPending();
						return result;
					}
				}
			}
			finally
			{
				_pumpGate.Release();
			}
		}

		private QueuedItem ReadNextItem()
		{
			var files = EnumerateQueueFiles();
			if (files.Count == 0)
				return null;
			var path = files[0];
			var fileName = Path.GetFileName(path);
			var parts = fileName.Split('.');
			if (parts.Length != 3 || parts[2] != "json" ||
				parts[1].Length != 64 || !parts[1].All(IsLowerHex))
			{
				throw new InvalidDataException("Behavior outbox file name is invalid.");
			}
			long sequence;
			if (!Int64.TryParse(
				parts[0],
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out sequence) || sequence <= 0)
			{
				throw new InvalidDataException("Behavior outbox sequence is invalid.");
			}
			var gameId = Path.GetFileName(Path.GetDirectoryName(path));
			if (!AdvisorBehaviorContract.IsSafeToken(gameId))
			{
				throw new InvalidDataException("Behavior outbox game ID is invalid.");
			}
			var json = ValidateQueuedFile(path, parts[1]);
			return new QueuedItem
			{
				Path = path,
				Json = json,
				AnonymousGameId = AnonymousGameId(gameId),
				BehaviorSequence = sequence,
				TransportSha256 = parts[1]
			};
		}

		private List<string> EnumerateQueueFiles()
		{
			if (!Directory.Exists(_root))
				return new List<string>();
			RecoverPendingFiles();
			var result = new List<string>();
			foreach (var gameDirectory in Directory.GetDirectories(_root).OrderBy(
				item => item, StringComparer.OrdinalIgnoreCase))
			{
				AssertWithinRoot(gameDirectory);
				result.AddRange(Directory.GetFiles(gameDirectory, "*" + JsonSuffix)
					.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
			}
			return result;
		}

		/// <summary>
		/// Completes an enqueue that reached the durable write but was interrupted before the atomic
		/// rename. A malformed pending file blocks the queue instead of being silently discarded.
		/// </summary>
		private void RecoverPendingFiles()
		{
			if (!Directory.Exists(_root))
				return;
			foreach (var pending in Directory.GetFiles(
				_root,
				"*" + JsonSuffix + PendingSuffix,
				SearchOption.AllDirectories))
			{
				AssertWithinRoot(pending);
				var target = pending.Substring(0, pending.Length - PendingSuffix.Length);
				AssertWithinRoot(target);
				var fileName = Path.GetFileName(target);
				var parts = fileName.Split('.');
				if (parts.Length != 3 || parts[2] != "json" ||
					parts[1].Length != 64 || !parts[1].All(IsLowerHex))
				{
					throw new InvalidDataException("Behavior outbox pending file name is invalid.");
				}
				ValidateQueuedFile(pending, parts[1]);
				if (File.Exists(target))
				{
					ValidateQueuedFile(target, parts[1]);
					File.Delete(pending);
				}
				else
				{
					File.Move(pending, target);
				}
			}
		}

		private string ValidateQueuedFile(string path, string expectedHash)
		{
			AssertWithinRoot(path);
			var file = new FileInfo(path);
			if (!file.Exists || file.Length <= 0 || file.Length > MaximumRecordBytes)
				throw new InvalidDataException("Behavior outbox record size is invalid.");
			string json;
			using (var reader = new StreamReader(
				new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
				new UTF8Encoding(false, true),
				true))
			{
				json = reader.ReadToEnd();
			}
			if (!Sha256(json).Equals(expectedHash, StringComparison.Ordinal))
				throw new InvalidDataException("Behavior outbox record hash is invalid.");
			return json;
		}

		private bool HasExactDurableCopy(
			string pendingPath,
			string targetPath,
			string expectedHash)
		{
			if (File.Exists(targetPath))
			{
				ValidateQueuedFile(targetPath, expectedHash);
				return true;
			}
			if (File.Exists(pendingPath))
			{
				ValidateQueuedFile(pendingPath, expectedHash);
				return true;
			}
			return false;
		}

		private static bool IsExactAcknowledgement(
			QueuedItem item,
			AdvisorBehaviorAppendResult acknowledgement)
		{
			if (item == null || acknowledgement == null ||
				(!acknowledgement.Logged && !acknowledgement.Duplicate))
			{
				return false;
			}
			return item.BehaviorSequence == acknowledgement.BehaviorSequence &&
				IsBehaviorId(acknowledgement.BehaviorId) &&
				item.AnonymousGameId.Equals(acknowledgement.GameId, StringComparison.Ordinal);
		}

		private int SafeCountPending()
		{
			try
			{
				return CountPending();
			}
			catch
			{
				return -1;
			}
		}

		private string GetGameDirectory(string gameId)
		{
			if (!AdvisorBehaviorContract.IsSafeToken(gameId))
			{
				throw new ArgumentException("Behavior game ID must be a privacy-safe token.", nameof(gameId));
			}
			var path = Path.GetFullPath(Path.Combine(_root, gameId));
			AssertWithinRoot(path);
			return path;
		}

		private void AssertWithinRoot(string path)
		{
			var resolved = Path.GetFullPath(path);
			var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
				Path.DirectorySeparatorChar;
			if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Behavior outbox path escaped its configured root.");
		}

		private static string SequenceText(long sequence)
		{
			return sequence.ToString("D20", CultureInfo.InvariantCulture);
		}

		private static bool IsLowerHex(char value)
		{
			return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
		}

		private static string Sha256(string value)
		{
			using (var algorithm = SHA256.Create())
			{
				var digest = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
				return string.Concat(digest.Select(
					item => item.ToString("x2", CultureInfo.InvariantCulture)));
			}
		}

		private static string AnonymousGameId(string value)
		{
			return "anon-" + Sha256((value ?? "").Trim()).Substring(0, 16);
		}

		private static bool IsBehaviorId(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length == 73 &&
				value.StartsWith("behavior-", StringComparison.Ordinal) &&
				value.Substring("behavior-".Length).All(IsLowerHex);
		}

		private static bool IsOutboxIoFailure(Exception error)
		{
			return error is IOException || error is UnauthorizedAccessException ||
				error is InvalidDataException || error is FormatException;
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(AdvisorBehaviorOutbox));
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			_pumpGate.Dispose();
		}

		private sealed class QueuedItem
		{
			internal string Path { get; set; }
			internal string Json { get; set; }
			internal string AnonymousGameId { get; set; }
			internal long BehaviorSequence { get; set; }
			internal string TransportSha256 { get; set; }
		}
	}
}
