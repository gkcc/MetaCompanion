using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MetaCompanion
{
	internal class PostGameMetaRefresher
	{
		private const string DefaultDeckRankRanges =
			"DIAMOND_THROUGH_LEGEND,DIAMOND_FOUR_THROUGH_DIAMOND_ONE,PLATINUM,GOLD,BRONZE_THROUGH_GOLD";
		private const int DefaultDeckLimitPerRange = 250;

		private readonly object _lock = new object();
		private DateTime _lastStartedAt = DateTime.MinValue;
		private bool _isRunning;

		public bool TryRefreshAfterGame(PluginConfig config, Action onCompleted)
		{
			config = config ?? new PluginConfig();
			if (!config.EnablePostGameMetaRefresh)
			{
				Log.Debug("Post-game local meta refresh is disabled.");
				return false;
			}

			var scriptPath = GetUpdateScriptPath(MetaCompanionPlugin.DataDirectory);
			if (!File.Exists(scriptPath))
			{
				Log.Warn("Post-game local meta refresh script not found: " + scriptPath);
				return false;
			}

			lock (_lock)
			{
				if (_isRunning)
				{
					Log.Debug("Post-game local meta refresh is already running.");
					return false;
				}

				var cooldown = TimeSpan.FromMinutes(
					Math.Max(0, config.PostGameMetaRefreshCooldownMinutes));
				if (cooldown > TimeSpan.Zero && DateTime.Now - _lastStartedAt < cooldown)
				{
					Log.Debug("Post-game local meta refresh skipped by cooldown.");
					return false;
				}

				_isRunning = true;
				_lastStartedAt = DateTime.Now;
			}

			var refreshPlan = BuildRefreshPlan(config, MetaCompanionPlugin.DataDirectory, DateTime.Now);
			Task.Run(async () =>
				{
					try
					{
						var delaySeconds = Math.Max(0, config.PostGameMetaRefreshDelaySeconds);
						if (delaySeconds > 0)
						{
							await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
						}

						RunRefresh(scriptPath, config, refreshPlan);
						onCompleted?.Invoke();
					}
					catch (Exception ex)
					{
						Log.Warn("Post-game local meta refresh failed: " + ex.Message);
					}
					finally
					{
						lock (_lock)
						{
							_isRunning = false;
						}
					}
				});
			return true;
		}

		internal static string GetUpdateScriptPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "Tools", "Update-MetaCompanionData.ps1");
		}

		internal static string GetDeckSnapshotPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "hsreplay_deckcodes.txt");
		}

		internal static string GetBranchSnapshotPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "archetype_deck_branches.tsv");
		}

		internal static string GetMetaSummaryPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "Premium", "Meta", "latest", "summary.json");
		}

		internal static string GetMetaMatrixPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "Premium", "Meta", "latest",
				"head_to_head_archetype_matchups_v2.json");
		}

		internal static string GetDataRefreshAttemptPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "post_game_data_refresh.last");
		}

		internal static string GetPremiumCookiePath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "hsreplay_cookie.txt");
		}

		internal static string BuildArguments(string scriptPath, PluginConfig config)
		{
			return BuildArguments(scriptPath, config, false);
		}

		internal static string BuildArguments(
			string scriptPath,
			PluginConfig config,
			bool includeFullDataRefresh,
			string premiumTimeRangeOverride = null,
			string metaTimeRangeOverride = null,
			string branchCandidateTimeRangeOverride = null,
			bool premiumStopOnUnsupported = false,
			bool includeDeckSnapshotRefresh = false,
			bool includePersonalRecommendations = true)
		{
			config = config ?? new PluginConfig();
			var arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
				" -LocalMeta" +
				(includePersonalRecommendations ? " -PersonalRecommendations" : "") +
				" -RecommendationTop " + Math.Max(1, config.LocalRecommendationTop)
					.ToString(CultureInfo.InvariantCulture) +
				" -PersonalRecommendationHistoryDays " + Math.Max(1, config.LocalRecommendationHistoryDays)
					.ToString(CultureInfo.InvariantCulture) +
				" -PersonalRecommendationLocalWeight " + config.LocalRecommendationWeight
					.ToString(CultureInfo.InvariantCulture) +
				" -LocalMetaMinConfidence " + Math.Max(0, config.LocalMetaMinConfidence)
					.ToString(CultureInfo.InvariantCulture);

			if (includeDeckSnapshotRefresh || includeFullDataRefresh)
			{
				arguments +=
					" -RankRanges " + Quote(DefaultDeckRankRanges) +
					" -LimitPerRange " + DefaultDeckLimitPerRange.ToString(CultureInfo.InvariantCulture) +
					" -MaxDecks " + Math.Max(1, config.PostGameDataRefreshMaxDecks)
						.ToString(CultureInfo.InvariantCulture) +
					" -Parallelism " + Math.Max(1, config.PostGameDataRefreshParallelism)
						.ToString(CultureInfo.InvariantCulture);
			}

			if (includeFullDataRefresh)
			{
				var premiumTimeRange = NormalizeValue(
					premiumTimeRangeOverride,
					NormalizeValue(config.PostGamePrimaryTimeRange, "CURRENT_PATCH"));
				var metaTimeRange = NormalizeValue(
					metaTimeRangeOverride,
					NormalizeValue(config.PostGamePrimaryTimeRange, "CURRENT_PATCH"));
				arguments +=
					" -Premium -Meta" +
					" -PremiumTimeRange " + Quote(premiumTimeRange) +
					" -MetaTimeRange " + Quote(metaTimeRange) +
					" -PremiumMaxDecks " + Math.Max(1, config.PostGamePremiumRefreshMaxDecks)
						.ToString(CultureInfo.InvariantCulture);
				if (premiumStopOnUnsupported)
				{
					arguments += " -PremiumStopOnUnsupported";
				}
			}

			return arguments;
		}

		internal static string BuildArguments(
			string scriptPath,
			PluginConfig config,
			PostGameRefreshPlan refreshPlan,
			bool useFallbackRanges = false)
		{
			refreshPlan = refreshPlan ?? new PostGameRefreshPlan();
			return BuildArguments(
				scriptPath,
				config,
				refreshPlan.IncludeFullDataRefresh,
				useFallbackRanges ? refreshPlan.PremiumFallbackTimeRange : refreshPlan.PrimaryTimeRange,
				useFallbackRanges ? refreshPlan.MetaFallbackTimeRange : refreshPlan.PrimaryTimeRange,
				useFallbackRanges ? refreshPlan.PremiumFallbackTimeRange : refreshPlan.PrimaryTimeRange,
				refreshPlan.IncludeFullDataRefresh && !useFallbackRanges,
				refreshPlan.IncludeDeckSnapshotRefresh,
				refreshPlan.IncludePersonalRecommendations);
		}

		internal static PostGameRefreshPlan BuildRefreshPlan(
			PluginConfig config,
			string dataDirectory,
			DateTime now)
		{
			config = config ?? new PluginConfig();
			var plan = new PostGameRefreshPlan();
			if (!config.EnablePostGameDataRefresh)
			{
				plan.IncludePersonalRecommendations = HasPremiumMetaCache(dataDirectory);
				return plan;
			}

			var maxAge = TimeSpan.FromHours(Math.Max(1, config.PostGameDataRefreshCooldownHours));
			if (IsFresh(GetDataRefreshAttemptPath(dataDirectory), now, maxAge))
			{
				plan.IncludePersonalRecommendations = HasPremiumMetaCache(dataDirectory);
				return plan;
			}

			var deckSnapshotFresh = IsFresh(GetDeckSnapshotPath(dataDirectory), now, maxAge);
			var premiumMetaFresh = IsFresh(GetMetaSummaryPath(dataDirectory), now, maxAge) &&
				IsFresh(GetMetaMatrixPath(dataDirectory), now, maxAge);
			var hasPremiumMetaCache = HasPremiumMetaCache(dataDirectory);
			var hasPremiumCookie = HasPremiumCookie(dataDirectory);

			if (!deckSnapshotFresh)
			{
				plan.IncludeDeckSnapshotRefresh = true;
			}

			if (hasPremiumCookie && (!deckSnapshotFresh || !premiumMetaFresh))
			{
				plan.IncludeFullDataRefresh = true;
				plan.IncludeDeckSnapshotRefresh = true;
				plan.IncludePersonalRecommendations = true;
			}
			else
			{
				plan.IncludePersonalRecommendations = hasPremiumMetaCache;
			}

			plan.PrimaryTimeRange = NormalizeValue(config.PostGamePrimaryTimeRange, "CURRENT_PATCH");
			plan.MetaFallbackTimeRange = NormalizeValue(config.PostGameMetaFallbackTimeRange, "LAST_3_DAYS");
			plan.PremiumFallbackTimeRange = NormalizeValue(config.PostGamePremiumFallbackTimeRange, "LAST_7_DAYS");
			return plan;
		}

		internal static bool IsFullDataRefreshNeeded(
			string dataDirectory,
			DateTime now,
			TimeSpan maxAge)
		{
			return !IsFresh(GetDeckSnapshotPath(dataDirectory), now, maxAge) ||
				!IsFresh(GetMetaSummaryPath(dataDirectory), now, maxAge) ||
				!IsFresh(GetMetaMatrixPath(dataDirectory), now, maxAge);
		}

		internal static bool HasPremiumCookie(string dataDirectory)
		{
			var path = GetPremiumCookiePath(dataDirectory);
			if (!File.Exists(path))
			{
				return false;
			}

			return File.ReadLines(path)
				.Any(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"));
		}

		internal static bool HasPremiumMetaCache(string dataDirectory)
		{
			return File.Exists(GetMetaSummaryPath(dataDirectory)) &&
				File.Exists(GetMetaMatrixPath(dataDirectory));
		}

		private static void RunRefresh(
			string scriptPath,
			PluginConfig config,
			PostGameRefreshPlan refreshPlan)
		{
			var logDirectory = Path.Combine(MetaCompanionPlugin.DataDirectory, "Logs");
			Directory.CreateDirectory(logDirectory);
			var logPath = Path.Combine(
				logDirectory,
				"post-game-refresh-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

			refreshPlan = refreshPlan ?? new PostGameRefreshPlan();
			if (refreshPlan.IncludeFullDataRefresh)
			{
				TouchDataRefreshAttempt(MetaCompanionPlugin.DataDirectory);
				Log.Info("Post-game full HSReplay data refresh is due.");
			}

			var primaryArguments = BuildArguments(scriptPath, config, refreshPlan);

			try
			{
				RunRefreshProcess(scriptPath, primaryArguments, logPath);
			}
			catch (Exception ex)
			{
				if (!refreshPlan.ShouldRetryWithFallback)
				{
					throw;
				}

				Log.Warn("Post-game full data refresh failed with primary time range " +
					refreshPlan.PrimaryTimeRange + "; retrying with fallback ranges. " + ex.Message);
				var fallbackLogPath = Path.Combine(
					logDirectory,
					"post-game-refresh-fallback-" +
					DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
				var fallbackArguments = BuildArguments(scriptPath, config, refreshPlan, true);
				RunRefreshProcess(scriptPath, fallbackArguments, fallbackLogPath);
			}
			Log.Info("Post-game local meta refresh complete.");
		}

		private static void RunRefreshProcess(string scriptPath, string arguments, string logPath)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = arguments,
				WorkingDirectory = Path.GetDirectoryName(scriptPath),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};

			Log.Info("Starting post-game meta refresh.");
			using (var process = Process.Start(startInfo))
			{
				if (process == null)
				{
					throw new InvalidOperationException("Unable to start powershell.exe");
				}

				var output = process.StandardOutput.ReadToEnd();
				var error = process.StandardError.ReadToEnd();
				process.WaitForExit();

				File.WriteAllText(
					logPath,
					output + Environment.NewLine + error,
					System.Text.Encoding.UTF8);

				if (process.ExitCode != 0)
				{
					throw new InvalidOperationException(
						"refresh script exited with code " + process.ExitCode + "; log: " + logPath);
				}
			}
		}

		private static bool IsFresh(string path, DateTime now, TimeSpan maxAge)
		{
			if (!File.Exists(path))
			{
				return false;
			}

			return now - File.GetLastWriteTime(path) < maxAge;
		}

		private static void TouchDataRefreshAttempt(string dataDirectory)
		{
			Directory.CreateDirectory(dataDirectory);
			File.WriteAllText(
				GetDataRefreshAttemptPath(dataDirectory),
				DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
		}

		private static string Quote(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
		}

		private static string NormalizeValue(string value, string fallback)
		{
			value = (value ?? "").Trim();
			return string.IsNullOrEmpty(value) ? fallback : value;
		}
	}

	internal class PostGameRefreshPlan
	{
		public bool IncludeDeckSnapshotRefresh { get; set; }
		public bool IncludeFullDataRefresh { get; set; }
		public bool IncludePersonalRecommendations { get; set; } = true;
		public string PrimaryTimeRange { get; set; } = "CURRENT_PATCH";
		public string MetaFallbackTimeRange { get; set; } = "LAST_3_DAYS";
		public string PremiumFallbackTimeRange { get; set; } = "LAST_7_DAYS";

		public bool ShouldRetryWithFallback
		{
			get
			{
				return IncludeFullDataRefresh &&
					(!string.Equals(PrimaryTimeRange, MetaFallbackTimeRange, StringComparison.OrdinalIgnoreCase) ||
						!string.Equals(PrimaryTimeRange, PremiumFallbackTimeRange, StringComparison.OrdinalIgnoreCase));
			}
		}
	}
}
