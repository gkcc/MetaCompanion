using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanion
{
	public enum AdvisorRecommendationUpdateKind
	{
		Scheduled,
		Thinking,
		Recommendations,
		WorkerUnavailable,
		Stale,
		Cancelled
	}

	public sealed class AdvisorRecommendationUpdateEventArgs : EventArgs
	{
		public AdvisorRecommendationUpdateKind Kind { get; internal set; }
		public string StateId { get; internal set; } = "";
		public string RequestId { get; internal set; } = "";
		public string Message { get; internal set; } = "";
		public AdvisorSolveResponse Response { get; internal set; }
		public Exception Error { get; internal set; }
		public bool IsStale { get; internal set; }
		public DateTime TimestampUtc { get; internal set; } = DateTime.UtcNow;
	}

	/// <summary>
	/// Debounces game changes, cancels superseded searches and publishes updates asynchronously.
	/// It deliberately has no WPF dependency; callers can provide their UI SynchronizationContext.
	/// </summary>
	public sealed class AdvisorRecommendationController : IDisposable
	{
		private readonly object _sync = new object();
		private readonly TimeSpan _debounce;
		private readonly SynchronizationContext _callbackContext;
		private readonly AdvisorSolveOptions _defaultOptions;
		private IAdvisorWorkerClient _client;
		private CancellationTokenSource _pendingCancellation;
		private CancellationTokenSource _activeCancellation;
		private string _activeRequestId = "";
		private string _activeStateId = "";
		private string _currentStateId = "";
		private Task _observationTail = Task.FromResult(true);
		private long _generation;
		private long _factoryGeneration;
		private bool _disposed;

		public AdvisorRecommendationController(
			IAdvisorWorkerClient client,
			TimeSpan? debounce = null,
			AdvisorSolveOptions defaultOptions = null,
			SynchronizationContext callbackContext = null)
		{
			_client = client;
			_debounce = debounce ?? TimeSpan.FromMilliseconds(180);
			if (_debounce < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(debounce));
			_defaultOptions = CloneOptions(defaultOptions ?? new AdvisorSolveOptions());
			_callbackContext = callbackContext ?? SynchronizationContext.Current;
		}

		public event EventHandler<AdvisorRecommendationUpdateEventArgs> Updated;

		public string CurrentStateId
		{
			get { lock (_sync) return _currentStateId; }
		}

		public bool IsSolving
		{
			get { lock (_sync) return _activeCancellation != null; }
		}

		public bool HasPendingOrActiveForState(string stateId)
		{
			if (string.IsNullOrWhiteSpace(stateId))
				return false;
			lock (_sync)
			{
				return !_disposed &&
					(_pendingCancellation != null || _activeCancellation != null) &&
					string.Equals(_currentStateId, stateId, StringComparison.Ordinal);
			}
		}

		public void SetClient(IAdvisorWorkerClient client)
		{
			lock (_sync)
			{
				ThrowIfDisposed();
				_client = client;
			}
		}

		/// <summary>
		/// Schedules an already detached snapshot. This method does no network or search work on
		/// the calling thread and immediately invalidates the preceding state's UI result.
		/// </summary>
		public bool SubmitSnapshot(
			AdvisorGameState snapshot, AdvisorSolveOptions options = null, bool force = false,
			AdvisorHdtRootCandidateSet hdtRootCandidates = null)
		{
			if (snapshot == null)
				throw new ArgumentNullException(nameof(snapshot));
			if (string.IsNullOrWhiteSpace(snapshot.StateId))
				throw new ArgumentException("Snapshot state ID is required.", nameof(snapshot));
			if (hdtRootCandidates != null && !string.Equals(
				hdtRootCandidates.StateId, snapshot.StateId, StringComparison.Ordinal))
			{
				throw new ArgumentException(
					"HDT root candidates must be bound to the submitted snapshot.",
					nameof(hdtRootCandidates));
			}

			CancellationTokenSource oldPending;
			CancellationTokenSource oldActive;
			IAdvisorWorkerClient oldClient;
			string oldRequestId;
			string oldStateId;
			CancellationTokenSource pending;
			long generation;
			lock (_sync)
			{
				ThrowIfDisposed();
				if (!force && string.Equals(_currentStateId, snapshot.StateId, StringComparison.Ordinal))
					return false;
				oldPending = _pendingCancellation;
				oldActive = _activeCancellation;
				oldClient = _client;
				oldRequestId = _activeRequestId;
				oldStateId = _activeStateId;
				pending = new CancellationTokenSource();
				_pendingCancellation = pending;
				_activeCancellation = null;
				_activeRequestId = "";
				_activeStateId = "";
				_currentStateId = snapshot.StateId;
				generation = ++_generation;
			}

			CancelAndDispose(oldPending);
			if (oldActive != null)
			{
				oldActive.Cancel();
				oldActive.Dispose();
			}
			RequestServerCancellation(oldClient, oldRequestId, oldStateId);
			Publish(new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Scheduled,
				StateId = snapshot.StateId,
				Message = AdvisorUserMessages.StateChanged
			});
			RunSolveAsync(
				snapshot,
				CloneOptions(options ?? _defaultOptions),
				hdtRootCandidates,
				generation,
				pending).Forget();
			return true;
		}

		/// <summary>
		/// Runs snapshot extraction away from the HDT event/UI thread. Only the newest submitted
		/// factory may enqueue its result, preventing out-of-order extraction from reviving old state.
		/// </summary>
		public Task SubmitSnapshotAsync(
			Func<AdvisorGameState> snapshotFactory,
			AdvisorSolveOptions options = null,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			if (snapshotFactory == null)
				throw new ArgumentNullException(nameof(snapshotFactory));
			var factoryGeneration = Interlocked.Increment(ref _factoryGeneration);
			return Task.Run(() =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				var snapshot = snapshotFactory();
				cancellationToken.ThrowIfCancellationRequested();
				if (factoryGeneration == Interlocked.Read(ref _factoryGeneration))
					SubmitSnapshot(snapshot, options);
			}, cancellationToken);
		}

		public void CancelCurrent(string message = null)
		{
			CancellationTokenSource pending;
			CancellationTokenSource active;
			IAdvisorWorkerClient client;
			string requestId;
			string stateId;
			lock (_sync)
			{
				if (_disposed)
					return;
				pending = _pendingCancellation;
				active = _activeCancellation;
				client = _client;
				requestId = _activeRequestId;
				stateId = _activeStateId;
				_pendingCancellation = null;
				_activeCancellation = null;
				_activeRequestId = "";
				_activeStateId = "";
				// A caller can cancel before the delayed refresh proves that the public
				// game state actually changed. Forget the accepted fingerprint so that
				// the same detached state can be submitted again without a force flag.
				_currentStateId = "";
				_generation++;
			}
			CancelAndDispose(pending);
			CancelAndDispose(active);
			RequestServerCancellation(client, requestId, stateId);
			Publish(new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Cancelled,
				StateId = stateId,
				RequestId = requestId,
				Message = AdvisorUserMessages.Status(message, AdvisorUserMessages.SearchCancelled)
			});
		}

		/// <summary>
		/// Atomically cancels only when the named state still owns pending or active work. This
		/// prevents repeated non-actionable captures from publishing duplicate cancellations.
		/// </summary>
		public bool CancelCurrentIfPendingOrActive(string stateId, string message = null)
		{
			if (string.IsNullOrWhiteSpace(stateId))
				return false;
			CancellationTokenSource pending;
			CancellationTokenSource active;
			IAdvisorWorkerClient client;
			string requestId;
			lock (_sync)
			{
				if (_disposed ||
					(_pendingCancellation == null && _activeCancellation == null) ||
					!string.Equals(_currentStateId, stateId, StringComparison.Ordinal))
				{
					return false;
				}
				pending = _pendingCancellation;
				active = _activeCancellation;
				client = _client;
				requestId = _activeRequestId;
				_pendingCancellation = null;
				_activeCancellation = null;
				_activeRequestId = "";
				_activeStateId = "";
				_currentStateId = "";
				_generation++;
			}
			CancelAndDispose(pending);
			CancelAndDispose(active);
			RequestServerCancellation(client, requestId, stateId);
			Publish(new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Cancelled,
				StateId = stateId,
				RequestId = requestId,
				Message = AdvisorUserMessages.Status(
					message, AdvisorUserMessages.SearchCancelled)
			});
			return true;
		}

		/// <summary>
		/// Training observations are sent in invocation order. A transition batch can contain
		/// several actions whose action_sequence is meaningful only when the worker logs them FIFO.
		/// Failures remain visible on the returned task but never break the queue for later records.
		/// </summary>
		public Task<AdvisorObservationResult> ObserveAsync(
			AdvisorObservation observation, CancellationToken cancellationToken = default(CancellationToken))
		{
			IAdvisorWorkerClient client;
			Task previous;
			Task<AdvisorObservationResult> current;
			lock (_sync)
			{
				ThrowIfDisposed();
				client = _client;
				if (client == null)
					throw new InvalidOperationException("Advisor worker client is not available.");
				previous = _observationTail;
				current = ObserveAfterAsync(
					previous, client, observation, cancellationToken);
				_observationTail = IgnoreObservationFailureAsync(current);
			}
			return current;
		}

		/// <summary>
		/// Sends an already serialized terminal result through the same FIFO as action observations.
		/// The durable result outbox owns retries; this method only preserves observation ordering.
		/// </summary>
		internal Task<AdvisorResultAppendResult> AppendResultJsonAsync(
			string json,
			CancellationToken cancellationToken)
		{
			IAdvisorResultClient client;
			Task previous;
			Task<AdvisorResultAppendResult> current;
			lock (_sync)
			{
				ThrowIfDisposed();
				client = _client as IAdvisorResultClient;
				if (client == null)
					throw new InvalidOperationException("Advisor result worker client is not available.");
				previous = _observationTail;
				current = AppendResultAfterAsync(previous, client, json, cancellationToken);
				_observationTail = IgnoreObservationFailureAsync(current);
			}
			return current;
		}

		private static async Task<AdvisorObservationResult> ObserveAfterAsync(
			Task previous,
			IAdvisorWorkerClient client,
			AdvisorObservation observation,
			CancellationToken cancellationToken)
		{
			await previous.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return await client.ObserveAsync(observation, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<AdvisorResultAppendResult> AppendResultAfterAsync(
			Task previous,
			IAdvisorResultClient client,
			string json,
			CancellationToken cancellationToken)
		{
			await previous.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return await client.AppendResultJsonAsync(json, cancellationToken).ConfigureAwait(false);
		}

		private static async Task IgnoreObservationFailureAsync(Task observation)
		{
			try
			{
				await observation.ConfigureAwait(false);
			}
			catch
			{
				// The caller owns diagnostics for this observation. Keeping the tail successful
				// ensures a failed local write cannot starve later actions or the terminal result.
			}
		}

		public void Dispose()
		{
			CancellationTokenSource pending;
			CancellationTokenSource active;
			IAdvisorWorkerClient client;
			string requestId;
			string stateId;
			lock (_sync)
			{
				if (_disposed)
					return;
				_disposed = true;
				pending = _pendingCancellation;
				active = _activeCancellation;
				client = _client;
				requestId = _activeRequestId;
				stateId = _activeStateId;
				_pendingCancellation = null;
				_activeCancellation = null;
				_client = null;
			}
			CancelAndDispose(pending);
			CancelAndDispose(active);
			RequestServerCancellation(client, requestId, stateId);
		}

		private async Task RunSolveAsync(
			AdvisorGameState snapshot, AdvisorSolveOptions options,
			AdvisorHdtRootCandidateSet hdtRootCandidates,
			long generation, CancellationTokenSource pending)
		{
			var requestId = "";
			try
			{
				await Task.Delay(_debounce, pending.Token).ConfigureAwait(false);
				IAdvisorWorkerClient client;
				CancellationTokenSource active;
				requestId = Guid.NewGuid().ToString("N");
				lock (_sync)
				{
					if (_disposed || generation != _generation ||
						!ReferenceEquals(_pendingCancellation, pending))
						return;
					_pendingCancellation = null;
					client = _client;
					active = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
					_activeCancellation = active;
					_activeRequestId = requestId;
					_activeStateId = snapshot.StateId;
				}

				if (client == null)
				{
					PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
					{
						Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
						StateId = snapshot.StateId,
						RequestId = requestId,
						Message = AdvisorUserMessages.WorkerUnavailable
					});
					return;
				}

				PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
				{
					Kind = AdvisorRecommendationUpdateKind.Thinking,
					StateId = snapshot.StateId,
					RequestId = requestId,
					Message = AdvisorUserMessages.Searching
				});
				if (string.IsNullOrWhiteSpace(options.EnvironmentVersion))
					options.EnvironmentVersion = snapshot.EnvironmentVersion ?? "";
				var totalBudget = Math.Max(25, options.TimeBudgetMilliseconds);
				var initialBudget = Math.Max(25, Math.Min(options.InitialBudgetMilliseconds, totalBudget));
				var twoStage = initialBudget < totalBudget;
				var firstOptions = CloneOptions(options);
				firstOptions.TimeBudgetMilliseconds = twoStage ? initialBudget : totalBudget;
				firstOptions.MaxIterations = twoStage
					? Math.Max(1, Math.Min(options.InitialMaxIterations, options.MaxIterations))
					: Math.Max(1, options.MaxIterations);
				firstOptions.MaxDepth = twoStage
					? Math.Max(1, Math.Min(options.InitialMaxDepth, options.MaxDepth))
					: Math.Max(1, options.MaxDepth);
				var request = new AdvisorSolveRequest
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = requestId,
					State = snapshot,
					Options = firstOptions,
					HdtRootCandidates = hdtRootCandidates,
					Metadata = BuildTrajectoryMetadata(snapshot, twoStage ? "initial" : "single")
				};
				var totalTimer = Stopwatch.StartNew();
				var response = await client.SolveAsync(request, active.Token).ConfigureAwait(false);
				LogSolveResponse(twoStage ? "initial" : "single", response, totalTimer.ElapsedMilliseconds);
				if (!string.Equals(response.StateId, snapshot.StateId, StringComparison.Ordinal))
				{
					PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
					{
						Kind = AdvisorRecommendationUpdateKind.Stale,
						StateId = snapshot.StateId,
						RequestId = requestId,
						Message = AdvisorUserMessages.StaleResult,
						Response = response,
						IsStale = true
					});
					return;
				}

				var remainingBudget = Math.Max(0, totalBudget - (int)totalTimer.ElapsedMilliseconds);
				var runFinalStage = twoStage && remainingBudget >= 25 &&
					ShouldRefine(response) && IsCurrent(generation, snapshot.StateId);
				response.IsFinal = !runFinalStage;
				if (runFinalStage)
				{
					response.Status = AdvisorProtocol.StatusPartial;
					if (string.IsNullOrWhiteSpace(response.Message))
						response.Message = AdvisorUserMessages.InitialResult;
				}
				PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
				{
					Kind = AdvisorRecommendationUpdateKind.Recommendations,
					StateId = snapshot.StateId,
					RequestId = requestId,
					Message = AdvisorUserMessages.Status(
						response.Message,
						runFinalStage ? AdvisorUserMessages.InitialResult : "建议计算完成。"),
					Response = response
				});

				if (!runFinalStage)
					return;

				var finalRequestId = Guid.NewGuid().ToString("N");
				lock (_sync)
				{
					if (_disposed || generation != _generation)
						return;
					_activeRequestId = finalRequestId;
					_activeStateId = snapshot.StateId;
				}
				var finalOptions = CloneOptions(options);
				finalOptions.TimeBudgetMilliseconds = remainingBudget;
				finalOptions.MaxIterations = Math.Max(1, options.MaxIterations);
				finalOptions.MaxDepth = Math.Max(1, options.MaxDepth);
				AdvisorSolveResponse finalResponse;
				try
				{
					finalResponse = await client.SolveAsync(new AdvisorSolveRequest
					{
						ApiVersion = AdvisorProtocol.ApiVersion,
						RequestId = finalRequestId,
						State = snapshot,
						Options = finalOptions,
						HdtRootCandidates = hdtRootCandidates,
						Metadata = BuildTrajectoryMetadata(snapshot, "final")
					}, active.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					LogSolveFailure("final", ex, true);
					var preserved = FinalizeInitialResponse(response, totalTimer.ElapsedMilliseconds);
					PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
					{
						Kind = AdvisorRecommendationUpdateKind.Recommendations,
						StateId = snapshot.StateId,
						RequestId = response.RequestId,
						Message = preserved.Message,
						Response = preserved,
						Error = ex
					});
					return;
				}
				LogSolveResponse("final", finalResponse, totalTimer.ElapsedMilliseconds);
				if (!string.Equals(finalResponse.StateId, snapshot.StateId, StringComparison.Ordinal))
				{
					PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
					{
						Kind = AdvisorRecommendationUpdateKind.Stale,
						StateId = snapshot.StateId,
						RequestId = finalRequestId,
						Message = AdvisorUserMessages.StaleResult,
						Response = finalResponse,
						IsStale = true
					});
					return;
				}
				finalResponse.IsFinal = true;
				finalResponse.ElapsedMilliseconds = totalTimer.ElapsedMilliseconds;
				PublishIfCurrent(generation, new AdvisorRecommendationUpdateEventArgs
				{
					Kind = AdvisorRecommendationUpdateKind.Recommendations,
					StateId = snapshot.StateId,
					RequestId = finalRequestId,
					Message = AdvisorUserMessages.Status(finalResponse.Message, "建议计算完成。"),
					Response = finalResponse
				});
			}
			catch (OperationCanceledException)
			{
				// A state change deliberately cancels the old request. Never republish it.
			}
			catch (Exception ex)
			{
				LogSolveFailure("initial", ex, false);
				PublishIfCurrent(
					generation,
					BuildSolveFailureUpdate(snapshot, requestId, ex));
			}
			finally
			{
				lock (_sync)
				{
					if (generation == _generation)
					{
						_pendingCancellation = null;
						if (_activeCancellation != null)
							_activeCancellation.Dispose();
						_activeCancellation = null;
						_activeRequestId = "";
						_activeStateId = "";
					}
				}
				pending.Dispose();
			}
		}

		private static AdvisorRecommendationUpdateEventArgs BuildSolveFailureUpdate(
			AdvisorGameState snapshot, string requestId, Exception error)
		{
			var message = AdvisorUserMessages.Failure(error, AdvisorUserMessages.SolveFailed);
			if (!AdvisorUserMessages.IsExpectedCoverageFailure(error))
			{
				return new AdvisorRecommendationUpdateEventArgs
				{
					Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
					StateId = snapshot?.StateId ?? "",
					RequestId = requestId ?? "",
					Message = message,
					Error = error
				};
			}

			var response = new AdvisorSolveResponse
			{
				ApiVersion = AdvisorProtocol.ApiVersion,
				RequestId = requestId ?? "",
				StateId = snapshot?.StateId ?? "",
				Status = AdvisorProtocol.StatusUnsupported,
				Message = message,
				IsFinal = true,
				EnvironmentVersion = snapshot?.EnvironmentVersion ?? ""
			};
			return new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Recommendations,
				StateId = response.StateId,
				RequestId = response.RequestId,
				Message = message,
				Response = response,
				// Preserve the original exception for compatibility classification and
				// diagnostics, while the UI receives only the controlled Chinese response.
				Error = error
			};
		}

		private static void LogSolveResponse(
			string stage, AdvisorSolveResponse response, long fallbackElapsedMilliseconds)
		{
			var summary = AdvisorUserMessages.SolveDiagnosticSummary(
				response, stage, fallbackElapsedMilliseconds);
			if (string.Equals(response?.Status, AdvisorProtocol.StatusCancelled,
				StringComparison.OrdinalIgnoreCase))
			{
				Log.Debug(summary);
			}
			else
			{
				Log.Info(summary);
			}
		}

		private static void LogSolveFailure(
			string stage, Exception error, bool preservingInitialResult)
		{
			var summary = AdvisorUserMessages.SolveFailureDiagnostic(
				error, stage, preservingInitialResult);
			if (AdvisorUserMessages.IsExpectedCoverageFailure(error))
				Log.Info(summary);
			else
				Log.Warn(summary);
		}

		private void PublishIfCurrent(long generation, AdvisorRecommendationUpdateEventArgs args)
		{
			lock (_sync)
			{
				if (_disposed || generation != _generation ||
					!string.Equals(args.StateId, _currentStateId, StringComparison.Ordinal))
					return;
			}
			Publish(args);
		}

		private bool IsCurrent(long generation, string stateId)
		{
			lock (_sync)
			{
				return !_disposed && generation == _generation &&
					string.Equals(stateId, _currentStateId, StringComparison.Ordinal);
			}
		}

		private static bool IsRefinableStatus(string status)
		{
			return string.Equals(status, AdvisorProtocol.StatusOk, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(status, AdvisorProtocol.StatusPartial, StringComparison.OrdinalIgnoreCase);
		}

		private static bool ShouldRefine(AdvisorSolveResponse response)
		{
			if (response == null || !IsRefinableStatus(response.Status))
				return false;
			var coverage = response.Coverage;
			if (coverage != null && coverage.RootActionCoverageContractValid &&
				coverage.RootActionCoverageComplete && coverage.PortfolioOptimalityProven)
			{
				return false;
			}
			foreach (var recommendation in response.Recommendations ??
				new List<AdvisorRecommendation>())
			{
				if (recommendation != null && recommendation.IsProvenLethal)
					return false;
			}
			return true;
		}

		private static AdvisorSolveResponse FinalizeInitialResponse(
			AdvisorSolveResponse response, long elapsedMilliseconds)
		{
			var warnings = new List<string>(response.Warnings ?? new List<string>());
			const string warning = "最终深化阶段未完成，当前展示的是首批近似结果。";
			if (!warnings.Contains(warning))
				warnings.Add(warning);
			return new AdvisorSolveResponse
			{
				ApiVersion = response.ApiVersion,
				RequestId = response.RequestId,
				SchemaVersion = response.SchemaVersion,
				StateId = response.StateId,
				Status = AdvisorProtocol.StatusPartial,
				Message = "更深路线校验未完成，已保留首批建议；当前结果仅供参考。",
				IsFinal = true,
				GeneratedAtUtc = response.GeneratedAtUtc,
				ElapsedMilliseconds = elapsedMilliseconds,
				Iterations = response.Iterations,
				Progress = response.Progress,
				ModelVersion = response.ModelVersion,
				EnvironmentVersion = response.EnvironmentVersion,
				Coverage = response.Coverage,
				Recommendations = new List<AdvisorRecommendation>(
					response.Recommendations ?? new List<AdvisorRecommendation>()),
				BehaviorReferences = response.BehaviorReferences ??
					new AdvisorBehaviorReferenceSet(),
				Warnings = warnings
			};
		}

		private void Publish(AdvisorRecommendationUpdateEventArgs args)
		{
			var callback = Updated;
			if (callback == null)
				return;
			args.TimestampUtc = DateTime.UtcNow;
			SendOrPostCallback invoke = ignored =>
			{
				try { callback(this, args); }
				catch { }
			};
			if (_callbackContext != null)
				_callbackContext.Post(invoke, null);
			else
				ThreadPool.QueueUserWorkItem(ignored => invoke(null));
		}

		private static void RequestServerCancellation(
			IAdvisorWorkerClient client, string requestId, string stateId)
		{
			if (client == null || (string.IsNullOrWhiteSpace(requestId) && string.IsNullOrWhiteSpace(stateId)))
				return;
			Task.Run(async () =>
			{
				try
				{
					await client.CancelAsync(new AdvisorCancelRequest
					{
						RequestId = requestId ?? "", StateId = stateId ?? ""
					}, CancellationToken.None).ConfigureAwait(false);
				}
				catch { }
			}).Forget();
		}

		private static void CancelAndDispose(CancellationTokenSource source)
		{
			if (source == null)
				return;
			try { source.Cancel(); }
			catch (ObjectDisposedException) { }
			source.Dispose();
		}

		private static AdvisorSolveOptions CloneOptions(AdvisorSolveOptions value)
		{
			return new AdvisorSolveOptions
			{
				MaxRecommendations = value.MaxRecommendations,
				InitialBudgetMilliseconds = value.InitialBudgetMilliseconds,
				TimeBudgetMilliseconds = value.TimeBudgetMilliseconds,
				InitialMaxIterations = value.InitialMaxIterations,
				MaxIterations = value.MaxIterations,
				InitialMaxDepth = value.InitialMaxDepth,
				MaxDepth = value.MaxDepth,
				SearchSeed = value.SearchSeed,
				AllowApproximateEffects = value.AllowApproximateEffects,
				EnvironmentVersion = value.EnvironmentVersion ?? ""
			};
		}

		private static System.Collections.Generic.Dictionary<string, string> BuildTrajectoryMetadata(
			AdvisorGameState snapshot, string solveStage)
		{
			return new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "trajectory_schema", "trajectory-readiness-v1" },
				{ "decision_id", snapshot?.StateId ?? "" },
				{ "solve_stage", solveStage ?? "" },
				{ "snapshot_sequence", (snapshot?.SnapshotSequence ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) },
				{ "capture_contract", "hdt-public-snapshot-v1" }
			};
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
				throw new ObjectDisposedException(nameof(AdvisorRecommendationController));
		}
	}

	internal static class AdvisorTaskExtensions
	{
		/// <summary>Observes background exceptions without synchronously blocking an HDT callback.</summary>
		public static void Forget(this Task task)
		{
			if (task == null)
				return;
			task.ContinueWith(
				completed => { var ignored = completed.Exception; },
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
	}
}
