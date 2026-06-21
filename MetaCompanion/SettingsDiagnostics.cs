using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MetaCompanion
{
	internal static class SettingsDiagnostics
	{
		public static string BuildDiagnosticText(
			DateTime generatedAt,
			string dataDirectory,
			string logDirectory,
			string dataStatus,
			string recommendationStatus,
			string premiumStatus,
			MetaDataHealthSnapshot dataHealthSnapshot,
			RefreshTaskSnapshot refreshTaskSnapshot)
		{
			var builder = new StringBuilder();
			builder.AppendLine("Meta Companion 诊断信息");
			builder.AppendLine("生成时间: " +
				generatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
			AppendLineIfNotEmpty(builder, "数据目录: " + dataDirectory);
			AppendLineIfNotEmpty(builder, "日志目录: " + logDirectory);
			builder.AppendLine();
			builder.AppendLine("[数据源]");
			AppendLineIfNotEmpty(builder, dataStatus);
			AppendLineIfNotEmpty(builder, recommendationStatus);
			AppendLineIfNotEmpty(builder, premiumStatus);

			builder.AppendLine();
			builder.AppendLine("[数据健康]");
			if (dataHealthSnapshot == null)
			{
				builder.AppendLine("数据健康状态不可用");
			}
			else
			{
				AppendLineIfNotEmpty(builder, dataHealthSnapshot.UserMessage);
				foreach (var line in dataHealthSnapshot.DetailLines ?? new List<string>())
				{
					AppendLineIfNotEmpty(builder, line);
				}
			}

			builder.AppendLine();
			builder.AppendLine("[自动刷新]");
			if (refreshTaskSnapshot == null)
			{
				builder.AppendLine("自动刷新状态不可用");
			}
			else
			{
				AppendLineIfNotEmpty(builder, refreshTaskSnapshot.ToolsStatus);
				AppendLineIfNotEmpty(builder, refreshTaskSnapshot.ScheduledTaskStatus);
				AppendLineIfNotEmpty(builder, refreshTaskSnapshot.LatestLogStatus);
				AppendLineIfNotEmpty(builder, refreshTaskSnapshot.LatestLogPath);

				builder.AppendLine();
				builder.AppendLine("[最近刷新日志摘要]");
				if (refreshTaskSnapshot.LatestLogSummaryLines == null ||
					refreshTaskSnapshot.LatestLogSummaryLines.Count == 0)
				{
					builder.AppendLine("未找到日志摘要");
				}
				else
				{
					foreach (var line in refreshTaskSnapshot.LatestLogSummaryLines)
					{
						AppendLineIfNotEmpty(builder, line);
					}
				}
			}

			return RefreshTaskService.SanitizeDiagnosticText(builder.ToString().TrimEnd());
		}

		private static void AppendLineIfNotEmpty(StringBuilder builder, string line)
		{
			if (!string.IsNullOrWhiteSpace(line))
			{
				builder.AppendLine(line);
			}
		}
	}
}
