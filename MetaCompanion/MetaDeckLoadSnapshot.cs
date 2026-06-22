using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanion
{
	internal enum MetaDeckLoadStatus
	{
		Loading,
		Ready,
		Empty,
		Failed
	}

	internal class MetaDeckLoadSnapshot
	{
		public MetaDeckLoadStatus Status { get; set; }
		public int DeckCount { get; set; }
		public string ErrorSummary { get; set; } = "";
		public DateTime? StartedAt { get; set; }
		public DateTime? CompletedAt { get; set; }

		public bool IsReady
		{
			get { return Status == MetaDeckLoadStatus.Ready && DeckCount > 0; }
		}

		public string UserMessage
		{
			get
			{
				switch (Status)
				{
					case MetaDeckLoadStatus.Ready:
						return "牌组库已加载: " + DeckCount.ToString(CultureInfo.InvariantCulture) + " 套";
					case MetaDeckLoadStatus.Empty:
						return "牌组库不可用: 未加载到可用牌组快照";
					case MetaDeckLoadStatus.Failed:
						return "牌组库加载失败: " + ErrorSummary;
					default:
						return "牌组库加载中";
				}
			}
		}

		public static MetaDeckLoadSnapshot Loading(DateTime startedAt)
		{
			return new MetaDeckLoadSnapshot
			{
				Status = MetaDeckLoadStatus.Loading,
				StartedAt = startedAt
			};
		}

		public static MetaDeckLoadSnapshot Ready(int deckCount, DateTime startedAt, DateTime completedAt)
		{
			return new MetaDeckLoadSnapshot
			{
				Status = deckCount > 0 ? MetaDeckLoadStatus.Ready : MetaDeckLoadStatus.Empty,
				DeckCount = Math.Max(0, deckCount),
				StartedAt = startedAt,
				CompletedAt = completedAt
			};
		}

		public static MetaDeckLoadSnapshot Failed(string errorSummary, DateTime startedAt, DateTime completedAt)
		{
			return new MetaDeckLoadSnapshot
			{
				Status = MetaDeckLoadStatus.Failed,
				ErrorSummary = errorSummary ?? "",
				StartedAt = startedAt,
				CompletedAt = completedAt
			};
		}
	}

	internal static class MetaDeckLoadStatusStore
	{
		private const string StatusFileName = "meta_deck_load_status.tsv";

		public static string GetPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory ?? "", StatusFileName);
		}

		public static void Write(string dataDirectory, MetaDeckLoadSnapshot snapshot)
		{
			if (snapshot == null || string.IsNullOrWhiteSpace(dataDirectory))
			{
				return;
			}

			Directory.CreateDirectory(dataDirectory);
			File.WriteAllLines(
				GetPath(dataDirectory),
				new[]
				{
					"status\t" + snapshot.Status,
					"deck_count\t" + snapshot.DeckCount.ToString(CultureInfo.InvariantCulture),
					"started_at\t" + FormatTime(snapshot.StartedAt),
					"completed_at\t" + FormatTime(snapshot.CompletedAt),
					"error_summary\t" + SanitizeValue(snapshot.ErrorSummary)
				},
				Encoding.UTF8);
		}

		public static MetaDeckLoadSnapshot Read(string dataDirectory)
		{
			var path = GetPath(dataDirectory);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return null;
			}

			var values = File.ReadAllLines(path, Encoding.UTF8)
				.Select(line => line.Split(new[] {'\t'}, 2))
				.Where(parts => parts.Length == 2)
				.ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

			MetaDeckLoadStatus status;
			if (!Enum.TryParse(GetValue(values, "status"), true, out status))
			{
				return null;
			}

			int deckCount;
			int.TryParse(
				GetValue(values, "deck_count"),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out deckCount);

			return new MetaDeckLoadSnapshot
			{
				Status = status,
				DeckCount = Math.Max(0, deckCount),
				StartedAt = ParseTime(GetValue(values, "started_at")),
				CompletedAt = ParseTime(GetValue(values, "completed_at")),
				ErrorSummary = GetValue(values, "error_summary")
			};
		}

		private static string GetValue(Dictionary<string, string> values, string key)
		{
			return values.ContainsKey(key) ? values[key] : "";
		}

		private static string SanitizeValue(string value)
		{
			return string.IsNullOrWhiteSpace(value)
				? ""
				: value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
		}

		private static string FormatTime(DateTime? value)
		{
			return value.HasValue
				? value.Value.ToString("o", CultureInfo.InvariantCulture)
				: "";
		}

		private static DateTime? ParseTime(string value)
		{
			DateTime parsed;
			return DateTime.TryParse(
				value,
				CultureInfo.InvariantCulture,
				DateTimeStyles.RoundtripKind,
				out parsed)
				? parsed
				: (DateTime?)null;
		}
	}
}
