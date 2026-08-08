using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows;
using System;
using HsMode = Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode;

namespace MetaCompanion
{
	internal enum GameStartDashboardAction
	{
		None,
		Hide
	}

	internal enum AdvisorRuntimeAction
	{
		None,
		Enable,
		Disable,
		RestartWorker
	}

	internal struct AdvisorRuntimeMode
	{
		public AdvisorRuntimeMode(bool liveEnabled, bool trainingEnabled)
		{
			LiveEnabled = liveEnabled;
			TrainingEnabled = trainingEnabled;
		}

		public bool LiveEnabled { get; }
		public bool TrainingEnabled { get; }
		public bool RuntimeNeeded { get { return LiveEnabled || TrainingEnabled; } }
		public bool CaptureEnabled { get { return RuntimeNeeded; } }
		public bool ObserveEnabled { get { return TrainingEnabled; } }
		public bool SolveEnabled { get { return LiveEnabled; } }
		public bool UiEnabled { get { return LiveEnabled; } }
	}

	internal struct GameStartDecision
	{
		public GameStartDecision(bool shouldTrack, GameStartDashboardAction dashboardAction)
			: this(shouldTrack, dashboardAction, "")
		{
		}

		public GameStartDecision(
			bool shouldTrack,
			GameStartDashboardAction dashboardAction,
			string predictionUnavailableReason)
		{
			ShouldTrack = shouldTrack;
			DashboardAction = dashboardAction;
			PredictionUnavailableReason = predictionUnavailableReason ?? "";
		}

		public bool ShouldTrack { get; }
		public GameStartDashboardAction DashboardAction { get; }
		public string PredictionUnavailableReason { get; }
	}

	internal sealed class AdvisorPendingAction
	{
		public AdvisorGameState PreState { get; set; }
		public string Kind { get; set; } = "";
		/// <summary>
		/// Behavior-corpus action kind. This may be more specific than the canonical
		/// trajectory kind when the live simulator cannot replay the action yet.
		/// </summary>
		public string BehaviorKind { get; set; } = "";
		public int? SourceEntityId { get; set; }
		public int? TargetEntityId { get; set; }
		public string CardId { get; set; } = "";
		public string SourceEvent { get; set; } = "";
		public string SourceEntityResolution { get; set; } = "missing";
		public string TargetEntityResolution { get; set; } = "missing";
		/// <summary>
		/// Independent behavior-corpus proof for whether the action target was bound exactly or
		/// explicitly absent. This must not be inferred from TargetEntityResolution: the strict
		/// transition contract uses "not_applicable" for both an observed no-target action and an
		/// incomplete GameEvents callback.
		/// </summary>
		public string BehaviorTargetBindingStatus { get; set; } =
			AdvisorBehaviorTargetBindingStatus.Unknown;
		public DateTime ObservedAtUtc { get; set; }
		public long GameGeneration { get; set; }
		public long ActionSequence { get; set; }
		public long ActionEventSequence { get; set; }
		public int InterveningActionCount { get; set; }
		public int? OptionId { get; set; }
		public int? FrameId { get; set; }
		public int? SubOption { get; set; }
		public int? BoardPosition { get; set; }
		public string PowerStartWatermark { get; set; } = "";
		public string PowerEndWatermark { get; set; } = "";
		public long PowerCollectorEpoch { get; set; }
		public long PowerActionOrdinal { get; set; }
		public int PowerGapCount { get; set; }
		public AdvisorHdtRootCandidateSet HdtRootCandidates { get; set; }
		public List<AdvisorObservedChoice> Choices { get; set; } =
			new List<AdvisorObservedChoice>();
		public string ActionIdentityStatus { get; set; } =
			"unverified_hdt_gameevents_v1";
		public string ChoiceStatus { get; set; } = "not_observed";
		public string SimulatorStatus { get; set; } = "not_replayed";

		public bool HasPowerIdentityEvidence
		{
			get
			{
				return FrameId.HasValue && OptionId.HasValue &&
					PowerCollectorEpoch > 0 && PowerActionOrdinal > 0 && PowerGapCount >= 0 &&
					!string.IsNullOrWhiteSpace(PowerStartWatermark) &&
					!string.IsNullOrWhiteSpace(PowerEndWatermark);
			}
		}

		public bool HasExactPowerIdentity
		{
			get
			{
				return HasPowerIdentityEvidence &&
					AdvisorBehaviorTargetBindingStatus.IsComplete(
						BehaviorTargetBindingStatus,
						TargetEntityId) &&
					string.Equals(
						ActionIdentityStatus,
						"exact_hdt_power_v1",
						StringComparison.Ordinal) &&
					string.Equals(ChoiceStatus, "none", StringComparison.Ordinal) &&
					SubOption == -1 && (Choices == null || Choices.Count == 0);
			}
		}

		/// <summary>
		/// Exact local input for the independent behavior corpus. Unlike the strict
		/// trajectory tier, this also accepts a fully bound HDT choice or sub-option.
		/// The simulator still has to abstain from replaying that selected branch.
		/// </summary>
		public bool HasExactBehaviorPowerIdentity
		{
			get
			{
				if (HasExactPowerIdentity)
					return true;
				var selections = Choices ?? new List<AdvisorObservedChoice>();
				return HasPowerIdentityEvidence &&
					AdvisorBehaviorTargetBindingStatus.IsComplete(
						BehaviorTargetBindingStatus,
						TargetEntityId) &&
					SourceEntityId.HasValue && SourceEntityId.Value > 0 &&
					string.Equals(
						ActionIdentityStatus,
						"exact_hdt_power_choice_v1",
						StringComparison.Ordinal) &&
					string.Equals(ChoiceStatus, "selected", StringComparison.Ordinal) &&
					selections.Count > 0 && selections.All(item =>
						item != null &&
						string.Equals(item.Status, "selected", StringComparison.Ordinal) &&
						item.SourceEntityId == SourceEntityId &&
						!string.IsNullOrWhiteSpace(item.ChoiceType) &&
						item.OptionEntityIds != null && item.OptionEntityIds.Count > 0 &&
						item.OptionEntityIds.All(id => id > 0) &&
						item.OptionEntityIds.Distinct().Count() == item.OptionEntityIds.Count &&
						item.SelectedEntityIds != null && item.SelectedEntityIds.Count > 0 &&
						item.SelectedEntityIds.All(id => id > 0 &&
							item.OptionEntityIds.Contains(id)) &&
						item.SelectedEntityIds.Distinct().Count() ==
							item.SelectedEntityIds.Count);
			}
		}

		/// <summary>
		/// Exact local-input evidence that is also representable by the canonical
		/// trajectory contract. Location activation deliberately remains outside this
		/// tier until the simulator can replay its effect.
		/// </summary>
		public bool HasStrictTrajectoryPowerIdentity
		{
			get
			{
				return HasExactPowerIdentity && string.Equals(
					SimulatorStatus,
					"not_replayed",
					StringComparison.Ordinal);
			}
		}
	}

	internal static class AdvisorBehaviorTargetBindingStatus
	{
		internal const string Unknown = "unknown";
		internal const string ExactEntityId = "exact_entity_id";
		internal const string ExplicitNone = "explicit_none";

		internal static bool IsComplete(string status, int? targetEntityId)
		{
			return (string.Equals(status, ExactEntityId, StringComparison.Ordinal) &&
					targetEntityId.HasValue && targetEntityId.Value > 0) ||
				(string.Equals(status, ExplicitNone, StringComparison.Ordinal) &&
					!targetEntityId.HasValue);
		}
	}

	/// <summary>
	/// Conservatively joins an HDT action callback to a later detached snapshot. The join is
	/// deliberately a non-training candidate: public GameEvents do not expose a complete action
	/// trace, and two equal captures prove only that HDT stopped changing long enough to copy it.
	/// </summary>
	internal sealed class AdvisorTransitionCandidateTracker
	{
		private readonly List<AdvisorPendingAction> _pending =
			new List<AdvisorPendingAction>();
		private string _candidateStateId = "";
		private long _candidateGameGeneration;
		private long _candidateRefreshRevision;
		private int _candidateSightings;
		private long _observationSequence;

		internal int PendingCount
		{
			get { return _pending.Count; }
		}

		internal void Reset()
		{
			_pending.Clear();
			_observationSequence = 0;
			InvalidateBoundary();
		}

		internal void InvalidateBoundary()
		{
			_candidateStateId = "";
			_candidateGameGeneration = 0;
			_candidateRefreshRevision = 0;
			_candidateSightings = 0;
		}

		internal bool Register(AdvisorPendingAction action)
		{
			if (action == null || action.PreState == null ||
				string.IsNullOrWhiteSpace(action.PreState.StateId))
			{
				return false;
			}
			var mergeTarget = FindMergeTarget(action);
			if (mergeTarget != null)
			{
				MergeActionEvidence(mergeTarget, action);
				InvalidateBoundary();
				return false;
			}
			foreach (var pending in _pending)
				pending.InterveningActionCount++;
			_pending.Add(action);
			InvalidateBoundary();
			return true;
		}

		private AdvisorPendingAction FindMergeTarget(AdvisorPendingAction incoming)
		{
			var compatible = _pending.Where(existing =>
				existing != null && existing.PreState != null &&
				existing.GameGeneration == incoming.GameGeneration &&
				string.Equals(existing.PreState.StateId, incoming.PreState.StateId, StringComparison.Ordinal) &&
				string.Equals(existing.Kind, incoming.Kind, StringComparison.Ordinal) &&
				CompatibleIdentity(existing.SourceEntityId, incoming.SourceEntityId) &&
				CompatibleIdentity(existing.TargetEntityId, incoming.TargetEntityId) &&
				CompatibleText(existing.CardId, incoming.CardId) &&
				IsNear(existing.ObservedAtUtc, incoming.ObservedAtUtc)).ToList();
			if (compatible.Count == 0)
				return null;

			if (incoming.HasPowerIdentityEvidence)
			{
				var duplicate = compatible.FirstOrDefault(existing =>
					existing.HasPowerIdentityEvidence && string.Equals(
						existing.PowerStartWatermark,
						incoming.PowerStartWatermark,
						StringComparison.Ordinal));
				if (duplicate != null)
					return duplicate;
				return compatible.FirstOrDefault(existing => !existing.HasPowerIdentityEvidence);
			}

			// A lower-fidelity GameEvents callback arriving after the Power trace is the same
			// action, not a second action. Prefer the newest unmatched exact identity.
			return compatible.LastOrDefault(existing => existing.HasPowerIdentityEvidence);
		}

		private static bool IsNear(DateTime left, DateTime right)
		{
			if (left == DateTime.MinValue || right == DateTime.MinValue)
				return false;
			return Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalSeconds) <= 5.0;
		}

		private static bool CompatibleIdentity(int? left, int? right)
		{
			return !left.HasValue || !right.HasValue || left.Value == right.Value;
		}

		private static bool CompatibleText(string left, string right)
		{
			return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
				string.Equals(left, right, StringComparison.Ordinal);
		}

		private static void MergeActionEvidence(
			AdvisorPendingAction target,
			AdvisorPendingAction incoming)
		{
			if (incoming.HasPowerIdentityEvidence || !target.HasPowerIdentityEvidence)
			{
				target.BehaviorKind = incoming.BehaviorKind ?? target.BehaviorKind ?? "";
				target.SourceEntityId = incoming.SourceEntityId ?? target.SourceEntityId;
				target.TargetEntityId = incoming.TargetEntityId ?? target.TargetEntityId;
				if (!string.IsNullOrWhiteSpace(incoming.CardId))
					target.CardId = incoming.CardId;
				target.OptionId = incoming.OptionId;
				target.FrameId = incoming.FrameId;
				target.SubOption = incoming.SubOption;
				target.BoardPosition = incoming.BoardPosition;
				target.PowerStartWatermark = incoming.PowerStartWatermark ?? "";
				target.PowerEndWatermark = incoming.PowerEndWatermark ?? "";
				target.PowerCollectorEpoch = incoming.PowerCollectorEpoch;
				target.PowerActionOrdinal = incoming.PowerActionOrdinal;
				target.PowerGapCount = incoming.PowerGapCount;
				target.HdtRootCandidates = incoming.HdtRootCandidates;
				target.Choices = incoming.Choices == null
					? new List<AdvisorObservedChoice>()
					: new List<AdvisorObservedChoice>(incoming.Choices);
				target.ActionIdentityStatus = incoming.ActionIdentityStatus ?? "unverified";
				target.ChoiceStatus = incoming.ChoiceStatus ?? "unresolved";
				target.SimulatorStatus = incoming.SimulatorStatus ?? "not_replayed";
				target.SourceEntityResolution = incoming.SourceEntityResolution ?? "missing";
				target.TargetEntityResolution = incoming.TargetEntityResolution ?? "missing";
				target.BehaviorTargetBindingStatus = incoming.BehaviorTargetBindingStatus ??
					AdvisorBehaviorTargetBindingStatus.Unknown;
			}
			if (!string.IsNullOrWhiteSpace(incoming.SourceEvent) &&
				(target.SourceEvent ?? "").IndexOf(incoming.SourceEvent, StringComparison.Ordinal) < 0)
			{
				target.SourceEvent = string.IsNullOrWhiteSpace(target.SourceEvent)
					? incoming.SourceEvent
					: target.SourceEvent + "+" + incoming.SourceEvent;
			}
		}

		internal void MarkInterveningAction()
		{
			foreach (var pending in _pending)
				pending.InterveningActionCount++;
			InvalidateBoundary();
		}

		internal List<AdvisorObservation> ObserveSnapshot(
			AdvisorGameState snapshot, long gameGeneration, long refreshRevision)
		{
			if (_pending.Count == 0 || !IsStableBoundaryCandidate(snapshot))
			{
				InvalidateBoundary();
				return new List<AdvisorObservation>();
			}
			if (_pending.Any(item => item.GameGeneration != gameGeneration) ||
				_pending.Any(item => !string.Equals(
					item.PreState.GameId, snapshot.GameId, StringComparison.Ordinal)) ||
				_pending.Any(item => string.Equals(
					item.PreState.StateId, snapshot.StateId, StringComparison.Ordinal)))
			{
				InvalidateBoundary();
				return new List<AdvisorObservation>();
			}

			var sameCandidate = string.Equals(
				_candidateStateId, snapshot.StateId, StringComparison.Ordinal) &&
				_candidateGameGeneration == gameGeneration &&
				_candidateRefreshRevision == refreshRevision;
			if (!sameCandidate)
			{
				_candidateStateId = snapshot.StateId;
				_candidateGameGeneration = gameGeneration;
				_candidateRefreshRevision = refreshRevision;
				_candidateSightings = 1;
				return new List<AdvisorObservation>();
			}
			_candidateSightings++;
			if (_candidateSightings < 2)
				return new List<AdvisorObservation>();

			var coalesced = _pending.Count > 1 ||
				_pending.Any(item => item.InterveningActionCount > 0);
			var observations = new List<AdvisorObservation>(_pending.Count);
			foreach (var item in _pending)
			{
				if (item.PowerActionOrdinal > 0)
				{
					item.ActionSequence = item.PowerActionOrdinal;
					_observationSequence = Math.Max(
						_observationSequence, item.PowerActionOrdinal);
				}
				else
				{
					item.ActionSequence = ++_observationSequence;
				}
				observations.Add(BuildObservation(item, snapshot, coalesced));
			}
			_pending.Clear();
			InvalidateBoundary();
			return observations;
		}

		internal int DiscardUnresolved()
		{
			var count = _pending.Count;
			_pending.Clear();
			InvalidateBoundary();
			return count;
		}

