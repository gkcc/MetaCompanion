using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaCompanion
{
	/// <summary>
	/// One public HDT action callback waiting for a detached post-action snapshot. This queue is
	/// intentionally independent from AdvisorTransitionCandidateTracker: behavior evidence can be
	/// useful even when it is not complete enough for a replayable RL transition.
	/// </summary>
	internal sealed class AdvisorBehaviorPendingEvidence
	{
		internal long GameGeneration { get; set; }
		internal long EventOrdinal { get; set; }
		internal int InterveningActionCount { get; set; }
		internal DateTime ObservedAtUtc { get; set; }
		internal AdvisorGameState PreState { get; set; }
		internal string ActorSide { get; set; } = "unknown";
		internal string ActorPlayerId { get; set; } = "";
		internal string ActorEvidence { get; set; } = "unknown";
		internal string IdentityStatus { get; set; } = "unknown";
		internal string VisibilityStatus { get; set; } = "hidden_source";
		internal string SourceEvent { get; set; } = "unknown";
		internal AdvisorBehaviorAction Action { get; set; } = new AdvisorBehaviorAction();
		internal string TargetBindingStatus { get; set; } =
			AdvisorBehaviorTargetBindingStatus.Unknown;
		internal bool HasPowerIdentity { get; set; }
		internal string PowerIdentityKey { get; set; } = "";
	}

	/// <summary>
	/// Preserves both players' public action evidence in callback order, waits for two equal detached
	/// post-state captures, and conservatively labels ambiguous/overlapped evidence as ineligible.
	/// </summary>
	internal sealed class AdvisorBehaviorPendingTracker
	{
		private readonly List<AdvisorBehaviorPendingEvidence> _pending =
			new List<AdvisorBehaviorPendingEvidence>();
		private long _nextEventOrdinal;
		private string _candidateStateId = "";
		private AdvisorGameState _candidateSnapshot;
		private long _candidateGeneration;
		private long _candidateRefreshRevision;
		private int _candidateSightings;

		internal int PendingCount
		{
			get { return _pending.Count; }
		}

		internal void Reset()
		{
			_pending.Clear();
			_nextEventOrdinal = 0;
			InvalidateBoundary();
		}

		internal void InvalidateBoundary()
		{
			_candidateStateId = "";
			_candidateSnapshot = null;
			_candidateGeneration = 0;
			_candidateRefreshRevision = 0;
			_candidateSightings = 0;
		}

		/// <summary>
		/// Registers an action or merges exact local Power identity into the matching GameEvents
		/// callback. Returns true only when a new chronological item was appended.
		/// </summary>
		internal bool Register(AdvisorBehaviorPendingEvidence evidence)
		{
			if (evidence == null || evidence.PreState == null || evidence.Action == null ||
				evidence.GameGeneration <= 0 || evidence.ObservedAtUtc == DateTime.MinValue ||
				string.IsNullOrWhiteSpace(evidence.PreState.StateId) ||
				string.IsNullOrWhiteSpace(evidence.PreState.GameId) ||
				(string.IsNullOrWhiteSpace(evidence.Action.Kind)))
			{
				return false;
			}

			var mergeTarget = FindPowerMergeTarget(evidence);
			if (mergeTarget != null)
			{
				MergePowerEvidence(mergeTarget, evidence);
				InvalidateBoundary();
				return false;
			}

			foreach (var pending in _pending)
				pending.InterveningActionCount++;
			evidence.EventOrdinal = ++_nextEventOrdinal;
			_pending.Add(evidence);
			InvalidateBoundary();
			return true;
		}

		internal List<AdvisorBehaviorCapture> ObserveSnapshot(
			AdvisorGameState snapshot,
			long gameGeneration,
			long refreshRevision)
		{
			if (_pending.Count == 0 || snapshot == null ||
				string.IsNullOrWhiteSpace(snapshot.StateId) ||
				string.IsNullOrWhiteSpace(snapshot.GameId))
			{
				InvalidateBoundary();
				return new List<AdvisorBehaviorCapture>();
			}
			if (_pending.Any(item => item.GameGeneration != gameGeneration) ||
				_pending.Any(item => !string.Equals(
					item.PreState.GameId, snapshot.GameId, StringComparison.Ordinal)) ||
				_pending.Any(item => item.PreState.SnapshotSequence >= snapshot.SnapshotSequence))
			{
				InvalidateBoundary();
				return new List<AdvisorBehaviorCapture>();
			}

			var sameCandidate = string.Equals(
				_candidateStateId, snapshot.StateId, StringComparison.Ordinal) &&
				_candidateGeneration == gameGeneration &&
				_candidateRefreshRevision == refreshRevision;
			if (!sameCandidate)
			{
				_candidateStateId = snapshot.StateId;
				_candidateSnapshot = snapshot;
				_candidateGeneration = gameGeneration;
				_candidateRefreshRevision = refreshRevision;
				_candidateSightings = 1;
				return new List<AdvisorBehaviorCapture>();
			}

			_candidateSightings++;
			_candidateSnapshot = snapshot;
			if (_candidateSightings < 2)
				return new List<AdvisorBehaviorCapture>();
			// A callback followed by two copies of the original fingerprint is still not a
			// post-action boundary. Keep waiting; terminal drain will retain it as unverified.
			if (_pending.Any(item => string.Equals(
				item.PreState.StateId, snapshot.StateId, StringComparison.Ordinal)))
			{
				return new List<AdvisorBehaviorCapture>();
			}

			var completed = _pending.OrderBy(item => item.EventOrdinal).ToList();
			ResolveOpponentHiddenPlays(completed, snapshot);
			var captures = BuildCaptures(completed, snapshot, false);
			_pending.Clear();
			InvalidateBoundary();
			return captures;
		}

		internal List<AdvisorBehaviorCapture> DrainUnresolved(long gameGeneration)
		{
			var drained = _pending
				.Where(item => item.GameGeneration == gameGeneration)
				.OrderBy(item => item.EventOrdinal)
				.ToList();
			var candidate = _candidateGeneration == gameGeneration &&
				_candidateSnapshot != null && drained.All(item => string.Equals(
					item.PreState?.GameId,
					_candidateSnapshot.GameId,
					StringComparison.Ordinal))
				? _candidateSnapshot : null;
			if (candidate != null)
				ResolveOpponentHiddenPlays(drained, candidate);
			_pending.RemoveAll(item => item.GameGeneration == gameGeneration);
			InvalidateBoundary();
			return BuildCaptures(drained, candidate, true);
		}

		private AdvisorBehaviorPendingEvidence FindPowerMergeTarget(
			AdvisorBehaviorPendingEvidence incoming)
		{
			if (!incoming.HasPowerIdentity && !_pending.Any(item => item.HasPowerIdentity))
				return null;
			var compatible = _pending.Where(existing =>
				existing != null && existing.PreState != null &&
				existing.GameGeneration == incoming.GameGeneration &&
				string.Equals(existing.ActorSide, incoming.ActorSide, StringComparison.Ordinal) &&
				string.Equals(existing.PreState.StateId, incoming.PreState.StateId, StringComparison.Ordinal) &&
				string.Equals(existing.Action?.Kind, incoming.Action?.Kind, StringComparison.Ordinal) &&
				CompatibleIdentity(existing.Action?.SourceEntityId, incoming.Action?.SourceEntityId) &&
				CompatibleIdentity(existing.Action?.TargetEntityId, incoming.Action?.TargetEntityId) &&
				CompatibleTargetBinding(
					existing.TargetBindingStatus,
					incoming.TargetBindingStatus) &&
				CompatibleText(existing.Action?.CardId, incoming.Action?.CardId) &&
				IsNear(existing.ObservedAtUtc, incoming.ObservedAtUtc)).ToList();
			if (compatible.Count == 0)
				return null;

			if (incoming.HasPowerIdentity)
			{
				var duplicate = compatible.FirstOrDefault(existing =>
					existing.HasPowerIdentity && !string.IsNullOrWhiteSpace(incoming.PowerIdentityKey) &&
					string.Equals(
						existing.PowerIdentityKey,
						incoming.PowerIdentityKey,
						StringComparison.Ordinal));
				return duplicate ?? compatible.FirstOrDefault(existing => !existing.HasPowerIdentity);
			}

			return compatible.LastOrDefault(existing => existing.HasPowerIdentity);
		}

		private static void MergePowerEvidence(
			AdvisorBehaviorPendingEvidence target,
			AdvisorBehaviorPendingEvidence incoming)
		{
			var exact = incoming.HasPowerIdentity ? incoming : target;
			var callback = incoming.HasPowerIdentity ? target : incoming;
			target.Action = new AdvisorBehaviorAction
			{
				Kind = exact.Action?.Kind ?? callback.Action?.Kind ?? "",
				SourceEntityId = exact.Action?.SourceEntityId ?? callback.Action?.SourceEntityId,
				TargetEntityId = exact.Action?.TargetEntityId ?? callback.Action?.TargetEntityId,
				CardId = !string.IsNullOrWhiteSpace(exact.Action?.CardId)
					? exact.Action.CardId : callback.Action?.CardId ?? "",
				SubOption = exact.Action?.SubOption,
				BoardPosition = exact.Action?.BoardPosition,
				ChoiceStatus = exact.Action?.ChoiceStatus ?? "unresolved",
				Choices = CloneChoices(exact.Action?.Choices)
			};
			target.ActorEvidence = exact.ActorEvidence ?? "hdt_power_log";
			target.IdentityStatus = exact.IdentityStatus ?? "unknown";
			target.VisibilityStatus = exact.VisibilityStatus ?? "public_pre_state";
			target.SourceEvent = exact.SourceEvent ?? "hdt_power_log";
			target.TargetBindingStatus = exact.TargetBindingStatus ??
				AdvisorBehaviorTargetBindingStatus.Unknown;
			target.HasPowerIdentity = true;
			target.PowerIdentityKey = exact.PowerIdentityKey ?? "";
			if (callback.ObservedAtUtc != DateTime.MinValue &&
				callback.ObservedAtUtc < target.ObservedAtUtc)
			{
				target.ObservedAtUtc = callback.ObservedAtUtc;
			}
		}

		private static List<AdvisorBehaviorCapture> BuildCaptures(
			IList<AdvisorBehaviorPendingEvidence> items,
			AdvisorGameState postState,
			bool unresolved)
		{
			var result = new List<AdvisorBehaviorCapture>();
			var groups = (items ?? new List<AdvisorBehaviorPendingEvidence>())
				.Where(item => item != null && item.PreState != null)
				.GroupBy(item => item.PreState.StateId ?? "")
				.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
			foreach (var item in items ?? new List<AdvisorBehaviorPendingEvidence>())
			{
				if (item == null || item.PreState == null || item.Action == null)
					continue;
				var identityStatus = item.IdentityStatus ?? "unknown";
				if (!string.Equals(item.Action.Kind, "end_turn", StringComparison.Ordinal) &&
					!HasCompleteTargetBinding(item))
				{
					identityStatus = "unknown";
				}
				var boundary = "isolated";
				int groupCount;
				if (unresolved || postState == null || string.Equals(
					item.PreState.StateId, postState.StateId, StringComparison.Ordinal))
				{
					boundary = "unverified";
				}
				else if (HasUnstableCapture(item.PreState) || HasUnstableCapture(postState))
				{
					boundary = "unstable";
				}
				else if (item.InterveningActionCount > 0 ||
					(groups.TryGetValue(item.PreState.StateId ?? "", out groupCount) && groupCount > 1))
				{
					boundary = "overlapped";
				}

				result.Add(new AdvisorBehaviorCapture
				{
					ObservedAtUtc = EnsureUtc(item.ObservedAtUtc),
					ActorSide = item.ActorSide ?? "unknown",
					ActorPlayerId = item.ActorPlayerId ?? "",
					ActorEvidence = item.ActorEvidence ?? "unknown",
					IdentityStatus = identityStatus,
					VisibilityStatus = item.VisibilityStatus ?? "hidden_source",
					BoundaryStatus = boundary,
					SourceEvent = item.SourceEvent ?? "unknown",
					Action = new AdvisorBehaviorAction
					{
						Kind = item.Action.Kind ?? "",
						SourceEntityId = item.Action.SourceEntityId,
						TargetEntityId = item.Action.TargetEntityId,
						CardId = item.Action.CardId ?? "",
						SubOption = item.Action.SubOption,
						BoardPosition = item.Action.BoardPosition,
						ChoiceStatus = item.Action.ChoiceStatus ?? "not_observed",
						Choices = CloneChoices(item.Action.Choices)
					},
					PreState = item.PreState,
					PostState = postState
				});
			}
			return result;
		}

		private static List<AdvisorObservedChoice> CloneChoices(
			IEnumerable<AdvisorObservedChoice> choices)
		{
			return (choices ?? new List<AdvisorObservedChoice>())
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
				}).ToList();
		}

		private static void ResolveOpponentHiddenPlays(
			IEnumerable<AdvisorBehaviorPendingEvidence> items,
			AdvisorGameState postState)
		{
			if (postState == null || postState.Opponent == null)
				return;
			var postHandIds = new HashSet<int>((postState.Opponent.Hand ??
				new List<AdvisorEntityState>()).Where(item => item != null).Select(item => item.EntityId));
			var postEntities = EnumeratePlayerEntities(postState.Opponent)
				.Where(item => item != null && item.EntityId > 0)
				.GroupBy(item => item.EntityId)
				.ToDictionary(group => group.Key, group => group.First());
			var used = new HashSet<int>();

			foreach (var item in (items ?? Enumerable.Empty<AdvisorBehaviorPendingEvidence>())
				.OrderBy(value => value.EventOrdinal))
			{
				if (!string.Equals(item.ActorSide, "opponent", StringComparison.Ordinal) ||
					!string.Equals(item.Action?.Kind, "play_card", StringComparison.Ordinal) ||
					item.PreState?.Opponent == null || item.Action.SourceEntityId.HasValue)
				{
					continue;
				}
				var eventCardId = (item.Action.CardId ?? "").Trim();
				var candidates = new List<AdvisorEntityState>();
				foreach (var hidden in item.PreState.Opponent.Hand ?? new List<AdvisorEntityState>())
				{
					AdvisorEntityState revealed;
					if (hidden == null || hidden.EntityId <= 0 || used.Contains(hidden.EntityId) ||
						postHandIds.Contains(hidden.EntityId) ||
						!postEntities.TryGetValue(hidden.EntityId, out revealed) ||
						string.IsNullOrWhiteSpace(revealed.CardId) ||
						(!string.IsNullOrWhiteSpace(eventCardId) && !string.Equals(
							eventCardId, revealed.CardId, StringComparison.Ordinal)))
					{
						continue;
					}
					candidates.Add(revealed);
				}

				if (candidates.Select(candidate => candidate.EntityId).Distinct().Count() == 1)
				{
					var revealed = candidates[0];
					used.Add(revealed.EntityId);
					item.Action.SourceEntityId = revealed.EntityId;
					item.Action.CardId = revealed.CardId ?? eventCardId;
					item.IdentityStatus = HasCompleteTargetBinding(item)
						? "revealed_after_action" : "unknown";
					item.VisibilityStatus = "revealed_post_action";
				}
				else
				{
					item.IdentityStatus = "unknown";
					item.VisibilityStatus = string.IsNullOrWhiteSpace(eventCardId)
						? "hidden_source" : "revealed_post_action";
				}
			}
		}

		private static IEnumerable<AdvisorEntityState> EnumeratePlayerEntities(
			AdvisorPlayerState player)
		{
			if (player == null)
				yield break;
			foreach (var entity in new[]
			{
				player.PlayerEntity, player.Hero, player.HeroPower, player.Weapon
			}.Where(item => item != null))
				yield return entity;
			foreach (var entity in new[]
			{
				player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
				player.SetAside, player.RemovedFromGame, player.OtherEntities
			}.Where(list => list != null).SelectMany(list => list))
				if (entity != null)
					yield return entity;
		}

		private static bool HasUnstableCapture(AdvisorGameState state)
		{
			return state == null || (state.CaptureWarnings?.Count ?? 0) > 0 ||
				(state.UnknownData ?? new List<AdvisorDataGap>()).Any(item => item != null &&
					(string.Equals(item.Code, "entity_collection_unstable", StringComparison.Ordinal) ||
					 string.Equals(item.Code, "entity_capture_failed", StringComparison.Ordinal)));
		}

		private static bool IsNear(DateTime left, DateTime right)
		{
			if (left == DateTime.MinValue || right == DateTime.MinValue)
				return false;
			return Math.Abs((EnsureUtc(left) - EnsureUtc(right)).TotalSeconds) <= 5.0;
		}

		private static DateTime EnsureUtc(DateTime value)
		{
			if (value.Kind == DateTimeKind.Utc)
				return value;
			if (value.Kind == DateTimeKind.Local)
				return value.ToUniversalTime();
			return DateTime.SpecifyKind(value, DateTimeKind.Utc);
		}

		private static bool CompatibleIdentity(int? left, int? right)
		{
			return !left.HasValue || !right.HasValue || left.Value == right.Value;
		}

		private static bool CompatibleTargetBinding(string left, string right)
		{
			var leftKnown = !string.IsNullOrWhiteSpace(left) &&
				!string.Equals(
					left,
					AdvisorBehaviorTargetBindingStatus.Unknown,
					StringComparison.Ordinal);
			var rightKnown = !string.IsNullOrWhiteSpace(right) &&
				!string.Equals(
					right,
					AdvisorBehaviorTargetBindingStatus.Unknown,
					StringComparison.Ordinal);
			return !leftKnown || !rightKnown || string.Equals(left, right, StringComparison.Ordinal);
		}

		private static bool HasCompleteTargetBinding(AdvisorBehaviorPendingEvidence item)
		{
			if (item == null || item.Action == null ||
				!AdvisorBehaviorTargetBindingStatus.IsComplete(
					item.TargetBindingStatus,
					item.Action.TargetEntityId))
			{
				return false;
			}
			if (!item.Action.TargetEntityId.HasValue)
				return true;
			return new[] { item.PreState?.Player, item.PreState?.Opponent }
				.Where(player => player != null)
				.SelectMany(EnumeratePlayerEntities)
				.Where(entity => entity != null &&
					entity.EntityId == item.Action.TargetEntityId.Value)
				.GroupBy(entity => new
				{
					entity.EntityId,
					CardId = entity.CardId ?? "",
					entity.ControllerId,
					Zone = entity.Zone ?? ""
				})
				.Count() == 1;
		}

		private static bool CompatibleText(string left, string right)
		{
			return string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
				string.Equals(left, right, StringComparison.Ordinal);
		}
	}
}
