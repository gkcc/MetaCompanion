using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MetaCompanion
{
	internal sealed class TrainingLogSummary
	{
		internal TrainingLogSummary()
		{
			StatusMessage = "训练记录：尚无新轨迹。";
			ModelCoverageLines = new List<string>();
			LegacyMessage = "";
			LastUpdatedMessage = "";
			ReadIssueMessage = "";
			DeveloperNote = "开发者数据：原始严格轨迹与行为语料保持不变，不在设置页展示或打开。";
		}

		public string StatusMessage { get; internal set; }
		public List<string> ModelCoverageLines { get; internal set; }
		public string LegacyMessage { get; internal set; }
		public string LastUpdatedMessage { get; internal set; }
		public string ReadIssueMessage { get; internal set; }
		public string DeveloperNote { get; internal set; }
		public bool HasCurrentLog { get; internal set; }
		public bool HasLegacyLog { get; internal set; }
		public bool HasBehaviorLog { get; internal set; }
		public int RecentActionCount { get; internal set; }
		public int RecentReplayableActionCount { get; internal set; }
		public int RecentPowerIdentityCount { get; internal set; }
		public int RecentIncompleteActionCount { get; internal set; }
		public int RecentBehaviorCount { get; internal set; }
		public int RecentLocalBehaviorCount { get; internal set; }
		public int RecentOpponentBehaviorCount { get; internal set; }
		public int RecentUnknownBehaviorCount { get; internal set; }
		public int RecentBehaviorEligibleCount { get; internal set; }
	}

	internal enum UserMessageSeverity
	{
		Normal,
		Notice,
		ActionRequired
	}

	internal static class SettingsDiagnostics
	{
		internal const string CurrentTrainingLogFileName = "training-v2.jsonl";
		internal const string CurrentBehaviorLogFileName = "behavior-v1.jsonl";
		internal const string LegacyTrainingLogFileName = "training.jsonl";
		internal const string DeveloperLogDisclosure =
			"开发者日志用于排查问题，可能包含英文技术信息；普通使用请以界面中的中文摘要为准。";
		internal const string DeveloperLogConfirmation =
			"即将打开开发者日志。日志可能包含英文技术信息、文件位置和程序调用信息，这些内容并不都表示插件发生故障。\n\n普通使用无需查看。是否继续？";
		private const long TrainingLogReadLimitBytes = 4L * 1024L * 1024L;
		private const int TrainingLogLineLimitChars = 1024 * 1024;
		private const int MajorCategoryLimit = 3;

		private static readonly Regex ChineseTextRegex = new Regex(
			@"[\u3400-\u9fff]",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);
		private static readonly Regex TechnicalDetailRegex = new Regex(
			@"(?i)(\b[A-Za-z0-9_.]*(?:Exception|Error|Warning|Fatal)\b|\b(?:failed|failure|traceback|stack\s*trace|inner\s+exception|access\s+denied|unauthorized|forbidden|HRESULT|PowerShell(?:\.exe)?|cmd\.exe)\b|\bHTTP\s*[45]\d\d\b|\b0x[0-9a-f]{8}\b|\brequest[_\s-]?id\b|\b(?:token|cookie|authorization)\b\s*[:=]|\b(?:process\s*id|pid|line|file)\b\s*[:=#]?\s*\d*|\.(?:ps1|log|jsonl?|tsv)\b|\bat\s+[A-Za-z_][A-Za-z0-9_.<>]*\s*\(|[A-Za-z]:[\\/]|(?:\\\\|//)[^\\/\s]+[\\/]|/(?:Users|home|tmp|var)/)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		internal static string BuildUserStatus(
			UserMessageSeverity severity,
			string message,
			string nextStep = "")
		{
			var prefix = severity == UserMessageSeverity.Normal
				? "正常："
				: severity == UserMessageSeverity.Notice
					? "提示："
					: "需处理：";
			var fallback = severity == UserMessageSeverity.ActionRequired
				? "当前状态暂时无法确认。"
				: "当前状态暂无更多信息。";
			var safeMessage = HideTechnicalDetails(message, fallback);
			if (string.IsNullOrWhiteSpace(safeMessage))
			{
				safeMessage = fallback;
			}

			var result = prefix + EnsureSentence(
				safeMessage.Trim().TrimEnd('。', '！', '!', '：', ':'));
			var safeNextStep = HideTechnicalDetails(nextStep, "");
			if (!string.IsNullOrWhiteSpace(safeNextStep))
			{
				result += EnsureSentence(safeNextStep.Trim());
			}
			return result;
		}

		internal static string BuildUserFacingFailure(string action, string nextStep)
		{
			var subject = string.IsNullOrWhiteSpace(action) ? "操作" : action.Trim();
			subject = subject.TrimEnd('。', '！', '!', '：', ':');
			var message = BuildUserStatus(
				UserMessageSeverity.ActionRequired,
				subject + "失败",
				nextStep);
			return message + "技术详情已写入开发者日志（可能含英文）。";
		}

		internal static string HideTechnicalDetails(string text, string fallback)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return "";
			}

			var trimmed = text.Trim();
			return !ChineseTextRegex.IsMatch(trimmed) || TechnicalDetailRegex.IsMatch(trimmed)
				? fallback
				: trimmed;
		}

		internal static List<string> BuildUserVisibleLines(
			IEnumerable<string> lines,
			string technicalFallback)
		{
			var result = new List<string>();
			foreach (var line in lines ?? Enumerable.Empty<string>())
			{
				var visible = HideTechnicalDetails(line, technicalFallback);
				if (!string.IsNullOrWhiteSpace(visible) && !result.Contains(visible))
				{
					result.Add(visible);
				}
			}
			return result;
		}

		internal static List<string> BuildAdvisorModelStatusLines(
			AdvisorWorkerHealth health,
			bool runtimeRequested)
		{
			if (health == null)
			{
				return new List<string>
				{
					BuildUserStatus(
						UserMessageSeverity.Notice,
						runtimeRequested
							? "本机求解器正在启动或等待后台重试"
							: "本机求解器当前未运行",
						runtimeRequested
							? "稍后点击“刷新状态”查看模型状态"
							: "启用实战建议或脱敏数据保存后可查看模型状态")
				};
			}

			if (!health.IsReady)
			{
				return new List<string>
				{
					BuildUserStatus(
						UserMessageSeverity.ActionRequired,
						"本机求解器尚未就绪",
						"插件会在后台自动重试")
				};
			}

			if (!string.Equals(health.Backend, "rust", StringComparison.OrdinalIgnoreCase))
			{
				return new List<string>
				{
					BuildUserStatus(
						UserMessageSeverity.Notice,
						"当前使用兼容求解器，本方与对手的独立排序模型未接入",
						"基础建议与脱敏行为采集仍可继续")
				};
			}

			return new List<string>
			{
				BuildOrderingModelHealthLine(
					"本方决策排序器",
					health.SupportsDecisionRanker,
					health.DecisionRankerAvailable,
					health.DecisionRankerStatus,
					"仅重排规则引擎给出的本方合法候选"),
				BuildOrderingModelHealthLine(
					"对手行为先验",
					health.SupportsBehaviorSearchOrderingPrior,
					health.BehaviorPriorAvailable,
					health.BehaviorPriorStatus,
					"仅重排对手公开回应的搜索顺序")
			};
		}

		private static string BuildOrderingModelHealthLine(
			string label,
			bool supported,
			bool available,
			string status,
			string scope)
		{
			if (!supported)
			{
				return BuildUserStatus(
					UserMessageSeverity.Notice,
					label + "未由当前求解器提供",
					"继续使用确定性的基础搜索顺序");
			}
			var normalized = (status ?? "").Trim().ToLowerInvariant();
			if (available && normalized == "ready")
			{
				return BuildUserStatus(
					UserMessageSeverity.Normal,
					label + "可用",
					scope + "；不会生成动作、覆盖战术分数或声称最优");
			}
			if (normalized == "rejected")
			{
				return BuildUserStatus(
					UserMessageSeverity.ActionRequired,
					label + "未通过完整性或质量检查，已安全停用",
					"基础求解不受影响；重新训练并通过门禁后会自动恢复");
			}
			if (normalized == "disabled" || normalized == "not_found" ||
				string.IsNullOrWhiteSpace(normalized))
			{
				return BuildUserStatus(
					UserMessageSeverity.Notice,
					label + "尚未启用",
					"当前使用确定性的基础搜索顺序");
			}
			return BuildUserStatus(
				UserMessageSeverity.Notice,
				label + "状态暂时无法确认",
				"当前使用确定性的基础搜索顺序");
		}

		internal static string GetCurrentTrainingLogPath(string dataDirectory)
		{
			return Path.Combine(
				dataDirectory ?? "",
				"AdvisorWorker",
				CurrentTrainingLogFileName);
		}

		internal static string GetCurrentBehaviorLogPath(string dataDirectory)
		{
			return Path.Combine(
				dataDirectory ?? "",
				"AdvisorWorker",
				CurrentBehaviorLogFileName);
		}

		internal static double CalculateSettingsMaxHeight(double workAreaHeight)
		{
			if (Double.IsNaN(workAreaHeight) || Double.IsInfinity(workAreaHeight) ||
				workAreaHeight <= 0)
				return 760;
			return Math.Min(760, Math.Max(200, workAreaHeight - 40));
		}

		internal static string GetLegacyTrainingLogPath(string dataDirectory)
		{
			return Path.Combine(
				dataDirectory ?? "",
				"AdvisorWorker",
				LegacyTrainingLogFileName);
		}

		internal static TrainingLogSummary InspectTrainingLogs(string dataDirectory)
		{
			var currentPath = GetCurrentTrainingLogPath(dataDirectory);
			var behaviorPath = GetCurrentBehaviorLogPath(dataDirectory);
			var legacyPath = GetLegacyTrainingLogPath(dataDirectory);
			var summary = new TrainingLogSummary
			{
				HasCurrentLog = File.Exists(currentPath),
				HasLegacyLog = File.Exists(legacyPath),
				HasBehaviorLog = File.Exists(behaviorPath)
			};

			if (summary.HasLegacyLog)
			{
				summary.LegacyMessage =
					"检测到旧版训练记录，已作为只读历史数据保留；插件不会继续追加或改写。";
			}

			if (!summary.HasCurrentLog && !summary.HasLegacyLog && !summary.HasBehaviorLog)
			{
				return summary;
			}

			InspectBehaviorLog(summary, behaviorPath);
			if (!summary.HasCurrentLog && !summary.HasLegacyLog)
			{
				summary.StatusMessage = AppendBehaviorSummary(
					"训练记录：尚无严格可回放轨迹。",
					summary);
				SetLastUpdatedMessage(summary, behaviorPath);
				return summary;
			}

			var summarizesLegacy = !summary.HasCurrentLog && summary.HasLegacyLog;
			var inspectedPath = summarizesLegacy ? legacyPath : currentPath;
			if (summarizesLegacy)
			{
				summary.LegacyMessage =
					"当前摘要来自旧版历史记录（只读）；插件不会继续追加或改写。";
			}

			var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			var warningCounts = new Dictionary<string, int>(StringComparer.Ordinal);
			var recordCount = 0;
			var solveCount = 0;
			var actionCount = 0;
			var replayableActionCount = 0;
			var powerIdentityCount = 0;
			var incompleteActionCount = 0;
			var invalidLineCount = 0;
			try
			{
				SetLastUpdatedMessage(
					summary,
					summary.HasBehaviorLog ? behaviorPath : inspectedPath,
					inspectedPath);
				ReadRecentTrainingRecords(
					inspectedPath,
					record =>
					{
						recordCount++;
						if (!summarizesLegacy && IsActionObservation(record))
						{
							actionCount++;
							var observation = GetDictionary(record, "observation");
							var metadata = GetDictionary(observation, "metadata");
							if (IsReplayableTrainingAction(metadata))
								replayableActionCount++;
							else if (IsPowerIdentityCandidate(metadata))
								powerIdentityCount++;
							else
								incompleteActionCount++;
						}
						if (!IsSolveRecord(record, summarizesLegacy))
							return;

						var result = GetDictionary(record, "result");
						if (result == null)
							return;

						solveCount++;
						Increment(statusCounts, GetString(result, "status"));
						CollectWarningCodes(result, warningCounts);
					},
					() => invalidLineCount++);
			}
			catch (Exception ex)
			{
				Log.Warn("Training log summary read failed: " + ex.GetType().Name);
				summary.StatusMessage =
					"训练记录：摘要暂时不可用，原始记录保持不变。";
				summary.ReadIssueMessage =
					"记录读取故障：详细原因已写入插件日志，请稍后刷新状态。";
				summary.StatusMessage = AppendBehaviorSummary(summary.StatusMessage, summary);
				return summary;
			}

			summary.RecentActionCount = actionCount;
			summary.RecentReplayableActionCount = replayableActionCount;
			summary.RecentPowerIdentityCount = powerIdentityCount;
			summary.RecentIncompleteActionCount = incompleteActionCount;

			if (invalidLineCount > 0)
			{
				summary.ReadIssueMessage = "记录读取故障：已跳过 " +
					invalidLineCount + " 条格式异常记录，其他统计仍可使用。";
			}

			if (solveCount == 0)
			{
				if (summarizesLegacy)
				{
					summary.StatusMessage =
						"训练记录：尚无新轨迹；旧版历史记录中没有可汇总的求解结果。";
				}
				else
				{
					summary.StatusMessage = actionCount > 0
						? "训练记录：近期读取 " + recordCount + " 条记录；" +
							BuildActionUsabilityMessage(
								actionCount,
								replayableActionCount,
								powerIdentityCount,
								incompleteActionCount) + "；暂时没有可汇总的求解结果。"
						: recordCount > 0
						? "训练记录：已有新轨迹，暂时没有可汇总的求解结果。"
						: invalidLineCount > 0
							? "训练记录：新记录暂时无法生成摘要，原始记录保持不变。"
							: "训练记录：尚无新轨迹。";
				}
				summary.StatusMessage = AppendBehaviorSummary(summary.StatusMessage, summary);
				return summary;
			}

			summary.StatusMessage = BuildSolveStatusMessage(
				recordCount,
				solveCount,
				statusCounts,
				summarizesLegacy,
				actionCount,
				replayableActionCount,
				powerIdentityCount,
				incompleteActionCount);
			summary.ModelCoverageLines = BuildModelCoverageLines(warningCounts);
			summary.StatusMessage = AppendBehaviorSummary(summary.StatusMessage, summary);
			return summary;
		}

		private static void InspectBehaviorLog(TrainingLogSummary summary, string path)
		{
			if (summary == null || !summary.HasBehaviorLog)
				return;

			var invalidLineCount = 0;
			try
			{
				ReadRecentTrainingRecords(
					path,
					record =>
					{
						if (!string.Equals(
							GetString(record, "schema"),
							"advisor-behavior-v1",
							StringComparison.Ordinal) ||
							GetValue(record, "rl_training_eligible") == null ||
							GetBoolean(record, "rl_training_eligible"))
						{
							invalidLineCount++;
							return;
						}

						summary.RecentBehaviorCount++;
						switch (GetString(record, "actor_side"))
						{
							case "local": summary.RecentLocalBehaviorCount++; break;
							case "opponent": summary.RecentOpponentBehaviorCount++; break;
							default: summary.RecentUnknownBehaviorCount++; break;
						}
						if (GetBoolean(record, "behavior_eligible"))
							summary.RecentBehaviorEligibleCount++;
					},
					() => invalidLineCount++);
			}
			catch (Exception ex)
			{
				Log.Warn("行为语料摘要读取失败。" + ex.GetType().Name);
				summary.ReadIssueMessage =
					"行为记录读取故障：原始记录保持不变，请稍后刷新状态。";
				return;
			}

			if (invalidLineCount > 0)
			{
				summary.ReadIssueMessage = "行为记录读取故障：已跳过 " +
					invalidLineCount + " 条格式异常记录，其他统计仍可使用。";
			}
		}

		private static string AppendBehaviorSummary(
			string status,
			TrainingLogSummary summary)
		{
			if (summary == null || !summary.HasBehaviorLog)
				return status;

			var detail = summary.RecentBehaviorCount <= 0
				? "双方行为语料暂时没有可汇总动作"
				: "双方行为语料近期 " + summary.RecentBehaviorCount + " 条（我方 " +
					summary.RecentLocalBehaviorCount + "、对手 " +
					summary.RecentOpponentBehaviorCount +
					(summary.RecentUnknownBehaviorCount > 0
						? "、来源未定 " + summary.RecentUnknownBehaviorCount
						: "") + "），其中证据完整、可用于行为分析 " +
					summary.RecentBehaviorEligibleCount + " 条";
			return (status ?? "").Trim().TrimEnd('。') + "；" + detail +
				"；全部与强化学习轨迹隔离。";
		}

		private static void SetLastUpdatedMessage(
			TrainingLogSummary summary,
			params string[] paths)
		{
			var latest = (paths ?? new string[0])
				.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
				.Select(File.GetLastWriteTime)
				.OrderByDescending(value => value)
				.FirstOrDefault();
			if (latest != default(DateTime))
			{
				summary.LastUpdatedMessage = "最近更新时间：" +
					latest.ToString("yyyy-MM-dd HH:mm") + "。";
			}
		}

		private static void ReadRecentTrainingRecords(
			string path,
			Action<IDictionary<string, object>> onRecord,
			Action onInvalidLine)
		{
			using (var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete))
			{
				var start = Math.Max(0, stream.Length - TrainingLogReadLimitBytes);
				stream.Seek(start, SeekOrigin.Begin);
				using (var reader = new StreamReader(
					stream,
					Encoding.UTF8,
					true,
					4096,
					false))
				{
					// A bounded tail can begin in the middle of a UTF-8 character or JSON line.
					// Discard that one partial line rather than trying to repair or expose it.
					if (start > 0)
						reader.ReadLine();

					var serializer = new JavaScriptSerializer
					{
						MaxJsonLength = TrainingLogLineLimitChars,
						RecursionLimit = 128
					};
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						if (string.IsNullOrWhiteSpace(line))
							continue;
						if (line.Length > TrainingLogLineLimitChars)
						{
							onInvalidLine();
							continue;
						}

						try
						{
							var record = serializer.DeserializeObject(line) as IDictionary<string, object>;
							if (record == null)
								onInvalidLine();
							else
								onRecord(record);
						}
						catch (ArgumentException)
						{
							onInvalidLine();
						}
						catch (InvalidOperationException)
						{
							onInvalidLine();
						}
					}
				}
			}
		}

		private static bool IsSolveRecord(
			IDictionary<string, object> record,
			bool legacySchema)
		{
			if (!string.Equals(GetString(record, "kind"), "solve", StringComparison.OrdinalIgnoreCase))
				return false;
			var schema = GetString(record, "log_schema");
			return legacySchema
				? string.IsNullOrWhiteSpace(schema)
				: string.Equals(
					schema,
					"advisor-training-log-v2",
					StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsActionObservation(IDictionary<string, object> record)
		{
			if (!string.Equals(
				GetString(record, "kind"),
				"observation",
				StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			var observation = GetDictionary(record, "observation");
			return string.Equals(
				GetString(observation, "kind"),
				"action",
				StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsReplayableTrainingAction(
			IDictionary<string, object> metadata)
		{
			return metadata != null &&
				string.Equals(
					GetString(metadata, "capture_contract"),
					"trajectory-readiness-v1",
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					GetString(metadata, "completeness"),
					"complete_action_trace_v1",
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					GetString(metadata, "transition_status"),
					"replayable_exact",
					StringComparison.OrdinalIgnoreCase) &&
				GetBoolean(metadata, "training_eligible");
		}

		private static bool IsPowerIdentityCandidate(
			IDictionary<string, object> metadata)
		{
			return metadata != null &&
				string.Equals(
					GetString(metadata, "capture_contract"),
					"hdt_power_action_identity_v1",
					StringComparison.OrdinalIgnoreCase) &&
				string.Equals(
					GetString(metadata, "action_identity_status"),
					"exact_hdt_power_v1",
					StringComparison.OrdinalIgnoreCase) &&
				!GetBoolean(metadata, "training_eligible");
		}

		private static void CollectWarningCodes(
			IDictionary<string, object> result,
			IDictionary<string, int> warningCounts)
		{
			var coverage = GetDictionary(result, "coverage");
			var warningCountBefore = warningCounts.Values.Sum();
			CollectCodes(GetValue(coverage, "approximate_effects"), warningCounts);
			if (warningCounts.Values.Sum() == warningCountBefore)
			{
				CollectLegacyWarnings(GetValue(result, "warnings"), warningCounts);
			}
		}

		private static void CollectLegacyWarnings(
			object value,
			IDictionary<string, int> warningCounts)
		{
			var sequence = value as IEnumerable;
			if (sequence == null || value is string)
				return;
			foreach (var item in sequence)
			{
				var raw = item as string;
				if (string.IsNullOrWhiteSpace(raw))
					continue;
				// Reuse the UI boundary classifier: legacy logs store English diagnostic
				// prose instead of stable codes. Only its category code survives here.
				var label = DescribeWarningCode(AdvisorUserMessages.WarningCode(raw));
				if (!string.IsNullOrWhiteSpace(label))
					Increment(warningCounts, label);
			}
		}

		private static void CollectCodes(object value, IDictionary<string, int> warningCounts)
		{
			foreach (var item in AsDictionaries(value))
			{
				var label = DescribeWarningCode(GetString(item, "code"));
				if (!string.IsNullOrWhiteSpace(label))
					Increment(warningCounts, label);
			}
		}

		private static string BuildSolveStatusMessage(
			int recordCount,
			int solveCount,
			IDictionary<string, int> statusCounts,
			bool legacySource,
			int actionCount,
			int replayableActionCount,
			int powerIdentityCount,
			int incompleteActionCount)
		{
			var parts = statusCounts
				.Select(pair => new
				{
					Label = DescribeSolveStatus(pair.Key),
					Count = pair.Value,
					Order = SolveStatusOrder(pair.Key)
				})
				.GroupBy(item => item.Label)
				.Select(group => new
				{
					Label = group.Key,
					Count = group.Sum(item => item.Count),
					Order = group.Min(item => item.Order)
				})
				.OrderByDescending(item => item.Count)
				.ThenBy(item => item.Order)
				.ThenBy(item => item.Label, StringComparer.Ordinal)
				.Take(MajorCategoryLimit)
				.Select(item => item.Label + " " + item.Count + " 次")
				.ToArray();

			var actionSummary = legacySource
				? ""
				: "；" + BuildActionUsabilityMessage(
					actionCount,
					replayableActionCount,
					powerIdentityCount,
					incompleteActionCount);
			return (legacySource
					? "训练记录：尚无新轨迹；旧版历史记录近期读取 "
					: "训练记录：近期读取 ") +
				recordCount + " 条记录，其中 " +
				solveCount + " 次求解；" +
				string.Join("，", parts) + actionSummary + "。";
		}

		private static string BuildActionUsabilityMessage(
			int actionCount,
			int replayableActionCount,
			int powerIdentityCount,
			int incompleteActionCount)
		{
			if (actionCount <= 0)
				return "近期动作样本 0 条";

			var parts = new List<string>
			{
				"近期动作样本 " + actionCount + " 条",
				"已通过离线回放、可训练 " + replayableActionCount + " 条"
			};
			if (powerIdentityCount > 0)
			{
				parts.Add("HDT 精确动作身份待离线复核 " + powerIdentityCount + " 条");
			}
			if (incompleteActionCount > 0)
			{
				parts.Add("旧版或不完整动作证据 " + incompleteActionCount + " 条");
			}
			if (replayableActionCount == 0)
			{
				parts.Add("当前不能用于动作策略训练，但原始记录没有损坏");
			}
			return string.Join("，", parts.ToArray());
		}

		private static List<string> BuildModelCoverageLines(
			IDictionary<string, int> warningCounts)
		{
			if (warningCounts.Count == 0)
			{
				return new List<string> { "模型覆盖限制：近期未记录。" };
			}

			return warningCounts
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(MajorCategoryLimit)
				.Select(pair => "模型覆盖限制：" + pair.Key + "（" + pair.Value + " 项）")
				.ToList();
		}

		private static string DescribeSolveStatus(string status)
		{
			switch ((status ?? "").Trim().ToLowerInvariant())
			{
				case "ok":
					return "已完成";
				case "partial":
					return "已完成（存在模型覆盖限制）";
				case "cancelled":
				case "stale":
					return "已中止（局面变化或阶段切换）";
				case "unsupported":
					return "当前机制未覆盖";
				default:
					return "其他求解结果";
			}
		}

		private static int SolveStatusOrder(string status)
		{
			switch ((status ?? "").Trim().ToLowerInvariant())
			{
				case "ok": return 0;
				case "partial": return 1;
				case "cancelled":
				case "stale": return 2;
				case "unsupported": return 3;
				default: return 4;
			}
		}

		private static string DescribeWarningCode(string code)
		{
			var normalized = (code ?? "").Trim().ToLowerInvariant();
			switch (normalized)
			{
				case "unsupported_card_text":
					return "卡牌文本规则尚未覆盖";
				case "unsupported_card_mechanic":
				case "unsupported_rule":
					return "卡牌机制尚未覆盖";
				case "unsupported_card_type":
					return "卡牌类型尚未覆盖";
				case "unknown_snapshot_data":
				case "unknown_burn":
				case "hidden_draw_identity":
				case "hidden_hand":
					return "局面含未知或隐藏信息";
				case "missing_effect_target":
				case "multiple_target_groups":
					return "效果目标只能近似处理";
				case "counterplay_cancelled":
					return "对手回应评估已中止";
				case "counterplay_limit":
					return "对手回应评估受限";
				case "approximate_counterplay_deadline_reached":
					return "对手回应评估达到时间上限";
				case "approximate_counterplay_node_budget_exhausted":
					return "对手回应评估达到计算上限";
				case "unsupported_counterplay_verification":
				case "unsupported_counterplay_incomplete_candidate":
					return "对手回应验证尚未覆盖";
				case "unsupported_solver_abstention":
					return "当前卡牌规则尚未覆盖";
				case "unsupported_alternative_ignored_for_lethal":
					return "部分可选行动尚未覆盖";
				case "modeled_lethal_scope":
					return "斩杀结论仅覆盖当前可见规则";
				case "hidden_information":
					return "局面含未知或隐藏信息";
				case "capture_gap":
					return "局面采集信息不完整";
				case "approximate_model":
					return "部分效果使用近似模型";
				case "other_model_notice":
					return "其他覆盖限制";
				default:
					return string.IsNullOrWhiteSpace(normalized) ? "" : "其他覆盖限制";
			}
		}

		private static IEnumerable<IDictionary<string, object>> AsDictionaries(object value)
		{
			var sequence = value as IEnumerable;
			if (sequence == null || value is string)
				yield break;
			foreach (var item in sequence)
			{
				var dictionary = item as IDictionary<string, object>;
				if (dictionary != null)
					yield return dictionary;
			}
		}

		private static IDictionary<string, object> GetDictionary(
			IDictionary<string, object> dictionary,
			string key)
		{
			return GetValue(dictionary, key) as IDictionary<string, object>;
		}

		private static object GetValue(IDictionary<string, object> dictionary, string key)
		{
			if (dictionary == null)
				return null;
			object value;
			return dictionary.TryGetValue(key, out value) ? value : null;
		}

		private static string GetString(IDictionary<string, object> dictionary, string key)
		{
			var value = GetValue(dictionary, key);
			return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
		}

		private static bool GetBoolean(IDictionary<string, object> dictionary, string key)
		{
			var value = GetValue(dictionary, key);
			if (value is bool)
				return (bool)value;
			if (value == null)
				return false;
			var text = Convert.ToString(value, CultureInfo.InvariantCulture);
			return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
		}

		private static void Increment(IDictionary<string, int> counts, string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				key = "";
			int count;
			counts.TryGetValue(key, out count);
			counts[key] = count + 1;
		}

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
			builder.AppendLine("说明：以下仅包含中文状态摘要；本机路径和原始日志未复制。");
			builder.AppendLine("开发者日志可能包含英文技术信息，但普通信息不等于故障。");
			builder.AppendLine();
			builder.AppendLine("[数据源]");
			AppendLineIfNotEmpty(builder, HideTechnicalDetails(
				dataStatus, "需处理：数据源状态暂时无法确认。请在设置页刷新状态。"));
			AppendLineIfNotEmpty(builder, HideTechnicalDetails(
				recommendationStatus, "提示：推荐数据状态暂时无法确认。"));
			AppendLineIfNotEmpty(builder, HideTechnicalDetails(
				premiumStatus, "提示：对阵数据状态暂时无法确认。"));

			builder.AppendLine();
			builder.AppendLine("[数据健康]");
			if (dataHealthSnapshot == null)
			{
				builder.AppendLine("数据健康状态不可用");
			}
			else
			{
				AppendLineIfNotEmpty(
					builder,
					HideTechnicalDetails(
						dataHealthSnapshot.UserMessage,
						"数据健康状态读取失败，请重新生成数据快照。"));
				foreach (var line in BuildUserVisibleLines(
					dataHealthSnapshot.DetailLines,
					"部分详细状态无法显示，请打开插件日志查看原因。"))
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
				AppendLineIfNotEmpty(builder, HideTechnicalDetails(
					refreshTaskSnapshot.ToolsStatus,
					"需处理：自动刷新组件状态暂时无法确认。"));
				AppendLineIfNotEmpty(builder, HideTechnicalDetails(
					refreshTaskSnapshot.ScheduledTaskStatus,
					"提示：自动刷新计划状态暂时无法确认。"));
				AppendLineIfNotEmpty(builder, HideTechnicalDetails(
					refreshTaskSnapshot.LatestLogStatus,
					"提示：最近一次刷新结果暂时无法确认。"));

				builder.AppendLine();
				builder.AppendLine("[最近刷新日志摘要]");
				if (refreshTaskSnapshot.LatestLogSummaryLines == null ||
					refreshTaskSnapshot.LatestLogSummaryLines.Count == 0)
				{
					builder.AppendLine("未找到日志摘要");
				}
				else
				{
					builder.AppendLine("原始刷新日志未复制；需要排查时可打开开发者刷新日志（可能含英文）。");
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

		private static string EnsureSentence(string text)
		{
			return text.EndsWith("。", StringComparison.Ordinal) ||
				text.EndsWith("！", StringComparison.Ordinal) ||
				text.EndsWith("？", StringComparison.Ordinal)
				? text
				: text + "。";
		}
	}
}