		internal static bool IsStableBoundaryCandidate(AdvisorGameState snapshot)
		{
			if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StateId) ||
				string.IsNullOrWhiteSpace(snapshot.StateHash))
			{
				return false;
			}
			if (snapshot.Phase != null &&
				(snapshot.Phase.HasPendingChoice ||
				 snapshot.Phase.ProposedAttackerEntityId != 0 ||
				 snapshot.Phase.ProposedDefenderEntityId != 0))
			{
				return false;
			}
			if ((snapshot.UnknownData ?? new List<AdvisorDataGap>()).Any(item =>
				item != null &&
				(string.Equals(item.Code, "entity_collection_unstable", StringComparison.Ordinal) ||
				 string.Equals(item.Code, "entity_capture_failed", StringComparison.Ordinal))))
			{
				return false;
			}
			return !EnumerateEntities(snapshot).Any(entity =>
				ReadTag(entity, "ATTACKING") != 0 || ReadTag(entity, "DEFENDING") != 0);
		}

		private static AdvisorObservation BuildObservation(
			AdvisorPendingAction pending,
			AdvisorGameState postState,
			bool coalesced)
		{
			var preState = pending.PreState;
			var strictPowerIdentity = pending.HasStrictTrajectoryPowerIdentity;
			var trajectoryIdentityStatus = strictPowerIdentity
				? pending.ActionIdentityStatus
				: (pending.HasExactPowerIdentity && string.Equals(
					pending.SimulatorStatus,
					"unsupported_location_activation",
					StringComparison.Ordinal)
					? "unsupported_location_activation"
					: pending.ActionIdentityStatus);
			var captureWarningCount = postState?.CaptureWarnings?.Count ?? 0;
			var status = captureWarningCount > 0
				? "unstable"
				: (coalesced ? "overlapped" : "isolated");
			var observation = new AdvisorObservation
			{
				Kind = "action",
				StateId = preState.StateId,
				GameId = preState.GameId ?? "",
				ObservedAtUtc = pending.ObservedAtUtc,
				PreState = preState,
				PostState = postState,
				Action = new AdvisorObservedAction
				{
					Kind = pending.Kind ?? "",
					SourceEntityId = pending.SourceEntityId,
					TargetEntityId = pending.TargetEntityId,
					CardId = pending.CardId ?? "",
					OptionId = pending.OptionId,
					FrameId = pending.FrameId,
					SubOption = pending.SubOption,
					BoardPosition = pending.BoardPosition,
					PowerStartWatermark = pending.PowerStartWatermark ?? "",
					PowerEndWatermark = pending.PowerEndWatermark ?? "",
					HdtRootCandidates = pending.HdtRootCandidates,
					Choices = pending.Choices == null
						? new List<AdvisorObservedChoice>()
						: new List<AdvisorObservedChoice>(pending.Choices)
				},
				Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "source", pending.SourceEvent ?? "hdt_event" },
					{ "mode", preState.GameMode ?? "" },
					{ "action_sequence", pending.ActionSequence.ToString(CultureInfo.InvariantCulture) },
					{ "action_event_sequence", pending.ActionEventSequence.ToString(CultureInfo.InvariantCulture) },
					{ "trajectory_schema", "trajectory-readiness-v1" },
					{ "capture_contract", strictPowerIdentity
						? "hdt_power_action_identity_v1"
						: "partial_hdt_transition_candidate_v1" },
					{ "pre_state_id", preState.StateId ?? "" },
					{ "raw_pre_snapshot_hash", preState.StateHash ?? "" },
					{ "pre_snapshot_sequence", preState.SnapshotSequence.ToString(CultureInfo.InvariantCulture) },
					{ "post_state_id", postState?.StateId ?? "" },
					{ "raw_post_snapshot_hash", postState?.StateHash ?? "" },
					{ "post_snapshot_sequence", (postState?.SnapshotSequence ?? 0).ToString(CultureInfo.InvariantCulture) },
					{ "transition_status", "post_state_candidate_unverified" },
					{ "transition_verification", "producer_candidate_unverified" },
					{ "completeness", strictPowerIdentity
						? "exact_action_identity_unverified_transition_v1"
						: "partial_hdt_gameevents_v1" },
					{ "action_identity_status", trajectoryIdentityStatus ??
						"unverified_hdt_gameevents_v1" },
					{ "choice_status", pending.ChoiceStatus ?? "not_observed" },
					{ "simulator_status", pending.SimulatorStatus ?? "not_replayed" },
					{ "training_eligible", "false" },
					{ "boundary_status", status },
					{ "intervening_action_count", pending.InterveningActionCount.ToString(CultureInfo.InvariantCulture) },
					{ "capture_warning_count", captureWarningCount.ToString(CultureInfo.InvariantCulture) },
					{ "game_generation", pending.GameGeneration.ToString(CultureInfo.InvariantCulture) },
					{ "source_entity_resolution", pending.SourceEntityResolution ?? "missing" },
					{ "target_entity_resolution", pending.TargetEntityResolution ?? "missing" },
					{ "adapter", "hdt-snapshot-v1" },
					{ "hdt_version", preState.HdtVersion ?? "" },
					{ "environment_version", preState.EnvironmentVersion ?? "" },
					{ "snapshot_schema_version", preState.SchemaVersion.ToString(CultureInfo.InvariantCulture) }
				}
			};
			if (pending.PowerActionOrdinal > 0)
			{
				observation.Metadata["power_collector_epoch"] =
					pending.PowerCollectorEpoch.ToString(CultureInfo.InvariantCulture);
				observation.Metadata["power_action_ordinal"] =
					pending.PowerActionOrdinal.ToString(CultureInfo.InvariantCulture);
				observation.Metadata["power_gap_count"] =
					pending.PowerGapCount.ToString(CultureInfo.InvariantCulture);
			}
			return observation;
		}

		private static IEnumerable<AdvisorEntityState> EnumerateEntities(AdvisorGameState state)
		{
			if (state == null)
				yield break;
			var players = new[] { state.Player, state.Opponent };
			foreach (var player in players.Where(item => item != null))
			{
				foreach (var entity in new[]
				{
					player.PlayerEntity, player.Hero, player.HeroPower, player.Weapon
				}.Where(item => item != null))
					yield return entity;
				var lists = new[]
				{
					player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
					player.SetAside, player.RemovedFromGame, player.OtherEntities
				};
				foreach (var entity in lists.Where(list => list != null).SelectMany(list => list))
					if (entity != null)
						yield return entity;
			}
			if (state.GameEntity != null)
				yield return state.GameEntity;
			foreach (var entity in state.OtherPublicEntities ?? new List<AdvisorEntityState>())
				if (entity != null)
					yield return entity;
		}

		private static int ReadTag(AdvisorEntityState entity, string name)
		{
			int value;
			return entity?.Tags != null && entity.Tags.TryGetValue(name, out value) ? value : 0;
		}
	}

	public class MetaCompanionPlugin : IPlugin
	{
		public static readonly string DataDirectory = Path.Combine(Config.AppDataPath, "MetaCompanion");
		public static readonly string PluginDirectory =
			Path.Combine(Config.AppDataPath, "Plugins", "MetaCompanion");
		private static readonly string LogDirectory = Path.Combine(DataDirectory, "Logs");
		private static readonly object AdvisorWorkerHealthSnapshotLock = new object();
		private static AdvisorWorkerHealth _advisorWorkerHealthSnapshot;

		internal static AdvisorWorkerHealth GetAdvisorWorkerHealthSnapshot()
		{
			lock (AdvisorWorkerHealthSnapshotLock)
				return _advisorWorkerHealthSnapshot;
		}

		private static void SetAdvisorWorkerHealthSnapshot(AdvisorWorkerHealth health)
		{
			lock (AdvisorWorkerHealthSnapshotLock)
				_advisorWorkerHealthSnapshot = health;
		}

		private PluginConfig _config;
		private readonly object _metaDeckLock = new object();
		private ReadOnlyCollection<Deck> _metaDecks =
			new ReadOnlyCollection<Deck>(new List<Deck>());
		private MetaDeckLoadSnapshot _metaDeckLoadSnapshot =
			MetaDeckLoadSnapshot.Loading(DateTime.MinValue);
		private PredictionController _controller;
		private PredictionView _view;
		private readonly object _advisorStateLock = new object();
		private AdvisorView _advisorView;
		private AdvisorGameStateExtractor _advisorExtractor;
		private AdvisorRecommendationController _advisorController;
		private AdvisorWorkerProcessManager _advisorWorkerManager;
		private AdvisorWorkerLaunchOptions _advisorWorkerLaunchOptions;
		private AdvisorBehaviorCollector _advisorBehaviorCollector;
		private AdvisorBehaviorOutbox _advisorBehaviorOutbox;
		private IAdvisorBehaviorClient _advisorBehaviorClient;
		private AdvisorResultOutbox _advisorResultOutbox;
		private IAdvisorResultClient _advisorResultClient;
		private readonly AdvisorBehaviorPendingTracker _advisorBehaviorTracker =
			new AdvisorBehaviorPendingTracker();
		private bool _advisorBehaviorFlushInProgress;
		private bool _advisorBehaviorFlushRequested;
		private bool _advisorBehaviorCaptureFaulted;
		private Task _advisorBehaviorFlushTask = Task.CompletedTask;
		private int _advisorBehaviorFlushFailureCount;
		private bool _advisorResultFlushInProgress;
		private bool _advisorResultFlushRequested;
		private Task _advisorResultFlushTask = Task.CompletedTask;
		private int _advisorResultFlushFailureCount;
		private CancellationTokenSource _advisorLifetimeCancellation;
		private CancellationTokenSource _advisorWorkerStartCancellation;
		private CancellationTokenSource _advisorGameCancellation;
		private AdvisorGameState _lastAdvisorSnapshot;
		private readonly AdvisorTransitionCandidateTracker _advisorTransitionTracker =
			new AdvisorTransitionCandidateTracker();
		private long _advisorGameGeneration;
		private long _advisorRefreshRevision;
		private long _advisorActionSequence;
		private bool _advisorGameActive;
		private bool _advisorResultRecorded;
		private bool _advisorRuntimeEnabled;
		private bool _advisorRuntimeTrainingLog;
		private AdvisorWorkerBackendMode _advisorRuntimeBackendMode =
			AdvisorWorkerBackendMode.Auto;
		private bool _advisorWorkerStartInProgress;
		private bool _advisorRefreshPending;
		private bool _advisorRefreshForce;
		private DateTime _advisorRefreshDueUtc = DateTime.MaxValue;
		private DateTime _nextAdvisorPollUtc = DateTime.MaxValue;
		private DateTime _nextAdvisorWorkerStartUtc = DateTime.MinValue;
		private long _advisorWorkerLifecycleRevision;
		private string _advisorRefreshReason = "";
		private string _advisorAcceptedStateId = "";
		private string _advisorAcceptedRootFrameIdentity = "";
		private string _advisorEnvironmentVersion = "";
		private MetaDashboardView _metaDashboardView;
		private PostGameMetaRefresher _postGameMetaRefresher;
		private QuickDashboardRefresher _quickDashboardRefresher;
		private MatchHistoryRecorder _matchHistoryRecorder;
		private readonly HdtGameEventReplayGuard _hdtGameEventReplayGuard =
			new HdtGameEventReplayGuard();
		private readonly HdtPowerTraceCollector _hdtPowerTraceCollector =
			new HdtPowerTraceCollector();
		private long _hdtGameEventGeneration;
		private DateTime _nextDashboardPoll = DateTime.MinValue;
		private bool _wasInRecommendationScene;
		private bool _pendingRecommendationDashboardRefresh;
		private string _lastDashboardStateSignature;
		private static readonly TimeSpan DashboardPollInterval = TimeSpan.FromSeconds(1);
		private static readonly TimeSpan AdvisorWorkerRetryInterval = TimeSpan.FromSeconds(30);
		private static readonly TimeSpan AdvisorOutboxMaximumRetryDelay = TimeSpan.FromSeconds(30);

		private SettingsWindow _settingsWindow;

		public string Author
		{
			get { return "Meta Companion contributors"; }
		}

		public string Description
		{
			get { return "标准模式环境识别、对手卡组预测、赛后推荐助手。"; }
		}

		public System.Windows.Controls.MenuItem MenuItem
		{
			get { return null; }
		}

		public string Name
		{
			get { return "Meta Companion"; }
		}

		public string ButtonText
		{
			get { return "设置"; }
		}

		public void OnButtonPress()
		{
			if (_settingsWindow == null)
			{
				_settingsWindow = new SettingsWindow(_config);
				_settingsWindow.Closed += (sender, args) =>
				{
				    _settingsWindow = null;
				};
				_settingsWindow.Show();
			}
			else
			{
				_settingsWindow.Activate();
			}
		}

		public void OnLoad()
		{
			Log.Initialize();
			Log.Info("插件已启动（版本 0.1.0）。");
			if (!Directory.Exists(DataDirectory))
			{
				Directory.CreateDirectory(DataDirectory);
			}
			Log.Info("插件数据目录已就绪。");
			CustomLog.Initialize(LogDirectory);
			_advisorEnvironmentVersion = EnsureCurrentPatchState();

			_config = PluginConfig.Load();

			Log.Info("当前为本地数据源版本，已跳过上游自动更新。");

			StartMetaDeckLoad(new MetaRetriever());
			_view = new PredictionView(_config);
			InitializeLiveAdvisor();
			_metaDashboardView = new MetaDashboardView(_config);
			_quickDashboardRefresher = new QuickDashboardRefresher();
			_postGameMetaRefresher = new PostGameMetaRefresher(_quickDashboardRefresher);

			GameEvents.OnGameStart.Add(() =>
				{
					var gameEventSession = _hdtGameEventReplayGuard.BeginGame();
					Interlocked.Exchange(
						ref _hdtGameEventGeneration, gameEventSession.Generation);
					var format = Hearthstone_Deck_Tracker.Core.Game.CurrentFormat;
					var mode = Hearthstone_Deck_Tracker.Core.Game.CurrentGameMode;
					TryStartAdvisorGame(format, mode);
					MetaDeckLoadSnapshot metaDeckLoadSnapshot;
					var metaDecks = GetLoadedMetaDecks(out metaDeckLoadSnapshot);
					var decision = GetGameStartDecision(
						format,
						mode,
						_controller != null,
						metaDeckLoadSnapshot);
					if (decision.ShouldTrack)
					{
						Log.Info("已开始跟踪当前支持的对局。");
						_matchHistoryRecorder = new MatchHistoryRecorder(_config);
						_matchHistoryRecorder.Start(format.ToString(), mode.ToString());
						var opponent = new Opponent(Hearthstone_Deck_Tracker.Core.Game);
						var controller = new PredictionController(opponent, metaDecks);
						_controller = controller;
						_view.SetEnabled(true);
						if (decision.DashboardAction == GameStartDashboardAction.Hide)
						{
							_wasInRecommendationScene = false;
							_metaDashboardView?.ResetUserDismissed();
							_metaDashboardView?.Hide();
						}
						controller.OnPredictionUpdate.Add(_view.OnPredictionUpdate);
						controller.OnPredictionUpdate.Add(prediction =>
							_matchHistoryRecorder?.RecordPrediction(prediction, controller.OpponentClass));
					}
					else
					{
						if (_controller != null)
						{
							Log.Info("已忽略重复的对局开始事件。");
						}
						else
						{
							if (!string.IsNullOrWhiteSpace(decision.PredictionUnavailableReason))
							{
								Log.Warn("No deck predictions for " + format + " " + mode +
									" game: " + decision.PredictionUnavailableReason);
							}
							else
							{
								Log.Info("当前对局没有可用的牌组预测。");
							}
						}
					}
				});
			GameEvents.OnGameWon.Add(() =>
				{
					if (!ShouldProcessHdtGameEvent("game_won"))
						return;
					_matchHistoryRecorder?.SetResult("win");
					RecordAdvisorResult("win", "game_won");
				});
			GameEvents.OnGameLost.Add(() =>
				{
					if (!ShouldProcessHdtGameEvent("game_lost"))
						return;
					_matchHistoryRecorder?.SetResult("loss");
					RecordAdvisorResult("loss", "game_lost");
				});
			GameEvents.OnGameTied.Add(() =>
				{
					if (!ShouldProcessHdtGameEvent("game_tied"))
						return;
					_matchHistoryRecorder?.SetResult("tie");
					RecordAdvisorResult("tie", "game_tied");
				});
			GameEvents.OnGameEnd.Add(() =>
				{
					StopAdvisorGame("game_end");
					if (StopTrackingGame("game_end"))
					{
						_pendingRecommendationDashboardRefresh = true;
						_quickDashboardRefresher?.TryRefreshAfterGame(
							_config,
							() => UpdateStandardRecommendationDashboard(true));
						_postGameMetaRefresher?.TryRefreshAfterGame(
							_config,
							() => UpdateStandardRecommendationDashboard(true));
					}
					_hdtGameEventReplayGuard.EndGame(
						Interlocked.Read(ref _hdtGameEventGeneration));
				});
			GameEvents.OnInMenu.Add(() =>
				{
					StopAdvisorGame("in_menu");
					var wasTrackingGame = StopTrackingGame("in_menu");
					if (wasTrackingGame)
					{
						_pendingRecommendationDashboardRefresh = true;
						_quickDashboardRefresher?.TryRefreshAfterGame(
							_config,
							() => UpdateStandardRecommendationDashboard(true));
					}
					if (wasTrackingGame || _pendingRecommendationDashboardRefresh)
					{
						_pendingRecommendationDashboardRefresh = false;
						UpdateStandardRecommendationDashboard(true);
					}
					_hdtGameEventReplayGuard.EndGame(
						Interlocked.Read(ref _hdtGameEventGeneration));
				});
			GameEvents.OnOpponentDraw.Add(() =>
				{
					if (ShouldProcessHdtGameEvent("opponent_draw"))
					{
						_controller?.OnOpponentDraw();
						QueueAdvisorStateRefresh("opponent_draw");
					}
				});
			GameEvents.OnTurnStart.Add(activePlayer =>
				{
					if (ShouldProcessHdtGameEvent(
						"turn_start", activePlayer.ToString(), GetCurrentHdtTurnNumber()))
					{
						_controller?.OnTurnStart(activePlayer);
						if (activePlayer == ActivePlayer.Opponent)
						{
							RecordAdvisorAction("end_turn", null, null, "", "turn_passed_to_opponent");
						}
						else if (activePlayer == ActivePlayer.Player)
						{
							RecordOpponentAdvisorEndTurn();
							MarkAdvisorInterveningAction();
						}
						QueueAdvisorStateRefresh("turn_start", 300);
					}
				});
			GameEvents.OnPlayerDraw.Add(card =>
				{
					if (ShouldProcessHdtGameEvent("player_draw", GetCardEventIdentity(card)))
						QueueAdvisorStateRefresh("player_draw");
				});
			GameEvents.OnPlayerHandDiscard.Add(card =>
				{
					if (ShouldProcessHdtGameEvent(
						"player_hand_discard", GetCardEventIdentity(card)))
						QueueAdvisorStateRefresh("player_hand_discard", 300);
				});
			GameEvents.OnPlayerDeckDiscard.Add(card =>
				{
					if (ShouldProcessHdtGameEvent(
						"player_deck_discard", GetCardEventIdentity(card)))
						QueueAdvisorStateRefresh("player_deck_discard", 300);
				});
			GameEvents.OnPlayerFatigue.Add(damage =>
				{
					if (ShouldProcessHdtGameEvent(
						"player_fatigue", damage.ToString(CultureInfo.InvariantCulture)))
						QueueAdvisorStateRefresh("player_fatigue", 300);
				});
			GameEvents.OnPlayerPlay.Add(card =>
				{
					if (!ShouldProcessHdtGameEvent("player_play", GetCardEventIdentity(card)))
						return;
					RecordAdvisorCardAction("play_card", card, "player_play");
					QueueAdvisorStateRefresh("player_play", 300);
				});
			GameEvents.OnPlayerMinionAttack.Add(attack =>
				{
					if (!ShouldProcessHdtGameEvent("player_attack", GetAttackEventIdentity(attack)))
						return;
					RecordAdvisorAttack(attack, "player_attack");
					QueueAdvisorStateRefresh("player_attack", 300);
				});
			GameEvents.OnPlayerHeroPower.Add(() =>
				{
					if (!ShouldProcessHdtGameEvent("player_hero_power"))
						return;
					RecordAdvisorHeroPower();
					QueueAdvisorStateRefresh("player_hero_power", 300);
				});
			GameEvents.OnOpponentMinionAttack.Add(attack =>
				{
					if (!ShouldProcessHdtGameEvent("opponent_attack", GetAttackEventIdentity(attack)))
						return;
					RecordOpponentAdvisorAttack(attack);
					MarkAdvisorInterveningAction();
					QueueAdvisorStateRefresh("opponent_attack", 300);
				});
			GameEvents.OnOpponentHeroPower.Add(() =>
				{
					if (!ShouldProcessHdtGameEvent("opponent_hero_power"))
						return;
					RecordOpponentAdvisorHeroPower();
					MarkAdvisorInterveningAction();
					QueueAdvisorStateRefresh("opponent_hero_power", 300);
				});

			// Events that reveal cards need a 100ms delay. This is because HDT takes some extra
			// time to process all the tags we need, but it doesn't wait to send these callbacks.
			int delayMs = 250;
			GameEvents.OnOpponentPlay.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_play",
						GetCardEventIdentity(card),
						GetOpponentPlayedCardCount(),
						out eventGeneration))
					{
						return;
					}
					RecordOpponentAdvisorPlay(card);
					MarkAdvisorInterveningAction();
					QueueAdvisorStateRefresh("opponent_play", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentPlay(card);
					}
				});
			GameEvents.OnOpponentHandDiscard.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_hand_discard", GetCardEventIdentity(card), 0, out eventGeneration))
					{
						return;
					}
					QueueAdvisorStateRefresh("opponent_hand_discard", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentHandDiscard(card);
					}
				});
			GameEvents.OnOpponentDeckDiscard.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_deck_discard", GetCardEventIdentity(card), 0, out eventGeneration))
					{
						return;
					}
					QueueAdvisorStateRefresh("opponent_deck_discard", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentDeckDiscard(card);
					}
				});
			GameEvents.OnOpponentSecretTriggered.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_secret_triggered", GetCardEventIdentity(card), 0, out eventGeneration))
					{
						return;
					}
					QueueAdvisorStateRefresh("opponent_secret_triggered", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentSecretTriggered(card);
					}
				});
			GameEvents.OnOpponentJoustReveal.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_joust_reveal", GetCardEventIdentity(card), 0, out eventGeneration))
					{
						return;
					}
					QueueAdvisorStateRefresh("opponent_joust_reveal", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentJoustReveal(card);
					}
				});
			GameEvents.OnOpponentDeckToPlay.Add(async card =>
				{
					long eventGeneration;
					if (!ShouldProcessHdtGameEvent(
						"opponent_deck_to_play", GetCardEventIdentity(card), 0, out eventGeneration))
					{
						return;
					}
					QueueAdvisorStateRefresh("opponent_deck_to_play", 300);
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) &&
						_hdtGameEventReplayGuard.IsCurrent(eventGeneration))
					{
						controller?.OnOpponentDeckToPlay(card);
					}
				});
		}

		private void InitializeLiveAdvisor()
		{
			_advisorView = new AdvisorView(_config);
			_advisorExtractor = new AdvisorGameStateExtractor();
			_advisorBehaviorCollector = new AdvisorBehaviorCollector();
			_advisorBehaviorOutbox = new AdvisorBehaviorOutbox(
				Path.Combine(DataDirectory, "behavior-outbox-v1"));
			_advisorResultOutbox = new AdvisorResultOutbox(
				Path.Combine(DataDirectory, "result-outbox-v1"));
			_advisorBehaviorClient = null;
			_advisorResultClient = null;
			_advisorBehaviorFlushInProgress = false;
			_advisorBehaviorFlushRequested = false;
			_advisorBehaviorCaptureFaulted = false;
			_advisorBehaviorFlushTask = Task.CompletedTask;
			_advisorBehaviorFlushFailureCount = 0;
			_advisorResultFlushInProgress = false;
			_advisorResultFlushRequested = false;
			_advisorResultFlushTask = Task.CompletedTask;
			_advisorResultFlushFailureCount = 0;
			_advisorLifetimeCancellation = new CancellationTokenSource();
			var solveOptions = BuildAdvisorSolveOptions();
			_advisorController = new AdvisorRecommendationController(
				null,
				TimeSpan.FromMilliseconds(180),
				solveOptions,
				SynchronizationContext.Current);
			_advisorController.Updated += OnAdvisorRecommendationUpdated;

			var assemblyDirectory = Path.GetDirectoryName(
				typeof(MetaCompanionPlugin).Assembly.Location) ?? PluginDirectory;
			var solveTimeoutSeconds = Math.Max(5, _config.AdvisorSearchSeconds + 4);
			_advisorWorkerLaunchOptions = new AdvisorWorkerLaunchOptions
			{
				EnableTrainingLog = _config.EnableAdvisorTrainingLog,
				BackendMode = _config.AdvisorWorkerBackendMode,
				ClientOptions = new AdvisorWorkerClientOptions
				{
					SolveTimeout = TimeSpan.FromSeconds(solveTimeoutSeconds)
				}
			};
			_advisorWorkerManager = new AdvisorWorkerProcessManager(
				assemblyDirectory,
				DataDirectory,
				_advisorWorkerLaunchOptions);
			_advisorWorkerManager.Exited += OnAdvisorWorkerExited;
			lock (_advisorStateLock)
			{
				_advisorRuntimeEnabled = _config.EnableLiveAdvisor;
				_advisorRuntimeTrainingLog = _config.EnableAdvisorTrainingLog;
				_advisorRuntimeBackendMode = _config.AdvisorWorkerBackendMode;
				_nextAdvisorWorkerStartUtc = DateTime.MinValue;
			}
			EnsureAdvisorWorkerStarted();
		}

		private AdvisorSolveOptions BuildAdvisorSolveOptions()
		{
			var initialMilliseconds = Math.Max(1, _config.AdvisorInitialResultSeconds) * 1000;
			var totalMilliseconds = Math.Max(
				_config.AdvisorInitialResultSeconds,
				_config.AdvisorSearchSeconds) * 1000;
			return new AdvisorSolveOptions
			{
				MaxRecommendations = 3,
				InitialBudgetMilliseconds = initialMilliseconds,
				TimeBudgetMilliseconds = totalMilliseconds,
				InitialMaxIterations = AdvisorSolveOptions.DefaultInitialMaxIterations,
				MaxIterations = AdvisorSolveOptions.DefaultMaxIterations,
				InitialMaxDepth = AdvisorSolveOptions.DefaultInitialMaxDepth,
				MaxDepth = AdvisorSolveOptions.DefaultMaxDepth,
				AllowApproximateEffects = true,
				EnvironmentVersion = _advisorEnvironmentVersion ?? ""
			};
		}

		private void SynchronizeAdvisorRuntimeSettings()
		{
			if (_config == null || _advisorWorkerManager == null ||
				_advisorController == null || _advisorLifetimeCancellation == null)
			{
				return;
			}

			var configuredEnabled = _config.EnableLiveAdvisor;
			var configuredTrainingLog = _config.EnableAdvisorTrainingLog;
			var configuredBackendMode = _config.AdvisorWorkerBackendMode;
			AdvisorRuntimeAction action;
			bool runtimeLiveEnabled;
			bool runtimeNeeded;
			bool configuredRuntimeNeeded;
			bool trainingModeChanged;
			lock (_advisorStateLock)
			{
				runtimeLiveEnabled = _advisorRuntimeEnabled;
				trainingModeChanged =
					_advisorRuntimeTrainingLog != configuredTrainingLog;
				runtimeNeeded = GetAdvisorRuntimeMode(
					_advisorRuntimeEnabled, _advisorRuntimeTrainingLog).RuntimeNeeded;
				configuredRuntimeNeeded = GetAdvisorRuntimeMode(
					configuredEnabled, configuredTrainingLog).RuntimeNeeded;
				action = GetAdvisorRuntimeAction(
					_advisorRuntimeEnabled,
					_advisorRuntimeTrainingLog,
					configuredEnabled,
					configuredTrainingLog,
					_advisorRuntimeBackendMode,
					configuredBackendMode);
				_advisorRuntimeEnabled = configuredEnabled;
				_advisorRuntimeTrainingLog = configuredTrainingLog;
				_advisorRuntimeBackendMode = configuredBackendMode;
				if (_advisorWorkerLaunchOptions != null)
				{
					_advisorWorkerLaunchOptions.EnableTrainingLog = configuredTrainingLog;
					_advisorWorkerLaunchOptions.BackendMode = configuredBackendMode;
				}
			}
			if (trainingModeChanged)
			{
				ResetAdvisorPowerTraceForTrainingMode(configuredTrainingLog);
				ResetAdvisorBehaviorForTrainingMode(configuredTrainingLog);
			}
			if (runtimeNeeded == configuredRuntimeNeeded &&
				runtimeLiveEnabled != configuredEnabled)
			{
				ApplyAdvisorLiveModeChange(configuredEnabled);
			}

			switch (action)
			{
				case AdvisorRuntimeAction.Disable:
					DisableAdvisorRuntime();
					return;
				case AdvisorRuntimeAction.Enable:
					EnableAdvisorRuntime();
					return;
				case AdvisorRuntimeAction.RestartWorker:
					RestartAdvisorWorker("training_log_setting_changed");
					return;
				default:
					EnsureAdvisorWorkerStarted();
					return;
			}
		}

		private void ResetAdvisorPowerTraceForTrainingMode(bool trainingEnabled)
		{
			long generation;
			bool gameActive;
			lock (_advisorStateLock)
			{
				generation = _advisorGameGeneration;
				gameActive = _advisorGameActive;
			}
			if (!trainingEnabled || !gameActive)
			{
				_hdtPowerTraceCollector.Disconnect();
				return;
			}
			var game = Hearthstone_Deck_Tracker.Core.Game;
			var count = 0;
			try
			{
				count = game?.PowerLog?.Count ?? 0;
			}
			catch
			{
				count = 0;
			}
			// Training began after the Hearthstone GameStart anchor. Never replay the old
			// suffix and permanently taint this partial-game trace.
			_hdtPowerTraceCollector.BeginGeneration(
				generation, game, count, cleanGameStartAnchor: false);
		}

		private void ResetAdvisorBehaviorForTrainingMode(bool trainingEnabled)
		{
			List<AdvisorBehaviorCapture> unresolved = null;
			bool gameActive;
			long generation;
			lock (_advisorStateLock)
			{
				gameActive = _advisorGameActive;
				generation = _advisorGameGeneration;
				if (!trainingEnabled && gameActive)
					unresolved = _advisorBehaviorTracker.DrainUnresolved(generation);
				else
				{
					_advisorBehaviorTracker.Reset();
					if (trainingEnabled)
						_advisorBehaviorCaptureFaulted = false;
				}
			}

			if (unresolved != null && unresolved.Count > 0)
				CollectAdvisorBehaviorCaptures(unresolved);
			if (!gameActive)
			{
				_advisorBehaviorCollector?.EndGame();
				return;
			}
			if (!trainingEnabled)
			{
				_advisorBehaviorCollector?.SuspendGame();
				return;
			}

			_advisorBehaviorCollector?.BeginGame(_advisorExtractor.SessionGameAlias);
			QueueAdvisorStateRefresh("behavior_training_enabled", 0, true);
		}

		private void ApplyAdvisorLiveModeChange(bool liveEnabled)
		{
			bool gameActive;
			lock (_advisorStateLock)
			{
				gameActive = _advisorGameActive;
				if (!liveEnabled)
				{
					_advisorAcceptedStateId = "";
					_advisorAcceptedRootFrameIdentity = "";
				}
			}
			if (!liveEnabled)
			{
				_advisorController?.CancelCurrent("实时建议已关闭，训练采集继续运行。");
				_advisorView?.Hide();
				Log.Info("实时建议已关闭；训练采集状态保持不变。");
				return;
			}

			if (gameActive)
			{
				_advisorView?.OnGameStarted();
				QueueAdvisorStateRefresh("live_mode_enabled", 0, true);
			}
			else
			{
				TryStartAdvisorGameFromCurrentState();
			}
			Log.Info("实时建议已开启。");
		}

		internal static AdvisorRuntimeAction GetAdvisorRuntimeAction(
			bool runtimeEnabled,
			bool runtimeTrainingLog,
			bool configuredEnabled,
			bool configuredTrainingLog)
		{
			var runtime = GetAdvisorRuntimeMode(runtimeEnabled, runtimeTrainingLog);
			var configured = GetAdvisorRuntimeMode(
				configuredEnabled, configuredTrainingLog);
			if (runtime.RuntimeNeeded != configured.RuntimeNeeded)
			{
				return configured.RuntimeNeeded
					? AdvisorRuntimeAction.Enable
					: AdvisorRuntimeAction.Disable;
			}
			return configured.RuntimeNeeded &&
				runtimeTrainingLog != configuredTrainingLog
				? AdvisorRuntimeAction.RestartWorker
				: AdvisorRuntimeAction.None;
		}

		internal static AdvisorRuntimeMode GetAdvisorRuntimeMode(
			bool liveEnabled, bool trainingEnabled)
		{
			return new AdvisorRuntimeMode(liveEnabled, trainingEnabled);
		}

		internal static AdvisorRuntimeAction GetAdvisorRuntimeAction(
			bool runtimeEnabled,
			bool runtimeTrainingLog,
			bool configuredEnabled,
			bool configuredTrainingLog,
			AdvisorWorkerBackendMode runtimeBackendMode,
			AdvisorWorkerBackendMode configuredBackendMode)
		{
			var action = GetAdvisorRuntimeAction(
				runtimeEnabled,
				runtimeTrainingLog,
				configuredEnabled,
				configuredTrainingLog);
			if (action != AdvisorRuntimeAction.None)
				return action;
			return GetAdvisorRuntimeMode(
				configuredEnabled, configuredTrainingLog).RuntimeNeeded &&
				runtimeBackendMode != configuredBackendMode
				? AdvisorRuntimeAction.RestartWorker
				: AdvisorRuntimeAction.None;
		}

		internal static bool ShouldAttemptAdvisorWorkerStart(
			bool enabled,
			bool startInProgress,
			bool isRunning,
			DateTime nowUtc,
			DateTime nextAttemptUtc)
		{
			return enabled && !startInProgress && !isRunning && nowUtc >= nextAttemptUtc;
		}

		private void EnableAdvisorRuntime()
		{
			lock (_advisorStateLock)
			{
				_advisorWorkerLifecycleRevision++;
				_nextAdvisorWorkerStartUtc = DateTime.MinValue;
			}
			Log.Info("顾问运行时已启动。");
			TryStartAdvisorGameFromCurrentState();
			EnsureAdvisorWorkerStarted();
		}

		private void DisableAdvisorRuntime()
		{
			CancellationTokenSource startupCancellation;
			bool gameActive;
			lock (_advisorStateLock)
			{
				_advisorWorkerLifecycleRevision++;
				_nextAdvisorWorkerStartUtc = DateTime.MaxValue;
				startupCancellation = _advisorWorkerStartCancellation;
				gameActive = _advisorGameActive;
			}
			TryCancel(startupCancellation);
			_advisorController?.CancelCurrent("顾问运行时已关闭。");
			TrySetAdvisorClient(null);
			// Keep the lightweight game identity scope while Hearthstone is still in this game.
			// A later training re-enable can then resume the same behavior sequence instead of
			// creating a second sequence starting at one. The real HDT end/menu events still call
			// StopAdvisorGame and release the scope.
			if (!gameActive)
				StopAdvisorGame("setting_disabled");
			_advisorView?.Hide();
			_advisorWorkerManager?.Stop();
			Log.Info("顾问运行时已关闭。");
		}

		private void RestartAdvisorWorker(string reason)
		{
			CancellationTokenSource startupCancellation;
			lock (_advisorStateLock)
			{
				_advisorWorkerLifecycleRevision++;
				_nextAdvisorWorkerStartUtc = DateTime.MinValue;
				startupCancellation = _advisorWorkerStartCancellation;
			}
			TryCancel(startupCancellation);
			_advisorController?.CancelCurrent("本机求解器正在重启。");
			TrySetAdvisorClient(null);
			_advisorView?.Hide();
			_advisorWorkerManager?.Stop();
			Log.Info("正在重启本机求解器。");
			EnsureAdvisorWorkerStarted();
		}

		private void TryStartAdvisorGameFromCurrentState()
		{
			try
			{
				var game = Hearthstone_Deck_Tracker.Core.Game;
				if (game != null && game.IsRunning)
				{
					TryStartAdvisorGame(
						game.CurrentFormat,
						game.CurrentGameMode,
						cleanGameStartAnchor: false);
				}
			}
			catch (Exception ex)
			{
				Log.Warn(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "resume"));
			}
		}

		private void EnsureAdvisorWorkerStarted()
		{
			AdvisorWorkerProcessManager manager;
			CancellationTokenSource startCancellation;
			long lifecycleRevision;
			lock (_advisorStateLock)
			{
				manager = _advisorWorkerManager;
				var lifetime = _advisorLifetimeCancellation;
				var lifetimeEnded = lifetime == null || lifetime.IsCancellationRequested;
				var isRunning = manager != null && manager.IsRunning;
				if (lifetimeEnded || manager == null ||
					!ShouldAttemptAdvisorWorkerStart(
						GetAdvisorRuntimeMode(
							_advisorRuntimeEnabled,
							_advisorRuntimeTrainingLog).RuntimeNeeded,
						_advisorWorkerStartInProgress,
						isRunning,
						DateTime.UtcNow,
						_nextAdvisorWorkerStartUtc))
				{
					return;
				}
				startCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
				_advisorWorkerStartCancellation = startCancellation;
				_advisorWorkerStartInProgress = true;
				lifecycleRevision = _advisorWorkerLifecycleRevision;
			}
			StartAdvisorWorkerAsync(manager, startCancellation, lifecycleRevision).Forget();
		}

		private async Task StartAdvisorWorkerAsync(
			AdvisorWorkerProcessManager manager,
			CancellationTokenSource startCancellation,
			long lifecycleRevision)
		{
			var cancellationToken = startCancellation.Token;
			try
			{
				var client = await manager.StartAsync(cancellationToken).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				var accepted = false;
				var acceptedStateId = "";
				lock (_advisorStateLock)
				{
					if (GetAdvisorRuntimeMode(
						_advisorRuntimeEnabled,
						_advisorRuntimeTrainingLog).RuntimeNeeded &&
						lifecycleRevision == _advisorWorkerLifecycleRevision &&
						ReferenceEquals(manager, _advisorWorkerManager) &&
						ReferenceEquals(startCancellation, _advisorWorkerStartCancellation) &&
						manager.IsRunning)
					{
						accepted = TrySetAdvisorClient(client);
						if (accepted)
						{
							_nextAdvisorWorkerStartUtc = DateTime.MinValue;
							acceptedStateId = _lastAdvisorSnapshot?.StateId ?? "";
						}
					}
				}
				if (!accepted)
				{
					manager.Stop();
					return;
				}
				SetAdvisorWorkerHealthSnapshot(manager.LastHealth);
				var fallbackMessage = manager.LastStartUserMessage;
				var liveEnabled = false;
				lock (_advisorStateLock)
					liveEnabled = _advisorRuntimeEnabled;
				if (!string.IsNullOrWhiteSpace(fallbackMessage))
				{
					Log.Warn("Rust 求解器启动失败，已切换到 Python 兼容求解器。");
					if (liveEnabled)
						_advisorView?.OnWorkerUnavailable(acceptedStateId, fallbackMessage);
				}
				Log.Info("本机求解器已就绪。");
				foreach (var line in SettingsDiagnostics.BuildAdvisorModelStatusLines(
					manager.LastHealth,
					true))
				{
					Log.Info(line);
				}
				QueueAdvisorStateRefresh("worker_ready", 0, true);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				string stateId;
				var reportFailure = false;
				lock (_advisorStateLock)
				{
					stateId = _lastAdvisorSnapshot?.StateId ?? "";
					reportFailure = GetAdvisorRuntimeMode(
						_advisorRuntimeEnabled,
						_advisorRuntimeTrainingLog).RuntimeNeeded &&
						lifecycleRevision == _advisorWorkerLifecycleRevision &&
						ReferenceEquals(manager, _advisorWorkerManager) &&
						!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true);
					if (reportFailure)
					{
						_nextAdvisorWorkerStartUtc = DateTime.UtcNow + AdvisorWorkerRetryInterval;
					}
				}
				if (reportFailure)
				{
					Log.Warn(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "worker_start"));
					bool showUi;
					lock (_advisorStateLock)
						showUi = _advisorRuntimeEnabled;
					if (showUi)
					{
						_advisorView?.OnWorkerUnavailable(
							stateId,
							"本机求解器暂不可用，正在后台自动重试；HDT 仍可正常使用。");
					}
				}
			}
			finally
			{
				var retryImmediately = false;
				lock (_advisorStateLock)
				{
					if (ReferenceEquals(startCancellation, _advisorWorkerStartCancellation))
					{
						_advisorWorkerStartCancellation = null;
						_advisorWorkerStartInProgress = false;
						retryImmediately = GetAdvisorRuntimeMode(
							_advisorRuntimeEnabled,
							_advisorRuntimeTrainingLog).RuntimeNeeded && !manager.IsRunning &&
							_nextAdvisorWorkerStartUtc == DateTime.MinValue &&
							!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true);
					}
				}
				startCancellation.Dispose();
				if (retryImmediately)
					EnsureAdvisorWorkerStarted();
			}
		}

		private bool TrySetAdvisorClient(IAdvisorWorkerClient client)
		{
			if (client == null)
				SetAdvisorWorkerHealthSnapshot(null);
			try
			{
				if (_advisorController == null)
				{
					return false;
				}
				_advisorController.SetClient(client);
				var behaviorClient = client as IAdvisorBehaviorClient;
				var rawResultClient = client as IAdvisorResultClient;
				lock (_advisorStateLock)
				{
					_advisorBehaviorClient = behaviorClient;
					_advisorResultClient = rawResultClient == null || _advisorController == null
						? null
						: new AdvisorControllerResultClient(_advisorController);
					if (behaviorClient != null)
						_advisorBehaviorFlushFailureCount = 0;
					if (rawResultClient != null)
						_advisorResultFlushFailureCount = 0;
				}
				if (behaviorClient != null)
					QueueAdvisorBehaviorFlush();
				if (rawResultClient != null)
					QueueAdvisorResultFlush();
				return true;
			}
			catch (ObjectDisposedException)
			{
				lock (_advisorStateLock)
				{
					_advisorBehaviorClient = null;
					_advisorResultClient = null;
				}
				return false;
			}
		}

		private static void TryCancel(CancellationTokenSource source)
		{
			if (source == null)
			{
				return;
			}
			try
			{
				source.Cancel();
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private void OnAdvisorWorkerExited(object sender, AdvisorWorkerExitedEventArgs args)
		{
			if (args.Expected)
			{
				return;
			}
			string stateId;
			bool runtimeNeeded;
			bool liveEnabled;
			CancellationTokenSource startupCancellation;
			var fallbackImmediately = false;
			lock (_advisorStateLock)
			{
				stateId = _lastAdvisorSnapshot?.StateId ?? "";
				liveEnabled = _advisorRuntimeEnabled;
				runtimeNeeded = GetAdvisorRuntimeMode(
					_advisorRuntimeEnabled,
					_advisorRuntimeTrainingLog).RuntimeNeeded &&
					!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true);
				fallbackImmediately = ShouldImmediatelyFallbackAdvisorWorker(
					args, runtimeNeeded);
				_advisorWorkerLifecycleRevision++;
				_advisorRefreshRevision++;
				_advisorAcceptedStateId = "";
				_advisorAcceptedRootFrameIdentity = "";
				_nextAdvisorWorkerStartUtc = runtimeNeeded
					? (fallbackImmediately
						? DateTime.MinValue
						: DateTime.UtcNow + AdvisorWorkerRetryInterval)
					: DateTime.MaxValue;
				startupCancellation = _advisorWorkerStartCancellation;
			}
			TryCancel(startupCancellation);
			_advisorController?.CancelCurrent("本机求解器意外退出，旧建议已清除。");
			TrySetAdvisorClient(null);
			Log.Warn("本机求解器意外退出：后端=" + args.Backend +
				(args.ExitCode.HasValue ? "；退出码=" + args.ExitCode.Value : "") + "。");
			if (runtimeNeeded)
			{
				if (liveEnabled)
				{
					_advisorView?.OnWorkerUnavailable(
						stateId,
						fallbackImmediately
							? "Rust 求解器意外退出，正在自动切换到 Python 兼容求解器。"
							: "本机求解器意外退出；将在后台重试。");
				}
				if (fallbackImmediately)
					EnsureAdvisorWorkerStarted();
			}
		}

		internal static bool ShouldImmediatelyFallbackAdvisorWorker(
			AdvisorWorkerExitedEventArgs args, bool runtimeEnabled)
		{
			return runtimeEnabled && args != null && !args.Expected &&
				args.Backend == AdvisorWorkerBackendKind.Rust && args.FallbackAvailable;
		}

		internal static bool ShouldFallbackAdvisorSolveFailure(
			AdvisorRecommendationUpdateEventArgs args,
			bool runtimeEnabled,
			AdvisorWorkerBackendMode backendMode,
			AdvisorWorkerBackendKind activeBackend)
		{
			if (!runtimeEnabled || args == null ||
				backendMode != AdvisorWorkerBackendMode.Auto ||
				activeBackend != AdvisorWorkerBackendKind.Rust)
			{
				return false;
			}
			// A normal abstention belongs to one game state, not to the worker contract.
			// Keep Rust active for unsupported_scope/HTTP 422, limits and cancellations;
			// only a malformed or version-incompatible protocol response justifies
			// quarantining the native worker for the rest of this manager lifetime.
			return IsExplicitAdvisorCompatibilityException(args.Error);
		}

		internal static bool IsExplicitAdvisorCompatibilityException(Exception error)
		{
			if (error == null)
				return false;
			if (error is AdvisorWorkerProtocolException)
				return true;
			var aggregate = error as AggregateException;
			if (aggregate != null)
			{
				return aggregate.Flatten().InnerExceptions.Any(
					IsExplicitAdvisorCompatibilityException);
			}
			return error.InnerException != null &&
				IsExplicitAdvisorCompatibilityException(error.InnerException);
		}

		private bool TryFallbackAdvisorSolveToCompatibilityWorker(
			AdvisorRecommendationUpdateEventArgs args)
		{
			AdvisorWorkerProcessManager manager;
			CancellationTokenSource startupCancellation;
			lock (_advisorStateLock)
			{
				manager = _advisorWorkerManager;
				var configuredBackendMode = _config?.AdvisorWorkerBackendMode ??
					_advisorRuntimeBackendMode;
				var runtimeEnabled = (_config?.EnableLiveAdvisor ?? false) &&
					_advisorRuntimeEnabled && _advisorGameActive &&
					!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true) &&
					!string.IsNullOrWhiteSpace(_advisorAcceptedStateId) &&
					string.Equals(
						args?.StateId,
						_advisorAcceptedStateId,
						StringComparison.Ordinal);
				if (manager == null ||
					!ShouldFallbackAdvisorSolveFailure(
						args,
						runtimeEnabled,
						configuredBackendMode,
						manager.ActiveBackend) ||
					!manager.TryBeginCompatibilityFallback())
				{
					return false;
				}

				_advisorWorkerLifecycleRevision++;
				_advisorRefreshRevision++;
				_advisorAcceptedStateId = "";
				_advisorAcceptedRootFrameIdentity = "";
				_nextAdvisorWorkerStartUtc = DateTime.MinValue;
				startupCancellation = _advisorWorkerStartCancellation;
			}

			TryCancel(startupCancellation);
			_advisorController?.CancelCurrent(
				"Rust 求解协议不兼容，旧请求已取消。");
			TrySetAdvisorClient(null);
			manager.Stop();
			Log.Warn("Rust 求解协议不兼容，正在切换到 Python 兼容求解器。");
			_advisorView?.OnWorkerUnavailable(
				args.StateId,
				"Rust 求解协议不兼容，正在自动切换到 Python 兼容求解器并重新计算。");
			// A successful start queues worker_ready with force=true, so the unchanged current
			// state is submitted again instead of being suppressed by controller deduplication.
			EnsureAdvisorWorkerStarted();
			return true;
		}

		private void OnAdvisorRecommendationUpdated(
			object sender, AdvisorRecommendationUpdateEventArgs args)
		{
			if (args == null || _advisorView == null)
			{
				return;
			}
			AdvisorGameState displaySnapshot;
			lock (_advisorStateLock)
			{
				if (!_advisorRuntimeEnabled || !_advisorGameActive ||
					string.IsNullOrWhiteSpace(_advisorAcceptedStateId) ||
					!string.Equals(args.StateId, _advisorAcceptedStateId, StringComparison.Ordinal))
				{
					return;
				}
				displaySnapshot = _lastAdvisorSnapshot != null && string.Equals(
					_lastAdvisorSnapshot.StateId,
					args.StateId,
					StringComparison.Ordinal)
					? _lastAdvisorSnapshot
					: null;
			}
			var warningSummary = BuildAdvisorWarningLogSummary(args.Response?.Warnings);
			if (!string.IsNullOrWhiteSpace(warningSummary))
				Log.Debug(AdvisorUserMessages.RedactSecrets(warningSummary));

			switch (args.Kind)
			{
				case AdvisorRecommendationUpdateKind.Scheduled:
					_advisorView.OnStateChanged(args.StateId);
					break;
				case AdvisorRecommendationUpdateKind.Thinking:
					_advisorView.OnThinking(args.StateId, "正在搜索完整行动线…");
					break;
				case AdvisorRecommendationUpdateKind.Recommendations:
					if (!TryFallbackAdvisorSolveToCompatibilityWorker(args))
						_advisorView.OnRecommendations(
							args.Response,
							displaySnapshot,
							args.IsStale);
					break;
				case AdvisorRecommendationUpdateKind.WorkerUnavailable:
					if (!TryFallbackAdvisorSolveToCompatibilityWorker(args))
						_advisorView.OnWorkerUnavailable(args.StateId, args.Message);
					break;
				case AdvisorRecommendationUpdateKind.Stale:
					_advisorView.OnStale(args.StateId, args.Message);
					break;
			}
		}

		internal static string BuildAdvisorWarningLogSummary(IEnumerable<string> warnings)
		{
			return AdvisorUserMessages.WarningDiagnosticSummary(warnings);
		}

		private void TryStartAdvisorGame(
			Format? format,
			GameMode mode,
			bool cleanGameStartAnchor = true)
		{
			var runtimeMode = GetAdvisorRuntimeMode(
				_config?.EnableLiveAdvisor ?? false,
				_config?.EnableAdvisorTrainingLog ?? false);
			if (!ShouldStartAdvisorGame(format, mode, runtimeMode.RuntimeNeeded))
			{
				StopAdvisorGame("unsupported_mode");
				return;
			}

			CancellationTokenSource previous;
			long gameGeneration;
			lock (_advisorStateLock)
			{
				if (_advisorGameActive)
				{
					QueueAdvisorStateRefresh("duplicate_game_start", 300);
					return;
				}
				previous = _advisorGameCancellation;
				_advisorGameCancellation = new CancellationTokenSource();
				_advisorGameActive = true;
				_advisorResultRecorded = false;
				_lastAdvisorSnapshot = null;
				_advisorAcceptedStateId = "";
				_advisorAcceptedRootFrameIdentity = "";
				_advisorRefreshPending = false;
				_advisorRefreshForce = false;
				_advisorRefreshDueUtc = DateTime.MaxValue;
				_nextAdvisorPollUtc = DateTime.UtcNow.AddMilliseconds(500);
				_advisorActionSequence = 0;
				_advisorTransitionTracker.Reset();
				_advisorBehaviorTracker.Reset();
				_advisorBehaviorCaptureFaulted = false;
				_advisorRefreshRevision++;
				_advisorGameGeneration++;
				gameGeneration = _advisorGameGeneration;
			}
			previous?.Cancel();
			previous?.Dispose();
			var powerGame = Hearthstone_Deck_Tracker.Core.Game;
			var powerCount = 0;
			try
			{
				powerCount = powerGame?.PowerLog?.Count ?? 0;
			}
			catch
			{
				powerCount = 0;
			}
			_hdtPowerTraceCollector.BeginGeneration(
				gameGeneration,
				powerGame,
				powerCount,
				cleanGameStartAnchor || !runtimeMode.TrainingEnabled);
			_advisorExtractor.BeginGame();
			if (runtimeMode.TrainingEnabled)
			{
				_advisorBehaviorCollector?.BeginGame(_advisorExtractor.SessionGameAlias);
				QueueAdvisorBehaviorFlush();
			}
			else
			{
				_advisorBehaviorCollector?.EndGame();
			}
			if (runtimeMode.UiEnabled)
				_advisorView?.OnGameStarted();
			Log.Info("顾问对局采集已启动。");
			QueueAdvisorStateRefresh("game_start", 500);
		}

		internal static bool ShouldStartAdvisorGame(Format? format, GameMode mode, bool enabled)
		{
			if (!enabled)
			{
				return false;
			}
			if (mode == GameMode.Arena)
			{
				return true;
			}
			return format == Format.Standard &&
				(mode == GameMode.Ranked || mode == GameMode.Casual || mode == GameMode.Friendly);
		}

		internal static bool ShouldSolveAdvisorSnapshot(AdvisorGameState snapshot)
		{
			return snapshot != null &&
				snapshot.IsRunning &&
				snapshot.IsMulliganDone &&
				!snapshot.IsSpectating &&
				snapshot.IsLocalPlayerTurn == true &&
				!IsAdvisorHeroDead(snapshot.Player) &&
				!IsAdvisorHeroDead(snapshot.Opponent) &&
				(snapshot.Phase == null ||
					(!snapshot.Phase.HasPendingChoice && snapshot.Phase.CanLocalPlayerAct != false));
		}

		private static bool IsAdvisorHeroDead(AdvisorPlayerState player)
		{
			var hero = player?.Hero;
			return hero != null && hero.Health > 0 && hero.Health - hero.Damage <= 0;
		}

		private void MarkAdvisorInterveningAction()
		{
			lock (_advisorStateLock)
			{
				if (_advisorGameActive)
					_advisorTransitionTracker.MarkInterveningAction();
			}
		}

		private void QueueAdvisorStateRefresh(
			string reason, int delayMilliseconds = 250, bool force = false)
		{
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || !GetAdvisorRuntimeMode(
					_advisorRuntimeEnabled,
					_advisorRuntimeTrainingLog).CaptureEnabled)
				{
					return;
				}
				_advisorRefreshRevision++;
				_advisorRefreshPending = true;
				_advisorRefreshForce = _advisorRefreshForce || force;
				_advisorRefreshReason = reason ?? "state_change";
				_advisorRefreshDueUtc = DateTime.UtcNow.AddMilliseconds(
					Math.Max(0, delayMilliseconds));
				_nextAdvisorPollUtc = _advisorRefreshDueUtc.AddMilliseconds(500);
				_advisorTransitionTracker.InvalidateBoundary();
			}
		}

		private void ProcessPendingAdvisorRefresh()
		{
			long gameGeneration;
			long refreshRevision;
			bool force;
			string reason;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || !_advisorRefreshPending ||
					DateTime.UtcNow < _advisorRefreshDueUtc)
				{
					return;
				}
				_advisorRefreshPending = false;
				_advisorRefreshDueUtc = DateTime.MaxValue;
				gameGeneration = _advisorGameGeneration;
				refreshRevision = _advisorRefreshRevision;
				force = _advisorRefreshForce;
				_advisorRefreshForce = false;
				reason = _advisorRefreshReason;
			}
			CaptureAndSubmitAdvisorSnapshot(
				gameGeneration, refreshRevision, force, false, reason);
		}

		private void PollAdvisorStateIfDue()
		{
			long gameGeneration;
			long refreshRevision;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || _advisorRefreshPending ||
					!GetAdvisorRuntimeMode(
						_advisorRuntimeEnabled,
						_advisorRuntimeTrainingLog).CaptureEnabled ||
					DateTime.UtcNow < _nextAdvisorPollUtc)
				{
					return;
				}
				_nextAdvisorPollUtc = DateTime.UtcNow.AddMilliseconds(500);
				gameGeneration = _advisorGameGeneration;
				refreshRevision = _advisorRefreshRevision;
			}
			CaptureAndSubmitAdvisorSnapshot(
				gameGeneration, refreshRevision, false, true, "controlled_poll");
		}

		private void CaptureAndSubmitAdvisorSnapshot(
			long gameGeneration,
			long refreshRevision,
			bool force,
			bool isPoll,
			string reason)
		{
			try
			{
				var game = Hearthstone_Deck_Tracker.Core.Game;
				if (game == null)
				{
					return;
				}
				// OnUpdate is HDT's supported plugin synchronization boundary. Copy the mutable
				// GameV2 graph here; only detached DTOs are sent to background search work.
				var snapshot = _advisorExtractor.Capture(
					game,
					new AdvisorCaptureOptions
					{
						EnvironmentVersion = _advisorEnvironmentVersion ?? ""
					});
				var actionable = ShouldSolveAdvisorSnapshot(snapshot);
				AdvisorHdtRootCandidateSet hdtRootCandidates = null;
				if (actionable)
				{
					HdtPowerOptionsFrameEvidence stableFrame;
					string ignoredReason;
					if (_hdtPowerTraceCollector.TryGetStableOptionsFrame(
						gameGeneration, snapshot.StateId, out stableFrame))
					{
						AdvisorHdtRootCandidateBinder.TryBuild(
							stableFrame, snapshot, out hdtRootCandidates, out ignoredReason);
					}
				}
				var rootFrameIdentity = HdtRootCandidateIdentity(hdtRootCandidates);
				List<AdvisorObservation> transitionObservations;
				List<AdvisorBehaviorCapture> behaviorCaptures;
				var skipUnchangedPoll = false;
				var submitSnapshot = false;
				var hideLiveUi = false;
				var cancelStateId = "";
				lock (_advisorStateLock)
				{
					if (!_advisorGameActive || gameGeneration != _advisorGameGeneration ||
						refreshRevision != _advisorRefreshRevision)
					{
						return;
					}
					var runtimeMode = GetAdvisorRuntimeMode(
						_advisorRuntimeEnabled, _advisorRuntimeTrainingLog);
					if (!runtimeMode.CaptureEnabled)
						return;
					var unchanged = _lastAdvisorSnapshot != null &&
						string.Equals(
							_lastAdvisorSnapshot.StateId,
							snapshot.StateId,
							StringComparison.Ordinal);
					transitionObservations = runtimeMode.ObserveEnabled
						? _advisorTransitionTracker.ObserveSnapshot(
							snapshot, gameGeneration, refreshRevision)
						: new List<AdvisorObservation>();
					behaviorCaptures = runtimeMode.TrainingEnabled
						? _advisorBehaviorTracker.ObserveSnapshot(
							snapshot, gameGeneration, refreshRevision)
						: new List<AdvisorBehaviorCapture>();
					var newlyBoundRootFrame = !string.IsNullOrWhiteSpace(rootFrameIdentity) &&
						!string.Equals(
							rootFrameIdentity,
							_advisorAcceptedRootFrameIdentity,
							StringComparison.Ordinal);
					skipUnchangedPoll = isPoll && unchanged && !newlyBoundRootFrame;
					if (!skipUnchangedPoll)
					{
						_lastAdvisorSnapshot = snapshot;
						if (!unchanged)
							_advisorAcceptedRootFrameIdentity = rootFrameIdentity;
						else if (newlyBoundRootFrame)
							_advisorAcceptedRootFrameIdentity = rootFrameIdentity;
						if (runtimeMode.SolveEnabled && actionable)
						{
							_advisorAcceptedStateId = snapshot.StateId;
							submitSnapshot = true;
						}
						else
						{
							if (runtimeMode.UiEnabled && !actionable)
							{
								cancelStateId = _advisorAcceptedStateId;
								hideLiveUi = true;
							}
							_advisorAcceptedStateId = "";
							_advisorAcceptedRootFrameIdentity = "";
						}
					}
				}
				CollectAdvisorBehaviorCaptures(behaviorCaptures);
				ObserveAdvisorTransitions(transitionObservations);
				if (skipUnchangedPoll)
					return;
				if (submitSnapshot)
				{
					_advisorController.SubmitSnapshot(
						snapshot,
						BuildAdvisorSolveOptions(),
						force || !string.IsNullOrWhiteSpace(rootFrameIdentity),
						hdtRootCandidates);
					return;
				}
				if (!string.IsNullOrWhiteSpace(cancelStateId))
				{
					if (!_advisorController.CancelCurrentIfPendingOrActive(
						cancelStateId,
						"当前不是本方可操作阶段，已停止旧局面的计算。") &&
						string.Equals(
							_advisorController.CurrentStateId,
							cancelStateId,
							StringComparison.Ordinal))
					{
						_advisorController.CancelCurrent(
							"当前不是本方可操作阶段，已清除旧建议。");
					}
				}
				if (hideLiveUi)
					_advisorView?.Hide();
			}
			catch (Exception ex)
			{
				Log.Warn(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "snapshot"));
			}
		}

		private void RecordAdvisorCardAction(string kind, Card card, string sourceEvent)
		{
			var cardId = card?.Id ?? "";
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
			{
				snapshot = _lastAdvisorSnapshot;
			}
			string sourceResolution;
			var sourceEntityId = FindUniqueEntityId(
				snapshot,
				cardId,
				true,
				kind == "play_card" ? "hand" : "character",
				out sourceResolution);
			RecordAdvisorAction(
				kind, sourceEntityId, null, cardId, sourceEvent, snapshot,
				sourceResolution, "not_observed_by_hdt_gameevents");
		}

		/// <summary>
		/// Must run before any snapshot/worker processing in OnUpdate. Core.Game.PowerLog is a
		/// mutable HDT-owned list, so the collector copies only its unread suffix on this supported
		/// synchronization boundary and never sends raw lines to a worker or file.
		/// </summary>
		private void ProcessAdvisorPowerTrace()
		{
			long generation;
			bool shouldCollect;
			bool shouldRecord;
			lock (_advisorStateLock)
			{
				generation = _advisorGameGeneration;
				shouldCollect = _advisorGameActive && GetAdvisorRuntimeMode(
					_advisorRuntimeEnabled, _advisorRuntimeTrainingLog).CaptureEnabled;
				shouldRecord = _advisorGameActive && _advisorRuntimeTrainingLog;
			}
			if (!shouldCollect)
				return;

			var game = Hearthstone_Deck_Tracker.Core.Game;
			if (game == null)
			{
				_hdtPowerTraceCollector.Disconnect();
				return;
			}

			List<HdtPowerActionEvidence> evidence;
			try
			{
				evidence = _hdtPowerTraceCollector.Collect(
					game.PowerLog, generation, game);
			}
			catch (Exception ex)
			{
				Log.Debug(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "power"));
				return;
			}
			if (evidence.Count == 0)
				return;
			if (!shouldRecord)
				return;

			var accepted = false;
			foreach (var item in evidence)
				accepted = RecordAdvisorPowerAction(item, generation) || accepted;
			if (accepted)
				QueueAdvisorStateRefresh("hdt_power_action", 300);
		}

		private bool RecordAdvisorPowerAction(
			HdtPowerActionEvidence evidence,
			long gameGeneration)
		{
			var accepted = false;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || gameGeneration != _advisorGameGeneration ||
					!_advisorRuntimeTrainingLog)
				{
					accepted = false;
				}
				else
				{
					AdvisorPendingAction pending;
					if (TryCreatePowerPendingAction(
						evidence,
						_lastAdvisorSnapshot,
						gameGeneration,
						0,
						out pending))
					{
						if (_advisorTransitionTracker.Register(pending))
							pending.ActionEventSequence = ++_advisorActionSequence;
						RegisterAdvisorPowerBehaviorLocked(pending);
						accepted = true;
					}
				}
			}
			if (accepted)
				_hdtPowerTraceCollector.MarkActionRecorded(evidence?.PowerActionOrdinal ?? 0);
			else
				_hdtPowerTraceCollector.MarkActionRejected(evidence?.PowerActionOrdinal ?? 0);
			return accepted;
		}

		internal static bool TryCreatePowerPendingAction(
			HdtPowerActionEvidence evidence,
			AdvisorGameState snapshot,
			long gameGeneration,
			long actionEventSequence,
			out AdvisorPendingAction pending)
		{
			pending = null;
			if (evidence == null || snapshot == null || snapshot.Player == null ||
				string.IsNullOrWhiteSpace(snapshot.StateId) || snapshot.IsLocalPlayerTurn != true ||
				snapshot.Player.PlayerId <= 0 || evidence.FrameId <= 0 || evidence.OptionId < 0 ||
				evidence.PowerCollectorEpoch != gameGeneration ||
				evidence.PowerActionOrdinal <= 0 || evidence.PowerGapCount < 0 ||
				evidence.PowerStartWatermark <= 0 ||
				evidence.PowerEndWatermark <= evidence.PowerStartWatermark)
			{
				return false;
			}
			var evidenceTargetEntityId = evidence.Target == null
				? (int?)null : evidence.Target.EntityId;
			if (!AdvisorBehaviorTargetBindingStatus.IsComplete(
				evidence.TargetBindingStatus,
				evidenceTargetEntityId))
			{
				return false;
			}

			var kind = "";
			var behaviorKind = "";
			AdvisorEntityState source = null;
			AdvisorEntityState target = null;
			var simulatorStatus = "not_replayed";
			if (string.Equals(evidence.PowerBlockType, "MAIN_END", StringComparison.Ordinal))
			{
				if (evidence.OptionId != 0 || evidence.Source != null || evidence.Target != null ||
					evidence.SubOption != -1 || evidence.BoardPosition != 0)
					return false;
				kind = "end_turn";
			}
			else
			{
				if (evidence.Source == null || evidence.Source.PlayerId != snapshot.Player.PlayerId ||
					!TryFindUniqueStateEntity(snapshot, evidence.Source.EntityId, out source) ||
					source.ControllerId != snapshot.Player.PlayerId ||
					string.IsNullOrWhiteSpace(evidence.Source.CardId) ||
					!string.Equals(source.CardId, evidence.Source.CardId, StringComparison.Ordinal))
				{
					return false;
				}

				if (evidence.Target != null)
				{
					if (!TryFindUniqueStateEntity(snapshot, evidence.Target.EntityId, out target) ||
						target.ControllerId <= 0 || target.ControllerId != evidence.Target.PlayerId ||
					(!string.IsNullOrWhiteSpace(evidence.Target.CardId) &&
					 !string.Equals(target.CardId, evidence.Target.CardId, StringComparison.Ordinal)))
					{
						return false;
					}
				}

				if (string.Equals(evidence.PowerBlockType, "ATTACK", StringComparison.Ordinal))
				{
					if (target == null || evidence.BoardPosition != 0 ||
						!IsCharacter(snapshot.Player, source) ||
						!IsCharacter(snapshot.Opponent, target) ||
						!string.Equals(evidence.Source.Zone, "PLAY", StringComparison.Ordinal) ||
						!string.Equals(evidence.Target.Zone, "PLAY", StringComparison.Ordinal))
					{
						return false;
					}
					kind = "attack";
				}
				else if (string.Equals(evidence.PowerBlockType, "PLAY", StringComparison.Ordinal))
				{
					if (ContainsEntity(snapshot.Player.Hand, source.EntityId) &&
						string.Equals(evidence.Source.Zone, "HAND", StringComparison.Ordinal))
					{
						kind = "play_card";
					}
					else if (snapshot.Player.HeroPower != null &&
						snapshot.Player.HeroPower.EntityId == source.EntityId &&
						string.Equals(evidence.Source.Zone, "PLAY", StringComparison.Ordinal))
					{
						kind = "hero_power";
					}
					else if (ContainsEntity(snapshot.Player.Board, source.EntityId) &&
						string.Equals(source.CardType, "LOCATION", StringComparison.Ordinal) &&
						string.Equals(evidence.Source.Zone, "PLAY", StringComparison.Ordinal))
					{
						// Keep the strict trajectory play-shaped and explicitly unsupported, while the
						// independent behavior corpus records the exact observed location activation.
						kind = "play_card";
						behaviorKind = "location_activate";
						simulatorStatus = "unsupported_location_activation";
					}
					else
					{
						return false;
					}
				}
				else
				{
					return false;
				}
			}

			if (string.IsNullOrWhiteSpace(behaviorKind))
				behaviorKind = kind;
			var identityStatus = evidence.ActionIdentityStatus ?? "unverified";
			var choices = (evidence.Choices ?? new List<HdtPowerChoiceEvidence>())
				.Where(item => item != null)
				.Select(item => new AdvisorObservedChoice
				{
					ChoiceId = item.ChoiceId > 0 ? (int?)item.ChoiceId : null,
					ChoiceType = item.ChoiceType ?? "",
					SourceEntityId = item.SourceEntityId > 0 ? (int?)item.SourceEntityId : null,
					OptionEntityIds = new List<int>(
						item.OptionEntityIds ?? new List<int>()),
					SelectedEntityIds = new List<int>(item.EntityIds ?? new List<int>()),
					Status = item.Status ?? "unresolved"
				}).ToList();
			AdvisorHdtRootCandidateSet hdtRootCandidates = null;
			string ignoredCandidateReason;
			if (evidence.SubOption == -1 && evidence.OptionsFrame != null)
			{
				AdvisorHdtRootCandidateBinder.TryBuild(
					evidence.OptionsFrame,
					snapshot,
					out hdtRootCandidates,
					out ignoredCandidateReason);
			}
			pending = new AdvisorPendingAction
			{
				PreState = snapshot,
				Kind = kind,
				BehaviorKind = behaviorKind,
				SourceEntityId = source?.EntityId,
				TargetEntityId = target?.EntityId,
				CardId = source?.CardId ?? "",
				SourceEvent = "hdt_power_log",
				SourceEntityResolution = source == null ? "not_applicable" : "exact_entity_id",
				TargetEntityResolution = target == null ? "not_applicable" : "exact_entity_id",
				BehaviorTargetBindingStatus = evidence.TargetBindingStatus,
				ObservedAtUtc = DateTime.UtcNow,
				GameGeneration = gameGeneration,
				ActionEventSequence = actionEventSequence,
				OptionId = evidence.OptionId,
				FrameId = evidence.FrameId,
				SubOption = evidence.SubOption,
				BoardPosition = evidence.BoardPosition,
				PowerStartWatermark = "g" + gameGeneration.ToString(CultureInfo.InvariantCulture) +
					":" + evidence.PowerStartWatermark.ToString(CultureInfo.InvariantCulture),
				PowerEndWatermark = "g" + gameGeneration.ToString(CultureInfo.InvariantCulture) +
					":" + evidence.PowerEndWatermark.ToString(CultureInfo.InvariantCulture),
				PowerCollectorEpoch = evidence.PowerCollectorEpoch,
				PowerActionOrdinal = evidence.PowerActionOrdinal,
				PowerGapCount = evidence.PowerGapCount,
				HdtRootCandidates = hdtRootCandidates,
				Choices = choices,
				ActionIdentityStatus = identityStatus,
				ChoiceStatus = evidence.ChoiceStatus ?? "unresolved",
				SimulatorStatus = simulatorStatus
			};
			if (pending.HdtRootCandidates != null &&
				!SelectedActionOccursExactlyOnce(pending, pending.HdtRootCandidates))
			{
				pending.HdtRootCandidates = null;
			}
			return true;
		}

		private static bool SelectedActionOccursExactlyOnce(
			AdvisorPendingAction pending,
			AdvisorHdtRootCandidateSet candidateSet)
		{
			if (pending == null || candidateSet == null ||
				!string.Equals(pending.Kind, pending.BehaviorKind, StringComparison.Ordinal))
				return false;
			return (candidateSet.Candidates ?? new List<AdvisorHdtRootCandidate>()).Count(item =>
				item != null && item.Action != null && item.OptionId == pending.OptionId &&
				string.Equals(item.Action.Kind, pending.Kind, StringComparison.Ordinal) &&
				item.Action.SourceEntityId == pending.SourceEntityId &&
				item.Action.TargetEntityId == pending.TargetEntityId &&
				string.Equals(item.Action.CardId ?? "", pending.CardId ?? "", StringComparison.Ordinal) &&
				item.Action.BoardPosition == (pending.BoardPosition ?? 0)) == 1;
		}

		private static string HdtRootCandidateIdentity(AdvisorHdtRootCandidateSet candidateSet)
		{
			if (candidateSet == null)
				return "";
			return candidateSet.CollectorEpoch.ToString(CultureInfo.InvariantCulture) + ":" +
				candidateSet.FrameId.ToString(CultureInfo.InvariantCulture) + ":" +
				candidateSet.FrameWatermark.ToString(CultureInfo.InvariantCulture) + ":" +
				(candidateSet.StateId ?? "");
		}

		private static bool TryFindUniqueStateEntity(
			AdvisorGameState state,
			int entityId,
			out AdvisorEntityState entity)
		{
			entity = null;
			var matches = EnumerateAdvisorEntities(state)
				.Where(item => item != null && item.EntityId == entityId)
				.GroupBy(item => new
				{
					item.EntityId,
					CardId = item.CardId ?? "",
					item.ControllerId,
					Zone = item.Zone ?? ""
				}).Select(group => group.First()).ToList();
			if (matches.Count != 1)
				return false;
			entity = matches[0];
			return true;
		}

		private static IEnumerable<AdvisorEntityState> EnumerateAdvisorEntities(AdvisorGameState state)
		{
			if (state == null)
				yield break;
			foreach (var player in new[] { state.Player, state.Opponent }.Where(item => item != null))
			{
				foreach (var item in new[]
				{
					player.PlayerEntity, player.Hero, player.HeroPower, player.Weapon
				}.Where(item => item != null))
					yield return item;
				foreach (var item in new[]
				{
					player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
					player.SetAside, player.RemovedFromGame, player.OtherEntities
				}.Where(items => items != null).SelectMany(items => items))
					if (item != null)
						yield return item;
			}
			if (state.GameEntity != null)
				yield return state.GameEntity;
			foreach (var item in state.OtherPublicEntities ?? new List<AdvisorEntityState>())
				if (item != null)
					yield return item;
		}

		private static bool ContainsEntity(IEnumerable<AdvisorEntityState> entities, int entityId)
		{
			return (entities ?? Enumerable.Empty<AdvisorEntityState>()).Any(
				item => item != null && item.EntityId == entityId);
		}

		private static bool IsCharacter(AdvisorPlayerState player, AdvisorEntityState entity)
		{
			return player != null && entity != null &&
				((player.Hero != null && player.Hero.EntityId == entity.EntityId) ||
				 ContainsEntity(player.Board, entity.EntityId));
		}

		private void RecordAdvisorAttack(AttackInfo attack, string sourceEvent)
		{
			if (attack == null)
			{
				return;
			}
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
			{
				snapshot = _lastAdvisorSnapshot;
			}
			var sourceCardId = attack.Attacker?.Id ?? "";
			var targetCardId = attack.Defender?.Id ?? "";
			string sourceResolution;
			string targetResolution;
			var sourceEntityId = FindUniqueEntityId(
				snapshot, sourceCardId, true, "character", out sourceResolution);
			var targetEntityId = FindUniqueEntityId(
				snapshot, targetCardId, false, "character", out targetResolution);
			RecordAdvisorAction(
				"attack",
				sourceEntityId,
				targetEntityId,
				sourceCardId,
				sourceEvent,
				snapshot,
				sourceResolution,
				targetResolution);
		}

		private void RecordAdvisorHeroPower()
		{
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
			{
				snapshot = _lastAdvisorSnapshot;
			}
			RecordAdvisorAction(
				"hero_power",
				snapshot?.Player?.HeroPower?.EntityId,
				null,
				snapshot?.Player?.HeroPower?.CardId ?? "",
				"player_hero_power",
				snapshot,
				snapshot?.Player?.HeroPower == null ? "missing" : "dedicated_role_match",
				"not_observed_by_hdt_gameevents");
		}

		private void RecordOpponentAdvisorPlay(Card card)
		{
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
				snapshot = _lastAdvisorSnapshot;
			RegisterOpponentAdvisorBehavior(
				"play_card",
				null,
				null,
				card?.Id ?? "",
				"opponent_play",
				"hdt_opponent_event",
				snapshot,
				AdvisorBehaviorTargetBindingStatus.Unknown);
		}

		private void RecordOpponentAdvisorAttack(AttackInfo attack)
		{
			if (attack == null)
				return;
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
				snapshot = _lastAdvisorSnapshot;
			var sourceCardId = attack.Attacker?.Id ?? "";
			var targetCardId = attack.Defender?.Id ?? "";
			string sourceResolution;
			string targetResolution;
			var sourceEntityId = FindUniqueEntityId(
				snapshot, sourceCardId, false, "character", out sourceResolution);
			var targetEntityId = FindUniqueEntityId(
				snapshot, targetCardId, true, "character", out targetResolution);
			RegisterOpponentAdvisorBehavior(
				"attack",
				sourceEntityId,
				targetEntityId,
				sourceCardId,
				"opponent_attack",
				"hdt_opponent_event",
				snapshot,
				targetEntityId.HasValue
					? AdvisorBehaviorTargetBindingStatus.ExactEntityId
					: AdvisorBehaviorTargetBindingStatus.Unknown);
		}

		private void RecordOpponentAdvisorHeroPower()
		{
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
				snapshot = _lastAdvisorSnapshot;
			RegisterOpponentAdvisorBehavior(
				"hero_power",
				snapshot?.Opponent?.HeroPower?.EntityId,
				null,
				snapshot?.Opponent?.HeroPower?.CardId ?? "",
				"opponent_hero_power",
				"hdt_opponent_event",
				snapshot,
				AdvisorBehaviorTargetBindingStatus.Unknown);
		}

		private void RecordOpponentAdvisorEndTurn()
		{
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
				snapshot = _lastAdvisorSnapshot;
			RegisterOpponentAdvisorBehavior(
				"end_turn",
				null,
				null,
				"",
				"turn_passed_to_player",
				"active_player",
				snapshot,
				AdvisorBehaviorTargetBindingStatus.ExplicitNone);
		}

		private void RegisterOpponentAdvisorBehavior(
			string kind,
			int? sourceEntityId,
			int? targetEntityId,
			string cardId,
			string sourceEvent,
			string actorEvidence,
			AdvisorGameState expectedSnapshot,
			string targetBindingStatus)
		{
			lock (_advisorStateLock)
			{
				if (expectedSnapshot == null || _lastAdvisorSnapshot == null ||
					!string.Equals(
						expectedSnapshot.StateId,
						_lastAdvisorSnapshot.StateId,
						StringComparison.Ordinal))
				{
					return;
				}
				RegisterAdvisorBehaviorLocked(
					"opponent",
					"opponent",
					actorEvidence,
					sourceEvent,
					kind,
					sourceEntityId,
					targetEntityId,
					targetBindingStatus,
					cardId,
					expectedSnapshot,
					DateTime.UtcNow,
					false,
					"");
			}
		}

		private void RegisterAdvisorPowerBehaviorLocked(AdvisorPendingAction pending)
		{
			if (pending == null || pending.PreState == null ||
				!pending.HasExactBehaviorPowerIdentity)
			{
				return;
			}
			var behaviorKind = string.IsNullOrWhiteSpace(pending.BehaviorKind)
				? pending.Kind : pending.BehaviorKind;
			RegisterAdvisorBehaviorLocked(
				"local",
				"friendly",
				"hdt_power_log",
				"hdt_power_log",
				behaviorKind,
				pending.SourceEntityId,
				pending.TargetEntityId,
				pending.BehaviorTargetBindingStatus,
				pending.CardId,
				pending.PreState,
				pending.ObservedAtUtc,
				true,
				pending.PowerStartWatermark ?? "",
				pending.SubOption,
				pending.BoardPosition,
				pending.ChoiceStatus ?? "unresolved",
				pending.Choices);
		}

		private void RegisterAdvisorBehaviorLocked(
			string actorSide,
			string actorPlayerId,
			string actorEvidence,
			string sourceEvent,
			string kind,
			int? sourceEntityId,
			int? targetEntityId,
			string targetBindingStatus,
			string cardId,
			AdvisorGameState snapshot,
			DateTime observedAtUtc,
			bool hasPowerIdentity,
			string powerIdentityKey,
			int? subOption = null,
			int? boardPosition = null,
			string choiceStatus = "not_observed",
			IEnumerable<AdvisorObservedChoice> choices = null)
		{
			if (!_advisorGameActive || !_advisorRuntimeTrainingLog ||
				_advisorBehaviorCollector == null || snapshot == null ||
				string.IsNullOrWhiteSpace(snapshot.StateId) ||
				string.IsNullOrWhiteSpace(snapshot.GameId))
			{
				return;
			}
			var localActor = string.Equals(actorSide, "local", StringComparison.Ordinal);
			if (snapshot.IsLocalPlayerTurn != localActor)
				return;

			var endTurn = string.Equals(kind, "end_turn", StringComparison.Ordinal);
			var opponentHiddenPlay = !localActor &&
				string.Equals(kind, "play_card", StringComparison.Ordinal);
			var exactPublicIdentity = !endTurn && !opponentHiddenPlay &&
				HasExactPublicBehaviorBinding(
					snapshot,
					localActor,
					kind,
					sourceEntityId,
					targetEntityId,
					targetBindingStatus,
					cardId);
			var identityStatus = endTurn
				? "event_only"
				: (exactPublicIdentity ? "exact_public_entity" : "unknown");
			var visibilityStatus = opponentHiddenPlay
				? (string.IsNullOrWhiteSpace(cardId)
					? "hidden_source" : "revealed_post_action")
				: "public_pre_state";
			if (endTurn)
			{
				sourceEntityId = null;
				targetEntityId = null;
				cardId = "";
			}

			_advisorBehaviorTracker.Register(new AdvisorBehaviorPendingEvidence
			{
				GameGeneration = _advisorGameGeneration,
				ObservedAtUtc = observedAtUtc.Kind == DateTimeKind.Utc
					? observedAtUtc : observedAtUtc.ToUniversalTime(),
				PreState = snapshot,
				ActorSide = actorSide,
				ActorPlayerId = actorPlayerId,
				ActorEvidence = actorEvidence,
				IdentityStatus = identityStatus,
				VisibilityStatus = visibilityStatus,
				SourceEvent = sourceEvent,
				Action = new AdvisorBehaviorAction
				{
					Kind = kind ?? "",
					SourceEntityId = sourceEntityId,
					TargetEntityId = targetEntityId,
					CardId = cardId ?? "",
					SubOption = subOption,
					BoardPosition = boardPosition,
					ChoiceStatus = choiceStatus ?? "not_observed",
					Choices = (choices ?? new List<AdvisorObservedChoice>())
						.Where(item => item != null)
						.Select(item => new AdvisorObservedChoice
						{
							ChoiceId = item.ChoiceId,
							ChoiceType = item.ChoiceType ?? "",
							SourceEntityId = item.SourceEntityId,
							OptionEntityIds = new List<int>(
								item.OptionEntityIds ?? new List<int>()),
							SelectedEntityIds = new List<int>(
								item.SelectedEntityIds ?? new List<int>()),
							Status = item.Status ?? "unresolved"
						}).ToList()
				},
				TargetBindingStatus = targetBindingStatus ??
					AdvisorBehaviorTargetBindingStatus.Unknown,
				HasPowerIdentity = hasPowerIdentity,
				PowerIdentityKey = powerIdentityKey ?? ""
			});
		}

		internal static bool HasExactPublicBehaviorBinding(
			AdvisorGameState snapshot,
			bool localActor,
			string kind,
			int? sourceEntityId,
			int? targetEntityId,
			string targetBindingStatus,
			string cardId)
		{
			if (snapshot == null || !sourceEntityId.HasValue ||
				string.IsNullOrWhiteSpace(cardId))
			{
				return false;
			}
			AdvisorEntityState boundTarget = null;
			if (!AdvisorBehaviorTargetBindingStatus.IsComplete(
				targetBindingStatus,
				targetEntityId) ||
				(targetEntityId.HasValue &&
				 !TryFindUniqueStateEntity(snapshot, targetEntityId.Value, out boundTarget)))
			{
				return false;
			}
			var actor = localActor ? snapshot.Player : snapshot.Opponent;
			var other = localActor ? snapshot.Opponent : snapshot.Player;
			AdvisorEntityState source;
			if (actor == null || other == null ||
				!TryFindUniqueStateEntity(snapshot, sourceEntityId.Value, out source) ||
				!string.Equals(source.CardId, cardId, StringComparison.Ordinal))
			{
				return false;
			}

			if (string.Equals(kind, "play_card", StringComparison.Ordinal))
				return ContainsEntity(actor.Hand, source.EntityId);
			if (string.Equals(kind, "hero_power", StringComparison.Ordinal))
				return actor.HeroPower != null && actor.HeroPower.EntityId == source.EntityId;
			if (string.Equals(kind, "location_activate", StringComparison.Ordinal))
			{
				return ContainsEntity(actor.Board, source.EntityId) && string.Equals(
					source.CardType,
					"LOCATION",
					StringComparison.Ordinal);
			}
			if (!string.Equals(kind, "attack", StringComparison.Ordinal) ||
				!targetEntityId.HasValue || !IsCharacter(actor, source))
			{
				return false;
			}
			return IsCharacter(other, boundTarget);
		}

		internal static int? FindUniqueEntityId(
			AdvisorGameState snapshot,
			string cardId,
			bool playerSide,
			string location,
			out string resolution)
		{
			if (snapshot == null)
			{
				resolution = "snapshot_missing";
				return null;
			}
			if (string.IsNullOrWhiteSpace(cardId))
			{
				resolution = "card_id_missing";
				return null;
			}
			var player = playerSide ? snapshot.Player : snapshot.Opponent;
			if (player == null)
			{
				resolution = "player_state_missing";
				return null;
			}
			IEnumerable<AdvisorEntityState> candidates = location == "hand"
				? player.Hand ?? new List<AdvisorEntityState>()
				: (player.Board ?? new List<AdvisorEntityState>())
					.Concat(new[] { player.Hero, player.Weapon }.Where(item => item != null));
			var matches = candidates.Where(item =>
				item != null && string.Equals(item.CardId, cardId, StringComparison.Ordinal))
				.ToList();
			if (matches.Count == 1)
			{
				resolution = "unique_card_id_match";
				return matches[0].EntityId;
			}
			resolution = matches.Count == 0 ? "not_found" : "ambiguous_card_id_match";
			return null;
		}

		private void RecordAdvisorAction(
			string kind,
			int? sourceEntityId,
			int? targetEntityId,
			string cardId,
			string sourceEvent,
			AdvisorGameState expectedSnapshot = null,
			string sourceResolution = null,
			string targetResolution = null)
		{
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || !_advisorRuntimeTrainingLog)
				{
					return;
				}
				snapshot = expectedSnapshot ?? _lastAdvisorSnapshot;
				if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StateId))
				{
					return;
				}
				if (kind == "end_turn" && snapshot.IsLocalPlayerTurn != true)
				{
					return;
				}
				if (expectedSnapshot != null &&
					(_lastAdvisorSnapshot == null || !string.Equals(
						expectedSnapshot.StateId,
						_lastAdvisorSnapshot.StateId,
						StringComparison.Ordinal)))
				{
					return;
				}
				var noEntities = string.Equals(kind, "end_turn", StringComparison.Ordinal);
				var pending = new AdvisorPendingAction
				{
					PreState = snapshot,
					Kind = kind ?? "",
					BehaviorKind = kind ?? "",
					SourceEntityId = sourceEntityId,
					TargetEntityId = targetEntityId,
					CardId = cardId ?? "",
					SourceEvent = sourceEvent ?? "hdt_event",
					SourceEntityResolution = sourceResolution ?? (noEntities
						? "not_applicable"
						: (sourceEntityId.HasValue ? "unique_role_or_card_match" : "missing")),
					TargetEntityResolution = targetResolution ?? (noEntities
						? "not_applicable"
						: (targetEntityId.HasValue ? "unique_role_or_card_match" : "missing")),
					BehaviorTargetBindingStatus = noEntities
						? AdvisorBehaviorTargetBindingStatus.ExplicitNone
						: (targetEntityId.HasValue
							? AdvisorBehaviorTargetBindingStatus.ExactEntityId
							: AdvisorBehaviorTargetBindingStatus.Unknown),
					ObservedAtUtc = DateTime.UtcNow,
					GameGeneration = _advisorGameGeneration,
					ActionEventSequence = 0
				};
				if (_advisorTransitionTracker.Register(pending))
					pending.ActionEventSequence = ++_advisorActionSequence;
				RegisterAdvisorBehaviorLocked(
					"local",
					"friendly",
					string.Equals(kind, "end_turn", StringComparison.Ordinal)
						? "active_player" : "hdt_player_event",
					sourceEvent ?? "unknown",
					kind,
					sourceEntityId,
					targetEntityId,
					pending.BehaviorTargetBindingStatus,
					cardId,
					snapshot,
					pending.ObservedAtUtc,
					false,
					"");
			}
		}

		private void ObserveAdvisorTransitions(IEnumerable<AdvisorObservation> observations)
		{
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || !_advisorRuntimeTrainingLog)
					return;
			}
			foreach (var observation in observations ?? Enumerable.Empty<AdvisorObservation>())
				ObserveAdvisorAsync(observation).Forget();
		}

		private void CollectAdvisorBehaviorCaptures(
			IEnumerable<AdvisorBehaviorCapture> captures)
		{
			var collector = _advisorBehaviorCollector;
			var outbox = _advisorBehaviorOutbox;
			if (collector == null || outbox == null)
				return;
			lock (_advisorStateLock)
			{
				if (_advisorBehaviorCaptureFaulted)
					return;
			}
			var enqueued = false;
			foreach (var capture in captures ?? Enumerable.Empty<AdvisorBehaviorCapture>())
			{
				AdvisorBehaviorRecord record;
				string rejectionReason;
				try
				{
					if (!collector.TryCollectAndCommit(
						capture,
						candidate => { outbox.Enqueue(candidate); },
						out record,
						out rejectionReason))
					{
						Log.Debug("一条行为证据未通过公开信息安全校验，已忽略。");
						continue;
					}
					enqueued = true;
				}
				catch (Exception ex)
				{
					lock (_advisorStateLock)
						_advisorBehaviorCaptureFaulted = true;
					Log.Warn(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "behavior_outbox"));
					break;
				}
			}
			if (enqueued)
				QueueAdvisorBehaviorFlush();
		}

		private void QueueAdvisorBehaviorFlush()
		{
			lock (_advisorStateLock)
			{
				_advisorBehaviorFlushRequested = true;
				if (_advisorBehaviorFlushInProgress || !_advisorRuntimeTrainingLog ||
					_advisorBehaviorClient == null || _advisorBehaviorOutbox == null ||
					(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
				{
					return;
				}
				_advisorBehaviorFlushInProgress = true;
				_advisorBehaviorFlushTask = Task.Run(
					(Func<Task>)PumpAdvisorBehaviorOutboxAsync);
			}
		}

		private async Task PumpAdvisorBehaviorOutboxAsync()
		{
			try
			{
				while (true)
				{
					IAdvisorBehaviorClient client;
					AdvisorBehaviorOutbox outbox;
					CancellationToken cancellationToken;
					lock (_advisorStateLock)
					{
						if (!_advisorBehaviorFlushRequested || !_advisorRuntimeTrainingLog ||
							_advisorBehaviorClient == null || _advisorBehaviorOutbox == null ||
							(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
						{
							return;
						}
						_advisorBehaviorFlushRequested = false;
						client = _advisorBehaviorClient;
						outbox = _advisorBehaviorOutbox;
						cancellationToken = _advisorLifetimeCancellation.Token;
					}

					var result = await outbox.FlushAsync(client, cancellationToken)
						.ConfigureAwait(false);
					if (!result.Succeeded)
					{
						Log.Debug("行为语料暂未送达本机求解器，将保留并稍后重试。");
						int failureCount;
						lock (_advisorStateLock)
							failureCount = ++_advisorBehaviorFlushFailureCount;
						await Task.Delay(
							GetAdvisorOutboxRetryDelay(failureCount),
							cancellationToken).ConfigureAwait(false);
						lock (_advisorStateLock)
						{
							if (!_advisorRuntimeTrainingLog || _advisorBehaviorClient == null ||
								_advisorBehaviorOutbox == null ||
								(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
							{
								return;
							}
							_advisorBehaviorFlushRequested = true;
						}
						continue;
					}
					lock (_advisorStateLock)
						_advisorBehaviorFlushFailureCount = 0;
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			catch (Exception ex)
			{
				Log.Debug(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "behavior_flush"));
			}
			finally
			{
				var restart = false;
				lock (_advisorStateLock)
				{
					_advisorBehaviorFlushInProgress = false;
					restart = _advisorBehaviorFlushRequested && _advisorRuntimeTrainingLog &&
						_advisorBehaviorClient != null && _advisorBehaviorOutbox != null &&
						!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true);
				}
				if (restart)
					QueueAdvisorBehaviorFlush();
			}
		}

		internal static TimeSpan GetAdvisorOutboxRetryDelay(int consecutiveFailureCount)
		{
			var exponent = Math.Max(0, Math.Min(16, consecutiveFailureCount - 1));
			var milliseconds = 250L * (1L << exponent);
			return TimeSpan.FromMilliseconds(Math.Min(
				(long)AdvisorOutboxMaximumRetryDelay.TotalMilliseconds,
				milliseconds));
		}

		private void WaitForAdvisorBehaviorFlush(TimeSpan timeout)
		{
			Task task;
			lock (_advisorStateLock)
				task = _advisorBehaviorFlushTask;
			if (task == null || task.IsCompleted)
				return;
			try
			{
				task.Wait(timeout);
			}
			catch (AggregateException)
			{
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private void ReleaseAdvisorBehaviorOutbox()
		{
			AdvisorBehaviorOutbox outbox;
			Task flushTask;
			lock (_advisorStateLock)
			{
				outbox = _advisorBehaviorOutbox;
				_advisorBehaviorOutbox = null;
				flushTask = _advisorBehaviorFlushTask;
			}
			if (outbox == null)
				return;
			if (flushTask == null || flushTask.IsCompleted)
			{
				outbox.Dispose();
				return;
			}
			flushTask.ContinueWith(
				_ => outbox.Dispose(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}

		private void QueueAdvisorResultFlush()
		{
			lock (_advisorStateLock)
			{
				_advisorResultFlushRequested = true;
				if (_advisorResultFlushInProgress || !_advisorRuntimeTrainingLog ||
					_advisorResultClient == null || _advisorResultOutbox == null ||
					(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
				{
					return;
				}
				_advisorResultFlushInProgress = true;
				_advisorResultFlushTask = Task.Run((Func<Task>)PumpAdvisorResultOutboxAsync);
			}
		}

		private async Task PumpAdvisorResultOutboxAsync()
		{
			try
			{
				while (true)
				{
					IAdvisorResultClient client;
					AdvisorResultOutbox outbox;
					CancellationToken cancellationToken;
					lock (_advisorStateLock)
					{
						if (!_advisorResultFlushRequested || !_advisorRuntimeTrainingLog ||
							_advisorResultClient == null || _advisorResultOutbox == null ||
							(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
						{
							return;
						}
						_advisorResultFlushRequested = false;
						client = _advisorResultClient;
						outbox = _advisorResultOutbox;
						cancellationToken = _advisorLifetimeCancellation.Token;
					}

					var result = await outbox.FlushAsync(client, cancellationToken)
						.ConfigureAwait(false);
					if (!result.Succeeded)
					{
						Log.Debug("终局结果暂未送达本机求解器，已保留并将在后台重试。");
						int failureCount;
						lock (_advisorStateLock)
							failureCount = ++_advisorResultFlushFailureCount;
						await Task.Delay(
							GetAdvisorOutboxRetryDelay(failureCount),
							cancellationToken).ConfigureAwait(false);
						lock (_advisorStateLock)
						{
							if (!_advisorRuntimeTrainingLog || _advisorResultClient == null ||
								_advisorResultOutbox == null ||
								(_advisorLifetimeCancellation?.IsCancellationRequested ?? true))
							{
								return;
							}
							_advisorResultFlushRequested = true;
						}
						continue;
					}
					lock (_advisorStateLock)
						_advisorResultFlushFailureCount = 0;
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			catch (Exception ex)
			{
				Log.Debug(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "result_flush"));
			}
			finally
			{
				var restart = false;
				lock (_advisorStateLock)
				{
					_advisorResultFlushInProgress = false;
					restart = _advisorResultFlushRequested && _advisorRuntimeTrainingLog &&
						_advisorResultClient != null && _advisorResultOutbox != null &&
						!(_advisorLifetimeCancellation?.IsCancellationRequested ?? true);
				}
				if (restart)
					QueueAdvisorResultFlush();
			}
		}

		private void WaitForAdvisorResultFlush(TimeSpan timeout)
		{
			Task task;
			lock (_advisorStateLock)
				task = _advisorResultFlushTask;
			if (task == null || task.IsCompleted)
				return;
			try { task.Wait(timeout); }
			catch (AggregateException) { }
			catch (ObjectDisposedException) { }
		}

		private void ReleaseAdvisorResultOutbox()
		{
			AdvisorResultOutbox outbox;
			Task flushTask;
			lock (_advisorStateLock)
			{
				outbox = _advisorResultOutbox;
				_advisorResultOutbox = null;
				flushTask = _advisorResultFlushTask;
			}
			if (outbox == null)
				return;
			if (flushTask == null || flushTask.IsCompleted)
			{
				outbox.Dispose();
				return;
			}
			flushTask.ContinueWith(
				_ => outbox.Dispose(),
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}

		private void RecordAdvisorResult(string result, string sourceEvent)
		{
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || !_advisorRuntimeTrainingLog)
					return;
			}
			// Consume the latest suffix and close any completed root before freezing the
			// terminal counters. A root without a closing watermark is counted as a gap.
			ProcessAdvisorPowerTrace();
			List<HdtPowerActionEvidence> terminalEvidence;
			try
			{
				terminalEvidence = _hdtPowerTraceCollector.FinalizeAtTerminal();
			}
			catch
			{
				_hdtPowerTraceCollector.Disconnect();
				terminalEvidence = new List<HdtPowerActionEvidence>();
			}
			foreach (var evidence in terminalEvidence)
				RecordAdvisorPowerAction(evidence, evidence.PowerCollectorEpoch);
			var powerTrace = _hdtPowerTraceCollector.GetTraceSummary();
			AdvisorGameState snapshot;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || _advisorResultRecorded)
				{
					return;
				}
				snapshot = _lastAdvisorSnapshot;
			}
			if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StateId))
			{
				return;
			}
			var observation = new AdvisorObservation
			{
				Kind = "result",
				StateId = snapshot.StateId,
				GameId = snapshot.GameId ?? "",
				ObservedAtUtc = DateTime.UtcNow,
				Result = result ?? "unknown",
				Metadata = BuildAdvisorResultMetadata(sourceEvent, snapshot, powerTrace)
			};
			AdvisorResultOutbox outbox;
			lock (_advisorStateLock)
			{
				if (!_advisorGameActive || _advisorResultRecorded ||
					!ReferenceEquals(snapshot, _lastAdvisorSnapshot))
				{
					return;
				}
				outbox = _advisorResultOutbox;
			}
			if (outbox == null)
				return;
			try
			{
				// The recorded flag advances only after the exact terminal payload is durable.
				// A lost HTTP response therefore cannot turn into a permanently lost result.
				outbox.Enqueue(observation);
				lock (_advisorStateLock)
				{
					if (_advisorGameActive && ReferenceEquals(snapshot, _lastAdvisorSnapshot))
						_advisorResultRecorded = true;
				}
				QueueAdvisorResultFlush();
			}
			catch (Exception ex)
			{
				Log.Warn(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "result_outbox"));
			}
		}

		internal static Dictionary<string, string> BuildAdvisorResultMetadata(
			string sourceEvent,
			AdvisorGameState snapshot,
			HdtPowerTraceSummary powerTrace)
		{
			var trace = powerTrace ?? new HdtPowerTraceSummary();
			return new Dictionary<string, string>
			{
				{ "source", sourceEvent ?? "hdt_event" },
				{ "mode", snapshot?.GameMode ?? "" },
				{ "trajectory_schema", "trajectory-readiness-v1" },
				{ "capture_contract", "terminal_result_v1" },
				{ "completeness", "terminal_result" },
				{ "training_eligible", "true" },
				{ "game_generation", trace.GameGeneration.ToString(CultureInfo.InvariantCulture) },
				{ "power_collector_epoch", trace.PowerCollectorEpoch.ToString(CultureInfo.InvariantCulture) },
				{ "power_committed_action_count", trace.CommittedActionCount.ToString(CultureInfo.InvariantCulture) },
				{ "power_recorded_action_count", trace.RecordedActionCount.ToString(CultureInfo.InvariantCulture) },
				{ "power_gap_count", trace.GapCount.ToString(CultureInfo.InvariantCulture) },
				{ "power_trace_status", trace.TraceStatus ?? "tainted" }
			};
		}

		private async Task ObserveAdvisorAsync(AdvisorObservation observation)
		{
			try
			{
				await _advisorController.ObserveAsync(observation).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Log.Debug(AdvisorUserMessages.RuntimeFailureDiagnostic(ex, "observe"));
			}
		}

		private void StopAdvisorGame(string reason)
		{
			CancellationTokenSource cancellation;
			bool wasActive;
			bool liveEnabled;
			int discardedTransitionCandidates;
			List<AdvisorBehaviorCapture> unresolvedBehavior;
			lock (_advisorStateLock)
			{
				wasActive = _advisorGameActive;
				liveEnabled = _advisorRuntimeEnabled;
				discardedTransitionCandidates = _advisorTransitionTracker.DiscardUnresolved();
				unresolvedBehavior = _advisorRuntimeTrainingLog
					? _advisorBehaviorTracker.DrainUnresolved(_advisorGameGeneration)
					: new List<AdvisorBehaviorCapture>();
				_advisorBehaviorTracker.Reset();
				_advisorGameActive = false;
				_advisorGameGeneration++;
				_advisorRefreshRevision++;
				cancellation = _advisorGameCancellation;
				_advisorGameCancellation = null;
				_lastAdvisorSnapshot = null;
				_advisorAcceptedStateId = "";
				_advisorAcceptedRootFrameIdentity = "";
				_advisorRefreshPending = false;
				_advisorRefreshForce = false;
				_advisorRefreshDueUtc = DateTime.MaxValue;
				_nextAdvisorPollUtc = DateTime.MaxValue;
			}
			if (!wasActive)
			{
				return;
			}
			CollectAdvisorBehaviorCaptures(unresolvedBehavior);
			_advisorBehaviorCollector?.EndGame();
			_hdtPowerTraceCollector.Disconnect();
			if (discardedTransitionCandidates > 0)
			{
				Log.Debug("对局结束时放弃了 " +
					discardedTransitionCandidates.ToString(CultureInfo.InvariantCulture) +
					" 条未闭合训练候选；未声称存在后置局面。");
			}
			cancellation?.Cancel();
			cancellation?.Dispose();
			_advisorController?.CancelCurrent("顾问对局已结束。");
			if (liveEnabled && reason == "in_menu")
			{
				_advisorView?.OnMenu();
			}
			else if (liveEnabled)
			{
				_advisorView?.OnGameEnded();
			}
			Log.Debug("顾问对局采集已停止。");
		}

		private void StartMetaDeckLoad(IMetaRetriever metaRetriever)
		{
			var startedAt = DateTime.Now;
			var loading = MetaDeckLoadSnapshot.Loading(startedAt);
			SetMetaDeckLoadState(new List<Deck>(), loading);
			TryWriteMetaDeckLoadStatus(loading);
			Log.Info("牌组库正在后台加载。");

			Task.Run(async () =>
			{
				try
				{
					var decks = await metaRetriever.RetrieveMetaDecks(_config);
					decks = decks ?? new List<Deck>();
					var completedAt = DateTime.Now;
					var snapshot = MetaDeckLoadSnapshot.Ready(decks.Count, startedAt, completedAt);
					SetMetaDeckLoadState(decks, snapshot);
					TryWriteMetaDeckLoadStatus(snapshot);
					if (snapshot.IsReady)
					{
						Log.Info("牌组库加载完成，共 " + snapshot.DeckCount + " 套牌。");
					}
					else
					{
						Log.Warn("Meta deck library loaded no decks; predictions remain unavailable.");
					}
				}
				catch (Exception ex)
				{
					var summary = SummarizeException(ex);
					var snapshot = MetaDeckLoadSnapshot.Failed(summary, startedAt, DateTime.Now);
					SetMetaDeckLoadState(new List<Deck>(), snapshot);
					TryWriteMetaDeckLoadStatus(snapshot);
					Log.Warn("Meta deck library load failed: " + summary);
					Log.Error(ex);
				}
			});
		}

		private static string EnsureCurrentPatchState()
		{
			try
			{
				var result = PatchStateService.EnsureCurrentPatchState(DataDirectory);
				if (result.PatchChanged)
				{
					Log.Info("检测到炉石版本边界" +
						(string.IsNullOrWhiteSpace(result.PatchVersion)
							? ""
							: " (" + result.PatchVersion + ")") +
						"；已归档 " + result.ArchivedFileCount +
						" 个本地数据文件，开始新的版本统计窗口。");
				}
				return result.PatchVersion ?? "";
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to update patch state: " + ex.Message);
				return "";
			}
		}

		private void SetMetaDeckLoadState(List<Deck> decks, MetaDeckLoadSnapshot snapshot)
		{
			lock (_metaDeckLock)
			{
				_metaDecks = new ReadOnlyCollection<Deck>(decks ?? new List<Deck>());
				_metaDeckLoadSnapshot = snapshot ?? MetaDeckLoadSnapshot.Loading(DateTime.Now);
			}
		}

		private ReadOnlyCollection<Deck> GetLoadedMetaDecks(out MetaDeckLoadSnapshot snapshot)
		{
			lock (_metaDeckLock)
			{
				snapshot = _metaDeckLoadSnapshot;
				return _metaDecks;
			}
		}

		private static void TryWriteMetaDeckLoadStatus(MetaDeckLoadSnapshot snapshot)
		{
			try
			{
				MetaDeckLoadStatusStore.Write(DataDirectory, snapshot);
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to write meta deck load status: " + ex.Message);
			}
		}

		internal static string SummarizeException(Exception ex)
		{
			return ex == null
				? "Unknown error"
				: ex.GetType().Name + ": " + ex.Message;
		}

		public void OnUnload()
		{
			CancellationTokenSource startupCancellation;
			lock (_advisorStateLock)
			{
				_advisorRuntimeEnabled = false;
				_advisorWorkerLifecycleRevision++;
				_nextAdvisorWorkerStartUtc = DateTime.MaxValue;
				startupCancellation = _advisorWorkerStartCancellation;
			}
			TryCancel(startupCancellation);
			StopAdvisorGame("plugin_unload");
			WaitForAdvisorBehaviorFlush(TimeSpan.FromSeconds(2));
			WaitForAdvisorResultFlush(TimeSpan.FromSeconds(2));
			_advisorLifetimeCancellation?.Cancel();
			WaitForAdvisorBehaviorFlush(TimeSpan.FromSeconds(1));
			WaitForAdvisorResultFlush(TimeSpan.FromSeconds(1));
			lock (_advisorStateLock)
			{
				_advisorBehaviorClient = null;
				_advisorBehaviorFlushRequested = false;
				_advisorResultClient = null;
				_advisorResultFlushRequested = false;
			}
			if (_settingsWindow != null)
			{
			    if (_settingsWindow.IsVisible)
			    {
			        _settingsWindow.Close();
			    }
			    _settingsWindow = null;
			}
			_config?.Save();
			_matchHistoryRecorder?.Complete("plugin_unload");
			_matchHistoryRecorder = null;
			_view?.OnUnload();
			if (_advisorController != null)
			{
				_advisorController.Updated -= OnAdvisorRecommendationUpdated;
				_advisorController.Dispose();
				_advisorController = null;
			}
			if (_advisorWorkerManager != null)
			{
				_advisorWorkerManager.Exited -= OnAdvisorWorkerExited;
				_advisorWorkerManager.Dispose();
				_advisorWorkerManager = null;
			}
			_advisorView?.OnUnload();
			_advisorView = null;
			_advisorBehaviorCollector?.EndGame();
			_advisorBehaviorCollector = null;
			ReleaseAdvisorBehaviorOutbox();
			ReleaseAdvisorResultOutbox();
			_advisorLifetimeCancellation?.Dispose();
			_advisorLifetimeCancellation = null;
			_metaDashboardView?.OnUnload();
			_metaDashboardView = null;
			_postGameMetaRefresher = null;
			_quickDashboardRefresher = null;
		}

		public void OnUpdate()
		{
			SynchronizeAdvisorRuntimeSettings();
			ProcessAdvisorPowerTrace();
			ProcessPendingAdvisorRefresh();
			PollAdvisorStateIfDue();
			UpdateStandardRecommendationDashboard(false);
		}

		private sealed class AdvisorControllerResultClient : IAdvisorResultClient
		{
			private readonly AdvisorRecommendationController _controller;

			internal AdvisorControllerResultClient(AdvisorRecommendationController controller)
			{
				_controller = controller ?? throw new ArgumentNullException(nameof(controller));
			}

			public Task<AdvisorResultAppendResult> AppendResultJsonAsync(
				string json,
				CancellationToken cancellationToken)
			{
				return _controller.AppendResultJsonAsync(json, cancellationToken);
			}
		}

		internal static bool ShouldStartTrackingGame(Format? format, GameMode mode, bool alreadyTracking)
		{
			if (alreadyTracking)
			{
				return false;
			}

			return format == Format.Standard &&
				(mode == GameMode.Ranked || mode == GameMode.Casual || mode == GameMode.Friendly);
		}

		internal static GameStartDecision GetGameStartDecision(
			Format? format,
			GameMode mode,
			bool alreadyTracking)
		{
			var shouldTrack = ShouldStartTrackingGame(format, mode, alreadyTracking);
			return new GameStartDecision(
				shouldTrack,
				shouldTrack || ShouldHideDashboardOnGameStart(mode, alreadyTracking)
					? GameStartDashboardAction.Hide
					: GameStartDashboardAction.None);
		}

		internal static GameStartDecision GetGameStartDecision(
			Format? format,
			GameMode mode,
			bool alreadyTracking,
			MetaDeckLoadSnapshot metaDeckLoadSnapshot)
		{
			if (!ShouldStartTrackingGame(format, mode, alreadyTracking))
			{
				return new GameStartDecision(
					false,
					ShouldHideDashboardOnGameStart(mode, alreadyTracking)
						? GameStartDashboardAction.Hide
						: GameStartDashboardAction.None);
			}

			var snapshot = metaDeckLoadSnapshot ?? MetaDeckLoadSnapshot.Loading(DateTime.Now);
			if (!snapshot.IsReady)
			{
				return new GameStartDecision(
					false,
					GameStartDashboardAction.Hide,
					snapshot.UserMessage);
			}

			return new GameStartDecision(true, GameStartDashboardAction.Hide);
		}

		private static bool ShouldHideDashboardOnGameStart(GameMode mode, bool alreadyTracking)
		{
			return !alreadyTracking && mode != GameMode.None;
		}

		internal static bool ShouldShowStandardRecommendations(
			Format? format,
			GameMode gameMode,
			HsMode currentMode,
			bool trackingGame,
			bool enabled)
		{
			if (!enabled || trackingGame)
			{
				return false;
			}

			if (format.HasValue && format.Value != Format.Standard && format.Value != Format.All)
			{
				return false;
			}

			if (gameMode != GameMode.Ranked &&
				gameMode != GameMode.Casual &&
				gameMode != GameMode.Friendly &&
				gameMode != GameMode.None)
			{
				return false;
			}

			return currentMode == HsMode.TOURNAMENT;
		}

		private void UpdateStandardRecommendationDashboard(bool force)
		{
			if (!force && DateTime.Now < _nextDashboardPoll)
			{
				return;
			}
			_nextDashboardPoll = DateTime.Now.Add(DashboardPollInterval);

			var game = Hearthstone_Deck_Tracker.Core.Game;
			var shouldShow = game != null && ShouldShowStandardRecommendations(
				game.CurrentFormat,
				game.CurrentGameMode,
				game.CurrentMode,
				_controller != null,
				_config != null && _config.EnableMetaDashboard);
			LogDashboardStateIfChanged(game, shouldShow);

			if (shouldShow)
			{
				if (!_wasInRecommendationScene)
				{
					_wasInRecommendationScene = true;
					_metaDashboardView?.ResetUserDismissed();
				}
				if (!(_metaDashboardView?.UserDismissed ?? true))
				{
					_metaDashboardView?.ShowRecommendations();
				}
				return;
			}

			if (_wasInRecommendationScene)
			{
				_metaDashboardView?.ResetUserDismissed();
			}
			_wasInRecommendationScene = false;
			_metaDashboardView?.Hide();
		}

		private void LogDashboardStateIfChanged(
			Hearthstone_Deck_Tracker.Hearthstone.GameV2 game,
			bool shouldShow)
		{
			var signature = game == null
				? "no-game"
				: game.CurrentFormat + "|" + game.CurrentGameMode + "|" + game.CurrentMode +
					"|tracking=" + (_controller != null) +
					"|enabled=" + (_config != null && _config.EnableMetaDashboard) +
					"|show=" + shouldShow;
			if (signature == _lastDashboardStateSignature)
			{
				return;
			}

			_lastDashboardStateSignature = signature;
			Log.Debug("Recommendation dashboard state: " + signature);
		}

		private bool ShouldProcessHdtGameEvent(string kind)
		{
			long generation;
			return ShouldProcessHdtGameEvent(kind, "", 0, out generation);
		}

		private bool ShouldProcessHdtGameEvent(string kind, string detail)
		{
			long generation;
			return ShouldProcessHdtGameEvent(kind, detail, 0, out generation);
		}

		private bool ShouldProcessHdtGameEvent(
			string kind, string detail, int monotonicSequence)
		{
			long generation;
			return ShouldProcessHdtGameEvent(
				kind, detail, monotonicSequence, out generation);
		}

		private bool ShouldProcessHdtGameEvent(
			string kind,
			string detail,
			int monotonicSequence,
			out long generation)
		{
			generation = Interlocked.Read(ref _hdtGameEventGeneration);
			return _hdtGameEventReplayGuard.ShouldProcess(
				generation,
				new HdtGameEventStamp(
					kind,
					GetCurrentHdtTurnNumber(),
					monotonicSequence,
					detail));
		}

		private static int GetCurrentHdtTurnNumber()
		{
			try
			{
				var game = Hearthstone_Deck_Tracker.Core.Game;
				return game == null ? 0 : game.GetTurnNumber();
			}
			catch
			{
				return 0;
			}
		}

		private static int GetOpponentPlayedCardCount()
		{
			try
			{
				var cards = Hearthstone_Deck_Tracker.Core.Game?.Opponent?.CardsPlayedThisMatch;
				return cards == null ? 0 : cards.Count;
			}
			catch
			{
				return 0;
			}
		}

		private static string GetCardEventIdentity(Card card)
		{
			if (card == null)
			{
				return "";
			}
			return string.IsNullOrWhiteSpace(card.Id) ? card.Name ?? "" : card.Id;
		}

		private static string GetAttackEventIdentity(AttackInfo attack)
		{
			if (attack == null)
			{
				return "";
			}
			return GetCardEventIdentity(attack.Attacker) + ">" +
				GetCardEventIdentity(attack.Defender);
		}

		private bool StopTrackingGame(string reason)
		{
			var wasTrackingGame = _controller != null || _matchHistoryRecorder != null;
			if (!wasTrackingGame)
			{
				return false;
			}

			_matchHistoryRecorder?.Complete(reason);
			_matchHistoryRecorder = null;
			if (_controller != null)
			{
				_view.SetEnabled(false);
				Log.Debug("Disabling Meta Companion for end of game (" + reason + ")");
			}
			_controller = null;
			return true;
		}

		public Version Version
		{
			get { return new Version(0, 1, 0); }
		}
	}
}

