using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MetaCompanion
{
	public sealed class AdvisorCaptureOptions
	{
		public string EnvironmentVersion { get; set; } = "";
		public bool IncludeArenaPackageInference { get; set; } = true;
		public int MaximumEntityCount { get; set; } = 1000;
	}

	/// <summary>
	/// Converts HDT's mutable GameV2 graph into a detached DTO. The extractor copies every
	/// available entity tag, then derives commonly used combat/resource fields for the worker.
	/// It never calls Config.Save or any HDT mutation API.
	/// </summary>
	public sealed class AdvisorGameStateExtractor
	{
		private readonly Func<DateTime> _utcNow;
		private readonly object _sessionLock = new object();
		private long _sequence;
		private string _sessionGameKey = CreatePrivateGameAlias(null);

		public AdvisorGameStateExtractor()
			: this(() => DateTime.UtcNow)
		{
		}

		public AdvisorGameStateExtractor(Func<DateTime> utcNow)
		{
			if (utcNow == null)
				throw new ArgumentNullException(nameof(utcNow));
			_utcNow = utcNow;
		}

		/// <summary>
		/// Starts a new private game identity scope. The raw HDT GameStats ID is deliberately
		/// never copied into an advisor snapshot, and this alias remains fixed for the game.
		/// </summary>
		public void BeginGame(string gameKey = null)
		{
			lock (_sessionLock)
			{
				_sessionGameKey = CreatePrivateGameAlias(gameKey);
			}
		}

		internal string SessionGameAlias
		{
			get { return GetSessionGameKey(); }
		}

		public AdvisorGameState Capture(GameV2 game, AdvisorCaptureOptions options = null)
		{
			if (game == null)
				throw new ArgumentNullException(nameof(game));
			options = options ?? new AdvisorCaptureOptions();

			var state = new AdvisorGameState
			{
				SnapshotSequence = Interlocked.Increment(ref _sequence),
				CapturedAtUtc = EnsureUtc(_utcNow()),
				EnvironmentVersion = options.EnvironmentVersion ?? "",
				HdtVersion = typeof(GameV2).Assembly.GetName().Version?.ToString() ?? "",
				IsRunning = Safe(() => game.IsRunning, false, null, null),
				IsMulliganDone = Safe(() => game.IsMulliganDone, false, null, null),
				IsSpectating = Safe(() => game.Spectator, false, null, null),
				TurnNumber = Safe(() => game.GetTurnNumber(), 0, null, null),
				Format = Safe(() => game.CurrentFormat?.ToString() ?? "", "", null, null),
				FormatType = Safe(() => game.CurrentFormatType.ToString(), "", null, null),
				GameMode = Safe(() => game.CurrentGameMode.ToString(), "", null, null),
				GameType = Safe(() => game.CurrentGameType.ToString(), "", null, null),
				HdtMode = Safe(() => game.CurrentMode.ToString(), "", null, null)
			};

			var stats = Safe(() => (object)game.CurrentGameStats, null, state, "current_game_stats");
			var metadata = Safe(() => (object)game.MetaData, null, state, "game_metadata");
			state.GameId = GetSessionGameKey();
			state.HearthstoneBuild = AsNullableInt(GetMemberValue(metadata, "HearthstoneBuild"))
				?? AsNullableInt(GetMemberValue(stats, "HearthstoneBuild"));
			state.CurrentDeck = ExtractCurrentDeck(game, state);
			state.Arena = ExtractArena(game, stats, options, state);

			var player = Safe(() => game.Player, null, state, "player");
			var opponent = Safe(() => game.Opponent, null, state, "opponent");
			var playerEntity = Safe(() => game.PlayerEntity, null, state, "player_entity");
			var opponentEntity = Safe(() => game.OpponentEntity, null, state, "opponent_entity");
			var localPlayerId = player == null ? 0 : Safe(() => player.Id, 0, state, "player.id");
			var opponentPlayerId = opponent == null ? 0 : Safe(() => opponent.Id, 0, state, "opponent.id");

			var entities = SnapshotEntities(game, options.MaximumEntityCount, state);
			AddEntityIfMissing(entities, Safe(() => game.GameEntity, null, state, "game_entity"));
			AddEntityIfMissing(entities, playerEntity);
			AddEntityIfMissing(entities, opponentEntity);
			AddEntityIfMissing(entities, Safe(() => player?.Hero, null, state, "player.hero"));
			AddEntityIfMissing(entities, Safe(() => opponent?.Hero, null, state, "opponent.hero"));
			var extracted = new List<AdvisorEntityState>(entities.Count);
			foreach (var entity in entities)
			{
				var controller = ReadEntityTag(entity, "CONTROLLER");
				var isLocal = localPlayerId != 0 && controller == localPlayerId;
				var isOpponent = opponentPlayerId != 0 && controller == opponentPlayerId;
				var dto = ExtractEntity(entity, isLocal, isOpponent, state);
				if (dto != null)
					extracted.Add(dto);
			}

			state.GameEntity = FindExtracted(extracted, Safe(() => game.GameEntity?.Id ?? 0, 0, state, "game_entity.id"));
			state.Player = BuildPlayerState(player, playerEntity, extracted, true, state);
			state.Opponent = BuildPlayerState(opponent, opponentEntity, extracted, false, state);

			var knownIds = new HashSet<int>(CollectEntities(state.Player).Select(x => x.EntityId));
			knownIds.UnionWith(CollectEntities(state.Opponent).Select(x => x.EntityId));
			if (state.GameEntity != null)
				knownIds.Add(state.GameEntity.EntityId);
			state.OtherPublicEntities = extracted
				.Where(x => !knownIds.Contains(x.EntityId) && x.Visibility != "hidden")
				.OrderBy(x => x.EntityId)
				.ToList();

			state.Phase = ExtractPhase(game, state);
			DeriveActivePlayer(state);
			RecordExpectedHiddenInformation(state);
			RecordModeSupport(game, state);
			if (string.IsNullOrWhiteSpace(state.EnvironmentVersion))
			{
				state.UnknownData.Add(new AdvisorDataGap
				{
					Code = "environment_snapshot_unspecified",
					Path = "environment_version",
					Detail = "当前未提供补丁或卡池快照版本。"
				});
			}

			state.StateHash = AdvisorGameStateFingerprint.Compute(state);
			state.StateId = BuildStateId(GetGameKey(state), state.StateHash);
			return state;
		}

		private static AdvisorDeckState ExtractCurrentDeck(GameV2 game, AdvisorGameState state)
		{
			var result = new AdvisorDeckState();
			var mirrorDeck = Safe(() => (object)game.CurrentSelectedDeck, null, state, "current_selected_deck");
			if (mirrorDeck != null)
			{
				result.IsKnown = true;
				result.Source = "hdt_current_selected_deck";
				result.HearthstoneDeckId = AsLong(GetMemberValue(mirrorDeck, "Id"));
				result.DeckId = result.HearthstoneDeckId == 0
					? ""
					: result.HearthstoneDeckId.ToString(CultureInfo.InvariantCulture);
				result.Name = AsString(GetMemberValue(mirrorDeck, "Name"));
				result.HeroCardId = AsString(GetMemberValue(mirrorDeck, "Hero"));
				result.HeroPowerCardId = AsString(GetMemberValue(mirrorDeck, "HeroPower"));
				result.FormatType = AsInt(GetMemberValue(mirrorDeck, "FormatType"));
				result.DeckType = AsInt(GetMemberValue(mirrorDeck, "Type"));
				AppendMirrorCards(result.Cards, GetMemberValue(mirrorDeck, "Cards") as IEnumerable, false, "");
				AppendSideboards(result.Cards, GetMemberValue(mirrorDeck, "Sideboards") as IEnumerable);
			}

			if (result.Cards.Count == 0)
			{
				var playerCards = Safe(
					() => game.Player?.PlayerCardList?.ToList() ?? new List<Card>(),
					new List<Card>(), state, "player.player_card_list");
				result.Cards = playerCards
					.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
					.GroupBy(x => x.Id, StringComparer.Ordinal)
					.Select(g => new AdvisorDeckCard
					{
						CardId = g.Key,
						DbfId = Safe(() => g.First().DbfId, 0, null, null),
						Count = g.Sum(x => Math.Max(1, Safe(() => x.Count, 1, null, null)))
					})
					.OrderBy(x => x.CardId, StringComparer.Ordinal)
					.ToList();
				if (result.Cards.Count > 0)
				{
					result.IsKnown = true;
					result.Source = "hdt_player_card_list";
				}
			}

			if (!result.IsKnown)
			{
				state.UnknownData.Add(new AdvisorDataGap
				{
					Code = "current_deck_unavailable",
					Path = "current_deck",
					Detail = "HDT 当前未提供已选择或正在跟踪的玩家牌组。"
				});
			}
			return result;
		}

		private static void AppendMirrorCards(
			ICollection<AdvisorDeckCard> target, IEnumerable cards, bool sideboard, string ownerCardId)
		{
			if (cards == null)
				return;
			foreach (var card in cards)
			{
				if (card == null)
					continue;
				var cardId = AsString(GetMemberValue(card, "Id"));
				if (string.IsNullOrWhiteSpace(cardId))
					continue;
				target.Add(new AdvisorDeckCard
				{
					CardId = cardId,
					DbfId = LookupDbfId(cardId),
					Count = Math.Max(1, AsInt(GetMemberValue(card, "Count"))),
					PremiumType = AsInt(GetMemberValue(card, "PremiumType")),
					IsSideboard = sideboard,
					SideboardOwnerCardId = ownerCardId ?? ""
				});
			}
		}

		private static void AppendSideboards(ICollection<AdvisorDeckCard> target, IEnumerable sideboards)
		{
			if (sideboards == null)
				return;
			foreach (var entry in sideboards)
			{
				if (entry == null)
					continue;
				var owner = AsString(GetMemberValue(entry, "Key"));
				AppendMirrorCards(target, GetMemberValue(entry, "Value") as IEnumerable, true, owner);
			}
		}

		private static AdvisorArenaState ExtractArena(
			GameV2 game, object stats, AdvisorCaptureOptions options, AdvisorGameState state)
		{
			var arena = new AdvisorArenaState
			{
				IsArenaMatch = Safe(() => game.IsArenaMatch, false, state, "arena.is_match"),
				SeasonId = AsNullableInt(GetMemberValue(stats, "ArenaSeasonId")),
				Wins = AsNullableInt(GetMemberValue(stats, "ArenaWins")),
				Losses = AsNullableInt(GetMemberValue(stats, "ArenaLosses")),
				Rating = AsNullableInt(GetMemberValue(game, "ArenaRating"))
			};
			if (!arena.IsArenaMatch || !options.IncludeArenaPackageInference)
				return arena;

			arena.PackageInferenceAttempted = true;
			try
			{
				var manager = game.ArenaPackagesManager;
				var method = manager?.GetType().GetMethod(
					"GetOpponentsPackageCards", BindingFlags.Instance | BindingFlags.Public);
				var knownCards = game.Opponent?.OpponentCardList?.ToList() ?? new List<Card>();
				var tuple = method?.Invoke(manager, new object[] { knownCards });
				var anchor = GetMemberValue(tuple, "Item1") as Card;
				var packageCards = GetMemberValue(tuple, "Item2") as IEnumerable;
				arena.PackageAnchorCardId = anchor?.Id ?? "";
				arena.InferredPackageCards = ToKnownCards(packageCards, "hdt_arena_package");
			}
			catch (Exception ex)
			{
				AddCaptureWarning(state, "竞技场选牌包关联信息暂不可用。");
				LogCaptureFailure("arena.package_inference", ex);
			}
			return arena;
		}

		private static List<Entity> SnapshotEntities(
			GameV2 game, int maximumEntityCount, AdvisorGameState state)
		{
			maximumEntityCount = maximumEntityCount <= 0 ? 1000 : maximumEntityCount;
			for (var attempt = 0; attempt < 2; attempt++)
			{
				try
				{
					var result = game.Entities?.Values.Where(x => x != null).ToList()
						?? new List<Entity>();
					if (result.Count > maximumEntityCount)
					{
						AddCaptureWarning(state, "局面信息较多，本次快照已按安全上限截取。");
						result = result.OrderBy(x => x.Id).Take(maximumEntityCount).ToList();
					}
					return result;
				}
				catch (InvalidOperationException)
				{
					// Power-log updates can mutate the dictionary during an event callback. Retry once.
				}
			}
			state.UnknownData.Add(new AdvisorDataGap
			{
				Code = "entity_collection_unstable",
				Path = "entities",
				Detail = "局面正在更新，本次未能完整复制实体信息。"
			});
			return new List<Entity>();
		}

		private static AdvisorEntityState ExtractEntity(
			Entity entity, bool isLocal, bool isOpponent, AdvisorGameState state)
		{
			try
			{
				var tags = new Dictionary<string, int>(StringComparer.Ordinal);
				foreach (var pair in entity.Tags ?? new Dictionary<HearthDb.Enums.GameTag, int>())
					tags[pair.Key.ToString()] = pair.Value;

				var card = Safe(() => entity.Card, null, state, "entities[" + entity.Id + "].card");
				var zoneId = ReadTag(tags, "ZONE");
				var cardTypeId = ReadTag(tags, "CARDTYPE");
				var health = ReadTagOr(tags, "HEALTH", Safe(() => entity.Health, 0, null, null));
				if (cardTypeId == (int)HearthDb.Enums.CardType.HERO)
				{
					// Zero-valued tags are not always retained in Entity.Tags. Query HDT's
					// public entity API explicitly so temporary Attack effects can never
					// invent an unused hero attack in the worker.
					RecordHeroAttackHistoryTag(
						tags,
						cardTypeId,
						Safe(
							() => entity.GetTag(HearthDb.Enums.GameTag.NUM_ATTACKS_THIS_TURN),
							ReadTag(tags, "NUM_ATTACKS_THIS_TURN"), null, null));
				}
				var cardId = Safe(() => entity.CardId ?? "", "", state, null);
				var info = Safe(() => entity.Info, null, state, null);
				var revealed = Safe(() => info != null && info.RevealedOnHistory, false, null, null);
				var hiddenZone = isOpponent && (zoneId == 2 || zoneId == 3 || zoneId == 6 || zoneId == 7);
				var result = new AdvisorEntityState
				{
					EntityId = entity.Id,
					CardId = cardId,
					DbfId = card == null ? 0 : Safe(() => card.DbfId, 0, null, null),
					Name = card == null ? Safe(() => entity.Name ?? "", "", null, null) : Safe(() => card.Name ?? "", "", null, null),
					ZoneId = zoneId,
					Zone = EnumName(typeof(HearthDb.Enums.Zone), zoneId),
					ZonePosition = ReadTag(tags, "ZONE_POSITION"),
					ControllerId = ReadTag(tags, "CONTROLLER"),
					CardTypeId = cardTypeId,
					CardType = EnumName(typeof(HearthDb.Enums.CardType), cardTypeId),
					Cost = ReadTagOr(tags, "COST", Safe(() => entity.Cost, 0, null, null)),
					Attack = ReadTagOr(tags, "ATK", Safe(() => entity.Attack, 0, null, null)),
					Health = health,
					Damage = ReadTag(tags, "DAMAGE"),
					Armor = ReadTag(tags, "ARMOR"),
					Durability = ResolveDurability(tags, cardTypeId, health),
					IsKnown = !string.IsNullOrWhiteSpace(cardId),
					IsCreated = Safe(() => info != null && info.Created, false, null, null),
					IsRevealed = revealed || !hiddenZone,
					IsPlayableCard = Safe(() => entity.IsPlayableCard, false, null, null),
					IsExhausted = ReadTag(tags, "EXHAUSTED") != 0,
					IsFrozen = ReadTag(tags, "FROZEN") != 0,
					IsSilenced = ReadTag(tags, "SILENCED") != 0,
					HasTaunt = ReadTag(tags, "TAUNT") != 0,
					HasDivineShield = ReadTag(tags, "DIVINE_SHIELD") != 0,
					HasStealth = ReadTag(tags, "STEALTH") != 0,
					HasWindfury = ReadTag(tags, "WINDFURY") != 0 || ReadTag(tags, "MEGA_WINDFURY") != 0,
					HasMegaWindfury = ReadTag(tags, "MEGA_WINDFURY") != 0,
					HasRush = ReadTag(tags, "RUSH") != 0,
					HasCharge = ReadTag(tags, "CHARGE") != 0,
					HasLifesteal = ReadTag(tags, "LIFESTEAL") != 0,
					HasPoisonous = ReadTag(tags, "POISONOUS") != 0,
					HasReborn = ReadTag(tags, "REBORN") != 0,
					IsDormant = ReadTag(tags, "DORMANT") != 0,
					IsImmune = ReadTag(tags, "IMMUNE") != 0,
					CreatorEntityId = Safe(() => info?.CreatorId ?? ReadTag(tags, "CREATOR"), ReadTag(tags, "CREATOR"), null, null),
					OriginalCardId = Safe(() => info?.OriginalCardId ?? "", "", null, null),
					Visibility = isLocal && (zoneId == 2 || zoneId == 3) ? "private_owner" :
						(hiddenZone ? "hidden" : "public"),
					CardText = card == null ? "" : Safe(() => card.Text ?? "", "", null, null),
					EnglishText = card == null ? "" : Safe(() => card.EnglishText ?? "", "", null, null),
					Race = card == null ? "" : Safe(() => card.Race ?? "", "", null, null),
					CardClass = card == null ? "" : Safe(() => card.CardClass.ToString(), "", null, null),
					Rarity = card == null ? "" : Safe(() => card.Rarity.ToString(), "", null, null),
					Mechanics = card == null
						? new List<string>()
						: Safe(() => (card.Mechanics ?? new string[0]).ToList(), new List<string>(), null, null),
					Tags = tags
				};
				if (hiddenZone)
					ScrubHiddenOpponentEntity(result);
				return result;
			}
			catch (Exception ex)
			{
				state.UnknownData.Add(new AdvisorDataGap
				{
					Code = "entity_capture_failed",
					Path = "entities",
					EntityId = Safe(() => (int?)entity.Id, null, null, null),
					Detail = "该实体读取失败，已从本次快照中忽略。"
				});
				LogCaptureFailure("entities.item", ex);
				return null;
			}
		}

		internal static void RecordHeroAttackHistoryTag(
			IDictionary<string, int> tags,
			int cardTypeId,
			int attacksUsed)
		{
			if (tags == null || cardTypeId != (int)HearthDb.Enums.CardType.HERO)
				return;
			tags["NUM_ATTACKS_THIS_TURN"] = attacksUsed;
		}

		internal static int ResolveDurability(
			IDictionary<string, int> tags, int cardTypeId, int health)
		{
			int durability;
			if (tags != null && tags.TryGetValue("DURABILITY", out durability))
				return Math.Max(0, durability);
			if (cardTypeId == (int)HearthDb.Enums.CardType.WEAPON ||
				cardTypeId == (int)HearthDb.Enums.CardType.LOCATION)
			{
				// HDT exposes weapon durability and Location charges through HEALTH/DAMAGE
				// in live entities. Preserve that public value for the solver's generic
				// durability transition instead of silently sending zero.
				return Math.Max(0, health);
			}
			return 0;
		}

		/// <summary>
		/// HDT may internally retain an identity for an opponent entity in a hidden zone. That
		/// identity is not player-visible information and must never enter the worker payload,
		/// state fingerprint, or training log. Publicly inferred cards belong in a separately
		/// labelled belief source, never in an exact entity slot.
		/// </summary>
		internal static void ScrubHiddenOpponentEntity(AdvisorEntityState entity)
		{
			if (entity == null)
				return;
			var safeTags = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var key in new[] { "ZONE", "ZONE_POSITION", "CONTROLLER" })
			{
				int value;
				if (entity.Tags != null && entity.Tags.TryGetValue(key, out value))
					safeTags[key] = value;
			}

			entity.CardId = "";
			entity.DbfId = 0;
			entity.Name = "";
			entity.CardType = "UNKNOWN";
			entity.CardTypeId = 0;
			entity.Cost = 0;
			entity.Attack = 0;
			entity.Health = 0;
			entity.Damage = 0;
			entity.Armor = 0;
			entity.Durability = 0;
			entity.IsKnown = false;
			entity.IsCreated = false;
			entity.IsRevealed = false;
			entity.IsPlayableCard = false;
			entity.IsExhausted = false;
			entity.IsFrozen = false;
			entity.IsSilenced = false;
			entity.HasTaunt = false;
			entity.HasDivineShield = false;
			entity.HasStealth = false;
			entity.HasWindfury = false;
			entity.HasMegaWindfury = false;
			entity.HasRush = false;
			entity.HasCharge = false;
			entity.HasLifesteal = false;
			entity.HasPoisonous = false;
			entity.HasReborn = false;
			entity.IsDormant = false;
			entity.IsImmune = false;
			entity.CreatorEntityId = 0;
			entity.OriginalCardId = "";
			entity.Visibility = "hidden";
			entity.CardText = "";
			entity.EnglishText = "";
			entity.Race = "";
			entity.CardClass = "";
			entity.Rarity = "";
			entity.Mechanics = new List<string>();
			entity.Tags = safeTags;
		}

		private static AdvisorPlayerState BuildPlayerState(
			Player player, Entity playerEntity, IList<AdvisorEntityState> all,
			bool isLocal, AdvisorGameState state)
		{
			if (player == null)
			{
				state.UnknownData.Add(new AdvisorDataGap
				{
					Code = isLocal ? "player_unavailable" : "opponent_unavailable",
					Path = isLocal ? "player" : "opponent",
					Detail = "HDT 尚未初始化该玩家信息。"
				});
				return new AdvisorPlayerState { IsLocalPlayer = isLocal };
			}

			var playerId = Safe(() => player.Id, 0, state, isLocal ? "player.id" : "opponent.id");
			var entityId = Safe(() => playerEntity?.Id ?? 0, 0, state, null);
			var entityDto = FindExtracted(all, entityId);
			var controlled = all.Where(x => x.ControllerId == playerId).ToList();
			var resources = ExtractResources(entityDto);
			var result = new AdvisorPlayerState
			{
				PlayerId = playerId,
				EntityId = entityId,
				IsLocalPlayer = isLocal,
				Class = Safe(() => player.CurrentClass ?? "", "", state, null),
				OriginalClass = Safe(() => player.OriginalClass ?? "", "", state, null),
				HandCount = Safe(() => player.HandCount, 0, state, null),
				DeckCount = Safe(() => player.DeckCount, 0, state, null),
				Fatigue = Safe(() => player.Fatigue, 0, state, null),
				MaxHandSize = Safe(() => player.MaxHandSize, 10, state, null),
				// HDT Player.MaxMana exposes the global rules cap (normally 10).
				// The public RESOURCES tag is the permanent crystal count for this turn.
				MaxMana = Math.Max(0, resources.Total),
				Corpses = Safe(() => player.CorpsesLeft, null, state, null),
				HasCoin = Safe(() => player.HasCoin, false, state, null),
				Resources = resources,
				PlayerEntity = entityDto,
				KnownCardsInDeck = Safe(
					() => ToKnownCards(player.KnownCardsInDeck, "hdt_known_deck"),
					new List<AdvisorKnownCard>(), state, isLocal ? "player.known_deck" : "opponent.known_deck")
			};

			result.Hero = FindExtracted(all, Safe(() => player.Hero?.Id ?? 0, 0, state, null))
				?? controlled.FirstOrDefault(x => x.CardType == "HERO");
			result.HeroPower = controlled.FirstOrDefault(x => x.CardType == "HERO_POWER" && x.Zone == "PLAY")
				?? controlled.FirstOrDefault(x => x.CardType == "HERO_POWER");
			result.Weapon = controlled.FirstOrDefault(x => x.CardType == "WEAPON" && x.Zone == "PLAY");
			result.Hand = SortZone(controlled.Where(x => x.Zone == "HAND"));
			result.Deck = SortZone(controlled.Where(x => x.Zone == "DECK"));
			result.Graveyard = SortZone(controlled.Where(x => x.Zone == "GRAVEYARD"));
			result.Secrets = SortZone(controlled.Where(x => x.Zone == "SECRET"));
			result.SetAside = SortZone(controlled.Where(x => x.Zone == "SETASIDE"));
			result.RemovedFromGame = SortZone(controlled.Where(x => x.Zone == "REMOVEDFROMGAME"));
			// Board is intentionally limited to board-slot occupants. Heroes, weapons and hero
			// powers are carried in their dedicated properties and must not be duplicated.
			result.Board = SortZone(controlled.Where(x => x.Zone == "PLAY" &&
				(x.CardType == "MINION" || x.CardType == "LOCATION")));

			var classified = new HashSet<int>(CollectEntities(result).Select(x => x.EntityId));
			result.OtherEntities = controlled
				.Where(x => !classified.Contains(x.EntityId) && x.EntityId != entityId)
				.OrderBy(x => x.EntityId)
				.ToList();
			return result;
		}

		private static AdvisorResourceState ExtractResources(AdvisorEntityState playerEntity)
		{
			if (playerEntity == null)
				return new AdvisorResourceState();
			var result = new AdvisorResourceState
			{
				Total = ReadTag(playerEntity.Tags, "RESOURCES"),
				Used = ReadTag(playerEntity.Tags, "RESOURCES_USED"),
				Temporary = ReadTag(playerEntity.Tags, "TEMP_RESOURCES"),
				OverloadLocked = ReadTag(playerEntity.Tags, "OVERLOAD_LOCKED"),
				OverloadOwed = ReadTag(playerEntity.Tags, "OVERLOAD_OWED"),
				SpellPower = ReadTag(playerEntity.Tags, "SPELLPOWER")
			};
			result.Available = Math.Max(0, result.Total - result.Used + result.Temporary);
			return result;
		}

		private static AdvisorGamePhaseState ExtractPhase(GameV2 game, AdvisorGameState state)
		{
			var gameTags = state.GameEntity?.Tags;
			var playerTags = FindEntityTags(state.Player, state.Player.EntityId);
			var opponentTags = FindEntityTags(state.Opponent, state.Opponent.EntityId);
			var pendingChoice = Safe(
				() => game.Player?.OfferedEntities?.Any() == true,
				false, state, "phase.pending_choice");
			return new AdvisorGamePhaseState
			{
				Step = EnumName(typeof(HearthDb.Enums.Step), ReadTag(gameTags, "STEP")),
				NextStep = EnumName(typeof(HearthDb.Enums.Step), ReadTag(gameTags, "NEXT_STEP")),
				State = EnumName(typeof(HearthDb.Enums.State), ReadTag(gameTags, "STATE")),
				PlayerPlayState = EnumName(typeof(HearthDb.Enums.PlayState), ReadTag(playerTags, "PLAYSTATE")),
				OpponentPlayState = EnumName(typeof(HearthDb.Enums.PlayState), ReadTag(opponentTags, "PLAYSTATE")),
				MulliganState = EnumName(typeof(HearthDb.Enums.Mulligan), ReadTag(playerTags, "MULLIGAN_STATE")),
				ProposedAttackerEntityId = Safe(() => game.ProposedAttacker, 0, state, null),
				ProposedDefenderEntityId = Safe(() => game.ProposedDefender, 0, state, null),
				HasPendingChoice = pendingChoice
			};
		}

		private static void DeriveActivePlayer(AdvisorGameState state)
		{
			var playerTags = FindEntityTags(state.Player, state.Player.EntityId);
			var opponentTags = FindEntityTags(state.Opponent, state.Opponent.EntityId);
			var playerCurrent = ReadTag(playerTags, "CURRENT_PLAYER") != 0;
			var opponentCurrent = ReadTag(opponentTags, "CURRENT_PLAYER") != 0;
			if (playerCurrent && !opponentCurrent)
			{
				state.ActivePlayer = "player";
				state.IsLocalPlayerTurn = true;
			}
			else if (opponentCurrent && !playerCurrent)
			{
				state.ActivePlayer = "opponent";
				state.IsLocalPlayerTurn = false;
			}
			else
			{
				state.ActivePlayer = "unknown";
				state.IsLocalPlayerTurn = null;
			}
			state.Phase.CanLocalPlayerAct = state.IsLocalPlayerTurn.HasValue
				? (bool?)(state.IsLocalPlayerTurn.Value && state.IsRunning && state.IsMulliganDone)
				: null;
		}

		private static void RecordExpectedHiddenInformation(AdvisorGameState state)
		{
			var knownHand = state.Opponent.Hand.Count(x => x.IsKnown);
			var hiddenHand = Math.Max(0, state.Opponent.HandCount - knownHand);
			if (hiddenHand > 0)
				AddHiddenGap(state, "hidden_opponent_hand", "opponent.hand", hiddenHand);
			var knownDeck = state.Opponent.KnownCardsInDeck.Sum(x => x.Count);
			var hiddenDeck = Math.Max(0, state.Opponent.DeckCount - knownDeck);
			if (hiddenDeck > 0)
				AddHiddenGap(state, "hidden_opponent_deck", "opponent.deck", hiddenDeck);
			var hiddenSecrets = state.Opponent.Secrets.Count(x => !x.IsKnown);
			if (hiddenSecrets > 0)
				AddHiddenGap(state, "hidden_opponent_secrets", "opponent.secrets", hiddenSecrets);
		}

		private static void AddHiddenGap(AdvisorGameState state, string code, string path, int count)
		{
			state.UnknownData.Add(new AdvisorDataGap
			{
				Code = code,
				Path = path,
				Count = count,
				Detail = "这是预期的隐藏信息，求解器会按不确定信息处理。"
			});
		}

		private static void RecordModeSupport(GameV2 game, AdvisorGameState state)
		{
			if (Safe(() => game.IsBattlegroundsMatch, false, state, null))
				state.UnsupportedFeatures.Add("battlegrounds_rules");
			if (Safe(() => game.IsMercenariesMatch, false, state, null))
				state.UnsupportedFeatures.Add("mercenaries_rules");
			if (Safe(() => game.IsDungeonMatch == true, false, state, null))
				state.UnsupportedFeatures.Add("dungeon_run_rules");
			if (!Safe(() => game.IsTraditionalHearthstoneMatch, true, state, null) &&
				state.UnsupportedFeatures.Count == 0)
			{
				state.UnsupportedFeatures.Add("non_traditional_rules:" + state.GameType);
			}
		}

		private string GetGameKey(AdvisorGameState state)
		{
			return !string.IsNullOrWhiteSpace(state.GameId)
				? state.GameId
				: GetSessionGameKey();
		}

		private string GetSessionGameKey()
		{
			lock (_sessionLock)
				return _sessionGameKey;
		}

		private static string CreatePrivateGameAlias(string source)
		{
			var material = string.IsNullOrWhiteSpace(source)
				? Guid.NewGuid().ToString("N")
				: source.Trim();
			using (var sha = SHA256.Create())
			{
				var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
				return "g1-" + ToHex(bytes).Substring(0, 32);
			}
		}

		private static string BuildStateId(string gameKey, string stateHash)
		{
			using (var sha = SHA256.Create())
			{
				var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((gameKey ?? "") + "|" + stateHash));
				return "s1-" + ToHex(bytes).Substring(0, 32);
			}
		}

		private static AdvisorEntityState FindExtracted(IEnumerable<AdvisorEntityState> entities, int id)
		{
			return id == 0 ? null : entities.FirstOrDefault(x => x.EntityId == id);
		}

		private static void AddEntityIfMissing(ICollection<Entity> entities, Entity entity)
		{
			if (entity == null || entities == null)
				return;
			if (!entities.Any(x => x != null && x.Id == entity.Id))
				entities.Add(entity);
		}

		private static Dictionary<string, int> FindEntityTags(AdvisorPlayerState player, int id)
		{
			var entity = CollectEntities(player).FirstOrDefault(x => x.EntityId == id);
			return entity?.Tags;
		}

		private static IEnumerable<AdvisorEntityState> CollectEntities(AdvisorPlayerState player)
		{
			if (player == null)
				yield break;
			var lists = new[]
			{
				player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
				player.SetAside, player.RemovedFromGame, player.OtherEntities
			};
			var seen = new HashSet<int>();
			foreach (var entity in new[] { player.PlayerEntity, player.Hero, player.HeroPower, player.Weapon })
			{
				if (entity != null && seen.Add(entity.EntityId))
					yield return entity;
			}
			foreach (var list in lists)
			{
				foreach (var entity in list ?? new List<AdvisorEntityState>())
				{
					if (entity != null && seen.Add(entity.EntityId))
						yield return entity;
				}
			}
		}

		private static List<AdvisorEntityState> SortZone(IEnumerable<AdvisorEntityState> source)
		{
			return source.OrderBy(x => x.ZonePosition).ThenBy(x => x.EntityId).ToList();
		}

		private static List<AdvisorKnownCard> ToKnownCards(IEnumerable source, string sourceName)
		{
			if (source == null)
				return new List<AdvisorKnownCard>();
			var cards = new List<Card>();
			foreach (var value in source)
			{
				var card = value as Card;
				if (card != null && !string.IsNullOrWhiteSpace(card.Id))
					cards.Add(card);
			}
			return cards.GroupBy(x => x.Id, StringComparer.Ordinal)
				.Select(g => new AdvisorKnownCard
				{
					CardId = g.Key,
					DbfId = Safe(() => g.First().DbfId, 0, null, null),
					Count = g.Sum(x => Math.Max(1, Safe(() => x.Count, 1, null, null))),
					Source = sourceName ?? ""
				})
				.OrderBy(x => x.CardId, StringComparer.Ordinal)
				.ToList();
		}

		private static int LookupDbfId(string cardId)
		{
			try
			{
				int dbfId;
				return HearthDb.Cards.CardIdToDbfId.TryGetValue(cardId, out dbfId) ? dbfId : 0;
			}
			catch
			{
				return 0;
			}
		}

		private static int ReadEntityTag(Entity entity, string name)
		{
			if (entity?.Tags == null)
				return 0;
			foreach (var pair in entity.Tags)
			{
				if (string.Equals(pair.Key.ToString(), name, StringComparison.Ordinal))
					return pair.Value;
			}
			return 0;
		}

		private static int ReadTag(IDictionary<string, int> tags, string name)
		{
			int value;
			return tags != null && tags.TryGetValue(name, out value) ? value : 0;
		}

		private static int ReadTagOr(IDictionary<string, int> tags, string name, int fallback)
		{
			int value;
			return tags != null && tags.TryGetValue(name, out value) ? value : fallback;
		}

		private static string EnumName(Type enumType, int value)
		{
			var name = Enum.GetName(enumType, value);
			return name ?? value.ToString(CultureInfo.InvariantCulture);
		}

		private static object GetMemberValue(object instance, string name)
		{
			if (instance == null || string.IsNullOrWhiteSpace(name))
				return null;
			try
			{
				var type = instance.GetType();
				var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
				if (property != null)
					return property.GetValue(instance, null);
				var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
				return field?.GetValue(instance);
			}
			catch
			{
				return null;
			}
		}

		private static string AsString(object value)
		{
			return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
		}

		private static int AsInt(object value)
		{
			try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
			catch { return 0; }
		}

		private static int? AsNullableInt(object value)
		{
			if (value == null)
				return null;
			try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
			catch { return null; }
		}

		private static long AsLong(object value)
		{
			try { return value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
			catch { return 0L; }
		}

		private static T Safe<T>(Func<T> getter, T fallback, AdvisorGameState state, string path)
		{
			try
			{
				return getter();
			}
			catch (Exception ex)
			{
				if (state != null && !string.IsNullOrWhiteSpace(path))
				{
					AddCaptureWarning(state, "部分局面信息读取失败，已使用安全默认值。");
					LogCaptureFailure(path, ex);
				}
				return fallback;
			}
		}

		private static void AddCaptureWarning(AdvisorGameState state, string message)
		{
			if (state == null || string.IsNullOrWhiteSpace(message) ||
				state.CaptureWarnings.Contains(message))
			{
				return;
			}
			state.CaptureWarnings.Add(message);
		}

		private static void LogCaptureFailure(string path, Exception exception)
		{
			var detail = Unwrap(exception);
			Log.Debug("Advisor snapshot capture failed at " + (path ?? "unknown") + ": " + detail);
		}

		private static Exception Unwrap(Exception exception)
		{
			while (exception is TargetInvocationException && exception.InnerException != null)
				exception = exception.InnerException;
			return exception;
		}

		private static DateTime EnsureUtc(DateTime value)
		{
			if (value.Kind == DateTimeKind.Utc)
				return value;
			return value.Kind == DateTimeKind.Unspecified
				? DateTime.SpecifyKind(value, DateTimeKind.Utc)
				: value.ToUniversalTime();
		}

		internal static string ToHex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (var value in bytes)
				builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
			return builder.ToString();
		}
	}

	/// <summary>Stable fingerprint used to suppress duplicate solves and reject stale results.</summary>
	public static class AdvisorGameStateFingerprint
	{
		public static string Compute(AdvisorGameState state)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));
			var text = new StringBuilder(8192);
			Append(text, state.SchemaVersion);
			Append(text, state.TurnNumber);
			Append(text, state.ActivePlayer);
			Append(text, state.Format);
			Append(text, state.FormatType);
			Append(text, state.GameMode);
			Append(text, state.GameType);
			Append(text, state.HdtMode);
			Append(text, state.IsRunning);
			Append(text, state.IsMulliganDone);
			Append(text, state.HearthstoneBuild);
			Append(text, state.EnvironmentVersion);
			AppendPhase(text, state.Phase);
			AppendDeck(text, state.CurrentDeck);
			AppendArena(text, state.Arena);
			AppendPlayer(text, state.Player);
			AppendPlayer(text, state.Opponent);
			AppendEntity(text, state.GameEntity);
			foreach (var entity in (state.OtherPublicEntities ?? new List<AdvisorEntityState>())
				.OrderBy(x => x.EntityId))
				AppendEntity(text, entity);
			using (var sha = SHA256.Create())
				return AdvisorGameStateExtractor.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
		}

		private static void AppendPhase(StringBuilder text, AdvisorGamePhaseState phase)
		{
			if (phase == null) { Append(text, "null"); return; }
			Append(text, phase.Step); Append(text, phase.NextStep); Append(text, phase.State);
			Append(text, phase.PlayerPlayState); Append(text, phase.OpponentPlayState);
			Append(text, phase.MulliganState); Append(text, phase.ProposedAttackerEntityId);
			Append(text, phase.ProposedDefenderEntityId); Append(text, phase.HasPendingChoice);
		}

		private static void AppendDeck(StringBuilder text, AdvisorDeckState deck)
		{
			if (deck == null) { Append(text, "null"); return; }
			Append(text, deck.IsKnown); Append(text, deck.DeckId); Append(text, deck.Name);
			Append(text, deck.HeroCardId); Append(text, deck.HeroPowerCardId); Append(text, deck.FormatType);
			foreach (var card in (deck.Cards ?? new List<AdvisorDeckCard>())
				.OrderBy(x => x.IsSideboard).ThenBy(x => x.SideboardOwnerCardId, StringComparer.Ordinal)
				.ThenBy(x => x.CardId, StringComparer.Ordinal))
			{
				Append(text, card.CardId); Append(text, card.Count); Append(text, card.IsSideboard);
				Append(text, card.SideboardOwnerCardId);
			}
		}

		private static void AppendArena(StringBuilder text, AdvisorArenaState arena)
		{
			if (arena == null) { Append(text, "null"); return; }
			Append(text, arena.IsArenaMatch); Append(text, arena.SeasonId);
			Append(text, arena.Wins); Append(text, arena.Losses); Append(text, arena.Rating);
			Append(text, arena.PackageAnchorCardId);
			foreach (var card in (arena.InferredPackageCards ?? new List<AdvisorKnownCard>())
				.OrderBy(x => x.CardId, StringComparer.Ordinal))
			{
				Append(text, card.CardId); Append(text, card.Count);
			}
		}

		private static void AppendPlayer(StringBuilder text, AdvisorPlayerState player)
		{
			if (player == null) { Append(text, "null"); return; }
			Append(text, player.PlayerId); Append(text, player.EntityId); Append(text, player.Class);
			Append(text, player.OriginalClass); Append(text, player.HandCount); Append(text, player.DeckCount);
			Append(text, player.Fatigue); Append(text, player.MaxHandSize); Append(text, player.MaxMana);
			Append(text, player.Corpses); Append(text, player.HasCoin);
			if (player.Resources != null)
			{
				Append(text, player.Resources.Total); Append(text, player.Resources.Used);
				Append(text, player.Resources.Temporary); Append(text, player.Resources.OverloadLocked);
				Append(text, player.Resources.OverloadOwed); Append(text, player.Resources.Available);
				Append(text, player.Resources.SpellPower);
			}
			AppendEntity(text, player.PlayerEntity);
			AppendEntity(text, player.Hero);
			AppendEntity(text, player.HeroPower);
			AppendEntity(text, player.Weapon);
			var entities = new[]
			{
				player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
				player.SetAside, player.RemovedFromGame, player.OtherEntities
			}.Where(x => x != null).SelectMany(x => x).Where(x => x != null)
				.GroupBy(x => x.EntityId).Select(x => x.First()).OrderBy(x => x.EntityId);
			foreach (var entity in entities)
				AppendEntity(text, entity);
			foreach (var card in (player.KnownCardsInDeck ?? new List<AdvisorKnownCard>())
				.OrderBy(x => x.CardId, StringComparer.Ordinal))
			{
				Append(text, card.CardId); Append(text, card.Count);
			}
		}

		private static void AppendEntity(StringBuilder text, AdvisorEntityState entity)
		{
			if (entity == null) { Append(text, "null"); return; }
			Append(text, entity.EntityId); Append(text, entity.CardId); Append(text, entity.ZoneId);
			Append(text, entity.ZonePosition); Append(text, entity.ControllerId); Append(text, entity.CardTypeId);
			Append(text, entity.Cost); Append(text, entity.Attack); Append(text, entity.Health);
			Append(text, entity.Damage); Append(text, entity.Armor); Append(text, entity.Durability);
			foreach (var tag in (entity.Tags ?? new Dictionary<string, int>()).OrderBy(x => x.Key, StringComparer.Ordinal))
			{
				Append(text, tag.Key); Append(text, tag.Value);
			}
		}

		private static void Append(StringBuilder text, object value)
		{
			text.Append(value == null ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture));
			text.Append('\u001f');
		}
	}
}
