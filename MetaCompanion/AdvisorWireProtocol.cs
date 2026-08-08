using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace MetaCompanion
{
	/// <summary>
	/// Explicit PascalCase DTO to snake_case wire adapter. Keeping this mapping explicit prevents
	/// a serializer setting or a C# rename from silently changing the Python worker contract.
	/// </summary>
	public static class AdvisorWireProtocol
	{
		public const int DefaultMaximumJsonLength = 4 * 1024 * 1024;

		public static string SerializeSolveRequest(AdvisorSolveRequest request)
		{
			if (request == null)
				throw new ArgumentNullException(nameof(request));
			if (request.State == null)
				throw new ArgumentException("A solve request must contain a state.", nameof(request));
			if (string.IsNullOrWhiteSpace(request.RequestId))
				throw new ArgumentException("A solve request must contain a request ID.", nameof(request));

			var root = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "api_version", string.IsNullOrWhiteSpace(request.ApiVersion) ? AdvisorProtocol.ApiVersion : request.ApiVersion },
				{ "request_id", request.RequestId },
				{ "state", MapState(request.State) },
				{ "options", MapOptions(request.Options ?? new AdvisorSolveOptions()) },
				{ "metadata", ToObjectDictionary(request.Metadata) }
			};
			if (request.HdtRootCandidates != null)
				root["hdt_root_candidates"] = MapHdtRootCandidates(request.HdtRootCandidates);
			return Serialize(root);
		}

		public static string SerializeCancelRequest(AdvisorCancelRequest request)
		{
			if (request == null)
				throw new ArgumentNullException(nameof(request));
			var root = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "api_version", string.IsNullOrWhiteSpace(request.ApiVersion) ? AdvisorProtocol.ApiVersion : request.ApiVersion }
			};
			if (!string.IsNullOrWhiteSpace(request.RequestId))
				root["request_id"] = request.RequestId;
			if (!string.IsNullOrWhiteSpace(request.StateId))
				root["state_id"] = request.StateId;
			if (!root.ContainsKey("request_id") && !root.ContainsKey("state_id"))
				throw new ArgumentException("A cancel request needs a request ID or state ID.", nameof(request));
			return Serialize(root);
		}

		public static string SerializeObservation(AdvisorObservation observation)
		{
			if (observation == null)
				throw new ArgumentNullException(nameof(observation));
			var kind = (observation.Kind ?? "").Trim().ToLowerInvariant();
			if (kind != "action" && kind != "result")
				throw new ArgumentException("Observation kind must be action or result.", nameof(observation));
			if (string.IsNullOrWhiteSpace(observation.StateId))
				throw new ArgumentException("Observation state ID is required.", nameof(observation));
			var root = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "api_version", string.IsNullOrWhiteSpace(observation.ApiVersion) ? AdvisorProtocol.ApiVersion : observation.ApiVersion },
				{ "kind", kind }, { "state_id", observation.StateId },
				{ "game_id", observation.GameId ?? "" },
				{ "observed_at_utc", observation.ObservedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
				{ "result", observation.Result ?? "" },
				{ "pre_state", observation.PreState == null ? null : MapState(observation.PreState) },
				{ "post_state", observation.PostState == null ? null : MapState(observation.PostState) },
				{ "metadata", ToObjectDictionary(observation.Metadata) }
			};
			if (observation.Action != null)
			{
				var action = new Dictionary<string, object>(StringComparer.Ordinal)
				{
					{ "kind", observation.Action.Kind ?? "" },
					{ "source_entity_id", observation.Action.SourceEntityId.HasValue
						? observation.Action.SourceEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
					{ "target_entity_id", observation.Action.TargetEntityId.HasValue
						? observation.Action.TargetEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
					{ "card_id", observation.Action.CardId ?? "" },
					{ "sub_option", observation.Action.SubOption.HasValue
						? (object)observation.Action.SubOption.Value : null },
					{ "board_position", observation.Action.BoardPosition.HasValue
						? (object)observation.Action.BoardPosition.Value : null },
					{ "option_id", observation.Action.OptionId.HasValue
						? (object)observation.Action.OptionId.Value : null },
					{ "frame_id", observation.Action.FrameId.HasValue
						? (object)observation.Action.FrameId.Value : null },
					{ "power_start_watermark", string.IsNullOrWhiteSpace(
						observation.Action.PowerStartWatermark) ? null : observation.Action.PowerStartWatermark },
					{ "power_end_watermark", string.IsNullOrWhiteSpace(
						observation.Action.PowerEndWatermark) ? null : observation.Action.PowerEndWatermark },
					{ "choices", (observation.Action.Choices ?? new List<AdvisorObservedChoice>())
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
				if (observation.Action.HdtRootCandidates != null)
					action["hdt_root_candidates"] = MapHdtRootCandidates(
						observation.Action.HdtRootCandidates);
				root["action"] = action;
			}
			else
			{
				root["action"] = null;
			}
			return Serialize(root);
		}

		public static AdvisorObservationResult DeserializeObservationResult(string json)
		{
			var root = ParseObject(json);
			return new AdvisorObservationResult
			{
				ApiVersion = GetString(root, "api_version"), Status = GetString(root, "status"),
				Kind = GetString(root, "kind"), StateId = GetString(root, "state_id"),
				Logged = GetBool(root, false, "logged"),
				Duplicate = GetBool(root, false, "duplicate"),
				ResultId = GetString(root, "result_id"),
				GameId = GetString(root, "game_id"),
				Result = GetString(root, "result")
			};
		}

		internal static AdvisorResultObservationIdentity DeserializeResultObservationIdentity(
			string json)
		{
			var root = ParseObject(json);
			if (!string.Equals(GetString(root, "kind"), "result", StringComparison.Ordinal))
				throw new AdvisorWorkerProtocolException("Queued terminal observation kind is invalid.");
			var identity = new AdvisorResultObservationIdentity
			{
				GameId = GetString(root, "game_id"),
				StateId = GetString(root, "state_id"),
				Result = GetString(root, "result").Trim().ToLowerInvariant()
			};
			if (string.IsNullOrWhiteSpace(identity.GameId) ||
				string.IsNullOrWhiteSpace(identity.StateId) ||
				!(new[] { "win", "loss", "tie", "unknown" }).Contains(identity.Result))
			{
				throw new AdvisorWorkerProtocolException("Queued terminal observation identity is invalid.");
			}
			return identity;
		}

		internal static AdvisorResultAppendResult DeserializeResultAppendResult(string json)
		{
			var root = ParseObject(json);
			var apiVersion = GetString(root, "api_version");
			if (!string.Equals(apiVersion, AdvisorProtocol.ApiVersion, StringComparison.Ordinal))
				throw new AdvisorWorkerProtocolException("Result response API version is incompatible.");
			var result = new AdvisorResultAppendResult
			{
				Status = GetString(root, "status"),
				Kind = GetString(root, "kind"),
				Logged = GetBool(root, false, "logged"),
				Duplicate = GetBool(root, false, "duplicate"),
				ResultId = GetString(root, "result_id"),
				GameId = GetString(root, "game_id"),
				StateId = GetString(root, "state_id"),
				Result = GetString(root, "result")
			};
			if ((!result.Logged && !result.Duplicate) ||
				!string.Equals(result.Kind, "result", StringComparison.Ordinal) ||
				string.IsNullOrWhiteSpace(result.ResultId) ||
				string.IsNullOrWhiteSpace(result.GameId) ||
				string.IsNullOrWhiteSpace(result.StateId))
			{
				throw new AdvisorWorkerProtocolException(
					"Result response did not acknowledge a persisted or duplicate terminal record.");
			}
			return result;
		}

		internal static AdvisorBehaviorAppendResult DeserializeBehaviorAppendResult(string json)
		{
			var root = ParseObject(json);
			var apiVersion = GetString(root, "api_version");
			if (!string.Equals(apiVersion, AdvisorProtocol.ApiVersion, StringComparison.Ordinal))
				throw new AdvisorWorkerProtocolException("Behavior response API version is incompatible.");
			var result = new AdvisorBehaviorAppendResult
			{
				Status = GetString(root, "status"),
				Logged = GetBool(root, false, "logged"),
				Duplicate = GetBool(root, false, "duplicate"),
				BehaviorId = GetString(root, "behavior_id"),
				GameId = GetString(root, "game_id"),
				BehaviorSequence = GetLong(root, 0, "behavior_sequence")
			};
			if ((!result.Logged && !result.Duplicate) ||
				string.IsNullOrWhiteSpace(result.BehaviorId) ||
				string.IsNullOrWhiteSpace(result.GameId) ||
				result.BehaviorSequence <= 0)
			{
				throw new AdvisorWorkerProtocolException(
					"Behavior response did not acknowledge a persisted or duplicate record.");
			}
			return result;
		}

		public static AdvisorWorkerHealth DeserializeHealth(string json)
		{
			var root = ParseObject(json);
			var status = GetString(root, "status");
			var backend = GetString(root, "backend", "implementation");
			var capabilities = GetObject(root, "capabilities");
			var behaviorPrior = GetObject(root, "behavior_prior");
			var decisionRanker = GetObject(root, "decision_ranker");
			var rustBackend = string.Equals(backend, "rust", StringComparison.OrdinalIgnoreCase);
			return new AdvisorWorkerHealth
			{
				ApiVersion = GetString(root, "api_version"),
				Status = status,
				WorkerVersion = GetString(root, "worker_version", "package_version"),
				ModelVersion = GetString(root, "model_version"),
				Backend = backend,
				ParityProfile = GetString(root, "parity_profile"),
				SupportsCounterplayTurnpair = GetBool(
					capabilities,
					!rustBackend,
					"counterplay_turnpair_v1"),
				SupportsBehaviorSearchOrderingPrior = GetBool(
					capabilities,
					false,
					"behavior_search_ordering_prior_v1"),
				SupportsDecisionRanker = GetBool(
					capabilities,
					false,
					"hdt_decision_ranker_v1"),
				SupportsBehaviorReference = GetBool(
					capabilities,
					false,
					"hdt_behavior_reference_v1"),
				BehaviorPriorAvailable = GetBool(behaviorPrior, false, "available"),
				BehaviorPriorStatus = GetString(behaviorPrior, "status"),
				BehaviorPriorReason = GetString(behaviorPrior, "reason"),
				BehaviorPriorArtifactSha256 = GetString(behaviorPrior, "artifact_sha256"),
				DecisionRankerAvailable = GetBool(decisionRanker, false, "available"),
				DecisionRankerStatus = GetString(decisionRanker, "status"),
				DecisionRankerReason = GetString(decisionRanker, "reason"),
				DecisionRankerArtifactSha256 = GetString(
					decisionRanker, "artifact_sha256"),
				IsProductionReady = GetBool(
					root,
					!rustBackend,
					"production_ready", "is_production_ready"),
				Message = GetString(root, "message"),
				IsReady = GetBool(root, false, "is_ready") &&
					string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)
			};
		}

		public static AdvisorSolveResponse DeserializeSolveResponse(string json)
		{
			return DeserializeSolveResponse(json, null);
		}

		public static AdvisorSolveResponse DeserializeSolveResponse(
			string json, AdvisorSolveRequest originatingRequest)
		{
			var root = ParseObject(json);
			var response = new AdvisorSolveResponse
			{
				ApiVersion = GetString(root, "api_version"),
				RequestId = GetString(root, "request_id"),
				SchemaVersion = GetInt(root, AdvisorProtocol.SnapshotSchemaVersion, "schema_version"),
				StateId = GetString(root, "state_id"),
				Status = GetString(root, "status"),
				Message = GetString(root, "message"),
				IsFinal = GetBool(root, true, "is_final"),
				GeneratedAtUtc = GetDateTime(root, "generated_at_utc", "generated_at"),
				ElapsedMilliseconds = GetLong(root, 0, "elapsed_ms", "elapsed_milliseconds"),
				Iterations = GetInt(root, 0, "iterations"),
				Progress = ReadProgress(root),
				ModelVersion = GetString(root, "model_version"),
				EnvironmentVersion = GetString(root, "environment_version"),
				Coverage = ParseCoverage(GetObject(root, "coverage")),
				Warnings = GetStrings(root, "warnings")
			};
			foreach (var item in GetObjects(root, "recommendations"))
				response.Recommendations.Add(ParseRecommendation(item));
			response.BehaviorReferences = ParseBehaviorReferences(
				GetObject(root, "behavior_references"),
				GetObject(GetObject(root, "coverage"), "decision_ranker"),
				response,
				originatingRequest);
			ValidatePortfolioRecommendations(response);
			return response;
		}

		public static string TryReadErrorMessage(string json)
		{
			try
			{
				var root = ParseObject(json);
				var error = GetObject(root, "error");
				if (error == null)
					return GetString(root, "message");
				var message = GetString(error, "message");
				var path = GetString(error, "path");
				var code = GetString(error, "code");
				var prefix = string.IsNullOrWhiteSpace(code) ? "" : code + ": ";
				return prefix + message + (string.IsNullOrWhiteSpace(path) ? "" : " (" + path + ")");
			}
			catch
			{
				return "";
			}
		}

		public static string TryReadErrorCode(string json)
		{
			try
			{
				var root = ParseObject(json);
				var error = GetObject(root, "error");
				return error == null ? GetString(root, "code") : GetString(error, "code");
			}
			catch
			{
				return "";
			}
		}

		internal static string Serialize(IDictionary<string, object> value)
		{
			var serializer = NewSerializer();
			return serializer.Serialize(value);
		}

		internal static IDictionary<string, object> ParseObject(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
				throw new FormatException("The advisor worker returned an empty JSON document.");
			var root = NewSerializer().DeserializeObject(json) as IDictionary<string, object>;
			if (root == null)
				throw new FormatException("The advisor worker response must be a JSON object.");
			return root;
		}

		private static JavaScriptSerializer NewSerializer()
		{
			return new JavaScriptSerializer
			{
				MaxJsonLength = DefaultMaximumJsonLength,
				RecursionLimit = 128
			};
		}

		private static IDictionary<string, object> MapState(AdvisorGameState state)
		{
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "schema_version", state.SchemaVersion },
				{ "state_id", state.StateId ?? "" },
				{ "state_hash", state.StateHash ?? "" },
				{ "snapshot_sequence", state.SnapshotSequence },
				{ "captured_at_utc", state.CapturedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
				{ "game_id", state.GameId ?? "" },
				{ "turn_number", state.TurnNumber },
				{ "active_player", state.ActivePlayer ?? "unknown" },
				{ "is_local_player_turn", state.IsLocalPlayerTurn },
				{ "format", state.Format ?? "" },
				{ "format_type", state.FormatType ?? "" },
				{ "game_mode", state.GameMode ?? "" },
				{ "game_type", state.GameType ?? "" },
				{ "hdt_mode", state.HdtMode ?? "" },
				{ "is_running", state.IsRunning },
				{ "is_mulligan_done", state.IsMulliganDone },
				{ "is_spectating", state.IsSpectating },
				{ "hearthstone_build", state.HearthstoneBuild },
				{ "hdt_version", state.HdtVersion ?? "" },
				{ "environment_version", state.EnvironmentVersion ?? "" },
				{ "phase", MapPhase(state.Phase) },
				{ "current_deck", MapDeck(state.CurrentDeck) },
				{ "arena", MapArena(state.Arena) },
				{ "player", MapPlayer(state.Player) },
				{ "opponent", MapPlayer(state.Opponent) },
				{ "game_entity", MapEntity(state.GameEntity) },
				{ "other_public_entities", MapEntities(state.OtherPublicEntities) },
				{ "unknown_data", (state.UnknownData ?? new List<AdvisorDataGap>()).Select(MapGap).ToList() },
				{ "unsupported_features", (state.UnsupportedFeatures ?? new List<string>()).ToList() },
				{ "capture_warnings", (state.CaptureWarnings ?? new List<string>()).ToList() },
				{ "metadata", ToObjectDictionary(state.Metadata) }
			};
		}

		private static IDictionary<string, object> MapPhase(AdvisorGamePhaseState phase)
		{
			phase = phase ?? new AdvisorGamePhaseState();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "step", phase.Step ?? "" }, { "next_step", phase.NextStep ?? "" },
				{ "state", phase.State ?? "" }, { "player_play_state", phase.PlayerPlayState ?? "" },
				{ "opponent_play_state", phase.OpponentPlayState ?? "" },
				{ "mulligan_state", phase.MulliganState ?? "" },
				{ "proposed_attacker_entity_id", phase.ProposedAttackerEntityId },
				{ "proposed_defender_entity_id", phase.ProposedDefenderEntityId },
				{ "has_pending_choice", phase.HasPendingChoice },
				{ "can_local_player_act", phase.CanLocalPlayerAct }
			};
		}

		private static IDictionary<string, object> MapDeck(AdvisorDeckState deck)
		{
			deck = deck ?? new AdvisorDeckState();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "is_known", deck.IsKnown }, { "source", deck.Source ?? "" },
				{ "deck_id", deck.DeckId ?? "" }, { "hearthstone_deck_id", deck.HearthstoneDeckId },
				{ "name", deck.Name ?? "" }, { "hero_card_id", deck.HeroCardId ?? "" },
				{ "hero_power_card_id", deck.HeroPowerCardId ?? "" },
				{ "format_type", deck.FormatType }, { "deck_type", deck.DeckType },
				{ "cards", (deck.Cards ?? new List<AdvisorDeckCard>()).Select(x =>
					(IDictionary<string, object>)new Dictionary<string, object>(StringComparer.Ordinal)
					{
						{ "card_id", x.CardId ?? "" }, { "dbf_id", x.DbfId }, { "count", x.Count },
						{ "premium_type", x.PremiumType }, { "is_sideboard", x.IsSideboard },
						{ "sideboard_owner_card_id", x.SideboardOwnerCardId ?? "" }
					}).ToList() }
			};
		}

		private static IDictionary<string, object> MapArena(AdvisorArenaState arena)
		{
			arena = arena ?? new AdvisorArenaState();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "is_arena_match", arena.IsArenaMatch }, { "season_id", arena.SeasonId },
				{ "wins", arena.Wins }, { "losses", arena.Losses }, { "rating", arena.Rating },
				{ "package_inference_attempted", arena.PackageInferenceAttempted },
				{ "package_anchor_card_id", arena.PackageAnchorCardId ?? "" },
				{ "inferred_package_cards", MapKnownCards(arena.InferredPackageCards) }
			};
		}

		private static IDictionary<string, object> MapPlayer(AdvisorPlayerState player)
		{
			player = player ?? new AdvisorPlayerState();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "player_id", player.PlayerId }, { "entity_id", player.EntityId },
				{ "is_local_player", player.IsLocalPlayer }, { "class", player.Class ?? "" },
				{ "original_class", player.OriginalClass ?? "" }, { "hand_count", player.HandCount },
				{ "deck_count", player.DeckCount }, { "fatigue", player.Fatigue },
				{ "max_hand_size", player.MaxHandSize },
				// Defense in depth: never serialize HDT's rules-cap Player.MaxMana.
				// RESOURCES=0 is a real pre-first-turn state and must remain zero.
				{ "max_mana", Math.Max(0, player.Resources?.Total ?? 0) },
				{ "corpses", player.Corpses }, { "has_coin", player.HasCoin },
				{ "resources", MapResources(player.Resources) },
				{ "player_entity", MapEntity(player.PlayerEntity) }, { "hero", MapEntity(player.Hero) },
				{ "hero_power", MapEntity(player.HeroPower) }, { "weapon", MapEntity(player.Weapon) },
				{ "hand", MapEntities(player.Hand) }, { "board", MapEntities(player.Board) },
				{ "deck", MapEntities(player.Deck) }, { "graveyard", MapEntities(player.Graveyard) },
				{ "secrets", MapEntities(player.Secrets) }, { "set_aside", MapEntities(player.SetAside) },
				{ "removed_from_game", MapEntities(player.RemovedFromGame) },
				{ "other_entities", MapEntities(player.OtherEntities) },
				{ "known_cards_in_deck", MapKnownCards(player.KnownCardsInDeck) }
			};
		}

		private static IDictionary<string, object> MapResources(AdvisorResourceState resources)
		{
			resources = resources ?? new AdvisorResourceState();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "total", resources.Total }, { "used", resources.Used },
				{ "temporary", resources.Temporary }, { "overload_locked", resources.OverloadLocked },
				{ "overload_owed", resources.OverloadOwed }, { "available", resources.Available },
				{ "spell_power", resources.SpellPower }
			};
		}

		private static List<object> MapEntities(IEnumerable<AdvisorEntityState> entities)
		{
			return (entities ?? new List<AdvisorEntityState>()).Select(x => (object)MapEntity(x)).ToList();
		}

		private static IDictionary<string, object> MapEntity(AdvisorEntityState entity)
		{
			if (entity == null)
				return null;
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "entity_id", entity.EntityId }, { "card_id", entity.CardId ?? "" },
				{ "dbf_id", entity.DbfId }, { "name", entity.Name ?? "" },
				{ "zone", entity.Zone ?? "INVALID" }, { "zone_id", entity.ZoneId },
				{ "zone_position", entity.ZonePosition }, { "controller_id", entity.ControllerId },
				{ "card_type", entity.CardType ?? "UNKNOWN" }, { "card_type_id", entity.CardTypeId },
				{ "cost", entity.Cost }, { "attack", entity.Attack }, { "health", entity.Health },
				{ "damage", entity.Damage }, { "armor", entity.Armor }, { "durability", entity.Durability },
				{ "is_known", entity.IsKnown }, { "is_created", entity.IsCreated },
				{ "is_revealed", entity.IsRevealed }, { "is_playable_card", entity.IsPlayableCard },
				{ "is_exhausted", entity.IsExhausted }, { "is_frozen", entity.IsFrozen },
				{ "is_silenced", entity.IsSilenced }, { "has_taunt", entity.HasTaunt },
				{ "has_divine_shield", entity.HasDivineShield }, { "has_stealth", entity.HasStealth },
				{ "has_windfury", entity.HasWindfury }, { "has_mega_windfury", entity.HasMegaWindfury },
				{ "has_rush", entity.HasRush },
				{ "has_charge", entity.HasCharge }, { "has_lifesteal", entity.HasLifesteal },
				{ "has_poisonous", entity.HasPoisonous }, { "has_reborn", entity.HasReborn },
				{ "is_dormant", entity.IsDormant }, { "is_immune", entity.IsImmune },
				{ "creator_entity_id", entity.CreatorEntityId },
				{ "original_card_id", entity.OriginalCardId ?? "" }, { "visibility", entity.Visibility ?? "" },
				{ "card_text", entity.CardText ?? "" }, { "english_text", entity.EnglishText ?? "" },
				{ "race", entity.Race ?? "" }, { "card_class", entity.CardClass ?? "" },
				{ "rarity", entity.Rarity ?? "" }, { "mechanics", (entity.Mechanics ?? new List<string>()).ToList() },
				{ "tags", ToObjectDictionary(entity.Tags) }
			};
		}

		private static List<object> MapKnownCards(IEnumerable<AdvisorKnownCard> cards)
		{
			return (cards ?? new List<AdvisorKnownCard>()).Select(x => (object)
				new Dictionary<string, object>(StringComparer.Ordinal)
				{
					{ "card_id", x.CardId ?? "" }, { "dbf_id", x.DbfId },
					{ "count", x.Count }, { "source", x.Source ?? "" }
				}).ToList();
		}

		private static IDictionary<string, object> MapGap(AdvisorDataGap gap)
		{
			gap = gap ?? new AdvisorDataGap();
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "code", gap.Code ?? "" }, { "path", gap.Path ?? "" },
				{ "detail", gap.Detail ?? "" }, { "entity_id", gap.EntityId }, { "count", gap.Count }
			};
		}

		private static IDictionary<string, object> MapOptions(AdvisorSolveOptions options)
		{
			var result = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "time_budget_ms", Math.Max(1, options.TimeBudgetMilliseconds) },
				{ "max_iterations", Math.Max(1, options.MaxIterations) },
				{ "max_depth", Math.Max(1, options.MaxDepth) },
				{ "top_k", Math.Max(1, options.MaxRecommendations) },
				{ "allow_approximate_effects", options.AllowApproximateEffects },
				{ "environment_version", options.EnvironmentVersion ?? "" }
			};
			if (options.SearchSeed.HasValue)
				result["search_seed"] = options.SearchSeed.Value;
			return result;
		}

		private static IDictionary<string, object> MapHdtRootCandidates(
			AdvisorHdtRootCandidateSet value)
		{
			if (value == null)
				throw new ArgumentNullException(nameof(value));
			return new Dictionary<string, object>(StringComparer.Ordinal)
			{
				{ "contract", value.Contract ?? "" },
				{ "state_id", value.StateId ?? "" },
				{ "frame_id", value.FrameId },
				{ "collector_epoch", value.CollectorEpoch },
				{ "frame_watermark", value.FrameWatermark },
				{ "candidate_set_complete", value.CandidateSetComplete },
				{ "candidates", (value.Candidates ?? new List<AdvisorHdtRootCandidate>())
					.Where(item => item != null)
					.Select(item => (object)new Dictionary<string, object>(StringComparer.Ordinal)
					{
						{ "option_id", item.OptionId },
						{ "action", new Dictionary<string, object>(StringComparer.Ordinal)
							{
								{ "kind", item.Action?.Kind ?? "" },
								{ "source_entity_id", item.Action?.SourceEntityId.HasValue == true
									? item.Action.SourceEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
								{ "target_entity_id", item.Action?.TargetEntityId.HasValue == true
									? item.Action.TargetEntityId.Value.ToString(CultureInfo.InvariantCulture) : "" },
								{ "card_id", item.Action?.CardId ?? "" },
								{ "board_position", item.Action?.BoardPosition ?? 0 }
							} },
						{ "target_evidence", item.TargetEvidence ?? "" },
						{ "position_evidence", item.PositionEvidence ?? "" }
					}).ToList() }
			};
		}

		private static AdvisorBehaviorReferenceSet ParseBehaviorReferences(
			IDictionary<string, object> raw,
			IDictionary<string, object> rawDecisionRanker,
			AdvisorSolveResponse response,
			AdvisorSolveRequest originatingRequest)
		{
			var result = new AdvisorBehaviorReferenceSet();
			if (raw == null)
				return result;

			bool available;
			var hasCanonicalAvailable = TryGetCanonicalBool(raw, "available", out available);
			var rawStatus = GetStrictString(raw, "status");
			var claimedAvailable = string.Equals(
				rawStatus, "available", StringComparison.Ordinal) ||
				(hasCanonicalAvailable && available);
			result.Contract = GetStrictString(raw, "contract");
			result.Status = rawStatus;
			result.Available = hasCanonicalAvailable && available;
			result.Reason = GetStrictString(raw, "reason");
			result.Source = GetStrictString(raw, "source");
			result.ArtifactSha256 = GetStrictString(raw, "artifact_sha256");
			result.CandidateSetContract = GetStrictString(raw, "candidate_set_contract");

			// An unavailable reference is an ordinary fail-closed outcome. Never surface the
			// worker's developer reason in the live UI and do not treat it as an error.
			if (!claimedAvailable)
				return result;

			var candidateSetComplete = false;
			var behaviorReferenceEligible = false;
			var candidateGenerationAllowed = false;
			var tacticalScoreOverrideAllowed = false;
			var automaticActionAllowed = false;
			var livePolicyEligible = false;
			var rlTrainingEligible = false;
			var optimalityVerified = false;
			var outcomeUsedAsActionOptimality = false;
			var candidateCount = 0;
			var rankedCandidateCount = 0;
			var displayedReferenceCount = 0;
			var referenceItems = new List<IDictionary<string, object>>();
			var legalActions = new Dictionary<string, AdvisorHdtRootAction>(StringComparer.Ordinal);
			var valid = hasCanonicalAvailable && available &&
				string.Equals(result.Contract, AdvisorBehaviorReferenceSet.ContractId,
					StringComparison.Ordinal) &&
				string.Equals(rawStatus, "available", StringComparison.Ordinal) &&
				string.Equals(result.Source, AdvisorBehaviorReferenceSet.SourceId,
					StringComparison.Ordinal) &&
				HasKey(raw, "reason") && raw["reason"] is string &&
				result.Reason.Length == 0 &&
				string.Equals(result.CandidateSetContract,
					AdvisorHdtRootCandidateSet.ContractId, StringComparison.Ordinal) &&
				string.Equals((response?.Status ?? "").Trim(),
					AdvisorProtocol.StatusPartial, StringComparison.Ordinal) &&
				IsSha256(result.ArtifactSha256) &&
				TryGetCanonicalBool(raw, "candidate_set_complete", out candidateSetComplete) &&
				candidateSetComplete &&
				TryGetCanonicalCount(raw, "candidate_count", out candidateCount) &&
				TryGetCanonicalCount(raw, "ranked_candidate_count", out rankedCandidateCount) &&
				TryGetCanonicalCount(raw, "displayed_reference_count", out displayedReferenceCount) &&
				TryGetObjectItems(raw, "references", out referenceItems) &&
				TryGetCanonicalBool(raw, "behavior_reference_eligible",
					out behaviorReferenceEligible) && behaviorReferenceEligible &&
				TryGetCanonicalBool(raw, "candidate_generation_allowed",
					out candidateGenerationAllowed) && !candidateGenerationAllowed &&
				TryGetCanonicalBool(raw, "tactical_score_override_allowed",
					out tacticalScoreOverrideAllowed) && !tacticalScoreOverrideAllowed &&
				TryGetCanonicalBool(raw, "automatic_action_allowed",
					out automaticActionAllowed) && !automaticActionAllowed &&
				TryGetCanonicalBool(raw, "live_policy_eligible",
					out livePolicyEligible) && !livePolicyEligible &&
				TryGetCanonicalBool(raw, "rl_training_eligible",
					out rlTrainingEligible) && !rlTrainingEligible &&
				TryGetCanonicalBool(raw, "optimality_verified",
					out optimalityVerified) && !optimalityVerified &&
				TryGetCanonicalBool(raw, "outcome_used_as_action_optimality",
					out outcomeUsedAsActionOptimality) && !outcomeUsedAsActionOptimality &&
				TryBuildOriginatingBehaviorCandidateMap(
					originatingRequest, response, out legalActions) &&
				ValidateBehaviorReferenceRanker(rawDecisionRanker, result.ArtifactSha256);

			result.CandidateSetComplete = valid && candidateSetComplete;
			result.CandidateCount = valid ? candidateCount : 0;
			result.RankedCandidateCount = valid ? rankedCandidateCount : 0;
			result.DisplayedReferenceCount = valid ? displayedReferenceCount : 0;
			result.BehaviorReferenceEligible = valid && behaviorReferenceEligible;
			result.CandidateGenerationAllowed = valid && candidateGenerationAllowed;
			result.TacticalScoreOverrideAllowed = valid && tacticalScoreOverrideAllowed;
			result.AutomaticActionAllowed = valid && automaticActionAllowed;
			result.LivePolicyEligible = valid && livePolicyEligible;
			result.RlTrainingEligible = valid && rlTrainingEligible;
			result.OptimalityVerified = valid && optimalityVerified;
			result.OutcomeUsedAsActionOptimality = valid && outcomeUsedAsActionOptimality;

			if (valid)
			{
				var maximumDisplayed = Math.Max(
					1, originatingRequest?.Options?.MaxRecommendations ?? 1);
				valid = candidateCount == legalActions.Count &&
					rankedCandidateCount == candidateCount &&
					displayedReferenceCount == referenceItems.Count &&
					displayedReferenceCount > 0 &&
					displayedReferenceCount <= candidateCount &&
					displayedReferenceCount <= maximumDisplayed;
			}

			var seenLegalActionIds = new HashSet<string>(StringComparer.Ordinal);
			var previousProbability = Double.PositiveInfinity;
			var previousLegalActionId = "";
			for (var index = 0; valid && index < referenceItems.Count; index++)
			{
				AdvisorBehaviorReference reference;
				AdvisorHdtRootAction expectedAction;
				if (!TryParseBehaviorReference(
					referenceItems[index], index + 1, out reference) ||
					!legalActions.TryGetValue(reference.LegalActionId, out expectedAction) ||
					!BehaviorReferenceMatchesRoot(reference, expectedAction) ||
					!seenLegalActionIds.Add(reference.LegalActionId) ||
					reference.ObservedChoiceProbability > previousProbability + 0.000000000001 ||
					(Math.Abs(reference.ObservedChoiceProbability - previousProbability) <=
						0.000000000001 && index > 0 && string.CompareOrdinal(
							previousLegalActionId, reference.LegalActionId) > 0))
				{
					valid = false;
					break;
				}
				result.References.Add(reference);
				previousProbability = reference.ObservedChoiceProbability;
				previousLegalActionId = reference.LegalActionId;
			}

			if (valid && result.References.Count == displayedReferenceCount)
			{
				result.IsDisplayEligible = true;
				return result;
			}

			result.Available = false;
			result.Status = "invalid";
			result.Reason = "client_contract_rejected";
			result.CandidateSetComplete = false;
			result.CandidateCount = 0;
			result.RankedCandidateCount = 0;
			result.DisplayedReferenceCount = 0;
			result.BehaviorReferenceEligible = false;
			result.References.Clear();
			AddBehaviorReferenceWarning(response);
			return result;
		}

		private static bool TryBuildOriginatingBehaviorCandidateMap(
			AdvisorSolveRequest request,
			AdvisorSolveResponse response,
			out Dictionary<string, AdvisorHdtRootAction> legalActions)
		{
			legalActions = new Dictionary<string, AdvisorHdtRootAction>(StringComparer.Ordinal);
			var roots = request?.HdtRootCandidates;
			var state = request?.State;
			if (roots == null || state == null || response == null ||
				!string.Equals(roots.Contract, AdvisorHdtRootCandidateSet.ContractId,
					StringComparison.Ordinal) ||
				!roots.CandidateSetComplete ||
				string.IsNullOrWhiteSpace(state.StateId) ||
				!string.Equals(roots.StateId, state.StateId, StringComparison.Ordinal) ||
				!string.Equals(response.StateId, state.StateId, StringComparison.Ordinal) ||
				roots.FrameId <= 0 || roots.CollectorEpoch <= 0 || roots.FrameWatermark <= 0)
				return false;

			var suppliedCandidates = roots.Candidates ?? new List<AdvisorHdtRootCandidate>();
			var candidates = suppliedCandidates
				.Where(item => item != null).ToList();
			if (candidates.Count == 0 || candidates.Count > 512 ||
				candidates.Count != suppliedCandidates.Count ||
				candidates.Any(item => item.OptionId < 0))
				return false;

			var endTurnCount = 0;
			foreach (var candidate in candidates)
			{
				string legalActionId;
				if (!IsHdtCandidateEvidenceConsistent(candidate) ||
					!TryBuildHdtLegalActionId(candidate.Action, out legalActionId) ||
					legalActions.ContainsKey(legalActionId))
					return false;
				if (string.Equals(legalActionId, "end_turn", StringComparison.Ordinal))
					endTurnCount++;
				legalActions.Add(legalActionId, candidate.Action);
			}
			return endTurnCount == 1;
		}

		private static bool IsHdtCandidateEvidenceConsistent(
			AdvisorHdtRootCandidate candidate)
		{
			if (candidate?.Action == null)
				return false;
			var endTurn = string.Equals(
				candidate.Action.Kind, "end_turn", StringComparison.Ordinal);
			var expectedTargetEvidence = endTurn
				? "not_applicable"
				: candidate.Action.TargetEntityId.HasValue
					? "hdt_error_none" : "hdt_no_legal_target";
			var expectedPositionEvidence = candidate.Action.BoardPosition > 0
				? "core_board_slots_v1" : "not_applicable";
			return string.Equals(
				candidate.TargetEvidence, expectedTargetEvidence, StringComparison.Ordinal) &&
				string.Equals(
					candidate.PositionEvidence, expectedPositionEvidence, StringComparison.Ordinal);
		}

		private static bool TryBuildHdtLegalActionId(
			AdvisorHdtRootAction action, out string legalActionId)
		{
			legalActionId = "";
			if (action == null)
				return false;
			var kind = action.Kind ?? "";
			var hasSource = action.SourceEntityId.HasValue && action.SourceEntityId.Value > 0;
			var hasTarget = action.TargetEntityId.HasValue && action.TargetEntityId.Value > 0;
			var cardId = action.CardId ?? "";
			if (kind == "end_turn")
			{
				if (hasSource || hasTarget || action.BoardPosition != 0 || cardId.Length != 0)
					return false;
				legalActionId = "end_turn";
				return true;
			}
			if (kind != "play_card" && kind != "attack" && kind != "hero_power" &&
				kind != "location_activate")
				return false;
			if (!hasSource || string.IsNullOrWhiteSpace(cardId) || cardId.IndexOf(':') >= 0 ||
				(kind == "attack" && !hasTarget) ||
				action.BoardPosition < 0 || action.BoardPosition > 7 ||
				(kind != "play_card" && action.BoardPosition != 0))
				return false;
			legalActionId = kind + ":" + action.SourceEntityId.Value.ToString(
				CultureInfo.InvariantCulture) + ":" + (hasTarget
					? action.TargetEntityId.Value.ToString(CultureInfo.InvariantCulture)
					: "");
			if (action.BoardPosition > 0)
				legalActionId += ":position=" + action.BoardPosition.ToString(
					CultureInfo.InvariantCulture);
			return true;
		}

		private static bool TryParseBehaviorReference(
			IDictionary<string, object> raw, int expectedRank,
			out AdvisorBehaviorReference reference)
		{
			reference = null;
			int rank;
			double probability;
			bool probabilityCalibratedAsWinRate;
			bool optimalityVerified;
			var legalActionId = GetStrictString(raw, "legal_action_id");
			var actionRaw = GetObject(raw, "action");
			AdvisorAction action;
			if (!TryGetCanonicalCount(raw, "rank", out rank) || rank != expectedRank ||
				string.IsNullOrWhiteSpace(legalActionId) ||
				!TryGetCanonicalProbability(raw, "observed_choice_probability", out probability) ||
				!TryGetCanonicalBool(raw, "probability_calibrated_as_win_rate",
					out probabilityCalibratedAsWinRate) || probabilityCalibratedAsWinRate ||
				!TryGetCanonicalBool(raw, "optimality_verified", out optimalityVerified) ||
				optimalityVerified || !TryParseStrictBehaviorAction(actionRaw, out action))
				return false;
			reference = new AdvisorBehaviorReference
			{
				Rank = rank,
				LegalActionId = legalActionId,
				Action = action,
				ObservedChoiceProbability = probability,
				ProbabilityCalibratedAsWinRate = probabilityCalibratedAsWinRate,
				OptimalityVerified = optimalityVerified
			};
			return true;
		}

		private static bool TryParseStrictBehaviorAction(
			IDictionary<string, object> raw, out AdvisorAction action)
		{
			action = null;
			int index;
			string kind;
			string sourceEntityId;
			string targetEntityId;
			int? boardPosition;
			var actionId = GetStrictString(raw, "action_id");
			if (raw == null || !HasKey(raw, "kind") || !HasKey(raw, "type") ||
				!HasKey(raw, "source_entity_id") || !HasKey(raw, "target_entity_id") ||
				!HasKey(raw, "card_id") || !HasKey(raw, "text") ||
				!TryGetCanonicalCount(raw, "index", out index) || index != 1 ||
				!TryGetCanonicalActionKind(raw, out kind) ||
				!TryGetCanonicalActionEntityId(raw, "source_entity_id", out sourceEntityId) ||
				!TryGetCanonicalActionEntityId(raw, "target_entity_id", out targetEntityId) ||
				!TryGetCanonicalBoardPosition(raw, kind, out boardPosition) ||
				!IsCanonicalActionIdentity(raw, actionId) ||
				!(raw["card_id"] is string) || !(raw["text"] is string))
				return false;
			var parsed = ParseActions(new[] { raw });
			if (parsed.Count != 1 || !parsed[0].HasCanonicalIndex ||
				!parsed[0].HasCanonicalActionId)
				return false;
			action = parsed[0];
			return true;
		}

		private static bool BehaviorReferenceMatchesRoot(
			AdvisorBehaviorReference reference, AdvisorHdtRootAction root)
		{
			if (reference?.Action == null || root == null)
				return false;
			string expectedLegalActionId;
			if (!TryBuildHdtLegalActionId(root, out expectedLegalActionId) ||
				!string.Equals(reference.LegalActionId, expectedLegalActionId,
					StringComparison.Ordinal))
				return false;
			var expectedActionId = expectedLegalActionId == "end_turn"
				? "end_turn::" : expectedLegalActionId;
			return reference.Action.Index == 1 &&
				reference.Action.HasCanonicalIndex && reference.Action.HasCanonicalActionId &&
				string.Equals(reference.Action.ActionId, expectedActionId,
					StringComparison.Ordinal) &&
				string.Equals(reference.Action.Type, root.Kind, StringComparison.Ordinal) &&
				reference.Action.SourceEntityId == root.SourceEntityId &&
				reference.Action.TargetEntityId == root.TargetEntityId &&
				reference.Action.BoardPosition == (root.BoardPosition > 0
					? (int?)root.BoardPosition : null) &&
				string.Equals(reference.Action.CardId, root.CardId,
					StringComparison.Ordinal);
		}

		private static bool ValidateBehaviorReferenceRanker(
			IDictionary<string, object> raw, string artifactSha256)
		{
			bool orderingApplied;
			bool localActionsOnly;
			bool searchOrderingOnly;
			bool candidateGenerationAllowed;
			bool scoreOverrideAllowed;
			bool livePolicyEligible;
			bool rlTrainingEligible;
			bool optimalityVerified;
			int orderingAttemptCount;
			var status = GetStrictString(raw, "status");
			var statusValid = status == "applied" || status == "available_not_applicable";
			return statusValid &&
				string.Equals(GetStrictString(raw, "artifact_sha256"), artifactSha256,
					StringComparison.OrdinalIgnoreCase) &&
				TryGetCanonicalCount(raw, "ordering_attempt_count", out orderingAttemptCount) &&
				TryGetCanonicalBool(raw, "ordering_applied", out orderingApplied) &&
				TryGetCanonicalBool(raw, "local_actions_only", out localActionsOnly) &&
				localActionsOnly &&
				TryGetCanonicalBool(raw, "search_ordering_only", out searchOrderingOnly) &&
				searchOrderingOnly &&
				TryGetCanonicalBool(raw, "candidate_generation_allowed",
					out candidateGenerationAllowed) && !candidateGenerationAllowed &&
				TryGetCanonicalBool(raw, "score_override_allowed", out scoreOverrideAllowed) &&
				!scoreOverrideAllowed &&
				TryGetCanonicalBool(raw, "live_policy_eligible", out livePolicyEligible) &&
				!livePolicyEligible &&
				TryGetCanonicalBool(raw, "rl_training_eligible", out rlTrainingEligible) &&
				!rlTrainingEligible &&
				TryGetCanonicalBool(raw, "optimality_verified", out optimalityVerified) &&
				!optimalityVerified &&
				(status == "applied"
					? orderingApplied && orderingAttemptCount > 0
					: !orderingApplied && orderingAttemptCount >= 0);
		}

		private static bool TryGetCanonicalProbability(
			IDictionary<string, object> raw, string key, out double probability)
		{
			probability = 0;
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || value == null ||
				value is string || value is bool)
				return false;
			try
			{
				probability = Convert.ToDouble(value, CultureInfo.InvariantCulture);
			}
			catch
			{
				return false;
			}
			return !Double.IsNaN(probability) && !Double.IsInfinity(probability) &&
				probability >= 0 && probability <= 1;
		}

		private static bool IsSha256(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length == 64 &&
				value.All(character =>
					(character >= '0' && character <= '9') ||
					(character >= 'a' && character <= 'f') ||
					(character >= 'A' && character <= 'F'));
		}

		private static void AddBehaviorReferenceWarning(AdvisorSolveResponse response)
		{
			const string warning = "历史打法参考未通过完整合法动作校验，已安全隐藏；战术建议不受影响。";
			if (response != null && !response.Warnings.Contains(warning))
				response.Warnings.Add(warning);
		}

		private static AdvisorRecommendation ParseRecommendation(IDictionary<string, object> raw)
		{
			var counterplay = GetObject(raw, "counterplay");
			var opponentResponse = GetObject(raw, "opponent_response");
			var reportedResponseVerified = GetBool(raw, false, "is_response_verified");
			var result = new AdvisorRecommendation
			{
				Rank = GetInt(raw, 0, "rank"),
				LineId = GetString(raw, "line_id"),
				ExpectedWinRate = GetDouble(raw, 0, "expected_win_rate", "expected_win_probability"),
				ScoreKind = GetString(raw, "score_kind", "value_kind"),
				IsProvenLethal = GetBool(raw, false, "is_proven_lethal"),
				ProofKind = GetString(raw, "proof_kind"),
				ProofScope = GetString(raw, "proof_scope"),
				WorstCaseScore = GetNullableDouble(raw, "worst_case_score"),
				ResponseScope = GetString(raw, "response_scope"),
				ResponseKind = GetString(raw, "response_kind"),
				ResponseSearchComplete = GetBool(raw, false, "response_search_complete"),
				IsResponseVerified = reportedResponseVerified,
				ResponseIsProvenLethal = GetBool(raw, false, "response_is_proven_lethal"),
				MinimaxValue = GetNullableDouble(raw, "minimax_value"),
				VerifiedPortfolioRegret = GetNullableDouble(raw, "verified_portfolio_regret"),
				AlternativeKind = GetString(raw, "alternative_kind"),
				IsSafeAfterResponse = GetNullableBool(raw, "is_safe_after_response"),
				OpponentResponseTacticalValue = opponentResponse == null
					? null
					: GetNullableDouble(opponentResponse, "tactical_value"),
				ResponseNodesExpanded = GetInt(raw, 0, "response_nodes_expanded"),
				ResponseSearchedDepth = GetInt(raw, 0, "response_searched_depth"),
				ResponseTranspositionHits = GetInt(raw, 0, "response_transposition_hits"),
				WinRateLow = GetNullableDouble(raw, "win_rate_low"),
				WinRateHigh = GetNullableDouble(raw, "win_rate_high"),
				Confidence = GetNullableDouble(raw, "confidence"),
				Visits = GetInt(raw, 0, "visits"),
				Summary = GetString(raw, "summary", "rationale"),
				Risks = GetStrings(raw, "risks"),
				ApproximateEffects = GetStrings(raw, "approximate_effects")
			};
			if (counterplay != null)
			{
				if (!result.WorstCaseScore.HasValue)
					result.WorstCaseScore = GetNullableDouble(counterplay, "worst_case_score");
				if (string.IsNullOrWhiteSpace(result.ResponseScope))
					result.ResponseScope = GetString(counterplay, "scope");
				if (!HasKey(raw, "response_search_complete"))
					result.ResponseSearchComplete = GetBool(counterplay, false, "search_complete");
				if (!HasKey(raw, "response_is_proven_lethal"))
					result.ResponseIsProvenLethal = GetBool(counterplay, false, "is_proven_lethal");
				if (result.ResponseNodesExpanded <= 0)
					result.ResponseNodesExpanded = GetInt(counterplay, 0, "nodes_expanded");
				if (result.ResponseSearchedDepth <= 0)
					result.ResponseSearchedDepth = GetInt(counterplay, 0, "searched_depth");
				if (result.ResponseTranspositionHits <= 0)
					result.ResponseTranspositionHits = GetInt(counterplay, 0, "transposition_hits");
			}
			if (string.IsNullOrWhiteSpace(result.ScoreKind))
				result.ScoreKind = "heuristic_state_value";
			var hasProofSignal = result.IsProvenLethal ||
				!string.IsNullOrWhiteSpace(result.ProofKind) ||
				!string.IsNullOrWhiteSpace(result.ProofScope);
			var hasValidProofContract = result.IsProvenLethal &&
				string.Equals(result.ProofKind, "modeled_lethal", StringComparison.Ordinal) &&
				string.Equals(result.ProofScope, "visible_generic_v2", StringComparison.Ordinal);
			if (hasProofSignal && !hasValidProofContract)
			{
				result.IsProvenLethal = false;
				result.Risks.Add("求解器返回的斩杀证明字段不一致，已按普通启发式路线显示。");
			}
			var interval = GetValues(raw, "confidence_interval").ToList();
			if (!result.WinRateLow.HasValue && interval.Count > 0)
				result.WinRateLow = AsNullableDouble(interval[0]);
			if (!result.WinRateHigh.HasValue && interval.Count > 1)
				result.WinRateHigh = AsNullableDouble(interval[1]);
			AppendActions(result.Actions, GetObjects(raw, "actions"));

			List<IDictionary<string, object>> canonicalResponseItems = null;
			List<IDictionary<string, object>> legacyResponseItems = null;
			List<IDictionary<string, object>> nestedResponseItems = null;
			var hasCanonicalResponseArray = opponentResponse != null &&
				TryGetObjectItems(opponentResponse, "actions", out canonicalResponseItems);
			var hasLegacyResponseArray = TryGetObjectItems(
				raw, "opponent_reply", out legacyResponseItems);
			var hasNestedResponseArray = counterplay != null &&
				TryGetObjectItems(counterplay, "actions", out nestedResponseItems);
			canonicalResponseItems = canonicalResponseItems ?? new List<IDictionary<string, object>>();
			legacyResponseItems = legacyResponseItems ?? new List<IDictionary<string, object>>();
			nestedResponseItems = nestedResponseItems ?? new List<IDictionary<string, object>>();
			var selectedResponseItems = hasCanonicalResponseArray
				? canonicalResponseItems
				: hasLegacyResponseArray
					? legacyResponseItems
					: nestedResponseItems;
			AppendActions(result.OpponentReply, selectedResponseItems);

			var components = GetObject(raw, "score_components") ??
				(counterplay == null ? null : GetObject(counterplay, "score_components"));
			if (components != null)
			{
				foreach (var pair in components)
				{
					var value = AsNullableDouble(pair.Value);
					if (value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value))
						result.ScoreComponents[pair.Key] = value.Value;
				}
			}

			var hasResponseSignal = reportedResponseVerified ||
				result.ResponseSearchComplete ||
				result.ResponseIsProvenLethal ||
				!string.IsNullOrWhiteSpace(result.ResponseScope) ||
				!string.IsNullOrWhiteSpace(result.ResponseKind) ||
				result.MinimaxValue.HasValue ||
				result.IsSafeAfterResponse.HasValue ||
				opponentResponse != null;
			if (hasResponseSignal)
			{
				var contractIssues = new List<string>();
				if (!reportedResponseVerified)
					contractIssues.Add("未标记为已验证");
				if (!string.Equals(
					result.ResponseScope,
					"visible_generic_turnpair_v1",
					StringComparison.Ordinal))
					contractIssues.Add("回应范围无效");
				if (!string.Equals(
					result.ResponseKind,
					"minimax_best_response",
					StringComparison.Ordinal))
					contractIssues.Add("回应类型无效");
				if (!HasKey(raw, "response_search_complete") || !result.ResponseSearchComplete)
					contractIssues.Add("回应搜索未完成");
				if (!IsFinite(result.MinimaxValue))
					contractIssues.Add("minimax 战术值缺失或无效");
				if (!result.IsSafeAfterResponse.HasValue)
					contractIssues.Add("安全字段缺失");
				else if (result.IsSafeAfterResponse.Value == result.ResponseIsProvenLethal)
					contractIssues.Add("安全字段与反杀证明冲突");
				if (opponentResponse == null || !hasCanonicalResponseArray)
					contractIssues.Add("标准对手回应动作缺失或无效");
				if (!IsFinite(result.OpponentResponseTacticalValue) ||
					!NearlyEqual(result.MinimaxValue, result.OpponentResponseTacticalValue))
					contractIssues.Add("对手回应战术值与 minimax 值不一致");

				var canonicalActions = ParseActions(canonicalResponseItems);
				if (!IsCanonicalActionSequence(canonicalActions, true))
					contractIssues.Add("标准对手回应动作标识或顺序无效");
				if (HasKey(raw, "opponent_reply") &&
					(!hasLegacyResponseArray || !ActionsEquivalent(canonicalActions, ParseActions(legacyResponseItems))))
					contractIssues.Add("标准对手回应与兼容回应动作不一致");
				if (counterplay != null)
				{
					if (HasKey(counterplay, "scope") && !string.Equals(
						GetString(counterplay, "scope"), result.ResponseScope, StringComparison.Ordinal))
						contractIssues.Add("兼容回应范围不一致");
					if (HasKey(counterplay, "search_complete") &&
						GetBool(counterplay, false, "search_complete") != result.ResponseSearchComplete)
						contractIssues.Add("兼容回应完成状态不一致");
					if (HasKey(counterplay, "is_proven_lethal") &&
						GetBool(counterplay, false, "is_proven_lethal") != result.ResponseIsProvenLethal)
						contractIssues.Add("兼容反杀证明不一致");
					if (HasKey(counterplay, "actions") &&
						(!hasNestedResponseArray || !ActionsEquivalent(canonicalActions, ParseActions(nestedResponseItems))))
						contractIssues.Add("标准对手回应与兼容嵌套动作不一致");
				}
				double componentMinimax;
				if (result.ScoreComponents.TryGetValue("minimax_value", out componentMinimax) &&
					!NearlyEqual(result.MinimaxValue, componentMinimax))
					contractIssues.Add("评分分项与 minimax 值不一致");

				result.IsResponseVerified = contractIssues.Count == 0;
				if (!result.IsResponseVerified)
				{
					result.ResponseIsProvenLethal = false;
					var detail = reportedResponseVerified
						? "求解器返回的对手回应契约不一致（" +
							string.Join("、", contractIssues.Distinct().ToArray()) +
							"），已按未验证回应显示。"
						: "对手回应搜索尚未完整验证，当前路线不能视为已验证安全。";
					if (!result.Risks.Contains(detail))
						result.Risks.Add(detail);
				}
			}
			if (result.VerifiedPortfolioRegret.HasValue &&
				(!IsFinite(result.VerifiedPortfolioRegret) || result.VerifiedPortfolioRegret.Value < -0.000001))
			{
				result.VerifiedPortfolioRegret = null;
				result.Risks.Add("求解器返回的备选差值无效，已隐藏该数值。");
			}
			var alternativeKind = (result.AlternativeKind ?? "").Trim().ToLowerInvariant();
			var knownAlternativeKinds = new HashSet<string>(StringComparer.Ordinal)
			{
				"co_optimal", "near_optimal", "best_found", "backup", "fallback"
			};
			if (!string.IsNullOrWhiteSpace(alternativeKind) &&
				!knownAlternativeKinds.Contains(alternativeKind))
			{
				result.Risks.Add("求解器返回了未知的备选方案类型，已按普通候选显示。");
				alternativeKind = "";
			}
			result.AlternativeKind = alternativeKind;
			if (string.Equals(alternativeKind, "co_optimal", StringComparison.Ordinal) &&
				(!result.VerifiedPortfolioRegret.HasValue ||
				 Math.Abs(result.VerifiedPortfolioRegret.Value) > 0.000001 ||
				 (!result.IsProvenLethal && !result.IsResponseVerified)))
			{
				result.AlternativeKind = "best_found";
				result.Risks.Add("共同最优标记缺少完整验证依据，已降级为当前已验证最佳。");
			}
			return result;
		}

		private static void ValidatePortfolioRecommendations(AdvisorSolveResponse response)
		{
			if (response == null)
				return;
			var recommendations = (response.Recommendations ??
				new List<AdvisorRecommendation>()).Where(item => item != null).ToList();
			var portfolioRecommendations = recommendations.Where(HasPortfolioSignal).ToList();
			var invalidFirstActions = new HashSet<AdvisorRecommendation>();
			var firstActionOwners = new Dictionary<string, AdvisorRecommendation>(StringComparer.Ordinal);
			var coverage = response.Coverage;
			var canCheckRootMembership = coverage != null &&
				coverage.HasRootActionCoverageContract &&
				coverage.RootActionCoverageContractValid;
			var legalIds = new HashSet<string>(
				coverage == null ? new List<string>() : coverage.LegalFirstActionIds,
				StringComparer.Ordinal);
			var generatedIds = new HashSet<string>(
				coverage == null ? new List<string>() : coverage.GeneratedFirstActionIds,
				StringComparer.Ordinal);
			var verifiedIds = new HashSet<string>(
				coverage == null ? new List<string>() : coverage.ResponseVerifiedFirstActionIds,
				StringComparer.Ordinal);

			// This response parser does not receive the originating snapshot, so it cannot
			// independently enumerate every legal Hearthstone root. It can still bind every
			// returned candidate to the worker's canonical arrays. The independent oracle
			// release gate remains responsible for detecting an omitted, unreturned legal root.
			foreach (var recommendation in portfolioRecommendations)
			{
				string firstActionId;
				if (!TryGetPortfolioFirstActionId(recommendation, out firstActionId))
				{
					invalidFirstActions.Add(recommendation);
					AddRisk(
						recommendation,
						"推荐路线的首步动作标识缺失或与动作字段不一致，已关闭多方案最优性结论。");
					continue;
				}
				if (!canCheckRootMembership || !legalIds.Contains(firstActionId) ||
					!generatedIds.Contains(firstActionId) ||
					(IsLineVerified(recommendation) && !verifiedIds.Contains(firstActionId)))
				{
					invalidFirstActions.Add(recommendation);
					AddRisk(
						recommendation,
						"推荐路线的首步不在可信的合法、已生成或已验证首步集合内，已关闭多方案最优性结论。");
				}
				AdvisorRecommendation previous;
				if (firstActionOwners.TryGetValue(firstActionId, out previous))
				{
					invalidFirstActions.Add(previous);
					invalidFirstActions.Add(recommendation);
					AddRisk(previous, "多条推荐重复使用同一个首步，已关闭多方案最优性结论。");
					AddRisk(recommendation, "多条推荐重复使用同一个首步，已关闭多方案最优性结论。");
				}
				else
				{
					firstActionOwners[firstActionId] = recommendation;
				}
			}
			if (invalidFirstActions.Count > 0)
			{
				InvalidatePortfolioContract(response);
				foreach (var recommendation in invalidFirstActions)
					recommendation.VerifiedPortfolioRegret = null;
			}

			var invalidRegrets = new HashSet<AdvisorRecommendation>();
			var comparable = portfolioRecommendations.Where(item =>
				IsLineVerified(item) && !invalidFirstActions.Contains(item)).ToList();
			var reportedRegrets = comparable.Where(
				item => item.VerifiedPortfolioRegret.HasValue).ToList();
			var hasZeroRegretAnchor = comparable.Any(item =>
				item.VerifiedPortfolioRegret.HasValue &&
				Math.Abs(item.VerifiedPortfolioRegret.Value) <= 0.000001);
			if (reportedRegrets.Count > 0 && !hasZeroRegretAnchor)
			{
				foreach (var recommendation in reportedRegrets)
				{
					invalidRegrets.Add(recommendation);
					AddRisk(
						recommendation,
						"已验证多方案缺少零差值基准，无法核对备选差值，已隐藏并降级。");
				}
			}
			if (coverage != null && coverage.PortfolioOptimalityProven &&
				reportedRegrets.Count > 0 && reportedRegrets.Count != comparable.Count)
			{
				foreach (var recommendation in comparable)
				{
					invalidRegrets.Add(recommendation);
					AddRisk(
						recommendation,
						"已证明的多方案结果混用了有差值和无差值路线，已关闭最优性结论。");
				}
			}
			if (hasZeroRegretAnchor)
			{
				var finiteValues = comparable.Where(item => IsFinite(item.MinimaxValue)).ToList();
				var bestReturnedValue = finiteValues.Count == 0
					? (double?)null
					: finiteValues.Max(item => item.MinimaxValue.Value);
				foreach (var recommendation in comparable.Where(
					item => item.VerifiedPortfolioRegret.HasValue))
				{
					var recomputedRegret = bestReturnedValue.HasValue && IsFinite(recommendation.MinimaxValue)
						? (double?)(bestReturnedValue.Value - recommendation.MinimaxValue.Value)
						: null;
					if (!recomputedRegret.HasValue ||
						!NearlyEqual(recommendation.VerifiedPortfolioRegret, recomputedRegret))
					{
						invalidRegrets.Add(recommendation);
						AddRisk(
							recommendation,
							"求解器返回的备选差值与已验证的最坏回应战术值不一致，已隐藏差值并降级。");
					}
				}
			}
			if (invalidRegrets.Count > 0)
			{
				InvalidatePortfolioContract(response);
				foreach (var recommendation in invalidRegrets)
					recommendation.VerifiedPortfolioRegret = null;
			}

			var portfolioOptimalityVerified = response.Coverage != null &&
				response.Coverage.HasRootActionCoverageContract &&
				response.Coverage.RootActionCoverageContractValid &&
				response.Coverage.RootActionCoverageComplete &&
				response.Coverage.PortfolioOptimalityProven;

			foreach (var recommendation in portfolioRecommendations)
			{
				var originalKind = recommendation.AlternativeKind ?? "";
				var lineVerified = IsLineVerified(recommendation);
				string expectedKind;
				if (!lineVerified)
				{
					expectedKind = "fallback";
					if (recommendation.VerifiedPortfolioRegret.HasValue)
					{
						recommendation.VerifiedPortfolioRegret = null;
						var hiddenDetail = "该路线尚未完成验证，已隐藏与最佳方案的差值。";
						if (!recommendation.Risks.Contains(hiddenDetail))
							recommendation.Risks.Add(hiddenDetail);
					}
				}
				else if (invalidFirstActions.Contains(recommendation) ||
					invalidRegrets.Contains(recommendation))
				{
					expectedKind = "backup";
				}
				else if (!recommendation.VerifiedPortfolioRegret.HasValue)
				{
					expectedKind = "best_found";
				}
				else if (Math.Abs(recommendation.VerifiedPortfolioRegret.Value) <= 0.000001)
				{
					expectedKind = portfolioOptimalityVerified ? "co_optimal" : "best_found";
				}
				else if (portfolioOptimalityVerified &&
					recommendation.VerifiedPortfolioRegret.Value <= 100.0)
				{
					expectedKind = "near_optimal";
				}
				else
				{
					expectedKind = "backup";
				}

				if (string.Equals(originalKind, expectedKind, StringComparison.Ordinal))
					continue;
				recommendation.AlternativeKind = expectedKind;
				var detail = string.Equals(originalKind, "co_optimal", StringComparison.Ordinal) &&
					string.Equals(expectedKind, "best_found", StringComparison.Ordinal)
					? "合法首步覆盖或完整搜索证明缺失，不能宣称共同最优；" +
						"已按当前已验证最佳显示。"
					: "备选方案分类与验证结果不一致，已按可信范围重新标记。";
				if (!recommendation.Risks.Contains(detail))
					recommendation.Risks.Add(detail);
			}
			FilterKnownCounterlethalAlternatives(response);
		}

		private static bool HasPortfolioSignal(AdvisorRecommendation recommendation)
		{
			return recommendation != null &&
				(!string.IsNullOrWhiteSpace(recommendation.AlternativeKind) ||
				 recommendation.VerifiedPortfolioRegret.HasValue);
		}

		private static bool IsLineVerified(AdvisorRecommendation recommendation)
		{
			return recommendation != null &&
				(recommendation.IsProvenLethal || recommendation.IsResponseVerified);
		}

		private static bool IsEndTurn(AdvisorAction action)
		{
			return action != null && string.Equals(
				(action.Type ?? "").Trim(), "end_turn", StringComparison.OrdinalIgnoreCase);
		}

		private static bool TryGetPortfolioFirstActionId(
			AdvisorRecommendation recommendation,
			out string firstActionId)
		{
			firstActionId = "";
			var actions = (recommendation == null ? null : recommendation.Actions) ??
				new List<AdvisorAction>();
			var present = actions.Where(item => item != null).ToList();
			if (!IsCanonicalActionSequence(present, false))
				return false;
			var ordered = present.OrderBy(item => item.Index).ToList();
			var first = ordered.FirstOrDefault(item => !IsEndTurn(item));
			if (first != null)
			{
				firstActionId = first.ActionId;
				return true;
			}
			var endTurn = ordered[0];
			if (!endTurn.HasCanonicalActionId ||
				!string.Equals(endTurn.ActionId, "end_turn::", StringComparison.Ordinal))
				return false;
			firstActionId = "end_turn";
			return true;
		}

		private static bool IsCanonicalActionSequence(
			IList<AdvisorAction> actions,
			bool allowEmpty)
		{
			if (actions == null || actions.Any(item => item == null))
				return false;
			if (actions.Count == 0)
				return allowEmpty;
			var ordered = actions.OrderBy(item => item.Index).ToList();
			if (ordered.Any(item => !item.HasCanonicalIndex || !item.HasCanonicalActionId))
				return false;
			for (var index = 0; index < ordered.Count; index++)
			{
				if (ordered[index].Index != index + 1)
					return false;
			}
			var endTurns = ordered.Where(IsEndTurn).ToList();
			return endTurns.Count <= 1 &&
				(endTurns.Count == 0 || ReferenceEquals(endTurns[0], ordered[ordered.Count - 1]));
		}

		private static void AddRisk(AdvisorRecommendation recommendation, string detail)
		{
			if (recommendation != null && !string.IsNullOrWhiteSpace(detail) &&
				!recommendation.Risks.Contains(detail))
				recommendation.Risks.Add(detail);
		}

		private static void InvalidatePortfolioContract(AdvisorSolveResponse response)
		{
			if (response?.Coverage != null)
			{
				response.Coverage.RootActionCoverageContractValid = false;
				response.Coverage.RootActionCoverageComplete = false;
				response.Coverage.PortfolioOptimalityProven = false;
			}
			var warning = "求解器返回的多方案验证信息相互矛盾，已关闭共同最优和近优结论。";
			if (response != null && !response.Warnings.Contains(warning))
				response.Warnings.Add(warning);
		}

		private static bool IsKnownSafeRecommendation(AdvisorRecommendation recommendation)
		{
			return recommendation != null &&
				(recommendation.IsProvenLethal ||
				 (recommendation.IsResponseVerified &&
				  recommendation.IsSafeAfterResponse == true &&
				  !recommendation.ResponseIsProvenLethal));
		}

		private static bool IsKnownCounterlethalRecommendation(
			AdvisorRecommendation recommendation)
		{
			return recommendation != null && !recommendation.IsProvenLethal &&
				recommendation.IsResponseVerified &&
				(recommendation.ResponseIsProvenLethal ||
				 recommendation.IsSafeAfterResponse == false);
		}

		private static void FilterKnownCounterlethalAlternatives(AdvisorSolveResponse response)
		{
			if (response == null)
				return;
			var ordered = (response.Recommendations ?? new List<AdvisorRecommendation>())
				.Where(item => item != null)
				.OrderBy(item => item.Rank <= 0 ? Int32.MaxValue : item.Rank)
				.ThenByDescending(item => item.ExpectedWinRate)
				.ToList();
			var safeExists = ordered.Any(IsKnownSafeRecommendation);
			var dangerousCount = ordered.Count(IsKnownCounterlethalRecommendation);
			if (safeExists && dangerousCount > 0)
			{
				ordered = ordered.Where(item => !IsKnownCounterlethalRecommendation(item)).ToList();
				var warning = "已有已验证安全路线，已隐藏 " + dangerousCount +
					" 条确认会遭反杀的备选路线。";
				if (!response.Warnings.Contains(warning))
					response.Warnings.Add(warning);
			}
			else if (dangerousCount > 0 && dangerousCount == ordered.Count)
			{
				ordered = ordered.Take(1).ToList();
				var warning = "当前返回的已验证路线都会遭到反杀，仅保留排序最高的一条用于风险提示。";
				if (!response.Warnings.Contains(warning))
					response.Warnings.Add(warning);
			}
			else if (dangerousCount > 0)
			{
				var warning = "部分已验证备选会遭到反杀，当前没有已验证安全路线。";
				if (!response.Warnings.Contains(warning))
					response.Warnings.Add(warning);
			}
			for (var index = 0; index < ordered.Count; index++)
				ordered[index].Rank = index + 1;
			response.Recommendations = ordered;
		}

		private static bool HasKey(IDictionary<string, object> raw, string key)
		{
			return raw != null && raw.ContainsKey(key);
		}

		private static bool IsSortedDistinctNonEmpty(IList<string> values)
		{
			if (values == null || values.Any(string.IsNullOrWhiteSpace))
				return false;
			return values.Count == values.Distinct(StringComparer.Ordinal).Count() &&
				values.SequenceEqual(values.OrderBy(item => item, StringComparer.Ordinal));
		}

		private static bool TryGetCanonicalStringArray(
			IDictionary<string, object> raw,
			string key,
			out List<string> items)
		{
			items = new List<string>();
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || value == null || value is string)
				return false;
			var sequence = value as IEnumerable;
			if (sequence == null)
				return false;
			foreach (var item in sequence)
			{
				var identifier = item as string;
				if (string.IsNullOrWhiteSpace(identifier))
				{
					items.Clear();
					return false;
				}
				items.Add(identifier);
			}
			return IsSortedDistinctNonEmpty(items);
		}

		private static bool TryGetCanonicalCount(
			IDictionary<string, object> raw,
			string key,
			out int count)
		{
			count = 0;
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || !(value is int))
				return false;
			count = (int)value;
			return count >= 0;
		}

		private static bool TryGetCanonicalBool(
			IDictionary<string, object> raw,
			string key,
			out bool result)
		{
			result = false;
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || !(value is bool))
				return false;
			result = (bool)value;
			return true;
		}

		private static string GetStrictString(IDictionary<string, object> raw, string key)
		{
			object value;
			return raw != null && raw.TryGetValue(key, out value) && value is string
				? (string)value
				: "";
		}

		private static bool TryGetCanonicalActionKind(
			IDictionary<string, object> raw,
			out string kind)
		{
			kind = "";
			object typeValue = null;
			object kindValue = null;
			var hasType = raw != null && raw.TryGetValue("type", out typeValue);
			var hasKind = raw != null && raw.TryGetValue("kind", out kindValue);
			if (!hasType && !hasKind)
				return false;
			if ((hasType && !(typeValue is string)) || (hasKind && !(kindValue is string)))
				return false;
			var typeText = hasType ? ((string)typeValue).Trim().ToLowerInvariant() : "";
			var kindText = hasKind ? ((string)kindValue).Trim().ToLowerInvariant() : "";
			if (hasType && hasKind && !string.Equals(typeText, kindText, StringComparison.Ordinal))
				return false;
			kind = hasType ? typeText : kindText;
			return kind == "play_card" || kind == "attack" ||
				kind == "hero_power" || kind == "location_activate" ||
				kind == "end_turn";
		}

		private static bool TryGetCanonicalActionEntityId(
			IDictionary<string, object> raw,
			string key,
			out string identifier)
		{
			identifier = "";
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || value == null)
				return true;
			if (value is string)
			{
				identifier = (string)value;
				return (identifier.Length == 0 || !string.IsNullOrWhiteSpace(identifier)) &&
					identifier.IndexOf(':') < 0;
			}
			if (value is int)
			{
				identifier = ((int)value).ToString(CultureInfo.InvariantCulture);
				return true;
			}
			if (value is long)
			{
				identifier = ((long)value).ToString(CultureInfo.InvariantCulture);
				return true;
			}
			return false;
		}

		private static bool TryGetCanonicalBoardPosition(
			IDictionary<string, object> raw,
			string kind,
			out int? boardPosition)
		{
			boardPosition = null;
			object value;
			if (raw == null || !raw.TryGetValue("board_position", out value))
				return true;

			long numeric;
			if (value is int)
				numeric = (int)value;
			else if (value is long)
				numeric = (long)value;
			else
				return false;

			if (!string.Equals(kind, "play_card", StringComparison.Ordinal) ||
				numeric < 1 || numeric > 7)
				return false;
			boardPosition = (int)numeric;
			return true;
		}

		private static bool IsCanonicalActionIdentity(
			IDictionary<string, object> raw,
			string actionId)
		{
			if (string.IsNullOrWhiteSpace(actionId))
				return false;
			string kind;
			string sourceEntityId;
			string targetEntityId;
			int? boardPosition;
			if (!TryGetCanonicalActionKind(raw, out kind) ||
				!TryGetCanonicalActionEntityId(raw, "source_entity_id", out sourceEntityId) ||
				!TryGetCanonicalActionEntityId(raw, "target_entity_id", out targetEntityId) ||
				!TryGetCanonicalBoardPosition(raw, kind, out boardPosition))
				return false;
			var entitiesValid = kind == "attack"
				? !string.IsNullOrWhiteSpace(sourceEntityId) &&
				  !string.IsNullOrWhiteSpace(targetEntityId)
				: kind == "play_card" || kind == "hero_power" ||
					kind == "location_activate"
					? !string.IsNullOrWhiteSpace(sourceEntityId)
					: kind == "end_turn" && string.IsNullOrEmpty(sourceEntityId) &&
					  string.IsNullOrEmpty(targetEntityId);
			var expectedActionId = kind + ":" + sourceEntityId + ":" + targetEntityId;
			if (boardPosition.HasValue)
				expectedActionId += ":position=" +
					boardPosition.Value.ToString(CultureInfo.InvariantCulture);
			return entitiesValid && string.Equals(
				actionId, expectedActionId, StringComparison.Ordinal);
		}

		private static bool TryGetObjectItems(
			IDictionary<string, object> raw,
			string key,
			out List<IDictionary<string, object>> items)
		{
			items = null;
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || value == null || value is string)
				return false;
			var sequence = value as IEnumerable;
			if (sequence == null)
				return false;
			var values = sequence.Cast<object>().ToList();
			if (values.Any(item => !(item is IDictionary<string, object>)))
				return false;
			items = values.Cast<IDictionary<string, object>>().ToList();
			return true;
		}

		private static List<AdvisorAction> ParseActions(
			IEnumerable<IDictionary<string, object>> source)
		{
			var result = new List<AdvisorAction>();
			AppendActions(result, source);
			return result;
		}

		private static bool ActionsEquivalent(
			IList<AdvisorAction> first,
			IList<AdvisorAction> second)
		{
			if (first == null || second == null || first.Count != second.Count)
				return false;
			for (var index = 0; index < first.Count; index++)
			{
				var left = first[index];
				var right = second[index];
				if (left.Index != right.Index ||
					!string.Equals(left.ActionId, right.ActionId, StringComparison.Ordinal) ||
					!string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) ||
					left.SourceEntityId != right.SourceEntityId ||
					left.TargetEntityId != right.TargetEntityId ||
					left.BoardPosition != right.BoardPosition ||
					!string.Equals(left.CardId, right.CardId, StringComparison.Ordinal))
					return false;
			}
			return true;
		}

		private static bool IsFinite(double? value)
		{
			return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value);
		}

		private static bool NearlyEqual(double? first, double? second)
		{
			if (!IsFinite(first) || !IsFinite(second))
				return false;
			var scale = Math.Max(1.0, Math.Max(Math.Abs(first.Value), Math.Abs(second.Value)));
			return Math.Abs(first.Value - second.Value) <= 0.000001 * scale;
		}

		private static void AppendActions(
			ICollection<AdvisorAction> destination,
			IEnumerable<IDictionary<string, object>> source)
		{
			foreach (var action in source ?? Enumerable.Empty<IDictionary<string, object>>())
			{
				int canonicalIndex;
				var hasCanonicalIndex = TryGetCanonicalCount(action, "index", out canonicalIndex) &&
					canonicalIndex > 0;
				var actionId = GetStrictString(action, "action_id");
				string canonicalKind;
				int? boardPosition = null;
				var hasCanonicalBoardPosition =
					TryGetCanonicalActionKind(action, out canonicalKind) &&
					TryGetCanonicalBoardPosition(action, canonicalKind, out boardPosition);
				destination.Add(new AdvisorAction
				{
					Index = hasCanonicalIndex ? canonicalIndex : destination.Count + 1,
					ActionId = actionId,
					HasCanonicalIndex = hasCanonicalIndex,
					HasCanonicalActionId = IsCanonicalActionIdentity(action, actionId),
					Type = GetString(action, "type", "kind"),
					SourceEntityId = GetNullableInt(action, "source_entity_id"),
					TargetEntityId = GetNullableInt(action, "target_entity_id"),
					BoardPosition = hasCanonicalBoardPosition ? boardPosition : null,
					CardId = GetString(action, "card_id"),
					Text = GetString(action, "text")
				});
			}
		}

		private static AdvisorCoverage ParseCoverage(IDictionary<string, object> raw)
		{
			if (raw == null)
				return new AdvisorCoverage();
			var result = new AdvisorCoverage
			{
				Exact = GetBool(raw, false, "exact"),
				ExactScope = GetString(raw, "exact_scope"),
				ScopedLethal = GetBool(raw, false, "scoped_lethal"),
				UnsupportedCount = GetInt(raw, 0, "unsupported_count"),
				PlannerModel = GetString(raw, "planner_model"),
				RulesModel = GetString(raw, "rules_model"),
				Overall = GetNullableDouble(raw, "overall"),
				CardCoverage = GetNullableDouble(raw, "card_coverage"),
				RuleCoverage = GetNullableDouble(raw, "rule_coverage"),
				ExactCardCount = GetInt(raw, 0, "exact_card_count"),
				ApproximateCardCount = GetInt(raw, 0, "approximate_card_count"),
				UnknownCardCount = GetInt(raw, 0, "unknown_card_count"),
				BehaviorPrior = ParseSearchOrderingStatus(
					GetObject(raw, "behavior_prior")),
				DecisionRanker = ParseSearchOrderingStatus(
					GetObject(raw, "decision_ranker")),
				Summary = GetString(raw, "summary")
			};
			var details = GetObject(raw, "details");
			var counterplay = details == null ? null : GetObject(details, "counterplay");
			counterplay = counterplay ?? GetObject(raw, "counterplay");
			var rootCoverage = counterplay ?? raw;
			var portfolioContractKeys = new[]
			{
				"legal_first_action_count",
				"legal_first_action_ids",
				"generated_first_action_count",
				"generated_first_action_ids",
				"response_verified_first_action_count",
				"response_verified_first_action_ids",
				"missing_first_action_ids",
				"root_action_coverage_complete",
				"portfolio_optimality_proven"
			};
			result.HasRootActionCoverageContract = portfolioContractKeys.All(
				key => HasKey(rootCoverage, key));
			if (result.HasRootActionCoverageContract)
			{
				int legalCount;
				int generatedCount;
				int verifiedCount;
				bool reportedComplete;
				bool reportedOptimalityProven;
				List<string> legalFirstActionIds;
				List<string> generatedFirstActionIds;
				List<string> responseVerifiedFirstActionIds;
				List<string> missingFirstActionIds;
				var legalCountValid = TryGetCanonicalCount(
					rootCoverage, "legal_first_action_count", out legalCount);
				var generatedCountValid = TryGetCanonicalCount(
					rootCoverage, "generated_first_action_count", out generatedCount);
				var verifiedCountValid = TryGetCanonicalCount(
					rootCoverage, "response_verified_first_action_count", out verifiedCount);
				var legalIdsValid = TryGetCanonicalStringArray(
					rootCoverage, "legal_first_action_ids", out legalFirstActionIds);
				var generatedIdsValid = TryGetCanonicalStringArray(
					rootCoverage, "generated_first_action_ids", out generatedFirstActionIds);
				var verifiedIdsValid = TryGetCanonicalStringArray(
					rootCoverage, "response_verified_first_action_ids",
					out responseVerifiedFirstActionIds);
				var missingIdsValid = TryGetCanonicalStringArray(
					rootCoverage, "missing_first_action_ids", out missingFirstActionIds);
				var completeTypeValid = TryGetCanonicalBool(
					rootCoverage, "root_action_coverage_complete", out reportedComplete);
				var optimalityTypeValid = TryGetCanonicalBool(
					rootCoverage, "portfolio_optimality_proven", out reportedOptimalityProven);
				result.LegalFirstActionCount = legalCountValid ? legalCount : 0;
				result.GeneratedFirstActionCount = generatedCountValid ? generatedCount : 0;
				result.ResponseVerifiedFirstActionCount = verifiedCountValid ? verifiedCount : 0;
				result.LegalFirstActionIds = legalFirstActionIds;
				result.GeneratedFirstActionIds = generatedFirstActionIds;
				result.ResponseVerifiedFirstActionIds = responseVerifiedFirstActionIds;
				result.MissingFirstActionIds = missingFirstActionIds;
				var legalIds = new HashSet<string>(
					result.LegalFirstActionIds, StringComparer.Ordinal);
				var generatedIds = new HashSet<string>(
					result.GeneratedFirstActionIds, StringComparer.Ordinal);
				var verifiedIds = new HashSet<string>(
					result.ResponseVerifiedFirstActionIds, StringComparer.Ordinal);
				var missingIds = new HashSet<string>(
					result.MissingFirstActionIds, StringComparer.Ordinal);
				var expectedMissingIds = new HashSet<string>(
					legalIds.Except(verifiedIds, StringComparer.Ordinal),
					StringComparer.Ordinal);
				var contractInternallyConsistent = legalCountValid && legalCount > 0 &&
					generatedCountValid && verifiedCountValid &&
					legalIdsValid && generatedIdsValid && verifiedIdsValid && missingIdsValid &&
					completeTypeValid && optimalityTypeValid &&
					legalCount == result.LegalFirstActionIds.Count &&
					generatedCount == result.GeneratedFirstActionIds.Count &&
					verifiedCount == result.ResponseVerifiedFirstActionIds.Count &&
					generatedIds.IsSubsetOf(legalIds) &&
					verifiedIds.IsSubsetOf(generatedIds) &&
					missingIds.SetEquals(expectedMissingIds);
				var derivedComplete = contractInternallyConsistent &&
					generatedCount == legalCount && verifiedCount == legalCount &&
					missingIds.Count == 0;
				var completionClaimConsistent = contractInternallyConsistent &&
					reportedComplete == derivedComplete;
				var optimalityClaimConsistent = contractInternallyConsistent &&
					(!reportedOptimalityProven || derivedComplete);
				result.RootActionCoverageContractValid = contractInternallyConsistent &&
					completionClaimConsistent && optimalityClaimConsistent;
				result.RootActionCoverageComplete = result.RootActionCoverageContractValid &&
					reportedComplete;
				result.PortfolioOptimalityProven =
					result.RootActionCoverageComplete && reportedOptimalityProven;
			}
			if (details != null)
			{
				foreach (var pair in details)
					result.Details[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "";
			}
			return result;
		}

		private static AdvisorSearchOrderingStatus ParseSearchOrderingStatus(
			IDictionary<string, object> raw)
		{
			if (raw == null)
				return new AdvisorSearchOrderingStatus();
			return new AdvisorSearchOrderingStatus
			{
				Status = GetString(raw, "status"),
				ArtifactSha256 = GetString(raw, "artifact_sha256"),
				OrderingAttemptCount = GetInt(raw, 0, "ordering_attempt_count"),
				OrderingApplied = GetBool(raw, false, "ordering_applied"),
				LocalActionsOnly = GetBool(raw, false, "local_actions_only"),
				SearchOrderingOnly = GetBool(raw, false, "search_ordering_only"),
				CandidateGenerationAllowed = GetBool(
					raw, false, "candidate_generation_allowed"),
				ScoreOverrideAllowed = GetBool(raw, false, "score_override_allowed"),
				LivePolicyEligible = GetBool(raw, false, "live_policy_eligible"),
				RlTrainingEligible = GetBool(raw, false, "rl_training_eligible"),
				OptimalityVerified = GetBool(raw, false, "optimality_verified")
			};
		}

		private static double? ReadProgress(IDictionary<string, object> root)
		{
			var direct = GetNullableDouble(root, "progress_fraction", "progress_value");
			if (direct.HasValue)
				return direct;
			var items = GetObjects(root, "progress").ToList();
			if (items.Count == 0)
				return GetBool(root, true, "is_final") ? (double?)1.0 : null;
			return GetNullableDouble(items[items.Count - 1], "fraction", "progress");
		}

		private static Dictionary<string, object> ToObjectDictionary<T>(IDictionary<string, T> source)
		{
			var result = new Dictionary<string, object>(StringComparer.Ordinal);
			if (source == null)
				return result;
			foreach (var pair in source)
				result[pair.Key] = pair.Value;
			return result;
		}

		private static IDictionary<string, object> GetObject(IDictionary<string, object> raw, string key)
		{
			object value;
			return raw != null && raw.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
		}

		private static IEnumerable<IDictionary<string, object>> GetObjects(IDictionary<string, object> raw, string key)
		{
			return GetValues(raw, key).OfType<IDictionary<string, object>>();
		}

		private static IEnumerable<object> GetValues(IDictionary<string, object> raw, string key)
		{
			object value;
			if (raw == null || !raw.TryGetValue(key, out value) || value == null || value is string)
				return Enumerable.Empty<object>();
			var sequence = value as IEnumerable;
			return sequence == null ? Enumerable.Empty<object>() : sequence.Cast<object>();
		}

		private static List<string> GetStrings(IDictionary<string, object> raw, string key)
		{
			return GetValues(raw, key).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "")
				.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
		}

		private static string GetString(IDictionary<string, object> raw, params string[] keys)
		{
			foreach (var key in keys)
			{
				object value;
				if (raw != null && raw.TryGetValue(key, out value) && value != null)
					return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
			}
			return "";
		}

		private static int GetInt(IDictionary<string, object> raw, int fallback, params string[] keys)
		{
			var value = GetValue(raw, keys);
			try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
			catch { return fallback; }
		}

		private static int? GetNullableInt(IDictionary<string, object> raw, params string[] keys)
		{
			var value = GetValue(raw, keys);
			if (value == null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
				return null;
			try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
			catch { return null; }
		}

		private static long GetLong(IDictionary<string, object> raw, long fallback, params string[] keys)
		{
			var value = GetValue(raw, keys);
			try { return value == null ? fallback : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
			catch { return fallback; }
		}

		private static double GetDouble(IDictionary<string, object> raw, double fallback, params string[] keys)
		{
			return GetNullableDouble(raw, keys) ?? fallback;
		}

		private static double? GetNullableDouble(IDictionary<string, object> raw, params string[] keys)
		{
			return AsNullableDouble(GetValue(raw, keys));
		}

		private static double? AsNullableDouble(object value)
		{
			if (value == null)
				return null;
			try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
			catch { return null; }
		}

		private static bool GetBool(IDictionary<string, object> raw, bool fallback, params string[] keys)
		{
			var value = GetValue(raw, keys);
			if (value is bool)
				return (bool)value;
			bool parsed;
			return value != null && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)
				? parsed : fallback;
		}

		private static bool? GetNullableBool(IDictionary<string, object> raw, params string[] keys)
		{
			var value = GetValue(raw, keys);
			if (value is bool)
				return (bool)value;
			bool parsed;
			return value != null && bool.TryParse(
				Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)
				? (bool?)parsed
				: null;
		}

		private static DateTime? GetDateTime(IDictionary<string, object> raw, params string[] keys)
		{
			var value = GetValue(raw, keys);
			if (value is DateTime)
				return ((DateTime)value).ToUniversalTime();
			DateTime parsed;
			return value != null && DateTime.TryParse(
				Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
				? (DateTime?)parsed : null;
		}

		private static object GetValue(IDictionary<string, object> raw, IEnumerable<string> keys)
		{
			if (raw == null)
				return null;
			foreach (var key in keys)
			{
				object value;
				if (raw.TryGetValue(key, out value))
					return value;
			}
			return null;
		}
	}
}
