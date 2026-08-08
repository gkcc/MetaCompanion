using System;
using System.Collections.Generic;

namespace MetaCompanion
{
	internal struct HdtGameEventStamp : IEquatable<HdtGameEventStamp>
	{
		internal HdtGameEventStamp(
			string kind,
			int turnNumber,
			int monotonicSequence,
			string detail)
		{
			Kind = kind ?? "";
			TurnNumber = turnNumber;
			MonotonicSequence = monotonicSequence;
			Detail = detail ?? "";
		}

		internal string Kind { get; }
		internal int TurnNumber { get; }
		internal int MonotonicSequence { get; }
		internal string Detail { get; }

		public bool Equals(HdtGameEventStamp other)
		{
			return string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
				TurnNumber == other.TurnNumber &&
				MonotonicSequence == other.MonotonicSequence &&
				string.Equals(Detail, other.Detail, StringComparison.Ordinal);
		}

		internal bool MatchesReplay(HdtGameEventStamp other)
		{
			// HDT supplies the live GetTurnNumber() value while replaying old callbacks after
			// a watcher restart. Kind-local sequence plus action detail remain stable, so replay
			// matching deliberately ignores only that drifting presentation-time turn number.
			return string.Equals(Kind, other.Kind, StringComparison.Ordinal) &&
				MonotonicSequence == other.MonotonicSequence &&
				string.Equals(Detail, other.Detail, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is HdtGameEventStamp && Equals((HdtGameEventStamp)obj);
		}

		public override int GetHashCode()
		{
			unchecked
			{
				var hash = 17;
				hash = hash * 31 + Kind.GetHashCode();
				hash = hash * 31 + TurnNumber;
				hash = hash * 31 + MonotonicSequence;
				hash = hash * 31 + Detail.GetHashCode();
				return hash;
			}
		}
	}

	internal struct HdtGameSessionStart
	{
		internal HdtGameSessionStart(long generation, bool isReplay)
		{
			Generation = generation;
			IsReplay = isReplay;
		}

		internal long Generation { get; }
		internal bool IsReplay { get; }
	}

	/// <summary>
	/// Suppresses a same-game prefix that HDT emits again when its Power.log watcher restarts.
	/// Each event kind owns a monotonic replay cursor, so a delayed or omitted callback in one
	/// stream cannot make unrelated streams accept the rest of a replay as new input.
	/// </summary>
	internal sealed class HdtGameEventReplayGuard
	{
		private readonly object _syncRoot = new object();
		private readonly Dictionary<string, List<HdtGameEventStamp>> _acceptedByKind =
			new Dictionary<string, List<HdtGameEventStamp>>(StringComparer.Ordinal);
		private readonly Dictionary<string, int> _replayCursorByKind =
			new Dictionary<string, int>(StringComparer.Ordinal);
		private long _generation;
		private bool _sessionActive;

		internal HdtGameSessionStart BeginGame()
		{
			lock (_syncRoot)
			{
				if (!_sessionActive)
				{
					_generation = _generation == long.MaxValue ? 1 : _generation + 1;
					_sessionActive = true;
					_acceptedByKind.Clear();
					_replayCursorByKind.Clear();
					return new HdtGameSessionStart(_generation, false);
				}

				_replayCursorByKind.Clear();
				foreach (var pair in _acceptedByKind)
				{
					if (pair.Value.Count > 0)
					{
						_replayCursorByKind[pair.Key] = 0;
					}
				}
				return new HdtGameSessionStart(
					_generation,
					_replayCursorByKind.Count > 0);
			}
		}

		internal bool ShouldProcess(long generation, HdtGameEventStamp stamp)
		{
			lock (_syncRoot)
			{
				if (!_sessionActive || generation != _generation ||
					string.IsNullOrWhiteSpace(stamp.Kind))
				{
					return false;
				}

				int replayCursor;
				List<HdtGameEventStamp> accepted;
				if (_replayCursorByKind.TryGetValue(stamp.Kind, out replayCursor) &&
					_acceptedByKind.TryGetValue(stamp.Kind, out accepted))
				{
					if (replayCursor < accepted.Count &&
						accepted[replayCursor].MatchesReplay(stamp))
					{
						replayCursor++;
						if (replayCursor >= accepted.Count)
						{
							_replayCursorByKind.Remove(stamp.Kind);
						}
						else
						{
							_replayCursorByKind[stamp.Kind] = replayCursor;
						}
						return false;
					}

					// This kind no longer matches the old prefix. Treat it as live data immediately;
					// a time window here would discard a real action after a fast watcher recovery.
					_replayCursorByKind.Remove(stamp.Kind);
				}

				if (!_acceptedByKind.TryGetValue(stamp.Kind, out accepted))
				{
					accepted = new List<HdtGameEventStamp>();
					_acceptedByKind[stamp.Kind] = accepted;
				}
				accepted.Add(stamp);
				return true;
			}
		}

		internal bool IsCurrent(long generation)
		{
			lock (_syncRoot)
			{
				return _sessionActive && generation == _generation;
			}
		}

		internal void EndGame(long generation)
		{
			lock (_syncRoot)
			{
				if (!_sessionActive || generation != _generation)
				{
					return;
				}
				_sessionActive = false;
				_replayCursorByKind.Clear();
				_acceptedByKind.Clear();
			}
		}
	}
}
