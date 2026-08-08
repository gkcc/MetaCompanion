using System;
using System.Collections.Generic;

namespace MetaCompanion
{
	/// <summary>
	/// Constants shared by the HDT plugin and the local advisor worker.
	/// The wire protocol is intentionally versioned independently from the snapshot schema.
	/// </summary>
	public static class AdvisorProtocol
	{
		public const string ApiVersion = "1.0";
		public const int SnapshotSchemaVersion = 1;
		public const string TokenHeaderName = "X-Advisor-Token";

		public const string StatusOk = "ok";
		public const string StatusPartial = "partial";
		public const string StatusThinking = "thinking";
		public const string StatusCancelled = "cancelled";
		public const string StatusUnsupported = "unsupported";
		public const string StatusError = "error";
	}

	/// <summary>
	/// A point-in-time, public-information snapshot of an HDT GameV2 instance.
	/// DTOs use ordinary mutable properties so JavaScriptSerializer and tests can construct them.
	/// </summary>
	public sealed class AdvisorGameState
	{
		public int SchemaVersion { get; set; } = AdvisorProtocol.SnapshotSchemaVersion;
		public string StateId { get; set; } = "";
		public string StateHash { get; set; } = "";
		public long SnapshotSequence { get; set; }
		public DateTime CapturedAtUtc { get; set; }
		public string GameId { get; set; } = "";
		public int TurnNumber { get; set; }
		public string ActivePlayer { get; set; } = "unknown";
		public bool? IsLocalPlayerTurn { get; set; }
		public string Format { get; set; } = "";
		public string FormatType { get; set; } = "";
		public string GameMode { get; set; } = "";
		public string GameType { get; set; } = "";
		public string HdtMode { get; set; } = "";
		public bool IsRunning { get; set; }
		public bool IsMulliganDone { get; set; }
		public bool IsSpectating { get; set; }
		public int? HearthstoneBuild { get; set; }
		public string HdtVersion { get; set; } = "";
		public string EnvironmentVersion { get; set; } = "";
		public AdvisorGamePhaseState Phase { get; set; } = new AdvisorGamePhaseState();
		public AdvisorDeckState CurrentDeck { get; set; } = new AdvisorDeckState();
		public AdvisorArenaState Arena { get; set; } = new AdvisorArenaState();
		public AdvisorPlayerState Player { get; set; } = new AdvisorPlayerState();
		public AdvisorPlayerState Opponent { get; set; } = new AdvisorPlayerState();
		public AdvisorEntityState GameEntity { get; set; }
		public List<AdvisorEntityState> OtherPublicEntities { get; set; } =
			new List<AdvisorEntityState>();
		public List<AdvisorDataGap> UnknownData { get; set; } = new List<AdvisorDataGap>();
		public List<string> UnsupportedFeatures { get; set; } = new List<string>();
		public List<string> CaptureWarnings { get; set; } = new List<string>();
		public Dictionary<string, string> Metadata { get; set; } =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	public sealed class AdvisorGamePhaseState
	{
		public string Step { get; set; } = "";
		public string NextStep { get; set; } = "";
		public string State { get; set; } = "";
		public string PlayerPlayState { get; set; } = "";
		public string OpponentPlayState { get; set; } = "";
		public string MulliganState { get; set; } = "";
		public int ProposedAttackerEntityId { get; set; }
		public int ProposedDefenderEntityId { get; set; }
		public bool HasPendingChoice { get; set; }
		public bool? CanLocalPlayerAct { get; set; }
	}

	public sealed class AdvisorDeckState
	{
		public bool IsKnown { get; set; }
		public string Source { get; set; } = "";
		public string DeckId { get; set; } = "";
		public long HearthstoneDeckId { get; set; }
		public string Name { get; set; } = "";
		public string HeroCardId { get; set; } = "";
		public string HeroPowerCardId { get; set; } = "";
		public int FormatType { get; set; }
		public int DeckType { get; set; }
		public List<AdvisorDeckCard> Cards { get; set; } = new List<AdvisorDeckCard>();
	}

	public sealed class AdvisorDeckCard
	{
		public string CardId { get; set; } = "";
		public int DbfId { get; set; }
		public int Count { get; set; }
		public int PremiumType { get; set; }
		public bool IsSideboard { get; set; }
		public string SideboardOwnerCardId { get; set; } = "";
	}

