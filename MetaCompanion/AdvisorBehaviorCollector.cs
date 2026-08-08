using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MetaCompanion
{
	internal static class AdvisorBehaviorContract
	{
		internal const string Schema = "advisor-behavior-v1";
		internal const string CorpusFileName = "behavior-v1.jsonl";
		internal const string Local = "local";
		internal const string Opponent = "opponent";
		internal const string Unknown = "unknown";
		internal const string FriendlyPlayer = "friendly";
		internal const string OpponentPlayer = "opponent";

		private static readonly Regex SafeToken = new Regex(
			@"^[A-Za-z0-9_.:-]+$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		internal static bool IsSafeToken(string value, bool allowEmpty = false)
		{
			var text = (value ?? "").Trim();
			return (allowEmpty && text.Length == 0) ||
				(text.Length > 0 && text.Length <= 256 && SafeToken.IsMatch(text));
		}

		internal static string RequireGameId(string value)
		{
			var text = (value ?? "").Trim();
			if (!IsSafeToken(text))
				throw new ArgumentException(
					"A privacy-safe behavior game ID is required.", nameof(value));
			return text;
		}
	}

	internal sealed class AdvisorBehaviorAction
	{
		internal string Kind { get; set; } = "";
		internal int? SourceEntityId { get; set; }
		internal int? TargetEntityId { get; set; }
		internal string CardId { get; set; } = "";
		internal int? SubOption { get; set; }
		internal int? BoardPosition { get; set; }
		internal string ChoiceStatus { get; set; } = "not_observed";
		internal List<AdvisorObservedChoice> Choices { get; set; } =
			new List<AdvisorObservedChoice>();

		internal IDictionary<string, object> ToWireValue()
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "kind", Kind ?? "" },
				{ "source_entity_id", SourceEntityId.HasValue
					? SourceEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
				{ "target_entity_id", TargetEntityId.HasValue
					? TargetEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
				{ "card_id", CardId ?? "" },
				{ "sub_option", SubOption.HasValue ? (object)SubOption.Value : null },
				{ "board_position", BoardPosition.HasValue ? (object)BoardPosition.Value : null },
				{ "choice_status", ChoiceStatus ?? "not_observed" },
				{ "choices", (Choices ?? new List<AdvisorObservedChoice>())
					.Where(item => item != null)
					.Select(item => (object)new Dictionary<string, object>(StringComparer.Ordinal)
					{
						{ "choice_id", item.ChoiceId.HasValue ? (object)item.ChoiceId.Value : null },
						{ "choice_type", item.ChoiceType ?? "" },
						{ "source_entity_id", item.SourceEntityId.HasValue
							? (object)item.SourceEntityId.Value : null },
						{ "option_entity_ids", (item.OptionEntityIds ?? new List<int>())
							.Select(id => (object)id).ToList() },
						{ "selected_entity_ids", (item.SelectedEntityIds ?? new List<int>())
							.Select(id => (object)id).ToList() },
						{ "status", item.Status ?? "unresolved" }
					}).ToList() }
			};
		}
	}

	internal sealed class AdvisorBehaviorCapture
	{
		internal DateTime ObservedAtUtc { get; set; }
		internal string ActorSide { get; set; } = "unknown";
		internal string ActorPlayerId { get; set; } = "";
		internal string ActorEvidence { get; set; } = "unknown";
		internal string IdentityStatus { get; set; } = "unknown";
		internal string VisibilityStatus { get; set; } = "hidden_source";
		internal string BoundaryStatus { get; set; } = "unverified";
		internal string SourceEvent { get; set; } = "unknown";
		internal AdvisorBehaviorAction Action { get; set; } = new AdvisorBehaviorAction();
		internal AdvisorGameState PreState { get; set; }
		internal AdvisorGameState PostState { get; set; }
	}

	internal sealed class AdvisorBehaviorPublicEntity
	{
		internal string EntityId { get; set; } = "";
		internal string CardId { get; set; } = "";
		internal string CardType { get; set; } = "UNKNOWN";
		internal int Cost { get; set; }
		internal int Attack { get; set; }
		internal int Health { get; set; }
		internal int CurrentHealth { get; set; }
		internal bool Playable { get; set; }
		internal bool CanAttack { get; set; }
		internal int Durability { get; set; }
		internal int CurrentDurability { get; set; }
		internal bool Hidden { get; set; }

		internal IDictionary<string, object> ToWireValue()
		{
			if (Hidden)
			{
				return new Dictionary<string, object>(StringComparer.Ordinal)
				{
					{ "entity_id", EntityId ?? "" },
					{ "visibility", "hidden" }
				};
			}
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "entity_id", EntityId ?? "" },
				{ "card_id", CardId ?? "" },
				{ "card_type", CardType ?? "UNKNOWN" },
				{ "cost", Cost },
				{ "attack", Attack },
				{ "health", Health },
				{ "current_health", CurrentHealth },
				{ "playable", Playable },
				{ "can_attack", CanAttack },
				{ "durability", Durability },
				{ "current_durability", CurrentDurability }
			};
		}
	}

	internal sealed class AdvisorBehaviorPublicPlayer
	{
		internal string PlayerId { get; set; } = "";
		internal AdvisorBehaviorPublicEntity Hero { get; set; }
		internal AdvisorBehaviorPublicEntity HeroPower { get; set; }
		internal AdvisorBehaviorPublicEntity Weapon { get; set; }
		internal List<AdvisorBehaviorPublicEntity> Hand { get; set; } =
			new List<AdvisorBehaviorPublicEntity>();
		internal List<AdvisorBehaviorPublicEntity> Board { get; set; } =
			new List<AdvisorBehaviorPublicEntity>();
		internal int Mana { get; set; }
		internal int MaxMana { get; set; }
		internal int Armor { get; set; }
		internal int DeckSize { get; set; }
		internal int Fatigue { get; set; }
		internal bool HeroPowerAvailable { get; set; }
		internal int SpellPower { get; set; }

		internal IDictionary<string, object> ToWireValue()
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "player_id", PlayerId ?? "" },
				{ "hero", Hero?.ToWireValue() },
				{ "hero_power", HeroPower?.ToWireValue() },
				{ "weapon", Weapon?.ToWireValue() },
				{ "hand", Hand.Select(item => (object)item.ToWireValue()).ToList() },
				{ "board", Board.Select(item => (object)item.ToWireValue()).ToList() },
				{ "mana", Mana },
				{ "max_mana", MaxMana },
				{ "armor", Armor },
				{ "deck_size", DeckSize },
				{ "fatigue", Fatigue },
				{ "hero_power_available", HeroPowerAvailable },
				{ "spell_power", SpellPower }
			};
		}
	}

	internal sealed class AdvisorBehaviorPublicState
	{
		internal string StateId { get; set; } = "";
		internal int Turn { get; set; }
		internal string ActivePlayerId { get; set; } = "";
		internal string PerspectivePlayerId { get; set; } = "friendly";
		internal AdvisorBehaviorPublicPlayer Friendly { get; set; }
		internal AdvisorBehaviorPublicPlayer Opponent { get; set; }
		internal string Patch { get; set; } = "";
		internal string Mode { get; set; } = "";

		internal IDictionary<string, object> ToWireValue()
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "state_id", StateId ?? "" },
				{ "turn", Turn },
				{ "active_player_id", ActivePlayerId ?? "" },
				{ "perspective_player_id", PerspectivePlayerId ?? "friendly" },
				{ "friendly", Friendly?.ToWireValue() },
				{ "opponent", Opponent?.ToWireValue() },
				{ "patch", Patch ?? "" },
				{ "mode", Mode ?? "" }
			};
		}
	}

	/// <summary>
	/// Unhashed request content for Rust /v1/behavior. Rust is the sole authority for
	/// game-ID anonymization, canonical JSON, content_sha256 and behavior_id.
	/// </summary>
	internal sealed class AdvisorBehaviorRecord
	{
		internal string Schema { get; set; } = AdvisorBehaviorContract.Schema;
		internal string GameId { get; set; } = "";
		internal long BehaviorSequence { get; set; }
		internal string ObservedAtUtc { get; set; } = "";
		internal string ActorSide { get; set; } = "unknown";
		internal string ActorPlayerId { get; set; } = "";
		internal string ActorEvidence { get; set; } = "unknown";
		internal string IdentityStatus { get; set; } = "unknown";
		internal string VisibilityStatus { get; set; } = "hidden_source";
		internal string BoundaryStatus { get; set; } = "unverified";
		internal string SourceEvent { get; set; } = "unknown";
		internal AdvisorBehaviorAction Action { get; set; }
		internal AdvisorBehaviorPublicState PreState { get; set; }
		internal AdvisorBehaviorPublicState PostState { get; set; }
		internal bool BehaviorEligible { get; set; }
		internal bool RlTrainingEligible { get { return false; } }

		internal IDictionary<string, object> ToWireValue()
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "schema", Schema ?? AdvisorBehaviorContract.Schema },
				{ "game_id", GameId ?? "" },
				{ "behavior_sequence", BehaviorSequence },
				{ "observed_at_utc", ObservedAtUtc ?? "" },
				{ "actor_side", ActorSide ?? "unknown" },
				{ "actor_player_id", ActorPlayerId ?? "" },
				{ "actor_evidence", ActorEvidence ?? "unknown" },
				{ "identity_status", IdentityStatus ?? "unknown" },
				{ "visibility_status", VisibilityStatus ?? "hidden_source" },
				{ "boundary_status", BoundaryStatus ?? "unverified" },
				{ "source_event", SourceEvent ?? "unknown" },
				{ "action", Action?.ToWireValue() },
				{ "pre_state", PreState?.ToWireValue() },
				{ "post_state", PostState?.ToWireValue() },
				{ "behavior_eligible", BehaviorEligible },
				{ "rl_training_eligible", false }
			};
		}
	}

	internal sealed class AdvisorBehaviorCollector
	{
		private static readonly HashSet<string> ActorSides = Set(
			"local", "opponent", "unknown");
		private static readonly HashSet<string> ActorPlayerIds = Set(
			"friendly", "opponent");
		private static readonly HashSet<string> ActorEvidence = Set(
			"active_player", "source_owner", "hdt_player_event",
			"hdt_opponent_event", "hdt_power_log", "hdt_replay_power", "unknown");
		private static readonly HashSet<string> IdentityStatuses = Set(
			"exact_public_entity", "revealed_after_action", "event_only", "unknown");
		private static readonly HashSet<string> VisibilityStatuses = Set(
			"public_pre_state", "revealed_post_action", "hidden_source");
		private static readonly HashSet<string> BoundaryStatuses = Set(
			"isolated", "overlapped", "unstable", "unverified");
		private static readonly HashSet<string> ActionKinds = Set(
			"play_card", "attack", "hero_power", "location_activate", "end_turn");
		private static readonly HashSet<string> ChoiceStatuses = Set(
			"none", "selected", "unresolved", "not_observed");
		private static readonly HashSet<string> CardTypes = Set(
			"HERO", "MINION", "SPELL", "WEAPON", "HERO_POWER", "LOCATION", "UNKNOWN");

		private readonly object _sync = new object();
		private string _gameId = "";
		private long _sequence;
		private bool _active;

		internal string GameId
		{
			get { lock (_sync) return _gameId; }
		}

		internal long Sequence
		{
			get { lock (_sync) return _sequence; }
		}

		internal bool BeginGame(string gameId)
		{
			var sessionGameId = AdvisorBehaviorContract.RequireGameId(gameId);
			lock (_sync)
			{
				if (string.Equals(_gameId, sessionGameId, StringComparison.Ordinal))
				{
					var resumed = !_active;
					_active = true;
					return resumed;
				}
				_gameId = sessionGameId;
				_sequence = 0;
				_active = true;
				return true;
			}
		}

		/// <summary>
		/// Pauses capture without relinquishing the current game's sequence. This is used only for a
		/// mid-game training toggle; EndGame remains the hard boundary that resets identity.
		/// </summary>
		internal void SuspendGame()
		{
			lock (_sync)
				_active = false;
		}

		internal void EndGame()
		{
			lock (_sync)
			{
				_gameId = "";
				_sequence = 0;
				_active = false;
			}
		}

		internal bool TryCollect(
			AdvisorBehaviorCapture capture,
			out AdvisorBehaviorRecord record)
		{
			string ignored;
			return TryCollect(capture, out record, out ignored);
		}

		internal bool TryCollect(
			AdvisorBehaviorCapture capture,
			out AdvisorBehaviorRecord record,
			out string rejectionReason)
		{
			return TryCollectCore(capture, null, out record, out rejectionReason);
		}

		/// <summary>
		/// Builds the next record and commits its sequence only after the durable commit callback
		/// returns normally. The callback runs under the collector lock so game boundaries and
		/// concurrent captures cannot observe or reuse an in-flight sequence. If it throws, the
		/// candidate is discarded and the same sequence remains available for the next capture.
		/// </summary>
		internal bool TryCollectAndCommit(
			AdvisorBehaviorCapture capture,
			Action<AdvisorBehaviorRecord> durableCommit,
			out AdvisorBehaviorRecord record,
			out string rejectionReason)
		{
			if (durableCommit == null)
				throw new ArgumentNullException(nameof(durableCommit));
			return TryCollectCore(capture, durableCommit, out record, out rejectionReason);
		}

		private bool TryCollectCore(
			AdvisorBehaviorCapture capture,
			Action<AdvisorBehaviorRecord> durableCommit,
			out AdvisorBehaviorRecord record,
			out string rejectionReason)
		{
			lock (_sync)
			{
				record = null;
				rejectionReason = "";
				if (!_active || string.IsNullOrWhiteSpace(_gameId))
				{
					rejectionReason = "behavior_game_not_started";
					return false;
				}
				try
				{
					var candidate = BuildRecord(capture, _gameId, checked(_sequence + 1));
					if (durableCommit != null)
						durableCommit(candidate);
					_sequence = candidate.BehaviorSequence;
					record = candidate;
					return true;
				}
				catch (BehaviorCaptureException ex)
				{
					rejectionReason = ex.Code;
					return false;
				}
			}
		}

		private static AdvisorBehaviorRecord BuildRecord(
			AdvisorBehaviorCapture capture,
			string gameId,
			long sequence)
		{
			if (capture == null || capture.PreState == null || capture.Action == null)
				Reject("behavior_capture_missing");
			if (capture.ObservedAtUtc == DateTime.MinValue ||
				capture.ObservedAtUtc.Kind != DateTimeKind.Utc)
				Reject("observed_at_utc_invalid");
			AssertStateGame(capture.PreState, gameId);
			if (capture.PostState != null)
				AssertStateGame(capture.PostState, gameId);

			var preState = ProjectState(capture.PreState);
			var postState = capture.PostState == null ? null : ProjectState(capture.PostState);
			var actorSide = EnumValue(capture.ActorSide, ActorSides, "actor_side_invalid");
			var actorPlayerId = EnumValue(
				capture.ActorPlayerId, ActorPlayerIds, "actor_player_id_invalid");
			var actorEvidence = EnumValue(
				capture.ActorEvidence, ActorEvidence, "actor_evidence_invalid");
			var identity = EnumValue(
				capture.IdentityStatus, IdentityStatuses, "identity_status_invalid");
			var visibility = EnumValue(
				capture.VisibilityStatus, VisibilityStatuses, "visibility_status_invalid");
			var boundary = EnumValue(
				capture.BoundaryStatus, BoundaryStatuses, "boundary_status_invalid");
			var sourceEvent = Normalize(capture.SourceEvent);
			var kind = EnumValue(capture.Action.Kind, ActionKinds, "action_kind_invalid");
			if (!AdvisorBehaviorContract.IsSafeToken(capture.Action.CardId, true))
				Reject("action_card_id_invalid");
			if (capture.Action.SourceEntityId.HasValue && capture.Action.SourceEntityId.Value <= 0)
				Reject("source_entity_id_invalid");
			if (capture.Action.TargetEntityId.HasValue && capture.Action.TargetEntityId.Value <= 0)
				Reject("target_entity_id_invalid");
			var choiceStatus = EnumValue(
				capture.Action.ChoiceStatus, ChoiceStatuses, "choice_status_invalid");
			ValidateActionSelection(capture.Action, actorSide, sourceEvent, choiceStatus);

			if (!string.Equals(actorPlayerId, preState.ActivePlayerId, StringComparison.Ordinal))
				Reject("actor_not_active_player");
			var computedSide = string.Equals(
				actorPlayerId, preState.PerspectivePlayerId, StringComparison.Ordinal)
				? "local" : "opponent";
			if (actorSide != "unknown" && actorSide != computedSide)
				Reject("actor_side_mismatch");
			ValidateSourceEvent(sourceEvent, actorSide, kind);
			ValidateActorEvidence(actorSide, actorEvidence, sourceEvent);
			ValidateActionBinding(
				capture.PreState,
				capture.Action,
				actorSide,
				actorPlayerId,
				identity,
				visibility);

			var eligible = ComputeBehaviorEligibility(
				actorSide,
				actorEvidence,
				identity,
				visibility,
				boundary,
				kind,
				choiceStatus,
				postState != null);
			return new AdvisorBehaviorRecord
			{
				GameId = gameId,
				BehaviorSequence = sequence,
				ObservedAtUtc = capture.ObservedAtUtc.ToString("o", CultureInfo.InvariantCulture),
				ActorSide = actorSide,
				ActorPlayerId = actorPlayerId,
				ActorEvidence = actorEvidence,
				IdentityStatus = identity,
				VisibilityStatus = visibility,
				BoundaryStatus = boundary,
				SourceEvent = sourceEvent,
				Action = new AdvisorBehaviorAction
				{
					Kind = kind,
					SourceEntityId = capture.Action.SourceEntityId,
					TargetEntityId = capture.Action.TargetEntityId,
					CardId = NormalizeToken(capture.Action.CardId),
					SubOption = capture.Action.SubOption,
					BoardPosition = capture.Action.BoardPosition,
					ChoiceStatus = choiceStatus,
					Choices = CloneChoices(capture.Action.Choices)
				},
				PreState = preState,
				PostState = postState,
				BehaviorEligible = eligible
			};
		}

		private static void AssertStateGame(AdvisorGameState state, string gameId)
		{
			if (!string.IsNullOrWhiteSpace(state?.GameId) && !string.Equals(
				state.GameId.Trim(),
				gameId,
				StringComparison.Ordinal))
				Reject("behavior_state_game_mismatch");
		}

		private static AdvisorBehaviorPublicState ProjectState(AdvisorGameState state)
		{
			if (state == null || state.Player == null || state.Opponent == null ||
				state.Player.Hero == null || state.Opponent.Hero == null ||
				state.TurnNumber < 1 ||
				!AdvisorBehaviorContract.IsSafeToken(state.StateId))
				Reject("public_state_invalid");
			var active = ActivePlayerId(state);
			var patch = state.HearthstoneBuild.HasValue
				? state.HearthstoneBuild.Value.ToString(CultureInfo.InvariantCulture)
				: "";
			var mode = NormalizeBehaviorMode(state);
			if (!AdvisorBehaviorContract.IsSafeToken(patch, true) ||
				!AdvisorBehaviorContract.IsSafeToken(mode, true))
				Reject("public_state_descriptor_invalid");
			return new AdvisorBehaviorPublicState
			{
				StateId = state.StateId.Trim(),
				Turn = state.TurnNumber,
				ActivePlayerId = active,
				PerspectivePlayerId = "friendly",
				Friendly = ProjectPlayer(state.Player, "friendly", false),
				Opponent = ProjectPlayer(state.Opponent, "opponent", true),
				Patch = patch,
				Mode = mode
			};
		}

		private static string ActivePlayerId(AdvisorGameState state)
		{
			if (state.IsLocalPlayerTurn.HasValue)
				return state.IsLocalPlayerTurn.Value ? "friendly" : "opponent";
			var label = Normalize(state.ActivePlayer);
			if (label == "player" || label == "friendly" || label == "local")
				return "friendly";
			if (label == "opponent" || label == "enemy")
				return "opponent";
			Reject("active_player_unknown");
			return "";
		}

		/// <summary>
		/// Produces the format bucket used by offline behavior learning. Ranked/Casual/Friendly
		/// identify the queue, not the card format, so they must never be allowed to silently
		/// turn a Wild game into Standard. Missing format evidence fails closed to unknown.
		/// </summary>
		internal static string NormalizeBehaviorMode(AdvisorGameState state)
		{
			if (state == null)
				return "unknown";
			var mode = Normalize(state.GameMode);
			var format = Normalize(state.Format);
			var formatType = Normalize(state.FormatType);
			var gameType = Normalize(state.GameType);
			if ((state.Arena != null && state.Arena.IsArenaMatch) ||
				mode.IndexOf("arena", StringComparison.Ordinal) >= 0 ||
				gameType.IndexOf("arena", StringComparison.Ordinal) >= 0)
			{
				return "arena";
			}
			if (formatType == "ft_standard" || formatType == "standard" ||
				format == "standard")
				return "standard";
			if (formatType == "ft_wild" || formatType == "wild" || format == "wild")
				return "wild";
			if (formatType == "ft_twist" || formatType == "twist" || format == "twist")
				return "twist";
			if (formatType == "ft_classic" || formatType == "classic" || format == "classic")
				return "classic";
			if (mode.IndexOf("tavern", StringComparison.Ordinal) >= 0 ||
				gameType.IndexOf("tavern", StringComparison.Ordinal) >= 0)
			{
				return "tavern_brawl";
			}
			if (mode == "ranked" || mode == "casual" || mode == "friendly" ||
				mode.Length == 0)
			{
				return "unknown";
			}
			var safe = Regex.Replace(mode, @"[^a-z0-9_.:-]+", "_").Trim('_');
			return safe.Length == 0 ? "unknown" : safe;
		}

		private static AdvisorBehaviorPublicPlayer ProjectPlayer(
			AdvisorPlayerState player,
			string role,
			bool hideHand)
		{
			var hand = (player.Hand ?? new List<AdvisorEntityState>())
				.Where(item => item != null).ToList();
			var board = (player.Board ?? new List<AdvisorEntityState>())
				.Where(item => item != null).ToList();
			if (hand.Count > 10)
				Reject("public_hand_capacity_exceeded");
			if (board.Count > 7)
				Reject("public_board_capacity_exceeded");
			return new AdvisorBehaviorPublicPlayer
			{
				PlayerId = role,
				Hero = ProjectEntity(player.Hero, false),
				HeroPower = ProjectEntity(player.HeroPower, false),
				Weapon = ProjectEntity(player.Weapon, false),
				Hand = hand
					.Select(item => ProjectEntity(item, hideHand)).ToList(),
				Board = board
					.Select(item => ProjectEntity(item, false)).ToList(),
				Mana = Math.Max(0, player.Resources?.Available ?? 0),
				// Keep training state aligned with the solve wire contract. HDT's
				// Player.MaxMana is the rules cap, not current permanent crystals.
				MaxMana = Math.Max(0, player.Resources?.Total ?? 0),
				Armor = Math.Max(0, player.Hero?.Armor ?? 0),
				DeckSize = Math.Max(0, player.DeckCount),
				Fatigue = Math.Max(0, player.Fatigue),
				HeroPowerAvailable = player.HeroPower != null && !player.HeroPower.IsExhausted,
				SpellPower = Math.Max(0, player.Resources?.SpellPower ?? 0)
			};
		}

		private static AdvisorBehaviorPublicEntity ProjectEntity(
			AdvisorEntityState entity,
			bool hidden)
		{
			if (entity == null)
				return null;
			if (entity.EntityId <= 0)
				Reject("public_entity_id_invalid");
			if (hidden)
			{
				return new AdvisorBehaviorPublicEntity
				{
					EntityId = entity.EntityId.ToString(CultureInfo.InvariantCulture),
					Hidden = true
				};
			}
			var cardType = NormalizeToken(entity.CardType).ToUpperInvariant();
			if (cardType == "INVALID" || cardType.Length == 0)
				cardType = "UNKNOWN";
			if (!CardTypes.Contains(cardType) ||
				!AdvisorBehaviorContract.IsSafeToken(entity.CardId, true))
				Reject("public_entity_identity_invalid");
			return new AdvisorBehaviorPublicEntity
			{
				EntityId = entity.EntityId.ToString(CultureInfo.InvariantCulture),
				CardId = NormalizeToken(entity.CardId),
				CardType = cardType,
				Cost = Math.Max(0, entity.Cost),
				Attack = Math.Max(0, entity.Attack),
				Health = Math.Max(0, entity.Health),
				CurrentHealth = Math.Max(0, entity.Health - entity.Damage),
				Playable = entity.IsPlayableCard,
				CanAttack = entity.Attack > 0 && !entity.IsExhausted && !entity.IsFrozen,
				Durability = Math.Max(0, entity.Durability),
				CurrentDurability = Math.Max(0, entity.Durability - entity.Damage)
			};
		}

		private static void ValidateSourceEvent(string sourceEvent, string actorSide, string kind)
		{
			string expectedSide;
			string expectedKind;
				switch (sourceEvent)
				{
					case "hdt_power_log": expectedSide = "local"; expectedKind = ""; break;
					case "hdt_replay_power": expectedSide = ""; expectedKind = ""; break;
				case "player_play": expectedSide = "local"; expectedKind = "play_card"; break;
				case "player_attack": expectedSide = "local"; expectedKind = "attack"; break;
				case "player_hero_power": expectedSide = "local"; expectedKind = "hero_power"; break;
				case "turn_passed_to_opponent": expectedSide = "local"; expectedKind = "end_turn"; break;
				case "opponent_play": expectedSide = "opponent"; expectedKind = "play_card"; break;
				case "opponent_attack": expectedSide = "opponent"; expectedKind = "attack"; break;
				case "opponent_hero_power": expectedSide = "opponent"; expectedKind = "hero_power"; break;
				case "turn_passed_to_player": expectedSide = "opponent"; expectedKind = "end_turn"; break;
				case "unknown": expectedSide = ""; expectedKind = ""; break;
				default: Reject("source_event_invalid"); return;
			}
			if (actorSide != "unknown" && expectedSide.Length > 0 && actorSide != expectedSide)
				Reject("source_event_actor_mismatch");
			if (expectedKind.Length > 0 && kind != expectedKind)
				Reject("source_event_action_mismatch");
			if (sourceEvent == "unknown" && actorSide != "unknown")
				Reject("known_actor_requires_source_event");
		}

		private static void ValidateActorEvidence(
			string actorSide,
			string evidence,
			string sourceEvent)
		{
			if (actorSide == "unknown")
			{
				if (evidence != "unknown" || sourceEvent != "unknown")
					Reject("unknown_actor_evidence_mismatch");
				return;
			}
			if (evidence == "unknown")
				Reject("known_actor_requires_evidence");
			if (evidence == "hdt_player_event" && actorSide != "local")
				Reject("actor_evidence_side_mismatch");
			if (evidence == "hdt_opponent_event" && actorSide != "opponent")
				Reject("actor_evidence_side_mismatch");
			if (evidence == "hdt_power_log" && actorSide != "local")
				Reject("power_evidence_must_be_local");
			if (sourceEvent == "hdt_power_log" && evidence != "hdt_power_log")
				Reject("power_source_requires_power_evidence");
			if (sourceEvent == "hdt_replay_power" && evidence != "hdt_replay_power")
				Reject("replay_source_requires_replay_evidence");
			if (evidence == "hdt_replay_power" && sourceEvent != "hdt_replay_power")
				Reject("replay_evidence_requires_replay_source");
			if (sourceEvent.StartsWith("player_", StringComparison.Ordinal) &&
				evidence != "hdt_player_event" && evidence != "source_owner")
				Reject("player_event_evidence_mismatch");
			if (sourceEvent.StartsWith("opponent_", StringComparison.Ordinal) &&
				evidence != "hdt_opponent_event" && evidence != "source_owner")
				Reject("opponent_event_evidence_mismatch");
			if (sourceEvent.StartsWith("turn_passed_", StringComparison.Ordinal) &&
				evidence != "active_player")
				Reject("turn_event_requires_active_player_evidence");
		}

		private static void ValidateActionBinding(
			AdvisorGameState state,
			AdvisorBehaviorAction action,
			string actorSide,
			string actorPlayerId,
			string identity,
			string visibility)
		{
			var entities = BuildEntityIndex(state);
			EntityBinding source = null;
			EntityBinding target = null;
			if (action.SourceEntityId.HasValue)
				entities.TryGetValue(action.SourceEntityId.Value, out source);
			if (action.TargetEntityId.HasValue)
				entities.TryGetValue(action.TargetEntityId.Value, out target);

			if (actorSide == "unknown")
			{
				if (identity != "unknown" || visibility != "hidden_source")
					Reject("unknown_actor_tier_mismatch");
				if (action.SourceEntityId.HasValue &&
					(source == null || source.Role != actorPlayerId ||
						!ControllerMatches(source)))
					Reject("source_owner_mismatch");
				return;
			}

			// HDT sometimes proves who acted and which callback fired without exposing a
			// unique source/target entity (for example, duplicate CardIDs or a hidden
			// opponent hand slot). Keep that evidence instead of dropping the action, but
			// never promote it to behavior-eligible data. Any entity ID that is supplied
			// still has to resolve to a semantically valid owner and zone.
			if (identity == "unknown")
			{
				if (action.Kind == "end_turn")
					Reject("end_turn_tier_mismatch");
				ValidateIncompleteKnownActionBinding(
					action, actorPlayerId, source, target);
				return;
			}

			if (action.Kind == "end_turn")
			{
				if (action.SourceEntityId.HasValue || action.TargetEntityId.HasValue ||
					!string.IsNullOrWhiteSpace(action.CardId))
					Reject("end_turn_must_not_have_entities");
				if (identity != "event_only" || visibility != "public_pre_state")
					Reject("end_turn_tier_mismatch");
				return;
			}

			if (!action.SourceEntityId.HasValue || source == null)
				Reject("source_not_in_pre_state");
			if (source.Role != actorPlayerId || !ControllerMatches(source))
				Reject("source_owner_mismatch");
			if (action.TargetEntityId.HasValue && target == null)
				Reject("target_not_in_pre_state");

			var cardId = NormalizeToken(action.CardId);
			if (action.Kind == "play_card")
			{
				if (source.Zone != "hand")
					Reject("play_source_not_in_hand");
				if (cardId.Length == 0)
					Reject("play_card_id_required");
				if (actorSide == "opponent")
				{
					if (identity != "revealed_after_action" ||
						visibility != "revealed_post_action")
						Reject("opponent_hidden_play_tier_mismatch");
				}
				else
				{
					if (identity != "exact_public_entity" ||
						visibility != "public_pre_state")
						Reject("local_play_tier_mismatch");
					if (!string.Equals(cardId, source.Entity.CardId, StringComparison.Ordinal))
						Reject("source_card_id_mismatch");
				}
				return;
			}

			if (identity != "exact_public_entity" || visibility != "public_pre_state")
				Reject("public_action_tier_mismatch");
			if (cardId.Length == 0 ||
				!string.Equals(cardId, source.Entity.CardId, StringComparison.Ordinal))
				Reject("source_card_id_mismatch");
			if (action.Kind == "attack")
			{
				if (source.Zone != "hero" && source.Zone != "board")
					Reject("attack_source_not_character");
				if (!action.TargetEntityId.HasValue || target == null)
					Reject("attack_target_required");
				if (target.Role == actorPlayerId ||
					(target.Zone != "hero" && target.Zone != "board"))
					Reject("attack_target_not_enemy_character");
			}
			else if (action.Kind == "hero_power" && source.Zone != "hero_power")
				Reject("hero_power_source_mismatch");
			else if (action.Kind == "location_activate")
			{
				if (source.Zone != "board")
					Reject("location_source_not_on_board");
				if (!string.Equals(
					source.Entity.CardType,
					"LOCATION",
					StringComparison.OrdinalIgnoreCase))
				{
					Reject("location_source_not_location");
				}
			}
		}

		private static void ValidateIncompleteKnownActionBinding(
			AdvisorBehaviorAction action,
			string actorPlayerId,
			EntityBinding source,
			EntityBinding target)
		{
			if (action.SourceEntityId.HasValue)
			{
				if (source == null)
					Reject("source_not_in_pre_state");
				if (source.Role != actorPlayerId || !ControllerMatches(source))
					Reject("source_owner_mismatch");
			}
			if (action.TargetEntityId.HasValue && target == null)
				Reject("target_not_in_pre_state");

			if (source != null)
			{
				if (action.Kind == "play_card" && source.Zone != "hand")
					Reject("play_source_not_in_hand");
				if (action.Kind == "attack" &&
					source.Zone != "hero" && source.Zone != "board")
					Reject("attack_source_not_character");
				if (action.Kind == "hero_power" && source.Zone != "hero_power")
					Reject("hero_power_source_mismatch");
				if (action.Kind == "location_activate")
				{
					if (source.Zone != "board")
						Reject("location_source_not_on_board");
					if (!string.Equals(
						source.Entity.CardType,
						"LOCATION",
						StringComparison.OrdinalIgnoreCase))
					{
						Reject("location_source_not_location");
					}
				}
			}
			if (action.Kind == "attack" && target != null &&
				(target.Role == actorPlayerId ||
					(target.Zone != "hero" && target.Zone != "board")))
			{
				Reject("attack_target_not_enemy_character");
			}
		}

		private static bool ComputeBehaviorEligibility(
			string actorSide,
			string actorEvidence,
			string identity,
			string visibility,
			string boundary,
			string kind,
			string choiceStatus,
			bool hasPostState)
		{
			if (!hasPostState || actorSide == "unknown" || actorEvidence == "unknown" ||
				boundary != "isolated" || choiceStatus == "unresolved")
				return false;
			if (kind == "end_turn")
				return identity == "event_only" && visibility == "public_pre_state";
			if (identity == "exact_public_entity")
				return visibility == "public_pre_state";
			return actorSide == "opponent" && kind == "play_card" &&
				identity == "revealed_after_action" && visibility == "revealed_post_action";
		}

		private static void ValidateActionSelection(
			AdvisorBehaviorAction action,
			string actorSide,
			string sourceEvent,
			string choiceStatus)
		{
			if (action.SubOption.HasValue && action.SubOption.Value < -1)
				Reject("sub_option_invalid");
			if (action.BoardPosition.HasValue && action.BoardPosition.Value < 0)
				Reject("board_position_invalid");
			var choices = action.Choices ?? new List<AdvisorObservedChoice>();
			if (choiceStatus == "not_observed")
			{
				if (action.SubOption.HasValue || action.BoardPosition.HasValue || choices.Count > 0)
					Reject("unobserved_choice_has_power_fields");
				return;
			}
			if (choiceStatus == "none")
			{
				if ((action.SubOption.HasValue && action.SubOption.Value != -1) ||
					choices.Count > 0)
				{
					Reject("choice_none_has_selection");
				}
				return;
			}
			if (choiceStatus == "selected" &&
				(actorSide != "local" || sourceEvent != "hdt_power_log"))
			{
				Reject("selected_choice_requires_local_power");
			}
			if (choiceStatus == "selected" && choices.Count == 0)
				Reject("selected_choice_missing");
			foreach (var choice in choices)
			{
				if (choice == null || !choice.SourceEntityId.HasValue ||
					choice.SourceEntityId.Value <= 0 ||
					choice.SourceEntityId != action.SourceEntityId ||
					(choice.ChoiceId.HasValue && choice.ChoiceId.Value <= 0) ||
					!AdvisorBehaviorContract.IsSafeToken(choice.ChoiceType) ||
					(choice.Status != "selected" && choice.Status != "unresolved"))
				{
					Reject("choice_identity_invalid");
				}
				var options = choice.OptionEntityIds ?? new List<int>();
				var selected = choice.SelectedEntityIds ?? new List<int>();
				if (options.Any(id => id <= 0) || options.Distinct().Count() != options.Count ||
					selected.Any(id => id <= 0) || selected.Distinct().Count() != selected.Count ||
					selected.Any(id => !options.Contains(id)))
				{
					Reject("choice_entity_ids_invalid");
				}
				if (choiceStatus == "selected" &&
					(choice.Status != "selected" || options.Count == 0 || selected.Count == 0))
				{
					Reject("selected_choice_incomplete");
				}
			}
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

		private static Dictionary<int, EntityBinding> BuildEntityIndex(AdvisorGameState state)
		{
			var result = new Dictionary<int, EntityBinding>();
			AddPlayerEntities(result, state.Player, "friendly");
			AddPlayerEntities(result, state.Opponent, "opponent");
			return result;
		}

		private static void AddPlayerEntities(
			IDictionary<int, EntityBinding> result,
			AdvisorPlayerState player,
			string role)
		{
			AddEntity(result, player, role, "hero", player?.Hero);
			AddEntity(result, player, role, "hero_power", player?.HeroPower);
			AddEntity(result, player, role, "weapon", player?.Weapon);
			foreach (var entity in player?.Hand ?? new List<AdvisorEntityState>())
				AddEntity(result, player, role, "hand", entity);
			foreach (var entity in player?.Board ?? new List<AdvisorEntityState>())
				AddEntity(result, player, role, "board", entity);
		}

		private static void AddEntity(
			IDictionary<int, EntityBinding> result,
			AdvisorPlayerState player,
			string role,
			string zone,
			AdvisorEntityState entity)
		{
			if (entity == null)
				return;
			if (entity.EntityId <= 0 || result.ContainsKey(entity.EntityId))
				Reject("duplicate_or_invalid_entity_id");
			result[entity.EntityId] = new EntityBinding
			{
				Role = role,
				Zone = zone,
				PlayerId = player?.PlayerId ?? 0,
				Entity = entity
			};
		}

		private static bool ControllerMatches(EntityBinding binding)
		{
			return binding.Entity.ControllerId <= 0 || binding.PlayerId <= 0 ||
				binding.Entity.ControllerId == binding.PlayerId;
		}

		private static string EnumValue(
			string value,
			ISet<string> allowed,
			string error)
		{
			var normalized = Normalize(value);
			if (!allowed.Contains(normalized))
				Reject(error);
			return normalized;
		}

		private static string Normalize(string value)
		{
			return (value ?? "").Trim().ToLowerInvariant();
		}

		private static string NormalizeToken(string value)
		{
			return (value ?? "").Trim();
		}

		private static HashSet<string> Set(params string[] values)
		{
			return new HashSet<string>(values, StringComparer.Ordinal);
		}

		private static void Reject(string code)
		{
			throw new BehaviorCaptureException(code);
		}

		private sealed class EntityBinding
		{
			internal string Role { get; set; }
			internal string Zone { get; set; }
			internal int PlayerId { get; set; }
			internal AdvisorEntityState Entity { get; set; }
		}

		private sealed class BehaviorCaptureException : Exception
		{
			internal BehaviorCaptureException(string code) : base(code)
			{
				Code = code ?? "behavior_capture_rejected";
			}

			internal string Code { get; private set; }
		}
	}

}
