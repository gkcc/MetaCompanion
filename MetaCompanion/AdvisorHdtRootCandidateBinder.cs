using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaCompanion
{
	/// <summary>
	/// Converts one complete HDT DebugPrintOptions frame into public-state-bound root actions.
	/// This component proves only the caller-side legality portfolio; effect simulation and
	/// optimality remain worker responsibilities.
	/// </summary>
	internal static class AdvisorHdtRootCandidateBinder
	{
		private const int MaximumCandidates = 512;

		internal static bool TryBuild(
			HdtPowerOptionsFrameEvidence frame,
			AdvisorGameState state,
			out AdvisorHdtRootCandidateSet candidateSet,
			out string reason)
		{
			candidateSet = null;
			reason = "";
			if (frame == null || state == null || state.Player == null ||
				state.Opponent == null || frame.CollectorEpoch <= 0 || frame.FrameId <= 0 ||
				frame.HeaderWatermark <= 0 || string.IsNullOrWhiteSpace(state.StateId) ||
				state.IsLocalPlayerTurn != true || state.Player.PlayerId <= 0 ||
				state.Opponent.PlayerId <= 0 || state.Player.PlayerId == state.Opponent.PlayerId ||
				state.Phase != null && (state.Phase.HasPendingChoice ||
					state.Phase.CanLocalPlayerAct == false))
			{
				reason = "state_or_frame_unavailable";
				return false;
			}

			var options = frame.Options ?? new List<HdtPowerOptionEvidence>();
			if (!HasContiguousIds(options.Select(item => item?.OptionId ?? -1)) ||
				options.Count == 0)
			{
				reason = "option_ids_not_contiguous";
				return false;
			}
			// HDT can publish an Options frame after GameState has accepted a play but before
			// PowerTaskList has moved the card out of the hand.  During that short window the
			// just-played entity is still present in the public snapshot and its option is marked
			// REQ_NOT_MINION_JUST_PLAYED.  Binding that post-input frame to the pre-input snapshot
			// silently removes the selected card from an otherwise "complete" root portfolio.
			// Reject every such in-flight frame; the caller may use independently generated roots
			// for the current snapshot and a later stable frame can bind to the updated snapshot.
			if (options.Any(ContainsInFlightActionResolutionError))
			{
				reason = "action_resolution_in_flight";
				return false;
			}
			var endTurn = options[0];
			if (endTurn == null || endTurn.OptionId != 0 ||
				!string.Equals(endTurn.Type, "END_TURN", StringComparison.Ordinal) ||
				endTurn.Entity != null || (endTurn.Targets?.Count ?? 0) != 0 ||
				(endTurn.SubOptions?.Count ?? 0) != 0)
			{
				reason = "end_turn_option_invalid";
				return false;
			}

			Dictionary<int, EntityBinding> entities;
			if (!TryIndexPublicEntities(state, out entities))
			{
				reason = "public_entity_index_invalid";
				return false;
			}

			var candidates = new List<AdvisorHdtRootCandidate>
			{
				new AdvisorHdtRootCandidate
				{
					OptionId = 0,
					Action = new AdvisorHdtRootAction { Kind = "end_turn" },
					TargetEvidence = "not_applicable",
					PositionEvidence = "not_applicable"
				}
			};
			var actionKeys = new HashSet<string>(StringComparer.Ordinal)
			{
				ActionKey(candidates[0].Action)
			};

			foreach (var option in options.Skip(1))
			{
				if (option == null)
				{
					reason = "null_option";
					return false;
				}
				if (!string.Equals(option.Error, "NONE", StringComparison.Ordinal))
					continue;
				if (!string.Equals(option.Type, "POWER", StringComparison.Ordinal) ||
					option.Entity == null || (option.SubOptions?.Count ?? 0) != 0)
				{
					reason = "legal_option_shape_unsupported";
					return false;
				}
				var targets = option.Targets ?? new List<HdtPowerTargetEvidence>();
				if (!HasContiguousIds(targets.Select(item => item?.TargetId ?? -1)))
				{
					reason = "target_ids_not_contiguous";
					return false;
				}

				EntityBinding source;
				if (!entities.TryGetValue(option.Entity.EntityId, out source) ||
					!string.Equals(source.Role, "friendly", StringComparison.Ordinal) ||
					option.Entity.PlayerId != state.Player.PlayerId ||
					source.Entity.ControllerId != state.Player.PlayerId ||
					string.IsNullOrWhiteSpace(source.Entity.CardId) ||
					(!string.IsNullOrWhiteSpace(option.Entity.CardId) && !string.Equals(
						option.Entity.CardId, source.Entity.CardId, StringComparison.Ordinal)))
				{
					reason = "legal_source_not_bound";
					return false;
				}

				string kind;
				if (!TryResolveKind(source, out kind))
				{
					reason = "legal_source_action_kind_unresolved";
					return false;
				}
				if (string.Equals(kind, "attack", StringComparison.Ordinal) &&
					HasTitanAbilityMarker(source.Entity))
				{
					reason = "titan_action_kind_ambiguous";
					return false;
				}

				var legalTargets = targets.Where(item => item != null &&
					string.Equals(item.Error, "NONE", StringComparison.Ordinal)).ToList();
				if (string.Equals(kind, "attack", StringComparison.Ordinal) &&
					legalTargets.Count == 0)
				{
					reason = "attack_target_domain_empty";
					return false;
				}
				var targetIds = new List<int?>();
				var seenTargets = new HashSet<int>();
				foreach (var target in legalTargets)
				{
					EntityBinding binding;
					if (target.Entity == null || target.Entity.EntityId <= 0 ||
						!seenTargets.Add(target.Entity.EntityId) ||
						!entities.TryGetValue(target.Entity.EntityId, out binding) ||
						binding.Entity.ControllerId != target.Entity.PlayerId ||
						(string.Equals(binding.Role, "opponent", StringComparison.Ordinal) &&
						 string.Equals(binding.Zone, "hand", StringComparison.Ordinal)) ||
						(!string.IsNullOrWhiteSpace(target.Entity.CardId) && !string.Equals(
							target.Entity.CardId, binding.Entity.CardId, StringComparison.Ordinal)))
					{
						reason = "legal_target_not_bound";
						return false;
					}
					if (string.Equals(kind, "attack", StringComparison.Ordinal) &&
						(!string.Equals(binding.Role, "opponent", StringComparison.Ordinal) ||
						 !(string.Equals(binding.Zone, "hero", StringComparison.Ordinal) ||
						   string.Equals(binding.Zone, "board", StringComparison.Ordinal))))
					{
						reason = "attack_target_domain_invalid";
						return false;
					}
					targetIds.Add(target.Entity.EntityId);
				}
				if (targetIds.Count == 0)
					targetIds.Add(null);

				var positions = new List<int> { 0 };
				var positionEvidence = "not_applicable";
				if (string.Equals(kind, "play_card", StringComparison.Ordinal) &&
					(string.Equals(source.Entity.CardType, "MINION", StringComparison.Ordinal) ||
					 string.Equals(source.Entity.CardType, "LOCATION", StringComparison.Ordinal)))
				{
					var boardCount = (state.Player.Board ?? new List<AdvisorEntityState>())
						.Count(item => item != null);
					if (boardCount >= 7)
					{
						reason = "placement_card_on_full_board";
						return false;
					}
					positions = Enumerable.Range(1, Math.Min(7, boardCount + 1)).ToList();
					positionEvidence = "core_board_slots_v1";
				}

				foreach (var targetId in targetIds)
				{
					foreach (var position in positions)
					{
						var action = new AdvisorHdtRootAction
						{
							Kind = kind,
							SourceEntityId = source.Entity.EntityId,
							TargetEntityId = targetId,
							CardId = source.Entity.CardId,
							BoardPosition = position
						};
						if (!actionKeys.Add(ActionKey(action)))
						{
							reason = "duplicate_candidate_action";
							return false;
						}
						candidates.Add(new AdvisorHdtRootCandidate
						{
							OptionId = option.OptionId,
							Action = action,
							TargetEvidence = legalTargets.Count > 0
								? "hdt_error_none" : "hdt_no_legal_target",
							PositionEvidence = positionEvidence
						});
						if (candidates.Count > MaximumCandidates)
						{
							reason = "candidate_count_exceeds_limit";
							return false;
						}
					}
				}
			}

			candidateSet = new AdvisorHdtRootCandidateSet
			{
				Contract = AdvisorHdtRootCandidateSet.ContractId,
				StateId = state.StateId,
				FrameId = frame.FrameId,
				CollectorEpoch = frame.CollectorEpoch,
				FrameWatermark = frame.HeaderWatermark,
				CandidateSetComplete = true,
				Candidates = candidates
			};
			return true;
		}

		private static bool HasContiguousIds(IEnumerable<int> ids)
		{
			var values = (ids ?? Enumerable.Empty<int>()).ToList();
			return values.Count == 0 || values.OrderBy(item => item)
				.SequenceEqual(Enumerable.Range(0, values.Count));
		}

		private static bool ContainsInFlightActionResolutionError(
			HdtPowerOptionEvidence option)
		{
			if (option == null)
				return false;
			if (IsInFlightActionResolutionError(option.Error))
				return true;
			return (option.Targets ?? new List<HdtPowerTargetEvidence>())
				.Any(item => item != null && IsInFlightActionResolutionError(item.Error)) ||
				(option.SubOptions ?? new List<HdtPowerSubOptionEvidence>())
				.Any(item => item != null && IsInFlightActionResolutionError(item.Error));
		}

		private static bool IsInFlightActionResolutionError(string error)
		{
			return !string.IsNullOrWhiteSpace(error) &&
				error.IndexOf("JUST_PLAYED", StringComparison.Ordinal) >= 0;
		}

		private static string ActionKey(AdvisorHdtRootAction action)
		{
			return string.Join("|", new[]
			{
				action?.Kind ?? "",
				action?.SourceEntityId?.ToString() ?? "",
				action?.TargetEntityId?.ToString() ?? "",
				action?.CardId ?? "",
				(action?.BoardPosition ?? 0).ToString()
			});
		}

		private static bool TryResolveKind(EntityBinding source, out string kind)
		{
			kind = "";
			var type = (source?.Entity?.CardType ?? "").ToUpperInvariant();
			if (string.Equals(source?.Zone, "hand", StringComparison.Ordinal) &&
				new[] { "HERO", "MINION", "SPELL", "WEAPON", "LOCATION" }.Contains(type))
				kind = "play_card";
			else if (string.Equals(source?.Zone, "hero_power", StringComparison.Ordinal) &&
				string.Equals(type, "HERO_POWER", StringComparison.Ordinal))
				kind = "hero_power";
			else if (string.Equals(source?.Zone, "board", StringComparison.Ordinal) &&
				string.Equals(type, "LOCATION", StringComparison.Ordinal))
				kind = "location_activate";
			else if ((string.Equals(source?.Zone, "hero", StringComparison.Ordinal) ||
				string.Equals(source?.Zone, "board", StringComparison.Ordinal)) &&
				(type == "HERO" || type == "MINION"))
				kind = "attack";
			return !string.IsNullOrWhiteSpace(kind);
		}

		private static bool HasTitanAbilityMarker(AdvisorEntityState entity)
		{
			return (entity?.Tags ?? new Dictionary<string, int>()).Keys.Any(key =>
				(key ?? "").StartsWith("TITAN_ABILITY_USED_", StringComparison.OrdinalIgnoreCase));
		}

		private static bool TryIndexPublicEntities(
			AdvisorGameState state,
			out Dictionary<int, EntityBinding> result)
		{
			result = new Dictionary<int, EntityBinding>();
			return AddPlayer(result, state.Player, "friendly") &&
				AddPlayer(result, state.Opponent, "opponent");
		}

		private static bool AddPlayer(
			IDictionary<int, EntityBinding> result,
			AdvisorPlayerState player,
			string role)
		{
			if (player == null)
				return false;
			if (!Add(result, player.Hero, role, "hero") ||
				!Add(result, player.HeroPower, role, "hero_power") ||
				!Add(result, player.Weapon, role, "weapon"))
				return false;
			foreach (var entity in player.Hand ?? new List<AdvisorEntityState>())
				if (!Add(result, entity, role, "hand"))
					return false;
			foreach (var entity in player.Board ?? new List<AdvisorEntityState>())
				if (!Add(result, entity, role, "board"))
					return false;
			return true;
		}

		private static bool Add(
			IDictionary<int, EntityBinding> result,
			AdvisorEntityState entity,
			string role,
			string zone)
		{
			if (entity == null)
				return true;
			if (entity.EntityId <= 0 || result.ContainsKey(entity.EntityId))
				return false;
			result[entity.EntityId] = new EntityBinding
			{
				Entity = entity,
				Role = role,
				Zone = zone
			};
			return true;
		}

		private sealed class EntityBinding
		{
			internal AdvisorEntityState Entity { get; set; }
			internal string Role { get; set; } = "";
			internal string Zone { get; set; } = "";
		}
	}
}