	public sealed class AdvisorArenaState
	{
		public bool IsArenaMatch { get; set; }
		public int? SeasonId { get; set; }
		public int? Wins { get; set; }
		public int? Losses { get; set; }
		public int? Rating { get; set; }
		public bool PackageInferenceAttempted { get; set; }
		public string PackageAnchorCardId { get; set; } = "";
		public List<AdvisorKnownCard> InferredPackageCards { get; set; } =
			new List<AdvisorKnownCard>();
	}

	public sealed class AdvisorPlayerState
	{
		public int PlayerId { get; set; }
		public int EntityId { get; set; }
		public bool IsLocalPlayer { get; set; }
		public string Class { get; set; } = "";
		public string OriginalClass { get; set; } = "";
		public int HandCount { get; set; }
		public int DeckCount { get; set; }
		public int Fatigue { get; set; }
		public int MaxHandSize { get; set; }
		public int MaxMana { get; set; }
		public int? Corpses { get; set; }
		public bool HasCoin { get; set; }
		public AdvisorResourceState Resources { get; set; } = new AdvisorResourceState();
		public AdvisorEntityState PlayerEntity { get; set; }
		public AdvisorEntityState Hero { get; set; }
		public AdvisorEntityState HeroPower { get; set; }
		public AdvisorEntityState Weapon { get; set; }
		public List<AdvisorEntityState> Hand { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> Board { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> Deck { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> Graveyard { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> Secrets { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> SetAside { get; set; } = new List<AdvisorEntityState>();
		public List<AdvisorEntityState> RemovedFromGame { get; set; } =
			new List<AdvisorEntityState>();
		public List<AdvisorEntityState> OtherEntities { get; set; } =
			new List<AdvisorEntityState>();
		public List<AdvisorKnownCard> KnownCardsInDeck { get; set; } =
			new List<AdvisorKnownCard>();
	}

	public sealed class AdvisorResourceState
	{
		public int Total { get; set; }
		public int Used { get; set; }
		public int Temporary { get; set; }
		public int OverloadLocked { get; set; }
		public int OverloadOwed { get; set; }
		public int Available { get; set; }
		public int SpellPower { get; set; }
	}

	public sealed class AdvisorKnownCard
	{
		public string CardId { get; set; } = "";
		public int DbfId { get; set; }
		public int Count { get; set; }
		public string Source { get; set; } = "";
	}

	public sealed class AdvisorEntityState
	{
		public int EntityId { get; set; }
		public string CardId { get; set; } = "";
		public int DbfId { get; set; }
		public string Name { get; set; } = "";
		public string Zone { get; set; } = "INVALID";
		public int ZoneId { get; set; }
		public int ZonePosition { get; set; }
		public int ControllerId { get; set; }
		public string CardType { get; set; } = "INVALID";
		public int CardTypeId { get; set; }
		public int Cost { get; set; }
		public int Attack { get; set; }
		public int Health { get; set; }
		public int Damage { get; set; }
		public int Armor { get; set; }
		public int Durability { get; set; }
		public bool IsKnown { get; set; }
		public bool IsCreated { get; set; }
		public bool IsRevealed { get; set; }
		public bool IsPlayableCard { get; set; }
		public bool IsExhausted { get; set; }
		public bool IsFrozen { get; set; }
		public bool IsSilenced { get; set; }
		public bool HasTaunt { get; set; }
		public bool HasDivineShield { get; set; }
		public bool HasStealth { get; set; }
		public bool HasWindfury { get; set; }
		public bool HasMegaWindfury { get; set; }
		public bool HasRush { get; set; }
		public bool HasCharge { get; set; }
		public bool HasLifesteal { get; set; }
		public bool HasPoisonous { get; set; }
		public bool HasReborn { get; set; }
		public bool IsDormant { get; set; }
		public bool IsImmune { get; set; }
		public int CreatorEntityId { get; set; }
		public string OriginalCardId { get; set; } = "";
		public string Visibility { get; set; } = "public";
		public string CardText { get; set; } = "";
		public string EnglishText { get; set; } = "";
		public string Race { get; set; } = "";
		public string CardClass { get; set; } = "";
		public string Rarity { get; set; } = "";
		public List<string> Mechanics { get; set; } = new List<string>();
		public Dictionary<string, int> Tags { get; set; } =
			new Dictionary<string, int>(StringComparer.Ordinal);
	}

	public sealed class AdvisorDataGap
	{
		public string Code { get; set; } = "";
		public string Path { get; set; } = "";
		public string Detail { get; set; } = "";
		public int? EntityId { get; set; }
		public int Count { get; set; }
	}

	public sealed class AdvisorSolveOptions
	{
		public const int DefaultInitialMaxIterations = 6000;
		public const int DefaultMaxIterations = 20000;
		public const int DefaultInitialMaxDepth = 8;
		public const int DefaultMaxDepth = 12;

		public int MaxRecommendations { get; set; } = 3;
		public int InitialBudgetMilliseconds { get; set; } = 2500;
		public int TimeBudgetMilliseconds { get; set; } = 10000;
		public int InitialMaxIterations { get; set; } = DefaultInitialMaxIterations;
		public int MaxIterations { get; set; } = DefaultMaxIterations;
		public int InitialMaxDepth { get; set; } = DefaultInitialMaxDepth;
		public int MaxDepth { get; set; } = DefaultMaxDepth;
		public int? SearchSeed { get; set; }
		public bool AllowApproximateEffects { get; set; } = true;
		public string EnvironmentVersion { get; set; } = "";
	}

	/// <summary>
	/// A complete main-action option frame supplied by HDT and bound to one detached public
	/// snapshot. HDT proves root legality only; it does not prove that the worker can simulate
	/// every effect or that any candidate is optimal.
	/// </summary>
	public sealed class AdvisorHdtRootCandidateSet
	{
		public const string ContractId = "hdt_complete_main_action_options_v1";

		public string Contract { get; set; } = ContractId;
		public string StateId { get; set; } = "";
		public int FrameId { get; set; }
		public long CollectorEpoch { get; set; }
		public long FrameWatermark { get; set; }
		public bool CandidateSetComplete { get; set; }
		public List<AdvisorHdtRootCandidate> Candidates { get; set; } =
			new List<AdvisorHdtRootCandidate>();
	}

	public sealed class AdvisorHdtRootCandidate
	{
		public int OptionId { get; set; }
		public AdvisorHdtRootAction Action { get; set; } = new AdvisorHdtRootAction();
		public string TargetEvidence { get; set; } = "not_applicable";
		public string PositionEvidence { get; set; } = "not_applicable";
	}

	public sealed class AdvisorHdtRootAction
	{
		public string Kind { get; set; } = "";
		public int? SourceEntityId { get; set; }
		public int? TargetEntityId { get; set; }
		public string CardId { get; set; } = "";
		public int BoardPosition { get; set; }
	}

	public sealed class AdvisorSolveRequest
	{
		public string ApiVersion { get; set; } = AdvisorProtocol.ApiVersion;
		public string RequestId { get; set; } = "";
		public AdvisorGameState State { get; set; }
		public AdvisorSolveOptions Options { get; set; } = new AdvisorSolveOptions();
		public AdvisorHdtRootCandidateSet HdtRootCandidates { get; set; }
		public Dictionary<string, string> Metadata { get; set; } =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	public sealed class AdvisorCancelRequest
	{
		public string ApiVersion { get; set; } = AdvisorProtocol.ApiVersion;
		public string RequestId { get; set; } = "";
		public string StateId { get; set; } = "";
	}

	public sealed class AdvisorObservation
	{
		public string ApiVersion { get; set; } = AdvisorProtocol.ApiVersion;
		/// <summary>Either action or result.</summary>
		public string Kind { get; set; } = "";
		public string StateId { get; set; } = "";
		public string GameId { get; set; } = "";
		public DateTime ObservedAtUtc { get; set; }
		public AdvisorObservedAction Action { get; set; }
		/// <summary>
		/// Producer-side transition boundary candidates. Their presence does not imply that the
		/// action is complete, replayable, or eligible for training; metadata carries that tier.
		/// </summary>
		public AdvisorGameState PreState { get; set; }
		public AdvisorGameState PostState { get; set; }
		public string Result { get; set; } = "";
		public Dictionary<string, string> Metadata { get; set; } =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	public sealed class AdvisorObservedAction
	{
		public string Kind { get; set; } = "";
		public int? SourceEntityId { get; set; }
		public int? TargetEntityId { get; set; }
		public string CardId { get; set; } = "";
		/// <summary>Selected entry in the HDT option frame.</summary>
		public int? OptionId { get; set; }
		/// <summary>ID printed by GameState.DebugPrintOptions for the enclosing frame.</summary>
		public int? FrameId { get; set; }
		public int? SubOption { get; set; }
		public int? BoardPosition { get; set; }
		/// <summary>Opaque, privacy-safe cursor; never contains a raw PowerLog line.</summary>
		public string PowerStartWatermark { get; set; } = "";
		public string PowerEndWatermark { get; set; } = "";
		/// <summary>
		/// Complete HDT root portfolio from the same pre-state and option frame. This remains
		/// behavior evidence and is never an optimal-action label.
		/// </summary>
		public AdvisorHdtRootCandidateSet HdtRootCandidates { get; set; }
		public List<AdvisorObservedChoice> Choices { get; set; } =
			new List<AdvisorObservedChoice>();
	}

	public sealed class AdvisorObservedChoice
	{
		public int? ChoiceId { get; set; }
		public string ChoiceType { get; set; } = "";
		public int? SourceEntityId { get; set; }
		public List<int> OptionEntityIds { get; set; } = new List<int>();
		public List<int> SelectedEntityIds { get; set; } = new List<int>();
		public string Status { get; set; } = "unresolved";
	}

	public sealed class AdvisorObservationResult
	{
		public string ApiVersion { get; set; } = "";
		public string Status { get; set; } = "";
		public string Kind { get; set; } = "";
		public string StateId { get; set; } = "";
		public bool Logged { get; set; }
		public bool Duplicate { get; set; }
		public string ResultId { get; set; } = "";
		public string GameId { get; set; } = "";
		public string Result { get; set; } = "";
	}

	public sealed class AdvisorWorkerHealth
	{
		public string ApiVersion { get; set; } = "";
		public string Status { get; set; } = "";
		public string WorkerVersion { get; set; } = "";
		public string ModelVersion { get; set; } = "";
		/// <summary>Stable implementation identity such as python or rust.</summary>
		public string Backend { get; set; } = "";
		/// <summary>Highest fixed parity profile completed by this build.</summary>
		public string ParityProfile { get; set; } = "";
		public bool SupportsCounterplayTurnpair { get; set; } = true;
		public bool SupportsBehaviorSearchOrderingPrior { get; set; }
		public bool SupportsDecisionRanker { get; set; }
		public bool SupportsBehaviorReference { get; set; }
		public bool BehaviorPriorAvailable { get; set; }
		public string BehaviorPriorStatus { get; set; } = "";
		public string BehaviorPriorReason { get; set; } = "";
		public string BehaviorPriorArtifactSha256 { get; set; } = "";
		public bool DecisionRankerAvailable { get; set; }
		public string DecisionRankerStatus { get; set; } = "";
		public string DecisionRankerReason { get; set; } = "";
		public string DecisionRankerArtifactSha256 { get; set; } = "";
		/// <summary>
		/// False keeps a preview worker out of the production selection path. Legacy
		/// workers without a backend marker remain compatible.
		/// </summary>
		public bool IsProductionReady { get; set; } = true;
		public string Message { get; set; } = "";
		public bool IsReady { get; set; }
	}

	public sealed class AdvisorSolveResponse
	{
		public string ApiVersion { get; set; } = "";
		public string RequestId { get; set; } = "";
		public int SchemaVersion { get; set; } = AdvisorProtocol.SnapshotSchemaVersion;
		public string StateId { get; set; } = "";
		public string Status { get; set; } = "";
		public string Message { get; set; } = "";
		public bool IsFinal { get; set; } = true;
		public DateTime? GeneratedAtUtc { get; set; }
		public long ElapsedMilliseconds { get; set; }
		public int Iterations { get; set; }
		public double? Progress { get; set; }
		public string ModelVersion { get; set; } = "";
		public string EnvironmentVersion { get; set; } = "";
		public AdvisorCoverage Coverage { get; set; } = new AdvisorCoverage();
		public List<AdvisorRecommendation> Recommendations { get; set; } =
			new List<AdvisorRecommendation>();
		/// <summary>
		/// Optional local-player behavior-cloning fallback. This ranks only the complete
		/// HDT-supplied legal root set and never represents win rate, optimality or an
		/// instruction that may be executed automatically.
		/// </summary>
		public AdvisorBehaviorReferenceSet BehaviorReferences { get; set; } =
			new AdvisorBehaviorReferenceSet();
		public List<string> Warnings { get; set; } = new List<string>();
	}

	public sealed class AdvisorBehaviorReferenceSet
	{
		public const string ContractId = "hdt_complete_candidate_behavior_reference_v1";
		public const string SourceId = "local_observed_behavior_cloning_v1";

		public string Contract { get; set; } = "";
		public string Status { get; set; } = "";
		public bool Available { get; set; }
		public string Reason { get; set; } = "";
		public string Source { get; set; } = "";
		public string ArtifactSha256 { get; set; } = "";
		public string CandidateSetContract { get; set; } = "";
		public bool CandidateSetComplete { get; set; }
		public int CandidateCount { get; set; }
		public int RankedCandidateCount { get; set; }
		public int DisplayedReferenceCount { get; set; }
		public List<AdvisorBehaviorReference> References { get; set; } =
			new List<AdvisorBehaviorReference>();
		public bool BehaviorReferenceEligible { get; set; }
		public bool CandidateGenerationAllowed { get; set; }
		public bool TacticalScoreOverrideAllowed { get; set; }
		public bool AutomaticActionAllowed { get; set; }
		public bool LivePolicyEligible { get; set; }
		public bool RlTrainingEligible { get; set; }
		public bool OptimalityVerified { get; set; }
		public bool OutcomeUsedAsActionOptimality { get; set; }
		/// <summary>
		/// Set only after the client binds every reference back to the originating complete
		/// HDT candidate frame and validates the model and safety contracts.
		/// </summary>
		public bool IsDisplayEligible { get; internal set; }
	}

	public sealed class AdvisorBehaviorReference
	{
		public int Rank { get; set; }
		public string LegalActionId { get; set; } = "";
		public AdvisorAction Action { get; set; } = new AdvisorAction();
		/// <summary>
		/// Behavior-cloning probability of the observed player choice. It is not a match
		/// win rate and is not calibrated as one.
		/// </summary>
		public double ObservedChoiceProbability { get; set; }
		public bool ProbabilityCalibratedAsWinRate { get; set; }
		public bool OptimalityVerified { get; set; }
	}

	public sealed class AdvisorCoverage
	{
		/// <summary>True only when the worker reports a complete proof for its declared scope.</summary>
		public bool Exact { get; set; }
		public string ExactScope { get; set; } = "";
		public bool ScopedLethal { get; set; }
		public int UnsupportedCount { get; set; }
		public string PlannerModel { get; set; } = "";
		public string RulesModel { get; set; } = "";
		public double? Overall { get; set; }
		public double? CardCoverage { get; set; }
		public double? RuleCoverage { get; set; }
		public int ExactCardCount { get; set; }
		public int ApproximateCardCount { get; set; }
		public int UnknownCardCount { get; set; }
		/// <summary>Opponent-only public behavior prior search-ordering status.</summary>
		public AdvisorSearchOrderingStatus BehaviorPrior { get; set; } =
			new AdvisorSearchOrderingStatus();
		/// <summary>Local-player-only decision ranker search-ordering status.</summary>
		public AdvisorSearchOrderingStatus DecisionRanker { get; set; } =
			new AdvisorSearchOrderingStatus();
		/// <summary>Number of legal actions available at the root of the current turn.</summary>
		public int LegalFirstActionCount { get; set; }
		/// <summary>Number of legal root actions for which the worker generated a complete line.</summary>
		public int GeneratedFirstActionCount { get; set; }
		/// <summary>Number of legal root actions with a fully verified visible response.</summary>
		public int ResponseVerifiedFirstActionCount { get; set; }
		public List<string> LegalFirstActionIds { get; set; } = new List<string>();
		public List<string> GeneratedFirstActionIds { get; set; } = new List<string>();
		public List<string> ResponseVerifiedFirstActionIds { get; set; } = new List<string>();
		/// <summary>
		/// True only when every legal root action received the portfolio verification pass.
		/// </summary>
		public bool HasRootActionCoverageContract { get; set; }
		/// <summary>
		/// True only when the canonical ID arrays, counts, set relationships and boolean
		/// claims are internally consistent. Presence alone is not treated as validity.
		/// </summary>
		public bool RootActionCoverageContractValid { get; set; }
		public bool RootActionCoverageComplete { get; set; }
		/// <summary>
		/// True only for an exhaustive friendly-continuation proof. Bounded PUCT root
		/// coverage alone must leave this false.
		/// </summary>
		public bool PortfolioOptimalityProven { get; set; }
		public List<string> MissingFirstActionIds { get; set; } = new List<string>();
		public string Summary { get; set; } = "";
		public Dictionary<string, string> Details { get; set; } =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	public sealed class AdvisorSearchOrderingStatus
	{
		public string Status { get; set; } = "";
		public string ArtifactSha256 { get; set; } = "";
		public int OrderingAttemptCount { get; set; }
		public bool OrderingApplied { get; set; }
		public bool LocalActionsOnly { get; set; }
		public bool SearchOrderingOnly { get; set; }
		public bool CandidateGenerationAllowed { get; set; }
		public bool ScoreOverrideAllowed { get; set; }
		public bool LivePolicyEligible { get; set; }
		public bool RlTrainingEligible { get; set; }
		public bool OptimalityVerified { get; set; }
	}

	public sealed class AdvisorRecommendation
	{
		public int Rank { get; set; }
		public string LineId { get; set; } = "";
		/// <summary>
		/// Legacy 0..1 score. Unless ScoreKind explicitly says otherwise this is an
		/// uncalibrated heuristic state value, not an empirical match win rate.
		/// </summary>
		public double ExpectedWinRate { get; set; }
		public string ScoreKind { get; set; } = "heuristic_state_value";
		public bool IsProvenLethal { get; set; }
		public string ProofKind { get; set; } = "";
		public string ProofScope { get; set; } = "";
		/// <summary>
		/// Lowest tactical value found after an explicit visible opponent response turn.
		/// This remains an uncalibrated state value, not a match win rate.
		/// </summary>
		public double? WorstCaseScore { get; set; }
		public string ResponseScope { get; set; } = "";
		public string ResponseKind { get; set; } = "";
		public bool ResponseSearchComplete { get; set; }
		/// <summary>
		/// True only after the client validates the complete turn-pair response contract.
		/// A worker-provided true value is not trusted by itself.
		/// </summary>
		public bool IsResponseVerified { get; set; }
		public bool ResponseIsProvenLethal { get; set; }
		/// <summary>Raw minimax tactical utility; this is not a probability.</summary>
		public double? MinimaxValue { get; set; }
		/// <summary>
		/// Difference from the best fully verified first-action minimax value. Zero means
		/// co-optimal only when the response also reports complete root-action coverage.
		/// </summary>
		public double? VerifiedPortfolioRegret { get; set; }
		/// <summary>
		/// Stable portfolio label: co_optimal, near_optimal, best_found, backup or fallback.
		/// </summary>
		public string AlternativeKind { get; set; } = "";
		/// <summary>
		/// Scoped safety result reported by the worker. It is trustworthy only when
		/// IsResponseVerified is true.
		/// </summary>
		public bool? IsSafeAfterResponse { get; set; }
		public double? OpponentResponseTacticalValue { get; set; }
		public int ResponseNodesExpanded { get; set; }
		public int ResponseSearchedDepth { get; set; }
		public int ResponseTranspositionHits { get; set; }
		public double? WinRateLow { get; set; }
		public double? WinRateHigh { get; set; }
		/// <summary>Confidence in the inclusive 0..1 range.</summary>
		public double? Confidence { get; set; }
		public int Visits { get; set; }
		public string Summary { get; set; } = "";
		public List<AdvisorAction> Actions { get; set; } = new List<AdvisorAction>();
		public List<AdvisorAction> OpponentReply { get; set; } = new List<AdvisorAction>();
		public Dictionary<string, double> ScoreComponents { get; set; } =
			new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		public List<string> Risks { get; set; } = new List<string>();
		public List<string> ApproximateEffects { get; set; } = new List<string>();
	}

	public sealed class AdvisorAction
	{
		public int Index { get; set; }
		/// <summary>
		/// Canonical worker identity in kind:source:target form, with an optional
		/// :position=N suffix for board-placement actions. Root portfolio validation
		/// never trusts this field unless it also matches the action fields.
		/// </summary>
		public string ActionId { get; set; } = "";
		internal bool HasCanonicalIndex { get; set; }
		internal bool HasCanonicalActionId { get; set; }
		public string Type { get; set; } = "";
		public int? SourceEntityId { get; set; }
		public int? TargetEntityId { get; set; }
		/// <summary>One-based insertion position from the left for a minion or location play.</summary>
		public int? BoardPosition { get; set; }
		public string CardId { get; set; } = "";
		public string Text { get; set; } = "";
	}
}
