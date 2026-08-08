using System;
using System.IO;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MetaCompanion
{
	public class PluginConfig
	{
		private static readonly string ConfigPath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, "config.xml");
		private bool _recommendationScopeSettingsMigrated;

		public string CurrentMetaFileVersion { get; set;  }
		public DateTime CurrentMetaFileDownloadTime { get; set; }
		public bool EnableMetaDashboard { get; set; } = true;
		public bool FitDeckListToDisplay { get; set; } = true;
		public bool EnableLateGamePanel { get; set; } = true;
		public bool EnableNativeHdtOpponentPredictions { get; set; } = true;
		public bool EnableLiveAdvisor { get; set; } = true;
		public bool EnableAdvisorTrainingLog { get; set; } = true;
		private AdvisorWorkerBackendMode _advisorWorkerBackendMode =
			global::MetaCompanion.AdvisorWorkerBackendMode.RustOnly;
		[XmlIgnore]
		public AdvisorWorkerBackendMode AdvisorWorkerBackendMode
		{
			get { return _advisorWorkerBackendMode; }
			set
			{
				_advisorWorkerBackendMode = Enum.IsDefined(
					typeof(global::MetaCompanion.AdvisorWorkerBackendMode), value)
						? value
						: global::MetaCompanion.AdvisorWorkerBackendMode.RustOnly;
			}
		}
		[XmlElement("AdvisorWorkerBackendMode")]
		public string AdvisorWorkerBackendModeSerialized
		{
			get { return AdvisorWorkerBackendMode.ToString(); }
			set
			{
				AdvisorWorkerBackendMode parsed;
				AdvisorWorkerBackendMode = Enum.TryParse(value, true, out parsed) &&
					Enum.IsDefined(typeof(global::MetaCompanion.AdvisorWorkerBackendMode), parsed)
						? parsed
						: global::MetaCompanion.AdvisorWorkerBackendMode.RustOnly;
			}
		}
		public int AdvisorInitialResultSeconds { get; set; } = 3;
		public int AdvisorSearchSeconds { get; set; } = 10;
		public int LateGameEvidenceThreshold { get; set; } = 8;
		public int LateGameRemainingDeckThreshold { get; set; } = 15;
		public int LateGamePredictionThreshold { get; set; } = 18;
		public bool LateGamePanelRightSide { get; set; } = false;
		public int LateGamePanelCardLimit { get; set; } = 10;
		public bool EnableMatchHistory { get; set; } = true;
		public bool EnablePredictionTimeline { get; set; } = true;
		public int LocalRecommendationHistoryDays { get; set; } = 3;
		public int LocalRecommendationHistoryMatches { get; set; } = 0;
		public DateTime LocalRecommendationHistoryClearedAt { get; set; } = DateTime.MinValue;
		public double LocalRecommendationWeight { get; set; } = 0.35;
		public int LocalRecommendationTop { get; set; } = 20;
		public int LocalMetaMinConfidence { get; set; } = 35;
		public double RecommendationRemotePriorGames { get; set; } =
			RecommendationStatistics.DefaultRemotePriorGames;
		public double RecommendationMatchupPriorGames { get; set; } =
			RecommendationStatistics.DefaultMatchupPriorGames;
		public int RecommendationPosteriorDraws { get; set; } =
			RecommendationStatistics.DefaultPosteriorDraws;
		public bool EnablePostGameMetaRefresh { get; set; } = false;
		public int PostGameMetaRefreshDelaySeconds { get; set; } = 8;
		public int PostGameMetaRefreshCooldownMinutes { get; set; } = 2;
		public bool EnablePostGameDataRefresh { get; set; } = false;
		public int PostGameDataRefreshCooldownHours { get; set; } = 24;
		public string PostGamePrimaryTimeRange { get; set; } = "LAST_7_DAYS";
		public string PostGameRankRange { get; set; } = "DIAMOND_THROUGH_LEGEND";
		public int RecommendationScopeSettingsVersion { get; set; } = 0;
		public string PostGameMetaFallbackTimeRange { get; set; } = "LAST_1_DAY";
		public string PostGamePremiumFallbackTimeRange { get; set; } = "LAST_7_DAYS";
		public int PostGameDataRefreshMaxDecks { get; set; } = 500;
		public int PostGamePremiumRefreshMaxDecks { get; set; } = 30;
		public int PostGameDataRefreshParallelism { get; set; } = 4;
		public bool PostGameDashboardPersistent { get; set; } = true;
		public int PostGameDashboardAutoHideSeconds { get; set; } = 0;
		public bool HasLateGamePanelPosition { get; set; } = false;
		public double LateGamePanelPositionX { get; set; } = 0.0;
		public double LateGamePanelPositionY { get; set; } = 0.0;
		public bool HasDashboardPanelPosition { get; set; } = false;
		public double DashboardPanelPositionX { get; set; } = 0.75;
		public double DashboardPanelPositionY { get; set; } = 0.1;
		public bool HasAdvisorPanelPosition { get; set; } = false;
		public double AdvisorPanelPositionX { get; set; } = 0.72;
		public double AdvisorPanelPositionY { get; set; } = 0.18;

		public static PluginConfig Load()
		{
			return Load(ConfigPath);
		}

		internal static PluginConfig Load(string configPath)
		{
			if (File.Exists(configPath))
			{
				PluginConfig config;
				using (var reader = new StreamReader(configPath))
				{
					config = Load(reader);
				}
				if (config != null && config._recommendationScopeSettingsMigrated)
				{
					try
					{
						config.Save(configPath);
					}
					catch (Exception ex)
					{
						Log.Warn("Recommendation scope config migration could not be saved: " + ex);
					}
				}
				return config;
			}
			else
			{
				return new PluginConfig();
			}
		}

		public static PluginConfig Load(StreamReader reader) {
			var serializer = new XmlSerializer(typeof(PluginConfig));
			PluginConfig config = null;
			try
			{
				config = (PluginConfig)serializer.Deserialize(reader);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
			if (config != null && config.RecommendationScopeSettingsVersion < 1)
			{
				if (string.IsNullOrWhiteSpace(config.PostGamePrimaryTimeRange) ||
					string.Equals(
						config.PostGamePrimaryTimeRange,
						"CURRENT_PATCH",
						StringComparison.OrdinalIgnoreCase))
				{
					config.PostGamePrimaryTimeRange = "LAST_7_DAYS";
				}
				if (string.IsNullOrWhiteSpace(config.PostGameRankRange))
				{
					config.PostGameRankRange = "DIAMOND_THROUGH_LEGEND";
				}
				config.RecommendationScopeSettingsVersion = 1;
				config._recommendationScopeSettingsMigrated = true;
			}
			return config;
		}

		public PluginConfig()
		{
			this.CurrentMetaFileVersion = "1";
		}

		public void ResetOverlayPositions()
		{
			HasLateGamePanelPosition = false;
			LateGamePanelPositionX = 0.0;
			LateGamePanelPositionY = 0.0;
			HasDashboardPanelPosition = false;
			DashboardPanelPositionX = 0.75;
			DashboardPanelPositionY = 0.1;
			HasAdvisorPanelPosition = false;
			AdvisorPanelPositionX = 0.72;
			AdvisorPanelPositionY = 0.18;
			Save();
		}

		public void Save()
		{
			Save(ConfigPath);
		}

		internal void Save(string configPath)
		{
			Log.Debug("Saving config");
			var directory = Path.GetDirectoryName(configPath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}
			using (var writer = new StreamWriter(configPath))
			{
				Save(writer);
			}
		}

		public void Save(StreamWriter writer) {
			var serializer = new XmlSerializer(typeof(PluginConfig));
			try
			{
				serializer.Serialize(writer, this);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
	}
}
