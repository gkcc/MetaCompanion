using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MetaCompanion
{
	internal enum MetaDataHealthOverallStatus
	{
		Ready,
		Partial,
		Empty,
		Stale,
		Error
	}

	internal class MetaDataHealthSnapshot
	{
		public MetaDataHealthOverallStatus OverallStatus { get; set; }
		public bool PredictionAvailable { get; set; }
		public bool RecommendationAvailable { get; set; }
		public bool PremiumAvailable { get; set; }
		public bool LocalHistoryAvailable { get; set; }
		public string UserMessage { get; set; } = "";
		public List<string> DetailLines { get; set; } = new List<string>();
	}

	internal class MetaDataHealthService
	{
		private static readonly Regex DeckCodeRegex =
			new Regex(@"AA[A-Za-z0-9+/=]+", RegexOptions.Compiled);

		private readonly string _dataDirectory;
		private readonly DateTime _now;
		private readonly TimeSpan _staleAfter;

		public MetaDataHealthService(string dataDirectory)
			: this(dataDirectory, DateTime.Now, TimeSpan.FromHours(24))
		{
		}

		public MetaDataHealthService(string dataDirectory, DateTime now, TimeSpan staleAfter)
		{
			_dataDirectory = dataDirectory ?? "";
			_now = now;
			_staleAfter = staleAfter <= TimeSpan.Zero ? TimeSpan.FromHours(24) : staleAfter;
		}

		public MetaDataHealthSnapshot Inspect()
		{
			try
			{
				return InspectCore();
			}
			catch (Exception ex)
			{
				return new MetaDataHealthSnapshot
				{
					OverallStatus = MetaDataHealthOverallStatus.Error,
					UserMessage = "\u6570\u636e\u5065\u5eb7\u68c0\u67e5\u5931\u8d25\uff0c\u8bf7\u91cd\u65b0\u751f\u6210\u6570\u636e\u5feb\u7167\u3002",
					DetailLines = new List<string>
					{
						"\u8bfb\u53d6\u672c\u5730\u6570\u636e\u72b6\u6001\u65f6\u9047\u5230\u9519\u8bef: " +
							ex.GetType().Name
					}
				};
			}
		}

		private MetaDataHealthSnapshot InspectCore()
		{
			var deck = InspectDeckSnapshot(GetPath("hsreplay_deckcodes.txt"));
			var summary = InspectFile(GetPath("Premium", "Meta", "latest", "summary.json"));
			var matrix = InspectFile(GetPath(
				"Premium", "Meta", "latest", "head_to_head_archetype_matchups_v2.json"));
			var manifest = InspectManifest(GetPath("Premium", "Meta", "latest", "manifest.json"));
			var recommendations = InspectRecommendationFile(GetPath(
				"Premium", "Meta", "latest", "personal_recommendations.tsv"));
			var localEnvironment = InspectLocalEnvironment(GetPath("local_meta_environment.tsv"));
			var cookie = InspectCookie();
			var metaDeckLoad = InspectMetaDeckLoad();
			var updateTool = InspectFile(GetPath("Tools", "Update-MetaCompanionData.ps1"));
			var refreshTool = InspectFile(GetPath("Tools", "Run-MetaCompanionRefresh.ps1"));

			var snapshot = new MetaDataHealthSnapshot
			{
				PredictionAvailable = deck.Exists && deck.Count > 0,
				PremiumAvailable = summary.Exists && matrix.Exists && manifest.Exists,
				RecommendationAvailable = recommendations.Exists && recommendations.Count > 0,
				LocalHistoryAvailable = localEnvironment.Exists && localEnvironment.Count > 0
			};

			var hasAnyData = deck.Exists ||
				summary.Exists ||
				matrix.Exists ||
				manifest.Exists ||
				recommendations.Exists ||
				localEnvironment.Exists;
			var toolsAvailable = updateTool.Exists && refreshTool.Exists;
			var stale = IsStale(deck) ||
				IsStale(summary) ||
				IsStale(matrix) ||
				IsStale(manifest) ||
				IsStale(recommendations) ||
				IsStale(localEnvironment);

			if (metaDeckLoad != null && metaDeckLoad.Status == MetaDeckLoadStatus.Failed)
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Error;
			}
			else if (metaDeckLoad != null &&
				(metaDeckLoad.Status == MetaDeckLoadStatus.Loading ||
					metaDeckLoad.Status == MetaDeckLoadStatus.Empty))
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Partial;
			}
			else if (!hasAnyData)
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Empty;
			}
			else if (stale)
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Stale;
			}
			else if (snapshot.PredictionAvailable &&
				snapshot.RecommendationAvailable &&
				snapshot.PremiumAvailable &&
				toolsAvailable)
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Ready;
			}
			else
			{
				snapshot.OverallStatus = MetaDataHealthOverallStatus.Partial;
			}

			snapshot.UserMessage = BuildUserMessage(snapshot, toolsAvailable, metaDeckLoad);
			snapshot.DetailLines = BuildDetailLines(
				deck,
				summary,
				matrix,
				manifest,
				recommendations,
				localEnvironment,
				cookie,
				metaDeckLoad,
				updateTool,
				refreshTool);
			return snapshot;
		}

		private string GetPath(params string[] parts)
		{
			if (string.IsNullOrWhiteSpace(_dataDirectory))
			{
				return "";
			}

			return Path.Combine(new[] {_dataDirectory}.Concat(parts).ToArray());
		}

		private FileHealthInfo InspectFile(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return new FileHealthInfo();
			}

			return new FileHealthInfo
			{
				Exists = true,
				LastWriteTime = File.GetLastWriteTime(path)
			};
		}

		private FileHealthInfo InspectDeckSnapshot(string path)
		{
			var info = InspectFile(path);
			if (!info.Exists)
			{
				return info;
			}

			var lines = File.ReadAllLines(path, Encoding.UTF8);
			var countLine = lines.FirstOrDefault(line =>
				line.TrimStart().StartsWith("# Count:", StringComparison.OrdinalIgnoreCase));
			int count;
			if (countLine != null &&
				int.TryParse(
					countLine.Substring(countLine.IndexOf(':') + 1).Trim(),
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out count))
			{
				info.Count = count;
				return info;
			}

			info.Count = lines.Count(line =>
				!string.IsNullOrWhiteSpace(line) &&
				!line.TrimStart().StartsWith("#") &&
				DeckCodeRegex.IsMatch(line));
			return info;
		}

		private ManifestHealthInfo InspectManifest(string path)
		{
			var info = new ManifestHealthInfo();
			var file = InspectFile(path);
			info.Exists = file.Exists;
			info.LastWriteTime = file.LastWriteTime;
			if (!info.Exists)
			{
				return info;
			}

			var values = new JavaScriptSerializer().DeserializeObject(
				File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
			if (values == null)
			{
				throw new InvalidDataException("manifest.json is not a JSON object.");
			}

			info.SelectedTimeRange = FirstNonEmpty(
				StringValue(values, "selected_time_range"),
				StringValue(values, "time_range"));
			info.PatchVersion = StringValue(values, "patch_version");
			return info;
		}

		private FileHealthInfo InspectRecommendationFile(string path)
		{
			var info = InspectFile(path);
			if (!info.Exists)
			{
				return info;
			}

			var lines = File.ReadAllLines(path, Encoding.UTF8);
			info.Count = Math.Max(0, lines.Count(line => !string.IsNullOrWhiteSpace(line)) - 1);
			return info;
		}

		private FileHealthInfo InspectLocalEnvironment(string path)
		{
			var info = InspectFile(path);
			if (!info.Exists)
			{
				return info;
			}

			var lines = File.ReadAllLines(path, Encoding.UTF8)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.ToList();
			if (lines.Count < 2)
			{
				info.Count = 0;
				return info;
			}

			var headers = lines[0].Split('\t');
			var gamesIndex = Array.FindIndex(headers,
				header => string.Equals(header, "games", StringComparison.OrdinalIgnoreCase));
			if (gamesIndex < 0)
			{
				info.Count = lines.Count - 1;
				return info;
			}

			var games = 0;
			foreach (var line in lines.Skip(1))
			{
				var values = line.Split('\t');
				if (gamesIndex >= values.Length)
				{
					continue;
				}

				int parsed;
				if (int.TryParse(
					values[gamesIndex],
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out parsed))
				{
					games += Math.Max(0, parsed);
				}
			}

			info.Count = games;
			return info;
		}

		private CookieHealthInfo InspectCookie()
		{
			var path = GetPath("hsreplay_cookie.txt");
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				return new CookieHealthInfo();
			}

			var file = new FileInfo(path);
			return new CookieHealthInfo
			{
				Exists = true,
				LastWriteTime = file.LastWriteTime,
				HasValue = PostGameMetaRefresher.HasPremiumCookie(_dataDirectory)
			};
		}

		private MetaDeckLoadSnapshot InspectMetaDeckLoad()
		{
			try
			{
				return MetaDeckLoadStatusStore.Read(_dataDirectory);
			}
			catch (Exception ex)
			{
				return MetaDeckLoadSnapshot.Failed(
					MetaCompanionPlugin.SummarizeException(ex),
					_now,
					_now);
			}
		}

		private bool IsStale(FileHealthInfo info)
		{
			return info != null &&
				info.Exists &&
				info.LastWriteTime.HasValue &&
				_now >= info.LastWriteTime.Value &&
				_now - info.LastWriteTime.Value > _staleAfter;
		}

		private static List<string> BuildDetailLines(
			FileHealthInfo deck,
			FileHealthInfo summary,
			FileHealthInfo matrix,
			ManifestHealthInfo manifest,
			FileHealthInfo recommendations,
			FileHealthInfo localEnvironment,
			CookieHealthInfo cookie,
			MetaDeckLoadSnapshot metaDeckLoad,
			FileHealthInfo updateTool,
			FileHealthInfo refreshTool)
		{
			var lines = new List<string>
			{
				BuildCountLine("HSReplay \u724c\u7ec4\u5e93", deck, "\u5957"),
				BuildPresenceLine("Premium summary.json", summary),
				BuildPresenceLine("Premium \u5bf9\u9635\u77e9\u9635", matrix),
				BuildPresenceLine("Premium manifest.json", manifest)
			};

			if (metaDeckLoad != null)
			{
				lines.Add(metaDeckLoad.UserMessage);
			}

			if (manifest.Exists)
			{
				AddValueLine(lines, "\u8fdc\u7a0b\u65f6\u95f4\u8303\u56f4", manifest.SelectedTimeRange);
				AddValueLine(lines, "\u8865\u4e01\u7248\u672c", manifest.PatchVersion);
			}

			lines.Add(BuildCountLine("\u4e2a\u4eba\u63a8\u8350", recommendations, "\u884c"));
			lines.Add(BuildCountLine("\u672c\u5730\u73af\u5883\u6837\u672c", localEnvironment, "\u5c40"));
			lines.Add(cookie.HasValue
				? "Premium Cookie \u5df2\u914d\u7f6e"
				: "Premium Cookie \u672a\u914d\u7f6e");

			var missingTools = new List<string>();
			if (!updateTool.Exists)
			{
				missingTools.Add("Update-MetaCompanionData.ps1");
			}
			if (!refreshTool.Exists)
			{
				missingTools.Add("Run-MetaCompanionRefresh.ps1");
			}
			lines.Add(missingTools.Count == 0
				? "\u5237\u65b0\u811a\u672c: \u5df2\u5b89\u88c5"
				: "\u5237\u65b0\u811a\u672c\u7f3a\u5931: " + string.Join(", ", missingTools.ToArray()));
			return lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
		}

		private static string BuildUserMessage(
			MetaDataHealthSnapshot snapshot,
			bool toolsAvailable,
			MetaDeckLoadSnapshot metaDeckLoad)
		{
			if (metaDeckLoad != null && metaDeckLoad.Status == MetaDeckLoadStatus.Failed)
			{
				return metaDeckLoad.UserMessage;
			}
			if (metaDeckLoad != null && metaDeckLoad.Status == MetaDeckLoadStatus.Loading)
			{
				return "牌组库加载中，预测暂不可用";
			}
			if (metaDeckLoad != null && metaDeckLoad.Status == MetaDeckLoadStatus.Empty)
			{
				return metaDeckLoad.UserMessage;
			}

			switch (snapshot.OverallStatus)
			{
				case MetaDataHealthOverallStatus.Empty:
					return "\u5c1a\u672a\u751f\u6210\u6570\u636e\u5feb\u7167";
				case MetaDataHealthOverallStatus.Stale:
					return "\u6570\u636e\u5feb\u7167\u5df2\u8fc7\u671f\uff0c\u5efa\u8bae\u5237\u65b0\u672c\u5730\u7f13\u5b58";
				case MetaDataHealthOverallStatus.Error:
					return "\u6570\u636e\u5065\u5eb7\u68c0\u67e5\u5931\u8d25\uff0c\u8bf7\u91cd\u65b0\u751f\u6210\u6570\u636e\u5feb\u7167\u3002";
			}

			if (snapshot.PredictionAvailable &&
				snapshot.RecommendationAvailable &&
				snapshot.PremiumAvailable)
			{
				return toolsAvailable
					? "\u5bf9\u5c40\u9884\u6d4b\u4e0e\u63a8\u8350\u6570\u636e\u53ef\u7528"
					: "\u5bf9\u5c40\u9884\u6d4b\u4e0e\u63a8\u8350\u6570\u636e\u53ef\u7528\uff0c\u5237\u65b0\u811a\u672c\u7f3a\u5931";
			}
			if (snapshot.PredictionAvailable && !snapshot.RecommendationAvailable)
			{
				return "\u5bf9\u5c40\u9884\u6d4b\u53ef\u7528\uff0c\u63a8\u8350\u6570\u636e\u672a\u751f\u6210";
			}
			if (!snapshot.PredictionAvailable && snapshot.RecommendationAvailable)
			{
				return "\u63a8\u8350\u6570\u636e\u53ef\u7528\uff0c\u5bf9\u5c40\u9884\u6d4b\u672a\u751f\u6210";
			}
			if (snapshot.PremiumAvailable)
			{
				return "Premium \u6570\u636e\u5df2\u540c\u6b65\uff0c\u63a8\u8350\u6570\u636e\u672a\u751f\u6210";
			}
			if (snapshot.LocalHistoryAvailable)
			{
				return "\u672c\u5730\u5bf9\u5c40\u73af\u5883\u5df2\u751f\u6210\uff0c\u8fdc\u7a0b\u9884\u6d4b\u6570\u636e\u672a\u751f\u6210";
			}
			return "\u6570\u636e\u5feb\u7167\u4e0d\u5b8c\u6574";
		}

		private static string BuildCountLine(string label, FileHealthInfo info, string unit)
		{
			if (info == null || !info.Exists)
			{
				return label + ": \u672a\u751f\u6210";
			}

			return label + ": " + info.Count.ToString(CultureInfo.InvariantCulture) + " " + unit +
				" | \u66f4\u65b0\u4e8e " + FormatTime(info.LastWriteTime);
		}

		private static string BuildPresenceLine(string label, FileHealthInfo info)
		{
			if (info == null || !info.Exists)
			{
				return label + ": \u672a\u540c\u6b65";
			}

			return label + ": \u5df2\u540c\u6b65 | \u66f4\u65b0\u4e8e " + FormatTime(info.LastWriteTime);
		}

		private static void AddValueLine(List<string> lines, string label, string value)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				lines.Add(label + ": " + value);
			}
		}

		private static string FormatTime(DateTime? value)
		{
			return value.HasValue
				? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
				: "\u672a\u77e5";
		}

		private static string StringValue(Dictionary<string, object> values, string key)
		{
			if (values == null || key == null || !values.ContainsKey(key))
			{
				return "";
			}

			var value = values[key];
			return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values == null
				? ""
				: values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
		}

		private class FileHealthInfo
		{
			public bool Exists { get; set; }
			public int Count { get; set; }
			public DateTime? LastWriteTime { get; set; }
		}

		private class ManifestHealthInfo : FileHealthInfo
		{
			public string SelectedTimeRange { get; set; } = "";
			public string PatchVersion { get; set; } = "";
		}

		private class CookieHealthInfo : FileHealthInfo
		{
			public bool HasValue { get; set; }
		}
	}
}
