using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MetaCompanion
{
	/// <summary>
	/// Canonical, privacy-safe entity identity extracted from an HDT PowerLog line. The localized
	/// entity name is deliberately neither retained nor exposed.
	/// </summary>
	internal sealed class HdtPowerEntityEvidence
	{
		public int EntityId { get; set; }
		public string Zone { get; set; } = "";
		public int ZonePosition { get; set; }
		public string CardId { get; set; } = "";
		public int PlayerId { get; set; }
	}

	internal sealed class HdtPowerChoiceEvidence
	{
		public int ChoiceId { get; set; }
		public string ChoiceType { get; set; } = "";
		public int SourceEntityId { get; set; }
		public List<int> OptionEntityIds { get; set; } = new List<int>();
		public List<int> EntityIds { get; set; } = new List<int>();
		public string Status { get; set; } = "unresolved";
	}

	/// <summary>
	/// One locally submitted action, joined across DebugPrintOptions, SendOption, and the root
	/// GameState power record. This proves only action identity; it does not prove state replay.
	/// </summary>
	internal sealed class HdtPowerActionEvidence
	{
		public string PowerBlockType { get; set; } = "";
		public int FrameId { get; set; }
		public int OptionId { get; set; }
		public int SubOption { get; set; } = -1;
		public int BoardPosition { get; set; }
		public HdtPowerEntityEvidence Source { get; set; }
		public HdtPowerEntityEvidence Target { get; set; }
		/// <summary>
		/// Exact target proof from the joined SendOption/root block. A null Target is complete only
		/// when this says explicit_none; unknown means the trace did not prove target completeness.
		/// </summary>
		public string TargetBindingStatus { get; set; } =
			AdvisorBehaviorTargetBindingStatus.Unknown;
		public long PowerStartWatermark { get; set; }
		public long PowerEndWatermark { get; set; }
		public long PowerCollectorEpoch { get; set; }
		public long PowerActionOrdinal { get; set; }
		public int PowerGapCount { get; set; }
		public string ActionIdentityStatus { get; set; } = "unverified";
		public string ChoiceStatus { get; set; } = "unresolved";
		public List<HdtPowerChoiceEvidence> Choices { get; set; } =
			new List<HdtPowerChoiceEvidence>();
		public HdtPowerOptionsFrameEvidence OptionsFrame { get; set; }
	}

	internal sealed class HdtPowerOptionsFrameEvidence
	{
		public long CollectorEpoch { get; set; }
		public int FrameId { get; set; }
		public long HeaderWatermark { get; set; }
		public List<HdtPowerOptionEvidence> Options { get; set; } =
			new List<HdtPowerOptionEvidence>();
	}

	internal sealed class HdtPowerOptionEvidence
	{
		public int OptionId { get; set; }
		public string Type { get; set; } = "";
		public HdtPowerEntityEvidence Entity { get; set; }
		public string Error { get; set; } = "";
		public List<HdtPowerTargetEvidence> Targets { get; set; } =
			new List<HdtPowerTargetEvidence>();
		public List<HdtPowerSubOptionEvidence> SubOptions { get; set; } =
			new List<HdtPowerSubOptionEvidence>();
	}

	internal sealed class HdtPowerSubOptionEvidence
	{
		public int SubOptionId { get; set; }
		public HdtPowerEntityEvidence Entity { get; set; }
		public string Error { get; set; } = "";
		public List<HdtPowerTargetEvidence> Targets { get; set; } =
			new List<HdtPowerTargetEvidence>();
	}

	internal sealed class HdtPowerTargetEvidence
	{
		public int TargetId { get; set; }
		public HdtPowerEntityEvidence Entity { get; set; }
		public string Error { get; set; } = "";
	}

	internal sealed class HdtPowerTraceSummary
	{
		public long GameGeneration { get; set; }
		public long PowerCollectorEpoch { get; set; }
		public long CommittedActionCount { get; set; }
		public long RecordedActionCount { get; set; }
		public int GapCount { get; set; }
		public string TraceStatus { get; set; } = "tainted";
	}

	/// <summary>
	/// Incrementally consumes Core.Game.PowerLog without retaining raw lines. It accepts only the
	/// local GameState diagnostic stream; PowerTaskList history/replay lines are intentionally
	/// ignored because they are not proof of a local input.
	/// </summary>
	internal sealed class HdtPowerTraceCollector
	{
		private const string ExactIdentityStatus = "exact_hdt_power_v1";
		private const string ExactChoiceIdentityStatus = "exact_hdt_power_choice_v1";
		private const string ChoiceUnresolvedStatus = "choice_unresolved";
		private const int MaxChoiceEntities = 1024;
		private const int MinimumReplayFingerprintRun = 4;
		private static readonly Regex EntityPattern = new Regex(
			@"\bid=(?<id>\d+)\s+zone=(?<zone>[A-Z_]+)\s+zonePos=(?<position>-?\d+)\s+cardId=(?<card>\S*)\s+player=(?<player>\d+)\]",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private long _generation = long.MinValue;
		private object _streamIdentity;
		private int _cursor;
		private ulong _lastLineFingerprint;
		private bool _hasLastLineFingerprint;
		private readonly List<ulong> _consumedLineFingerprints = new List<ulong>();
		private long _watermark;
		private int _blockDepth;
		private OptionsFrame _optionsFrame;
		private SubmittedOption _submittedOption;
		private RootAction _openRoot;
		private RootAction _completedRoot;
		private ChoiceAccumulator _activeChoice;
		private OptionsFrame _stableOptionsFrame;
		private string _stableOptionsFrameStateId = "";
		private long _committedActionCount;
		private int _gapIncidentCount;
		private readonly HashSet<long> _recordedActionOrdinals = new HashSet<long>();
		private readonly HashSet<long> _rejectedActionOrdinals = new HashSet<long>();

		internal int Cursor
		{
			get { return _cursor; }
		}

		internal long Generation
		{
			get { return _generation; }
		}

		/// <summary>
		/// Anchors a newly started game at the current end of the shared log. Historical entries are
		/// never replayed into a new game generation.
		/// </summary>
		internal void BeginGeneration(
			long generation,
			object streamIdentity,
			int currentLineCount,
			bool cleanGameStartAnchor = true)
		{
			_generation = generation;
			_streamIdentity = streamIdentity;
			_consumedLineFingerprints.Clear();
			_committedActionCount = 0;
			_gapIncidentCount = 0;
			_recordedActionOrdinals.Clear();
			_rejectedActionOrdinals.Clear();
			Reanchor(Math.Max(0, currentLineCount));
			if (!cleanGameStartAnchor || generation <= 0 || streamIdentity == null)
				MarkGap();
		}

		internal void Disconnect()
		{
			if (_generation > 0 && _streamIdentity != null)
				MarkGap();
			_streamIdentity = null;
			_consumedLineFingerprints.Clear();
			Reanchor(0);
		}

		internal void MarkActionRecorded(long actionOrdinal)
		{
			if (actionOrdinal <= 0 || actionOrdinal > _committedActionCount ||
				_rejectedActionOrdinals.Contains(actionOrdinal))
			{
				MarkGap();
				return;
			}
			_recordedActionOrdinals.Add(actionOrdinal);
		}

		internal void MarkActionRejected(long actionOrdinal)
		{
			if (actionOrdinal <= 0 || actionOrdinal > _committedActionCount)
			{
				MarkGap();
				return;
			}
			if (_recordedActionOrdinals.Contains(actionOrdinal) ||
				!_rejectedActionOrdinals.Add(actionOrdinal))
				return;
			MarkGap();
		}

		internal HdtPowerTraceSummary GetTraceSummary()
		{
			var missingActionCount = Math.Max(
				0L,
				_committedActionCount - _recordedActionOrdinals.Count -
				_rejectedActionOrdinals.Count);
			var effectiveGapCount = (int)Math.Min(
				Int32.MaxValue,
				(long)_gapIncidentCount + missingActionCount);
			var complete = _generation > 0 && effectiveGapCount == 0 &&
				_committedActionCount == _recordedActionOrdinals.Count;
			return new HdtPowerTraceSummary
			{
				GameGeneration = _generation,
				PowerCollectorEpoch = _generation,
				CommittedActionCount = _committedActionCount,
				RecordedActionCount = _recordedActionOrdinals.Count,
				GapCount = effectiveGapCount,
				TraceStatus = complete ? "complete" : "tainted"
			};
		}

		/// <summary>
		/// Returns an option frame only after one complete HDT update has observed no appended log
		/// lines. The first public state that consumes the frame owns it; the same frame can never be
		/// rebound to a different state ID.
		/// </summary>
		internal bool TryGetStableOptionsFrame(
			long generation,
			string stateId,
			out HdtPowerOptionsFrameEvidence evidence)
		{
			evidence = null;
			if (generation <= 0 || generation != _generation || _stableOptionsFrame == null ||
				!_stableOptionsFrame.IsComplete || string.IsNullOrWhiteSpace(stateId))
			{
				return false;
			}
			if (string.IsNullOrWhiteSpace(_stableOptionsFrameStateId))
				_stableOptionsFrameStateId = stateId;
			else if (!string.Equals(
				_stableOptionsFrameStateId, stateId, StringComparison.Ordinal))
			{
				// The frame has already been consumed by another public state. Discard both
				// the published clone and its parser source so a later empty poll cannot
				// promote the same HDT frame again under a different state ID.
				_stableOptionsFrame = null;
				_optionsFrame = null;
				_stableOptionsFrameStateId = "";
				return false;
			}
			evidence = ToEvidence(_stableOptionsFrame, _generation);
			return evidence != null;
		}

		internal List<HdtPowerActionEvidence> FinalizeAtTerminal()
		{
			var result = new List<HdtPowerActionEvidence>();
			FinalizeActiveChoice();
			if (_completedRoot != null)
			{
				// No following option frame exists at terminal. The root itself is committed,
				// but choice absence cannot be proven, so preserve it only as downgraded evidence.
				_completedRoot.ChoiceStatus = "unresolved";
				FinalizeCompletedRoot(result);
			}
			if (_openRoot != null)
			{
				// A root PLAY/ATTACK was observed, but its closing watermark was lost.
				// Count the committed action and a gap without inventing an identity record.
				CommitUnrecordableAction();
				_openRoot = null;
				_submittedOption = null;
			}
			_activeChoice = null;
			return result;
		}

		internal List<HdtPowerActionEvidence> Collect(
			IList<string> powerLog,
			long generation,
			object streamIdentity)
		{
			var result = new List<HdtPowerActionEvidence>();
			if (powerLog == null || streamIdentity == null)
			{
				Disconnect();
				return result;
			}

			int count;
			try
			{
				count = powerLog.Count;
			}
			catch
			{
				Disconnect();
				return result;
			}

			if (_generation != generation)
			{
				BeginGeneration(generation, streamIdentity, count);
				return result;
			}
			if (_streamIdentity == null)
			{
				_streamIdentity = streamIdentity;
				_consumedLineFingerprints.Clear();
				Reanchor(count);
				return result;
			}
			if (!ReferenceEquals(_streamIdentity, streamIdentity))
			{
				MarkGap();
				_streamIdentity = streamIdentity;
				_consumedLineFingerprints.Clear();
				Reanchor(count);
				return result;
			}

			if (count < _cursor || !CursorStillPointsAtSamePrefix(powerLog, count))
			{
				// A shortened/replaced/rewound List<string> cannot safely be joined to the old options
				// frame. Start after the currently visible tail and wait for a new frame header.
				MarkGap();
				_consumedLineFingerprints.Clear();
				Reanchor(count);
				return result;
			}

			var hadNewLines = count > _cursor;
			var copied = new List<string>(Math.Max(0, count - _cursor));
			var copiedFingerprints = new List<ulong>(Math.Max(0, count - _cursor));
			try
			{
				for (var index = _cursor; index < count; index++)
				{
					var line = powerLog[index] ?? "";
					copied.Add(line);
					copiedFingerprints.Add(Fingerprint(line));
				}
			}
			catch
			{
				MarkGap();
				_consumedLineFingerprints.Clear();
				Reanchor(SafeCount(powerLog));
				return result;
			}

			if (ContainsConsumedPrefixReplay(copiedFingerprints))
			{
				// Some HDT watcher recoveries append the old Power.log prefix to the same
				// List<string>. Parsing any part of that batch would manufacture duplicate player
				// actions, so discard the whole batch and permanently taint this game trajectory.
				MarkGap();
				Reanchor(count);
				return result;
			}

			foreach (var line in copied)
			{
				_watermark++;
				try
				{
					ProcessLine(line, _watermark, result);
				}
				catch
				{
					MarkGap();
					ResetParserState();
				}
			}
			_consumedLineFingerprints.AddRange(copiedFingerprints);

			_cursor = count;
			if (count > 0)
			{
				try
				{
					_lastLineFingerprint = Fingerprint(powerLog[count - 1] ?? "");
					_hasLastLineFingerprint = true;
				}
				catch
				{
					MarkGap();
					_consumedLineFingerprints.Clear();
					Reanchor(SafeCount(powerLog));
					result.Clear();
				}
			}
			else
			{
				_hasLastLineFingerprint = false;
			}
			if (hadNewLines)
			{
				_stableOptionsFrame = null;
				_stableOptionsFrameStateId = "";
			}
			else if (_optionsFrame != null && _optionsFrame.IsComplete &&
				_activeChoice == null && _submittedOption == null && _openRoot == null &&
				_completedRoot == null)
			{
				_stableOptionsFrame = CloneOptionsFrame(_optionsFrame);
			}
			return result;
		}

		private bool ContainsConsumedPrefixReplay(IList<ulong> incoming)
		{
			if (_consumedLineFingerprints.Count < MinimumReplayFingerprintRun ||
				incoming == null || incoming.Count < MinimumReplayFingerprintRun)
			{
				return false;
			}
			var requiredRun = Math.Min(8, _consumedLineFingerprints.Count);
			for (var start = 0; start + requiredRun <= incoming.Count; start++)
			{
				var matched = 0;
				var maximum = Math.Min(
					_consumedLineFingerprints.Count,
					incoming.Count - start);
				while (matched < maximum &&
					incoming[start + matched] == _consumedLineFingerprints[matched])
				{
					matched++;
				}
				if (matched >= requiredRun)
					return true;
			}
			return false;
		}

		private bool CursorStillPointsAtSamePrefix(IList<string> powerLog, int count)
		{
			if (_cursor == 0)
				return true;
			// A freshly anchored generation deliberately has no fingerprint yet. Its first read
			// establishes one without replaying the already-visible prefix.
			if (!_hasLastLineFingerprint)
				return count >= _cursor;
			if (count < _cursor)
				return false;
			try
			{
				return Fingerprint(powerLog[_cursor - 1] ?? "") == _lastLineFingerprint;
			}
			catch
			{
				return false;
			}
		}

		private static int SafeCount(IList<string> powerLog)
		{
			try
			{
				return Math.Max(0, powerLog?.Count ?? 0);
			}
			catch
			{
				return 0;
			}
		}

		private void Reanchor(int count)
		{
			_cursor = Math.Max(0, count);
			_hasLastLineFingerprint = false;
			ResetParserState();
		}

		private void ResetParserState()
		{
			_blockDepth = 0;
			_optionsFrame = null;
			_submittedOption = null;
			_openRoot = null;
			_completedRoot = null;
			_activeChoice = null;
			_stableOptionsFrame = null;
			_stableOptionsFrameStateId = "";
		}

		private void MarkGap()
		{
			if (_gapIncidentCount < Int32.MaxValue)
				_gapIncidentCount++;
		}

		private void CommitUnrecordableAction()
		{
			if (_committedActionCount < Int64.MaxValue)
				_committedActionCount++;
			MarkGap();
			_rejectedActionOrdinals.Add(_committedActionCount);
		}

		private HdtPowerActionEvidence CommitEvidence(HdtPowerActionEvidence evidence)
		{
			if (_committedActionCount == Int64.MaxValue)
			{
				MarkGap();
				return evidence;
			}
			_committedActionCount++;
			evidence.PowerCollectorEpoch = _generation;
			evidence.PowerActionOrdinal = _committedActionCount;
			evidence.PowerGapCount = _gapIncidentCount;
			return evidence;
		}

		private void ProcessLine(
			string line,
			long watermark,
			ICollection<HdtPowerActionEvidence> result)
		{
			string payload;
			if (TryPayload(line, "DebugPrintOptions", out payload))
			{
				ProcessOptions(payload, watermark, result);
				return;
			}
			if (TryPayload(line, "SendOption", out payload))
			{
				ProcessSendOption(payload, watermark);
				return;
			}
			if (TryPayload(line, "DebugPrintEntityChoices", out payload))
			{
				ProcessChoiceOffered(payload);
				return;
			}
			if (TryPayload(line, "DebugPrintEntitiesChosen", out payload))
			{
				ProcessEntitiesChosen(payload);
				return;
			}
			if (TryPayload(line, "SendChoices", out payload))
			{
				ProcessLegacySendChoices(payload);
				return;
			}
			if (TryPayload(line, "DebugPrintPower", out payload))
				ProcessPower(payload, watermark, result);
		}

		private void ProcessOptions(
			string payload,
			long watermark,
			ICollection<HdtPowerActionEvidence> result)
		{
			int frameId;
			if (TryReadLeadingInteger(payload, "id=", out frameId))
			{
				FinalizeActiveChoice();
				FinalizeCompletedRoot(result);
				if (_openRoot != null)
				{
					CommitUnrecordableAction();
					_openRoot = null;
					_submittedOption = null;
				}
				else if (_blockDepth != 0)
				{
					MarkGap();
				}
				_blockDepth = 0;
				if (frameId <= 0)
				{
					MarkGap();
					_optionsFrame = null;
				}
				else
				{
					_optionsFrame = new OptionsFrame
					{
						FrameId = frameId,
						HeaderWatermark = watermark
					};
				}
				return;
			}

			if (_optionsFrame == null)
				return;

			var trimmed = payload.TrimStart();
			if (trimmed.StartsWith("option ", StringComparison.Ordinal))
			{
				OptionEntry option;
				if (!TryParseOption(trimmed, out option) ||
					_optionsFrame.Options.ContainsKey(option.OptionId))
				{
					InvalidateOptionsFrame();
					return;
				}
				_optionsFrame.Options[option.OptionId] = option;
				_optionsFrame.CurrentOption = option;
				_optionsFrame.CurrentSubOption = null;
				return;
			}
			if (trimmed.StartsWith("subOption ", StringComparison.Ordinal))
			{
				SubOptionEntry subOption;
				if (_optionsFrame.CurrentOption == null ||
					!TryParseSubOption(trimmed, out subOption) ||
					_optionsFrame.CurrentOption.SubOptions.ContainsKey(subOption.SubOptionId))
				{
					InvalidateOptionsFrame();
					return;
				}
				_optionsFrame.CurrentOption.SubOptions[subOption.SubOptionId] = subOption;
				_optionsFrame.CurrentSubOption = subOption;
				return;
			}
			if (trimmed.StartsWith("target ", StringComparison.Ordinal))
			{
				TargetEntry target;
				if (_optionsFrame.CurrentOption == null || !TryParseTarget(trimmed, out target))
				{
					InvalidateOptionsFrame();
					return;
				}
				var targets = _optionsFrame.CurrentSubOption == null
					? _optionsFrame.CurrentOption.Targets
					: _optionsFrame.CurrentSubOption.Targets;
				if (targets.ContainsKey(target.TargetId))
				{
					InvalidateOptionsFrame();
					return;
				}
				targets[target.TargetId] = target;
				return;
			}

			// DebugPrintOptions currently has only id/option/subOption/target records. Treat a
			// future unparsed record as a contract change instead of guessing around it.
			InvalidateOptionsFrame();
		}

		private void InvalidateOptionsFrame()
		{
			if (_optionsFrame == null || _optionsFrame.Invalid)
				return;
			_optionsFrame.Invalid = true;
			MarkGap();
		}

		private void ProcessSendOption(string payload, long watermark)
		{
			// A suffix may begin inside a frame immediately after a safe re-anchor. Ignore it
			// until a complete frame header is observed; this is not a committed action.
			if (_optionsFrame == null)
				return;
			var fields = ParseIntegerFields(payload);
			int optionId;
			int subOption;
			int targetId;
			int boardPosition;
			if (!_optionsFrame.IsComplete ||
				!fields.TryGetValue("selectedOption", out optionId) ||
				!fields.TryGetValue("selectedSubOption", out subOption) ||
				!fields.TryGetValue("selectedTarget", out targetId) ||
				!fields.TryGetValue("selectedPosition", out boardPosition) ||
				optionId < 0 || subOption < -1 || targetId < 0 || boardPosition < 0)
			{
				if (!_optionsFrame.Invalid)
					MarkGap();
				_submittedOption = null;
				_optionsFrame = null;
				return;
			}

			OptionEntry option;
			if (!_optionsFrame.Options.TryGetValue(optionId, out option) ||
				!IsSubmittedOptionLegal(option, subOption, targetId, boardPosition))
			{
				MarkGap();
				_submittedOption = null;
				_optionsFrame = null;
				return;
			}

			if (_openRoot != null)
			{
				// The first root action was committed but lost its closing boundary.
				CommitUnrecordableAction();
				_openRoot = null;
			}
			// A legal SendOption replaced before any matching root may be a UI preview. It is
			// deliberately not counted as committed and does not taint the trace.
			_submittedOption = null;
			_submittedOption = new SubmittedOption
			{
				FrameId = _optionsFrame.FrameId,
				Option = option,
				OptionId = optionId,
				SubOption = subOption,
				TargetEntityId = targetId,
				BoardPosition = boardPosition,
				SendWatermark = watermark,
				Frame = CloneOptionsFrame(_optionsFrame),
				CollectorEpoch = _generation
			};
			_optionsFrame = null;
		}

		private static bool IsSubmittedOptionLegal(
			OptionEntry option,
			int subOption,
			int targetEntityId,
			int boardPosition)
		{
			if (string.Equals(option.Type, "END_TURN", StringComparison.Ordinal))
			{
				// HDT reports the ordinary end-turn pseudo-option with error=INVALID.
				return option.OptionId == 0 &&
					string.Equals(option.Error, "INVALID", StringComparison.Ordinal) &&
					subOption == -1 && targetEntityId == 0 && boardPosition == 0;
			}
			if (!string.Equals(option.Type, "POWER", StringComparison.Ordinal) ||
				!string.Equals(option.Error, "NONE", StringComparison.Ordinal) ||
				option.Entity == null)
			{
				return false;
			}

			IDictionary<int, TargetEntry> targets = option.Targets;
			if (subOption >= 0)
			{
				SubOptionEntry selected;
				if (!option.SubOptions.TryGetValue(subOption, out selected) ||
					!string.Equals(selected.Error, "NONE", StringComparison.Ordinal))
					return false;
				targets = selected.Targets;
			}
			if (targetEntityId == 0)
				return true;
			return targets.Values.Any(target =>
				target.Entity != null && target.Entity.EntityId == targetEntityId &&
				string.Equals(target.Error, "NONE", StringComparison.Ordinal));
		}

		private void ProcessPower(
			string payload,
			long watermark,
			ICollection<HdtPowerActionEvidence> result)
		{
			var trimmed = payload.TrimStart();
			var indentation = payload.Length - trimmed.Length;
			if (trimmed.StartsWith("BLOCK_START ", StringComparison.Ordinal))
			{
				var topLevel = indentation == 0 && _blockDepth == 0;
				_blockDepth++;
				if (topLevel)
					TryOpenRoot(trimmed, watermark);
				return;
			}
			if (trimmed.StartsWith("BLOCK_END", StringComparison.Ordinal))
			{
				if (_blockDepth > 0)
					_blockDepth--;
				if (_openRoot != null && _blockDepth == 0)
				{
					_openRoot.EndWatermark = watermark;
					_completedRoot = _openRoot;
					_openRoot = null;
					_submittedOption = null;
				}
				return;
			}

			if (_submittedOption != null &&
				string.Equals(_submittedOption.Option.Type, "END_TURN", StringComparison.Ordinal) &&
				indentation == 0 &&
				trimmed.StartsWith("TAG_CHANGE Entity=GameEntity ", StringComparison.Ordinal) &&
				trimmed.IndexOf("tag=STEP value=MAIN_END", StringComparison.Ordinal) >= 0)
			{
				result.Add(CommitEvidence(BuildEndTurnEvidence(_submittedOption, watermark)));
				_submittedOption = null;
			}
		}

		private void TryOpenRoot(string payload, long watermark)
		{
			if (_submittedOption == null ||
				!string.Equals(_submittedOption.Option.Type, "POWER", StringComparison.Ordinal))
				return;

			string blockType;
			HdtPowerEntityEvidence source;
			HdtPowerEntityEvidence target;
			int subOption;
			if (!TryParseRootBlock(payload, out blockType, out source, out target, out subOption))
			{
				if (payload.IndexOf("BlockType=PLAY", StringComparison.Ordinal) >= 0 ||
					payload.IndexOf("BlockType=ATTACK", StringComparison.Ordinal) >= 0)
				{
					CommitUnrecordableAction();
				}
				_submittedOption = null;
				return;
			}
			if (blockType != "PLAY" && blockType != "ATTACK")
				return;
			if (source == null || _submittedOption.Option.Entity == null ||
				!SameEntity(source, _submittedOption.Option.Entity))
			{
				// A different root proves only that the SendOption was not this action. It may
				// have been a UI preview, so do not create a false gap.
				_submittedOption = null;
				return;
			}
			if (subOption != _submittedOption.SubOption ||
				(target?.EntityId ?? 0) != _submittedOption.TargetEntityId ||
				(blockType == "ATTACK" && _submittedOption.BoardPosition != 0))
			{
				CommitUnrecordableAction();
				_submittedOption = null;
				return;
			}

			var root = new RootAction
			{
				Submission = _submittedOption,
				BlockType = blockType,
				Source = source,
				Target = target,
				StartWatermark = watermark,
				ChoiceStatus = _submittedOption.SubOption == -1 ? "none" : "selected"
			};
			if (_submittedOption.SubOption >= 0)
			{
				SubOptionEntry selected;
				var optionEntities = _submittedOption.Option.SubOptions.Values
					.Where(item => item != null && item.Entity != null &&
						string.Equals(item.Error, "NONE", StringComparison.Ordinal))
					.Select(item => item.Entity.EntityId)
					.Distinct().ToList();
				if (!_submittedOption.Option.SubOptions.TryGetValue(
					_submittedOption.SubOption, out selected) || selected.Entity == null ||
					!optionEntities.Contains(selected.Entity.EntityId))
				{
					root.ChoiceStatus = "unresolved";
				}
				else
				{
					root.Choices.Add(new HdtPowerChoiceEvidence
					{
						ChoiceType = "SUB_OPTION",
						SourceEntityId = source.EntityId,
						OptionEntityIds = optionEntities,
						EntityIds = new List<int> { selected.Entity.EntityId },
						Status = "selected"
					});
				}
			}
			_openRoot = root;
		}

		private void ProcessChoiceOffered(string payload)
		{
			var trimmed = payload.TrimStart();
			if (trimmed.StartsWith("id=", StringComparison.Ordinal))
			{
				FinalizeActiveChoice();
				int choiceId;
				ulong playerFingerprint;
				var choiceType = ReadTokenAfter(trimmed, "ChoiceType=");
				if (!TryReadLeadingInteger(trimmed, "id=", out choiceId) || choiceId <= 0 ||
					!IsSafeChoiceType(choiceType) ||
					!TryReadChoicePlayerFingerprint(
						trimmed, " TaskList=", out playerFingerprint))
				{
					MarkChoiceUnresolved();
					return;
				}
				_activeChoice = new ChoiceAccumulator
				{
					ChoiceId = choiceId,
					ChoiceType = choiceType,
					PlayerFingerprint = playerFingerprint,
					HasPlayerFingerprint = true,
					Root = _openRoot ?? _completedRoot
				};
				return;
			}
			if (_activeChoice == null)
				return;
			HdtPowerEntityEvidence entity;
			if (trimmed.StartsWith("Source=", StringComparison.Ordinal))
			{
				if (!TryParseEntity(trimmed, out entity) || _activeChoice.Source != null)
					_activeChoice.Invalid = true;
				else
					_activeChoice.Source = entity;
				return;
			}
			if (trimmed.StartsWith("Entities[", StringComparison.Ordinal))
			{
				int index;
				if (!TryParseIndexedEntity(trimmed, "Entities[", out index, out entity) ||
					index >= MaxChoiceEntities || _activeChoice.Options.ContainsKey(index))
					_activeChoice.Invalid = true;
				else
					_activeChoice.Options[index] = entity;
			}
		}

		private void ProcessEntitiesChosen(string payload)
		{
			var trimmed = payload.TrimStart();
			if (trimmed.StartsWith("id=", StringComparison.Ordinal))
			{
				int choiceId;
				int expectedSelectedCount;
				ulong playerFingerprint;
				if (!TryReadLeadingInteger(trimmed, "id=", out choiceId) || choiceId <= 0 ||
					!TryReadIntegerAfter(
						trimmed, "EntitiesCount=", out expectedSelectedCount) ||
					expectedSelectedCount < 0 || expectedSelectedCount > MaxChoiceEntities ||
					!TryReadChoicePlayerFingerprint(
						trimmed, " EntitiesCount=", out playerFingerprint))
				{
					MarkChoiceUnresolved();
					return;
				}
				if (_activeChoice == null || _activeChoice.ChoiceId != choiceId)
				{
					FinalizeActiveChoice();
					_activeChoice = new ChoiceAccumulator
					{
						ChoiceId = choiceId,
						Root = _openRoot ?? _completedRoot,
						Invalid = true
					};
				}
				if (_activeChoice.ChosenHeaderSeen ||
					!_activeChoice.HasPlayerFingerprint ||
					_activeChoice.PlayerFingerprint != playerFingerprint)
				{
					_activeChoice.Invalid = true;
				}
				_activeChoice.ChosenHeaderSeen = true;
				_activeChoice.ExpectedSelectedCount = expectedSelectedCount;
				return;
			}
			if (trimmed.StartsWith("Entities[", StringComparison.Ordinal))
			{
				HdtPowerEntityEvidence entity;
				int index;
				if (_activeChoice == null ||
					!TryParseIndexedEntity(trimmed, "Entities[", out index, out entity) ||
					index >= MaxChoiceEntities || _activeChoice.Selected.ContainsKey(index))
				{
					MarkChoiceUnresolved();
					return;
				}
				_activeChoice.Selected[index] = entity;
			}
		}

		private void ProcessLegacySendChoices(string payload)
		{
			var trimmed = payload.TrimStart();
			if (trimmed.StartsWith("id=", StringComparison.Ordinal))
			{
				FinalizeActiveChoice();
				int choiceId;
				var choiceType = ReadTokenAfter(trimmed, "ChoiceType=");
				if (!TryReadLeadingInteger(trimmed, "id=", out choiceId) || choiceId <= 0)
				{
					MarkChoiceUnresolved();
					return;
				}
				_activeChoice = new ChoiceAccumulator
				{
					ChoiceId = choiceId,
					ChoiceType = choiceType,
					Root = _openRoot ?? _completedRoot,
					ChosenHeaderSeen = true,
					Invalid = true
				};
				return;
			}
			if (trimmed.StartsWith("m_chosenEntities[", StringComparison.Ordinal))
			{
				HdtPowerEntityEvidence entity;
				int index;
				if (_activeChoice == null ||
					!TryParseIndexedEntity(
						trimmed, "m_chosenEntities[", out index, out entity) ||
					index >= MaxChoiceEntities || _activeChoice.Selected.ContainsKey(index))
					MarkChoiceUnresolved();
				else
					_activeChoice.Selected[index] = entity;
			}
		}

		private void FinalizeActiveChoice()
		{
			if (_activeChoice == null)
				return;
			var active = _activeChoice;
			_activeChoice = null;
			var root = active.Root;
			if (root == null)
				return;
			var optionIds = active.Options.OrderBy(item => item.Key)
				.Select(item => item.Value.EntityId).ToList();
			var selectedIds = active.Selected.OrderBy(item => item.Key)
				.Select(item => item.Value.EntityId).ToList();
			var exact = !active.Invalid && active.ChoiceId > 0 &&
				IsSafeChoiceType(active.ChoiceType) && active.Source != null && root.Source != null &&
				SameEntityIdentity(active.Source, root.Source) &&
				active.HasPlayerFingerprint && active.ChosenHeaderSeen &&
				active.ExpectedSelectedCount == selectedIds.Count &&
				HasCompleteEntityIndices(active.Options) &&
				HasCompleteEntityIndices(active.Selected) &&
				optionIds.Count > 0 && selectedIds.Count > 0 &&
				optionIds.Distinct().Count() == optionIds.Count &&
				selectedIds.Distinct().Count() == selectedIds.Count &&
				selectedIds.All(optionIds.Contains);
			root.Choices.Add(new HdtPowerChoiceEvidence
			{
				ChoiceId = active.ChoiceId,
				ChoiceType = active.ChoiceType ?? "",
				SourceEntityId = active.Source?.EntityId ?? 0,
				OptionEntityIds = optionIds,
				EntityIds = selectedIds,
				Status = exact ? "selected" : "unresolved"
			});
			if (exact && !string.Equals(root.ChoiceStatus, "unresolved", StringComparison.Ordinal))
				root.ChoiceStatus = "selected";
			else if (!exact)
				root.ChoiceStatus = "unresolved";
		}

		private void MarkChoiceUnresolved()
		{
			RootAction root;
			if (_activeChoice != null)
			{
				_activeChoice.Invalid = true;
				root = _activeChoice.Root;
			}
			else
			{
				root = _openRoot ?? _completedRoot;
			}
			if (root != null)
				root.ChoiceStatus = "unresolved";
		}

		private void FinalizeCompletedRoot(ICollection<HdtPowerActionEvidence> result)
		{
			if (_completedRoot == null)
				return;
			var root = _completedRoot;
			_completedRoot = null;
			var selected = string.Equals(root.ChoiceStatus, "selected", StringComparison.Ordinal) &&
				root.Choices.Count > 0 && root.Choices.All(item =>
					string.Equals(item.Status, "selected", StringComparison.Ordinal));
			var unresolved = string.Equals(root.ChoiceStatus, "unresolved", StringComparison.Ordinal) ||
				(!selected && (!string.Equals(root.ChoiceStatus, "none", StringComparison.Ordinal) ||
					root.Choices.Count > 0 || root.Submission.SubOption != -1));
			result.Add(CommitEvidence(new HdtPowerActionEvidence
			{
				PowerBlockType = root.BlockType,
				FrameId = root.Submission.FrameId,
				OptionId = root.Submission.OptionId,
				SubOption = root.Submission.SubOption,
				BoardPosition = root.Submission.BoardPosition,
				Source = root.Source,
				Target = root.Target,
				TargetBindingStatus = root.Target == null
					? AdvisorBehaviorTargetBindingStatus.ExplicitNone
					: AdvisorBehaviorTargetBindingStatus.ExactEntityId,
				PowerStartWatermark = root.StartWatermark,
				PowerEndWatermark = root.EndWatermark,
				ActionIdentityStatus = unresolved ? ChoiceUnresolvedStatus :
					(selected ? ExactChoiceIdentityStatus : ExactIdentityStatus),
				ChoiceStatus = unresolved ? "unresolved" : (selected ? "selected" : "none"),
				Choices = root.Choices.Select(CloneChoice).ToList(),
				OptionsFrame = ToEvidence(root.Submission.Frame, _generation)
			}));
		}

		private static HdtPowerActionEvidence BuildEndTurnEvidence(
			SubmittedOption submission,
			long endWatermark)
		{
			return new HdtPowerActionEvidence
			{
				PowerBlockType = "MAIN_END",
				FrameId = submission.FrameId,
				OptionId = submission.OptionId,
				SubOption = submission.SubOption,
				BoardPosition = submission.BoardPosition,
				TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				PowerStartWatermark = submission.SendWatermark,
				PowerEndWatermark = endWatermark,
				ActionIdentityStatus = ExactIdentityStatus,
				ChoiceStatus = "none",
				OptionsFrame = ToEvidence(submission.Frame, submission.CollectorEpoch)
			};
		}

		private static OptionsFrame CloneOptionsFrame(OptionsFrame source)
		{
			if (source == null)
				return null;
			var clone = new OptionsFrame
			{
				FrameId = source.FrameId,
				HeaderWatermark = source.HeaderWatermark,
				Invalid = source.Invalid
			};
			foreach (var pair in source.Options.OrderBy(item => item.Key))
			{
				var option = new OptionEntry
				{
					OptionId = pair.Value.OptionId,
					Type = pair.Value.Type ?? "",
					Entity = CloneEntity(pair.Value.Entity),
					Error = pair.Value.Error ?? ""
				};
				foreach (var target in pair.Value.Targets.OrderBy(item => item.Key))
				{
					option.Targets[target.Key] = new TargetEntry
					{
						TargetId = target.Value.TargetId,
						Entity = CloneEntity(target.Value.Entity),
						Error = target.Value.Error ?? ""
					};
				}
				foreach (var sub in pair.Value.SubOptions.OrderBy(item => item.Key))
				{
					var subClone = new SubOptionEntry
					{
						SubOptionId = sub.Value.SubOptionId,
						Entity = CloneEntity(sub.Value.Entity),
						Error = sub.Value.Error ?? ""
					};
					foreach (var target in sub.Value.Targets.OrderBy(item => item.Key))
					{
						subClone.Targets[target.Key] = new TargetEntry
						{
							TargetId = target.Value.TargetId,
							Entity = CloneEntity(target.Value.Entity),
							Error = target.Value.Error ?? ""
						};
					}
					option.SubOptions[sub.Key] = subClone;
				}
				clone.Options[pair.Key] = option;
			}
			return clone;
		}

		private static HdtPowerOptionsFrameEvidence ToEvidence(
			OptionsFrame source,
			long collectorEpoch)
		{
			if (source == null || !source.IsComplete || collectorEpoch <= 0)
				return null;
			return new HdtPowerOptionsFrameEvidence
			{
				CollectorEpoch = collectorEpoch,
				FrameId = source.FrameId,
				HeaderWatermark = source.HeaderWatermark,
				Options = source.Options.OrderBy(item => item.Key).Select(pair =>
					new HdtPowerOptionEvidence
					{
						OptionId = pair.Value.OptionId,
						Type = pair.Value.Type ?? "",
						Entity = CloneEntity(pair.Value.Entity),
						Error = pair.Value.Error ?? "",
						Targets = pair.Value.Targets.OrderBy(item => item.Key).Select(target =>
							new HdtPowerTargetEvidence
							{
								TargetId = target.Value.TargetId,
								Entity = CloneEntity(target.Value.Entity),
								Error = target.Value.Error ?? ""
							}).ToList(),
						SubOptions = pair.Value.SubOptions.OrderBy(item => item.Key).Select(sub =>
							new HdtPowerSubOptionEvidence
							{
								SubOptionId = sub.Value.SubOptionId,
								Entity = CloneEntity(sub.Value.Entity),
								Error = sub.Value.Error ?? "",
								Targets = sub.Value.Targets.OrderBy(item => item.Key).Select(target =>
									new HdtPowerTargetEvidence
									{
										TargetId = target.Value.TargetId,
										Entity = CloneEntity(target.Value.Entity),
										Error = target.Value.Error ?? ""
									}).ToList()
							}).ToList()
					}).ToList()
			};
		}

		private static HdtPowerEntityEvidence CloneEntity(HdtPowerEntityEvidence source)
		{
			return source == null ? null : new HdtPowerEntityEvidence
			{
				EntityId = source.EntityId,
				Zone = source.Zone ?? "",
				ZonePosition = source.ZonePosition,
				CardId = source.CardId ?? "",
				PlayerId = source.PlayerId
			};
		}

		private static HdtPowerChoiceEvidence CloneChoice(HdtPowerChoiceEvidence source)
		{
			return new HdtPowerChoiceEvidence
			{
				ChoiceId = source.ChoiceId,
				ChoiceType = source.ChoiceType ?? "",
				SourceEntityId = source.SourceEntityId,
				OptionEntityIds = new List<int>(source.OptionEntityIds ?? new List<int>()),
				EntityIds = new List<int>(source.EntityIds ?? new List<int>()),
				Status = source.Status ?? "unresolved"
			};
		}

		private static bool SameEntityIdentity(
			HdtPowerEntityEvidence left,
			HdtPowerEntityEvidence right)
		{
			return left != null && right != null && left.EntityId == right.EntityId &&
				left.PlayerId == right.PlayerId;
		}

		private static bool IsSafeChoiceType(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
				value.All(character => Char.IsLetterOrDigit(character) || character == '_');
		}

		private static bool TryParseOption(string payload, out OptionEntry result)
		{
			result = null;
			int optionId;
			if (!TryReadLeadingInteger(payload, "option ", out optionId) || optionId < 0)
				return false;
			var type = ReadTokenAfter(payload, "type=");
			string entityText;
			string error;
			if (string.IsNullOrWhiteSpace(type) ||
				!TryBetween(payload, "mainEntity=", " error=", out entityText) ||
				string.IsNullOrWhiteSpace(error = ReadTokenAfter(payload, "error=")))
				return false;
			HdtPowerEntityEvidence entity = null;
			if (!string.IsNullOrWhiteSpace(entityText) && !TryParseEntity(entityText, out entity))
				return false;
			result = new OptionEntry
			{
				OptionId = optionId,
				Type = type,
				Entity = entity,
				Error = error
			};
			return true;
		}

		private static bool TryParseSubOption(string payload, out SubOptionEntry result)
		{
			result = null;
			int subOptionId;
			string entityText;
			if (!TryReadLeadingInteger(payload, "subOption ", out subOptionId) ||
				subOptionId < 0 ||
				!TryBetween(payload, "entity=", " error=", out entityText))
				return false;
			HdtPowerEntityEvidence entity;
			var error = ReadTokenAfter(payload, "error=");
			if (!TryParseEntity(entityText, out entity) || string.IsNullOrWhiteSpace(error))
				return false;
			result = new SubOptionEntry
			{
				SubOptionId = subOptionId,
				Entity = entity,
				Error = error
			};
			return true;
		}

		private static bool TryParseTarget(string payload, out TargetEntry result)
		{
			result = null;
			int targetId;
			string entityText;
			if (!TryReadLeadingInteger(payload, "target ", out targetId) ||
				targetId < 0 ||
				!TryBetween(payload, "entity=", " error=", out entityText))
				return false;
			HdtPowerEntityEvidence entity;
			var error = ReadTokenAfter(payload, "error=");
			if (!TryParseEntity(entityText, out entity) || string.IsNullOrWhiteSpace(error))
				return false;
			result = new TargetEntry { TargetId = targetId, Entity = entity, Error = error };
			return true;
		}

		private static bool TryParseRootBlock(
			string payload,
			out string blockType,
			out HdtPowerEntityEvidence source,
			out HdtPowerEntityEvidence target,
			out int subOption)
		{
			blockType = ReadTokenAfter(payload, "BlockType=");
			source = null;
			target = null;
			subOption = -1;
			string sourceText;
			string targetText;
			if (string.IsNullOrWhiteSpace(blockType) ||
				!TryBetween(payload, " Entity=", " EffectCardId=", out sourceText) ||
				!TryBetween(payload, " Target=", " SubOption=", out targetText) ||
				!TryReadIntegerAfter(payload, "SubOption=", out subOption) ||
				!TryParseEntity(sourceText, out source))
				return false;
			if (targetText.Trim() == "0")
				return true;
			return TryParseEntity(targetText, out target);
		}

		private static bool TryParseEntity(string value, out HdtPowerEntityEvidence result)
		{
			result = null;
			var match = EntityPattern.Match(value ?? "");
			int entityId;
			int zonePosition;
			int playerId;
			if (!match.Success ||
				!Int32.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out entityId) ||
				!Int32.TryParse(match.Groups["position"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out zonePosition) ||
				!Int32.TryParse(match.Groups["player"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out playerId) ||
				entityId <= 0 || playerId <= 0)
				return false;
			result = new HdtPowerEntityEvidence
			{
				EntityId = entityId,
				Zone = match.Groups["zone"].Value,
				ZonePosition = zonePosition,
				CardId = match.Groups["card"].Value,
				PlayerId = playerId
			};
			return true;
		}

		private static bool TryParseIndexedEntity(
			string value,
			string prefix,
			out int index,
			out HdtPowerEntityEvidence result)
		{
			index = -1;
			result = null;
			if (value == null || prefix == null ||
				!value.StartsWith(prefix, StringComparison.Ordinal))
				return false;
			var end = value.IndexOf(']', prefix.Length);
			if (end <= prefix.Length || end + 1 >= value.Length || value[end + 1] != '=' ||
				!Int32.TryParse(
					value.Substring(prefix.Length, end - prefix.Length),
					NumberStyles.None,
					CultureInfo.InvariantCulture,
					out index) || index < 0)
				return false;
			return TryParseEntity(value, out result);
		}

		private static bool HasCompleteEntityIndices(
			IDictionary<int, HdtPowerEntityEvidence> entities)
		{
			return entities != null && entities.Count > 0 &&
				entities.Keys.Min() == 0 && entities.Keys.Max() == entities.Count - 1 &&
				Enumerable.Range(0, entities.Count).All(entities.ContainsKey);
		}

		private static bool TryReadChoicePlayerFingerprint(
			string value,
			string endMarker,
			out ulong result)
		{
			result = 0;
			string player;
			if (!TryBetween(value, " Player=", endMarker, out player))
				return false;
			player = player.Trim();
			if (string.IsNullOrWhiteSpace(player) || player.Length > 512)
				return false;
			result = Fingerprint(player);
			return true;
		}

		private static bool SameEntity(HdtPowerEntityEvidence left, HdtPowerEntityEvidence right)
		{
			return left != null && right != null &&
				left.EntityId == right.EntityId &&
				left.PlayerId == right.PlayerId &&
				string.Equals(left.CardId ?? "", right.CardId ?? "", StringComparison.Ordinal) &&
				string.Equals(left.Zone ?? "", right.Zone ?? "", StringComparison.Ordinal);
		}

		private static bool TryPayload(string line, string method, out string payload)
		{
			var marker = "GameState." + method + "() - ";
			var index = (line ?? "").IndexOf(marker, StringComparison.Ordinal);
			if (index < 0)
			{
				payload = "";
				return false;
			}
			payload = line.Substring(index + marker.Length);
			return true;
		}

		private static Dictionary<string, int> ParseIntegerFields(string value)
		{
			var result = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var part in (value ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var separator = part.IndexOf('=');
				int parsed;
				if (separator > 0 && Int32.TryParse(
					part.Substring(separator + 1),
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out parsed))
				{
					result[part.Substring(0, separator)] = parsed;
				}
			}
			return result;
		}

		private static bool TryReadLeadingInteger(string value, string prefix, out int result)
		{
			result = 0;
			if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal))
				return false;
			var start = prefix.Length;
			var end = start;
			while (end < value.Length && Char.IsDigit(value[end]))
				end++;
			return end > start && Int32.TryParse(
				value.Substring(start, end - start),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out result);
		}

		private static bool TryReadIntegerAfter(string value, string marker, out int result)
		{
			result = 0;
			var index = (value ?? "").IndexOf(marker, StringComparison.Ordinal);
			if (index < 0)
				return false;
			index += marker.Length;
			var end = index;
			if (end < value.Length && value[end] == '-')
				end++;
			while (end < value.Length && Char.IsDigit(value[end]))
				end++;
			return end > index && Int32.TryParse(
				value.Substring(index, end - index),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out result);
		}

		private static string ReadTokenAfter(string value, string marker)
		{
			var index = (value ?? "").IndexOf(marker, StringComparison.Ordinal);
			if (index < 0)
				return "";
			index += marker.Length;
			var end = index;
			while (end < value.Length && !Char.IsWhiteSpace(value[end]))
				end++;
			return value.Substring(index, end - index);
		}

		private static bool TryBetween(
			string value,
			string startMarker,
			string endMarker,
			out string result)
		{
			result = "";
			var start = (value ?? "").IndexOf(startMarker, StringComparison.Ordinal);
			if (start < 0)
				return false;
			start += startMarker.Length;
			var end = value.IndexOf(endMarker, start, StringComparison.Ordinal);
			if (end < start)
				return false;
			result = value.Substring(start, end - start);
			return true;
		}

		private static ulong Fingerprint(string value)
		{
			// In-memory FNV-1a is sufficient to detect a replaced list prefix. It is not written to
			// metadata and therefore cannot leak raw entity names or account identifiers.
			const ulong offset = 14695981039346656037UL;
			const ulong prime = 1099511628211UL;
			var hash = offset;
			foreach (var character in value ?? "")
			{
				hash ^= character;
				hash *= prime;
			}
			return hash;
		}

		private sealed class OptionsFrame
		{
			public int FrameId { get; set; }
			public long HeaderWatermark { get; set; }
			public bool Invalid { get; set; }
			public Dictionary<int, OptionEntry> Options { get; } =
				new Dictionary<int, OptionEntry>();
			public OptionEntry CurrentOption { get; set; }
			public SubOptionEntry CurrentSubOption { get; set; }

			public bool IsComplete
			{
				get
				{
					if (Invalid || Options.Count == 0 || !Options.ContainsKey(0))
						return false;
					var maximum = Options.Keys.Max();
					return Options.Count == maximum + 1 &&
						Enumerable.Range(0, maximum + 1).All(Options.ContainsKey);
				}
			}
		}

		private sealed class OptionEntry
		{
			public int OptionId { get; set; }
			public string Type { get; set; } = "";
			public HdtPowerEntityEvidence Entity { get; set; }
			public string Error { get; set; } = "";
			public Dictionary<int, TargetEntry> Targets { get; } =
				new Dictionary<int, TargetEntry>();
			public Dictionary<int, SubOptionEntry> SubOptions { get; } =
				new Dictionary<int, SubOptionEntry>();
		}

		private sealed class SubOptionEntry
		{
			public int SubOptionId { get; set; }
			public HdtPowerEntityEvidence Entity { get; set; }
			public string Error { get; set; } = "";
			public Dictionary<int, TargetEntry> Targets { get; } =
				new Dictionary<int, TargetEntry>();
		}

		private sealed class TargetEntry
		{
			public int TargetId { get; set; }
			public HdtPowerEntityEvidence Entity { get; set; }
			public string Error { get; set; } = "";
		}

		private sealed class SubmittedOption
		{
			public int FrameId { get; set; }
			public int OptionId { get; set; }
			public int SubOption { get; set; }
			public int TargetEntityId { get; set; }
			public int BoardPosition { get; set; }
			public long SendWatermark { get; set; }
			public OptionEntry Option { get; set; }
			public OptionsFrame Frame { get; set; }
			public long CollectorEpoch { get; set; }
		}

		private sealed class RootAction
		{
			public SubmittedOption Submission { get; set; }
			public string BlockType { get; set; } = "";
			public HdtPowerEntityEvidence Source { get; set; }
			public HdtPowerEntityEvidence Target { get; set; }
			public long StartWatermark { get; set; }
			public long EndWatermark { get; set; }
			public string ChoiceStatus { get; set; } = "none";
			public List<HdtPowerChoiceEvidence> Choices { get; } =
				new List<HdtPowerChoiceEvidence>();
		}

		private sealed class ChoiceAccumulator
		{
			public int ChoiceId { get; set; }
			public string ChoiceType { get; set; } = "";
			public RootAction Root { get; set; }
			public ulong PlayerFingerprint { get; set; }
			public bool HasPlayerFingerprint { get; set; }
			public HdtPowerEntityEvidence Source { get; set; }
			public Dictionary<int, HdtPowerEntityEvidence> Options { get; } =
				new Dictionary<int, HdtPowerEntityEvidence>();
			public Dictionary<int, HdtPowerEntityEvidence> Selected { get; } =
				new Dictionary<int, HdtPowerEntityEvidence>();
			public bool ChosenHeaderSeen { get; set; }
			public int ExpectedSelectedCount { get; set; } = -1;
			public bool Invalid { get; set; }
		}
	}
}
