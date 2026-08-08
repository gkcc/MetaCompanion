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
	/// Raw terminal-result transport. The serialized bytes are retained by the outbox and retried
	/// unchanged; the worker is responsible for content addressing and durable idempotency.
	/// </summary>
	internal interface IAdvisorResultClient
	{
		Task<AdvisorResultAppendResult> AppendResultJsonAsync(
			string json,
			CancellationToken cancellationToken);
	}

	internal sealed class AdvisorResultAppendResult
	{
		internal string Status { get; set; } = "";
		internal string Kind { get; set; } = "";
		internal bool Logged { get; set; }
		internal bool Duplicate { get; set; }
		internal string ResultId { get; set; } = "";
		internal string GameId { get; set; } = "";
		internal string StateId { get; set; } = "";
		internal string Result { get; set; } = "";
	}

	internal sealed class AdvisorResultObservationIdentity
	{
		internal string GameId { get; set; } = "";
		internal string StateId { get; set; } = "";
		internal string Result { get; set; } = "";
	}

	internal sealed class AdvisorResultPumpResult
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
	/// Physically independent, disk-backed FIFO for terminal /v1/observe payloads. Files are
	/// removed only after an exact worker acknowledgement. This directory must never be pointed at
	/// behavior-v1.jsonl or the behavior outbox.
	/// </summary>
	internal sealed class AdvisorResultOutbox : IDisposable
	{
		internal const int MaximumRecordBytes = 2 * 1024 * 1024;
		private const string JsonSuffix = ".json";
		private const string PendingSuffix = ".pending";
		private readonly string _root;
		private readonly object _fileLock = new object();
		private readonly SemaphoreSlim _pumpGate = new SemaphoreSlim(1, 1);
		private bool _disposed;

		internal AdvisorResultOutbox(string root)
		{
			if (string.IsNullOrWhiteSpace(root))
				throw new ArgumentException("Result outbox path is required.", nameof(root));
			_root = Path.GetFullPath(root);
		}

		internal string RootPath
		{
			get { return _root; }
		}

		/// <summary>
		/// Durably stores one terminal observation. Re-enqueuing identical content is harmless;
		/// different terminal content for the same game fails closed.
		/// </summary>
		internal bool Enqueue(AdvisorObservation observation)
		{
			ThrowIfDisposed();
			if (observation == null ||
				!string.Equals(observation.Kind, "result", StringComparison.OrdinalIgnoreCase) ||
				string.IsNullOrWhiteSpace(observation.GameId) ||
				string.IsNullOrWhiteSpace(observation.StateId))
			{
				throw new ArgumentException("Terminal result identity is incomplete.", nameof(observation));
			}

			var json = AdvisorWireProtocol.SerializeObservation(observation);
			var identity = AdvisorWireProtocol.DeserializeResultObservationIdentity(json);
			var bytes = new UTF8Encoding(false).GetBytes(json);
			if (bytes.Length == 0 || bytes.Length > MaximumRecordBytes)
				throw new InvalidOperationException("Result outbox record exceeds its size limit.");
			var digest = Sha256(json);

			lock (_fileLock)
			{
				Directory.CreateDirectory(_root);
				RecoverPendingFiles();
				foreach (var existingPath in EnumerateQueueFilesWithoutRecovery())
				{
					var existing = ReadItem(existingPath);
					if (!string.Equals(existing.Identity.GameId, identity.GameId, StringComparison.Ordinal))
						continue;
					if (string.Equals(existing.TransportSha256, digest, StringComparison.Ordinal) &&
						string.Equals(existing.Json, json, StringComparison.Ordinal))
					{
						return false;
					}
					throw new InvalidOperationException(
						"Result outbox contains conflicting terminal content for the same game.");
				}

				var order = NextQueueOrder();
				var target = Path.Combine(
					_root,
					QueueOrderText(order) + "." + digest + JsonSuffix);
				AssertWithinRoot(target);
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
					File.Move(temporary, target);
				}
				catch
				{
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

		internal async Task<AdvisorResultPumpResult> FlushAsync(
			IAdvisorResultClient client,
			CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			if (client == null)
			{
				return new AdvisorResultPumpResult
				{
					PendingCount = CountPending(),
					ErrorCode = "result_worker_unavailable"
				};
			}

			await _pumpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var result = new AdvisorResultPumpResult();
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
						result.ErrorCode = "result_outbox_read_failed";
						result.PendingCount = SafeCountPending();
						return result;
					}
					if (item == null)
					{
						result.PendingCount = 0;
						return result;
					}

					AdvisorResultAppendResult acknowledgement;
					try
					{
						acknowledgement = await client.AppendResultJsonAsync(
							item.Json,
							cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch
					{
						result.ErrorCode = "result_transport_failed";
						result.PendingCount = SafeCountPending();
						return result;
					}

					if (!IsExactAcknowledgement(item, acknowledgement))
					{
						result.ErrorCode = "result_acknowledgement_mismatch";
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
						result.ErrorCode = "result_outbox_ack_cleanup_failed";
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
			return files.Count == 0 ? null : ReadItem(files[0]);
		}

		private QueuedItem ReadItem(string path)
		{
			var fileName = Path.GetFileName(path);
			var parts = fileName.Split('.');
			if (parts.Length != 3 || parts[2] != "json" ||
				parts[1].Length != 64 || !parts[1].All(IsLowerHex))
			{
				throw new InvalidDataException("Result outbox file name is invalid.");
			}
			long order;
			if (!Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out order) ||
				order <= 0)
			{
				throw new InvalidDataException("Result outbox order is invalid.");
			}
			var json = ValidateQueuedFile(path, parts[1]);
			return new QueuedItem
			{
				Path = path,
				Json = json,
				Identity = AdvisorWireProtocol.DeserializeResultObservationIdentity(json),
				TransportSha256 = parts[1]
			};
		}

		private List<string> EnumerateQueueFiles()
		{
			if (!Directory.Exists(_root))
				return new List<string>();
			RecoverPendingFiles();
			return EnumerateQueueFilesWithoutRecovery();
		}

		private List<string> EnumerateQueueFilesWithoutRecovery()
		{
			if (!Directory.Exists(_root))
				return new List<string>();
			return Directory.GetFiles(_root, "*" + JsonSuffix, SearchOption.TopDirectoryOnly)
				.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
		}

		private void RecoverPendingFiles()
		{
			if (!Directory.Exists(_root))
				return;
			foreach (var pending in Directory.GetFiles(
				_root,
				"*" + JsonSuffix + PendingSuffix,
				SearchOption.TopDirectoryOnly))
			{
				AssertWithinRoot(pending);
				var target = pending.Substring(0, pending.Length - PendingSuffix.Length);
				AssertWithinRoot(target);
				var parts = Path.GetFileName(target).Split('.');
				if (parts.Length != 3 || parts[2] != "json" ||
					parts[1].Length != 64 || !parts[1].All(IsLowerHex))
				{
					throw new InvalidDataException("Result outbox pending file name is invalid.");
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
				throw new InvalidDataException("Result outbox record size is invalid.");
			string json;
			using (var reader = new StreamReader(
				new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
				new UTF8Encoding(false, true),
				true))
			{
				json = reader.ReadToEnd();
			}
			if (!Sha256(json).Equals(expectedHash, StringComparison.Ordinal))
				throw new InvalidDataException("Result outbox record hash is invalid.");
			return json;
		}

		private long NextQueueOrder()
		{
			var maximum = 0L;
			foreach (var path in EnumerateQueueFilesWithoutRecovery())
			{
				long parsed;
				var prefix = Path.GetFileName(path).Split('.')[0];
				if (!Int64.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
					throw new InvalidDataException("Result outbox order is invalid.");
				maximum = Math.Max(maximum, parsed);
			}
			return Math.Max(DateTime.UtcNow.Ticks, maximum + 1);
		}

		private static bool IsExactAcknowledgement(
			QueuedItem item,
			AdvisorResultAppendResult acknowledgement)
		{
			if (item == null || acknowledgement == null ||
				(!acknowledgement.Logged && !acknowledgement.Duplicate) ||
				!string.Equals(acknowledgement.Kind, "result", StringComparison.Ordinal) ||
				!IsResultId(acknowledgement.ResultId))
			{
				return false;
			}
			return string.Equals(
				AnonymousGameId(item.Identity.GameId), acknowledgement.GameId, StringComparison.Ordinal) &&
				string.Equals(item.Identity.StateId, acknowledgement.StateId, StringComparison.Ordinal) &&
				string.Equals(item.Identity.Result, acknowledgement.Result, StringComparison.Ordinal);
		}

		private int SafeCountPending()
		{
			try { return CountPending(); }
			catch { return -1; }
		}

		private void AssertWithinRoot(string path)
		{
			var resolved = Path.GetFullPath(path);
			var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
				Path.DirectorySeparatorChar;
			if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Result outbox path escaped its configured root.");
		}

		private static string QueueOrderText(long order)
		{
			return order.ToString("D20", CultureInfo.InvariantCulture);
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

		private static bool IsResultId(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length == 71 &&
				value.StartsWith("result-", StringComparison.Ordinal) &&
				value.Substring("result-".Length).All(IsLowerHex);
		}

		private static bool IsOutboxIoFailure(Exception error)
		{
			return error is IOException || error is UnauthorizedAccessException ||
				error is InvalidDataException || error is FormatException ||
				error is AdvisorWorkerProtocolException;
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(AdvisorResultOutbox));
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
			internal AdvisorResultObservationIdentity Identity { get; set; }
			internal string TransportSha256 { get; set; }
		}
	}
}
