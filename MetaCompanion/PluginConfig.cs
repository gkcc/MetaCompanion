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

		public string CurrentMetaFileVersion { get; set;  }
		public DateTime CurrentMetaFileDownloadTime { get; set; }
		public bool EnableMetaDashboard { get; set; } = true;
		public bool FitDeckListToDisplay { get; set; } = true;
		public bool EnableLateGamePanel { get; set; } = true;
		public bool EnableNativeHdtOpponentPredictions { get; set; } = true;
		public int LateGameEvidenceThreshold { get; set; } = 8;
		public int LateGameRemainingDeckThreshold { get; set; } = 15;
		public int LateGamePredictionThreshold { get; set; } = 18;
		public bool LateGamePanelRightSide { get; set; } = false;
		public int LateGamePanelCardLimit { get; set; } = 10;
		public bool EnableMatchHistory { get; set; } = true;
		public bool EnablePredictionTimeline { get; set; } = true;
		public int LocalRecommendationHistoryDays { get; set; } = 3;
		public double LocalRecommendationWeight { get; set; } = 0.35;
		public int LocalRecommendationTop { get; set; } = 20;
		public int LocalMetaMinConfidence { get; set; } = 35;
		public bool EnablePostGameMetaRefresh { get; set; } = false;
		public int PostGameMetaRefreshDelaySeconds { get; set; } = 8;
		public int PostGameMetaRefreshCooldownMinutes { get; set; } = 2;
		public bool EnablePostGameDataRefresh { get; set; } = false;
		public int PostGameDataRefreshCooldownHours { get; set; } = 24;
		public string PostGamePrimaryTimeRange { get; set; } = "CURRENT_PATCH";
		public string PostGameMetaFallbackTimeRange { get; set; } = "LAST_3_DAYS";
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

		public static PluginConfig Load()
		{
			if (File.Exists(ConfigPath))
			{
				using (var reader = new StreamReader(ConfigPath))
				{
					return Load(reader);
				}
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
			Save();
		}

		public void Save()
		{
			Log.Debug("Saving config");
			using (var writer = new StreamWriter(ConfigPath))
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
