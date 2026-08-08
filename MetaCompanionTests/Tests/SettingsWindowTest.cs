using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class SettingsWindowTest
	{
		[TestMethod]
		public void BuildDiagnosticText_IncludesStatusesWithoutRawLogTail()
		{
			var dataHealth = new MetaDataHealthSnapshot
			{
				UserMessage = "数据可用",
				DetailLines = new List<string>
				{
					"HSReplay 牌组库: 1 套",
					"HSReplay 高级数据登录凭据已配置",
					"Cookie: should-not-copy"
				}
			};
			var refreshTask = new RefreshTaskSnapshot
			{
				ToolsStatus = "高级刷新脚本已安装",
				ScheduledTaskStatus = "自动刷新已安装",
				LatestLogStatus = "最近刷新日志: 2026-06-22 09:00",
				LatestLogPath = @"C:\MetaCompanion\Logs\refresh-20260622.log",
				LatestLogSummaryLines = new List<string>
				{
					"line 1",
					"Cookie: secret-cookie-value"
				}
			};

			var text = SettingsDiagnostics.BuildDiagnosticText(
				new DateTime(2026, 6, 22, 9, 5, 0),
				@"C:\MetaCompanion",
				@"C:\MetaCompanion\Logs",
				"数据源: 可用",
				"推荐结果：前 3 名",
				"对阵矩阵: 已同步",
				dataHealth,
				refreshTask);

			StringAssert.Contains(text, "Meta Companion 诊断信息");
			StringAssert.Contains(text, "[数据健康]");
			StringAssert.Contains(text, "HSReplay 牌组库: 1 套");
			StringAssert.Contains(text, "[自动刷新]");
			StringAssert.Contains(text, "高级刷新脚本已安装");
			StringAssert.Contains(text, "[最近刷新日志摘要]");
			StringAssert.Contains(text, "打开开发者刷新日志");
			StringAssert.Contains(text, "本机路径和原始日志未复制");
			StringAssert.Contains(text, "开发者日志可能包含英文技术信息");
			Assert.IsFalse(text.Contains("line 1"));
			Assert.IsFalse(text.Contains("Cookie=[redacted]"));
			Assert.IsFalse(text.Contains("secret-cookie-value"));
			Assert.IsFalse(text.Contains("should-not-copy"));
			Assert.IsFalse(text.Contains("Top"));
			Assert.IsFalse(text.Contains(@"C:\MetaCompanion"));
			Assert.IsFalse(text.Contains("refresh-20260622.log"));
		}

		[TestMethod]
		public void BuildDiagnosticText_ReplacesExceptionsHttpFailuresAndPaths()
		{
			var text = SettingsDiagnostics.BuildDiagnosticText(
				new DateTime(2026, 7, 30, 10, 30, 0),
				@"C:\MetaCompanion",
				@"C:\MetaCompanion\Logs",
				@"数据读取失败：InvalidOperationException at Reader.Load() C:\MetaCompanion\data.tsv",
				"推荐读取失败：HTTP 500 request_id=abc",
				"对阵数据暂时不可用",
				new MetaDataHealthSnapshot
				{
					UserMessage = "读取失败：IOException",
					DetailLines = new List<string>
					{
						@"文件读取失败：C:\MetaCompanion\secret.json"
					}
				},
				new RefreshTaskSnapshot
				{
					ToolsStatus = "检查失败：HRESULT 0x80070002",
					ScheduledTaskStatus = "access denied",
					LatestLogStatus = @"读取失败：C:\MetaCompanion\Logs\refresh.log",
					LatestLogSummaryLines = new List<string> { "Traceback line 42" }
				});

			StringAssert.Contains(text, "中文状态摘要");
			StringAssert.Contains(text, "开发者日志可能包含英文技术信息");
			Assert.IsFalse(text.Contains("InvalidOperationException"));
			Assert.IsFalse(text.Contains("IOException"));
			Assert.IsFalse(text.Contains("HTTP 500"));
			Assert.IsFalse(text.Contains("request_id"));
			Assert.IsFalse(text.Contains("HRESULT"));
			Assert.IsFalse(text.Contains("0x80070002"));
			Assert.IsFalse(text.Contains("access denied"));
			Assert.IsFalse(text.Contains("Traceback"));
			Assert.IsFalse(text.Contains(@"C:\MetaCompanion"));
			Assert.IsFalse(text.Contains(".json"));
			Assert.IsFalse(text.Contains(".log"));
			Assert.IsFalse(text.Contains(".tsv"));
		}

		[TestMethod]
		public void BuildUserStatus_UsesThreeChineseSeverityPrefixes()
		{
			var normal = SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Normal,
				"推荐数据已加载");
			var notice = SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.Notice,
				"尚未生成推荐数据",
				"请先运行一次数据更新");
			var actionRequired = SettingsDiagnostics.BuildUserStatus(
				UserMessageSeverity.ActionRequired,
				"推荐数据读取失败",
				"请点击“刷新状态”重试");

			StringAssert.StartsWith(normal, "正常：");
			StringAssert.StartsWith(notice, "提示：");
			StringAssert.StartsWith(actionRequired, "需处理：");
			StringAssert.Contains(notice, "请先运行一次数据更新");
			StringAssert.Contains(actionRequired, "请点击“刷新状态”重试");
		}

		[TestMethod]
		public void BuildUserFacingFailure_DoesNotExposeTechnicalException()
		{
			var message = SettingsDiagnostics.BuildUserFacingFailure(
				@"InvalidOperationException at Reader.Load() C:\Users\Player\secret.tsv",
				"HTTP 500 request_id=private");

			StringAssert.StartsWith(message, "需处理：");
			StringAssert.Contains(message, "开发者日志");
			Assert.IsFalse(message.Contains("InvalidOperationException"));
			Assert.IsFalse(message.Contains("Reader.Load"));
			Assert.IsFalse(message.Contains(@"C:\Users"));
			Assert.IsFalse(message.Contains("secret.tsv"));
			Assert.IsFalse(message.Contains("HTTP 500"));
			Assert.IsFalse(message.Contains("request_id"));
		}

		[TestMethod]
		public void BuildUserVisibleLines_ReplacesEnglishAndTechnicalDetails()
		{
			var lines = SettingsDiagnostics.BuildUserVisibleLines(
				new[]
				{
					"牌组库读取正常",
					"读取失败：InvalidOperationException",
					"HTTP 500 request_id=abc",
					@"读取路径失败：C:\Users\Player\private.txt",
					"读取路径失败：C:/Users/Player/private.txt",
					"系统调用失败：HRESULT 0x80070002",
					"刷新失败：PowerShell.exe at Tool.Run()",
					"access denied to task scheduler"
				},
				"部分详细状态无法显示，请打开插件日志查看原因。");
			var text = string.Join("\n", lines.ToArray());

			Assert.AreEqual(2, lines.Count);
			StringAssert.Contains(text, "牌组库读取正常");
			StringAssert.Contains(text, "打开插件日志");
			Assert.IsFalse(text.Contains("InvalidOperationException"));
			Assert.IsFalse(text.Contains("HTTP 500"));
			Assert.IsFalse(text.Contains("request_id"));
			Assert.IsFalse(text.Contains(@"C:\Users"));
			Assert.IsFalse(text.Contains("C:/Users"));
			Assert.IsFalse(text.Contains("HRESULT"));
			Assert.IsFalse(text.Contains("0x80070002"));
			Assert.IsFalse(text.Contains("PowerShell.exe"));
			Assert.IsFalse(text.Contains("Tool.Run"));
			Assert.IsFalse(text.Contains("access denied"));
		}

		[TestMethod]
		public void AdvisorModelStatus_UsesChineseScopeAndNeverExposesRawHealthDetails()
		{
			var health = new AdvisorWorkerHealth
			{
				IsReady = true,
				Backend = "rust",
				SupportsDecisionRanker = true,
				DecisionRankerAvailable = true,
				DecisionRankerStatus = "ready",
				DecisionRankerReason = @"failed at C:\Users\Player\decision-ranker.json",
				DecisionRankerArtifactSha256 = new string('a', 64),
				SupportsBehaviorSearchOrderingPrior = true,
				BehaviorPriorAvailable = false,
				BehaviorPriorStatus = "rejected",
				BehaviorPriorReason = "invalid_json",
				BehaviorPriorArtifactSha256 = new string('b', 64)
			};

			var lines = SettingsDiagnostics.BuildAdvisorModelStatusLines(health, true);
			var text = string.Join("\n", lines.ToArray());
			Assert.AreEqual(2, lines.Count);
			StringAssert.Contains(text, "本方决策排序器可用");
			StringAssert.Contains(text, "仅重排规则引擎给出的本方合法候选");
			StringAssert.Contains(text, "对手行为先验未通过完整性或质量检查");
			StringAssert.Contains(text, "基础求解不受影响");
			Assert.IsFalse(text.Contains("failed"));
			Assert.IsFalse(text.Contains(@"C:\Users"));
			Assert.IsFalse(text.Contains("invalid_json"));
			Assert.IsFalse(text.Contains(new string('a', 64)));
			Assert.IsFalse(text.Contains(new string('b', 64)));

			var stopped = SettingsDiagnostics.BuildAdvisorModelStatusLines(null, false);
			StringAssert.Contains(stopped[0], "本机求解器当前未运行");
			StringAssert.Contains(stopped[0], "启用实战建议或脱敏数据保存");
		}

		[TestMethod]
		public void InspectTrainingLogs_WhenNoLogsExist_ReportsNoNewTrajectory()
		{
			WithTemporaryDataDirectory(root =>
			{
				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				StringAssert.Contains(summary.StatusMessage, "尚无新轨迹");
				Assert.AreEqual(0, summary.ModelCoverageLines.Count);
				Assert.AreEqual("", summary.LastUpdatedMessage);
				Assert.AreEqual("", summary.ReadIssueMessage);
				Assert.IsFalse(summary.HasCurrentLog);
				Assert.IsFalse(summary.HasLegacyLog);
				StringAssert.Contains(summary.DeveloperNote, "开发者数据");
				Assert.IsFalse(summary.DeveloperNote.Contains("JSONL"));
				Assert.IsFalse(summary.DeveloperNote.Contains("英文机器协议"));
				StringAssert.Contains(summary.DeveloperNote, "不在设置页展示或打开");
				Assert.IsFalse(summary.StatusMessage.Contains(root));
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_BehaviorOnlySeparatesBothSidesFromStrictTrajectories()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				File.WriteAllLines(
					Path.Combine(workerDirectory, "behavior-v1.jsonl"),
					new[]
					{
						"{\"schema\":\"advisor-behavior-v1\",\"actor_side\":\"local\",\"behavior_eligible\":true,\"rl_training_eligible\":false}",
						"{\"schema\":\"advisor-behavior-v1\",\"actor_side\":\"opponent\",\"behavior_eligible\":true,\"rl_training_eligible\":false}",
						"{\"schema\":\"advisor-behavior-v1\",\"actor_side\":\"opponent\",\"behavior_eligible\":false,\"rl_training_eligible\":false}"
					},
					new UTF8Encoding(false));

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				Assert.IsTrue(summary.HasBehaviorLog);
				Assert.IsFalse(summary.HasCurrentLog);
				Assert.AreEqual(3, summary.RecentBehaviorCount);
				Assert.AreEqual(1, summary.RecentLocalBehaviorCount);
				Assert.AreEqual(2, summary.RecentOpponentBehaviorCount);
				Assert.AreEqual(2, summary.RecentBehaviorEligibleCount);
				StringAssert.Contains(summary.StatusMessage, "尚无严格可回放轨迹");
				StringAssert.Contains(summary.StatusMessage, "双方行为语料近期 3 条");
				StringAssert.Contains(summary.StatusMessage, "我方 1、对手 2");
				StringAssert.Contains(summary.StatusMessage, "可用于行为分析 2 条");
				StringAssert.Contains(summary.StatusMessage, "与强化学习轨迹隔离");
				Assert.IsFalse(summary.StatusMessage.Contains(root));
				Assert.IsFalse(summary.StatusMessage.Contains("JSONL"));
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_LegacyOnly_SummarizesCodesInChineseWithoutRewritingFile()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				var legacyPath = Path.Combine(workerDirectory, "training.jsonl");
				var secretDetail = @"C:\Users\Player\secret Cookie=do-not-show Ranger General Sylvanas";
				File.WriteAllLines(
					legacyPath,
					new[]
					{
						LegacySolve("cancelled", new[]
						{
							"unsupported_card_mechanic", "unsupported_card_mechanic",
							"unsupported_card_text", "unknown_snapshot_data"
						}, secretDetail),
						LegacySolve("cancelled", new[]
						{
							"unsupported_card_mechanic", "unsupported_card_text"
						}, secretDetail),
						LegacySolveWithRawWarnings("unsupported", new[]
						{
							"Arcane Bolt: card_text_not_parsed " + secretDetail,
							"HDT reported 2 unknown or intentionally hidden snapshot-data entry/entries. " + secretDetail,
							"Arcane Bolt has a currently playable unsupported rule; ranked advice is withheld. " + secretDetail,
							"An English warning without a safe code prefix " + secretDetail
						}),
						LegacySolve("ok", new[] { "multiple_target_groups" }, secretDetail),
						"{\"kind\":\"observation\",\"player_name\":\"Private Player\"}"
					},
					new UTF8Encoding(false));
				var before = File.ReadAllBytes(legacyPath);
				var beforeWriteTime = File.GetLastWriteTimeUtc(legacyPath);

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);
				var visible = string.Join("\n", new[] { summary.StatusMessage }
					.Concat(summary.ModelCoverageLines)
					.Concat(new[]
					{
						summary.LastUpdatedMessage,
						summary.LegacyMessage,
						summary.ReadIssueMessage,
						summary.DeveloperNote
					}).ToArray());

				Assert.IsTrue(summary.HasLegacyLog);
				Assert.IsFalse(summary.HasCurrentLog);
				StringAssert.Contains(summary.StatusMessage, "尚无新轨迹");
				StringAssert.Contains(summary.StatusMessage, "旧版历史记录");
				StringAssert.Contains(summary.StatusMessage, "5 条记录");
				StringAssert.Contains(summary.StatusMessage, "4 次求解");
				StringAssert.Contains(summary.StatusMessage, "已中止（局面变化或阶段切换） 2 次");
				StringAssert.Contains(summary.StatusMessage, "当前机制未覆盖 1 次");
				StringAssert.Contains(summary.LastUpdatedMessage, "最近更新时间：");
				StringAssert.Contains(summary.LegacyMessage, "旧版历史记录（只读）");
				Assert.IsTrue(summary.ModelCoverageLines.Count <= 3);
				Assert.IsTrue(summary.ModelCoverageLines.All(line =>
					line.StartsWith("模型覆盖限制：", StringComparison.Ordinal)));
				StringAssert.Contains(visible, "卡牌机制尚未覆盖（4 项）");
				Assert.IsFalse(visible.Contains("unsupported_card_mechanic"));
				Assert.IsFalse(visible.Contains("Ranger General Sylvanas"));
				Assert.IsFalse(visible.Contains("Cookie"));
				Assert.IsFalse(visible.Contains("do-not-show"));
				Assert.IsFalse(visible.Contains(@"C:\Users"));
				Assert.IsFalse(visible.Contains(root));
				CollectionAssert.AreEqual(before, File.ReadAllBytes(legacyPath));
				Assert.AreEqual(beforeWriteTime, File.GetLastWriteTimeUtc(legacyPath));
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_CurrentLogTakesPriorityAndLegacyRemainsReadOnly()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				var currentPath = Path.Combine(workerDirectory, "training-v2.jsonl");
				var legacyPath = Path.Combine(workerDirectory, "training.jsonl");
				File.WriteAllText(
					currentPath,
					CurrentSolve("partial", new[] { "unsupported_card_text" }) + "\n",
					new UTF8Encoding(false));
				File.WriteAllText(
					legacyPath,
					LegacySolve("unsupported", new[] { "unsupported_card_mechanic" }, "old detail") + "\n",
					new UTF8Encoding(false));
				var legacyBefore = File.ReadAllBytes(legacyPath);

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				Assert.IsTrue(summary.HasCurrentLog);
				Assert.IsTrue(summary.HasLegacyLog);
				StringAssert.Contains(summary.StatusMessage, "1 条记录");
				StringAssert.Contains(summary.StatusMessage, "已完成（存在模型覆盖限制） 1 次");
				Assert.IsFalse(summary.StatusMessage.Contains("尚无新轨迹"));
				Assert.IsFalse(summary.StatusMessage.Contains("当前机制未覆盖"));
				StringAssert.Contains(summary.LegacyMessage, "只读历史数据保留");
				StringAssert.Contains(summary.ModelCoverageLines[0], "卡牌文本规则尚未覆盖");
				CollectionAssert.AreEqual(legacyBefore, File.ReadAllBytes(legacyPath));
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_SeparatesReplayablePowerAndIncompleteActions()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				var currentPath = Path.Combine(workerDirectory, "training-v2.jsonl");
				File.WriteAllLines(
					currentPath,
					new[]
					{
						CurrentAction(
							"trajectory-readiness-v1",
							"complete_action_trace_v1",
							"replayable_exact",
							"",
							true),
						CurrentAction(
							"hdt_power_action_identity_v1",
							"exact_action_identity_unverified_transition_v1",
							"post_state_candidate_unverified",
							"exact_hdt_power_v1",
							false),
						CurrentAction(
							"partial_hdt_transition_candidate_v1",
							"partial_hdt_gameevents_v1",
							"post_state_candidate_unverified",
							"",
							false),
						CurrentSolve("ok", new string[0])
					},
					new UTF8Encoding(false));

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				Assert.AreEqual(3, summary.RecentActionCount);
				Assert.AreEqual(1, summary.RecentReplayableActionCount);
				Assert.AreEqual(1, summary.RecentPowerIdentityCount);
				Assert.AreEqual(1, summary.RecentIncompleteActionCount);
				StringAssert.Contains(summary.StatusMessage, "近期动作样本 3 条");
				StringAssert.Contains(summary.StatusMessage, "可训练 1 条");
				StringAssert.Contains(summary.StatusMessage, "待离线复核 1 条");
				StringAssert.Contains(summary.StatusMessage, "不完整动作证据 1 条");
				Assert.IsFalse(summary.StatusMessage.Contains("JSONL"));
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_ExplainsWhenAllRecentActionsAreNotTrainable()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				File.WriteAllText(
					Path.Combine(workerDirectory, "training-v2.jsonl"),
					CurrentAction(
						"partial_hdt_transition_candidate_v1",
						"partial_hdt_gameevents_v1",
						"post_state_candidate_unverified",
						"",
						false) + "\n",
					new UTF8Encoding(false));

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				Assert.AreEqual(1, summary.RecentActionCount);
				Assert.AreEqual(0, summary.RecentReplayableActionCount);
				StringAssert.Contains(summary.StatusMessage, "可训练 0 条");
				StringAssert.Contains(summary.StatusMessage, "当前不能用于动作策略训练");
				StringAssert.Contains(summary.StatusMessage, "原始记录没有损坏");
			});
		}

		[TestMethod]
		public void InspectTrainingLogs_MalformedRecordIsReportedAsReadFaultNotModelLimit()
		{
			WithTemporaryDataDirectory(root =>
			{
				var workerDirectory = Path.Combine(root, "AdvisorWorker");
				Directory.CreateDirectory(workerDirectory);
				File.WriteAllText(
					Path.Combine(workerDirectory, "training-v2.jsonl"),
					"{not-json}\n",
					new UTF8Encoding(false));

				var summary = SettingsDiagnostics.InspectTrainingLogs(root);

				StringAssert.Contains(summary.StatusMessage, "原始记录保持不变");
				StringAssert.Contains(summary.ReadIssueMessage, "记录读取故障");
				Assert.AreEqual(0, summary.ModelCoverageLines.Count);
				Assert.IsFalse(summary.ReadIssueMessage.Contains("not-json"));
			});
		}

		[TestMethod]
		public void CalculateSettingsMaxHeight_CapsLargeDisplaysAndFitsSmallerWorkAreas()
		{
			Assert.AreEqual(760D, SettingsDiagnostics.CalculateSettingsMaxHeight(1080D));
			Assert.AreEqual(560D, SettingsDiagnostics.CalculateSettingsMaxHeight(600D));
			Assert.AreEqual(320D, SettingsDiagnostics.CalculateSettingsMaxHeight(360D));
			Assert.AreEqual(760D, SettingsDiagnostics.CalculateSettingsMaxHeight(Double.NaN));
		}

		[TestMethod]
		public void AdvisorWorkerBackendMode_UsesStableChineseComboBoxOrder()
		{
			var settingsType = typeof(PluginConfig).Assembly.GetType(
				"MetaCompanion.SettingsWindow",
				true);
			var toIndex = settingsType.GetMethod(
				"BackendModeToIndex",
				BindingFlags.Static | BindingFlags.NonPublic);
			var fromIndex = settingsType.GetMethod(
				"BackendModeFromIndex",
				BindingFlags.Static | BindingFlags.NonPublic);
			Assert.IsNotNull(toIndex);
			Assert.IsNotNull(fromIndex);

			Assert.AreEqual(0, toIndex.Invoke(null, new object[] { AdvisorWorkerBackendMode.Auto }));
			Assert.AreEqual(1, toIndex.Invoke(null, new object[] { AdvisorWorkerBackendMode.RustOnly }));
			Assert.AreEqual(2, toIndex.Invoke(null, new object[] { AdvisorWorkerBackendMode.PythonOnly }));
			Assert.AreEqual(0, toIndex.Invoke(null, new object[] { (AdvisorWorkerBackendMode)999 }));

			Assert.AreEqual(
				AdvisorWorkerBackendMode.Auto,
				fromIndex.Invoke(null, new object[] { 0 }));
			Assert.AreEqual(
				AdvisorWorkerBackendMode.RustOnly,
				fromIndex.Invoke(null, new object[] { 1 }));
			Assert.AreEqual(
				AdvisorWorkerBackendMode.PythonOnly,
				fromIndex.Invoke(null, new object[] { 2 }));
			Assert.AreEqual(
				AdvisorWorkerBackendMode.Auto,
				fromIndex.Invoke(null, new object[] { 999 }));
		}

		[TestMethod]
		public void RemoteScopeSelectors_MapTimeAndRankWithoutLocalRankSetting()
		{
			var settingsType = typeof(PluginConfig).Assembly.GetType(
				"MetaCompanion.SettingsWindow", true);
			Func<string, object, object> invoke = (name, value) => settingsType.GetMethod(
				name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { value });

			Assert.AreEqual(0, invoke("RemoteTimeRangeToIndex", "LAST_7_DAYS"));
			Assert.AreEqual("LAST_7_DAYS", invoke("RemoteTimeRangeFromIndex", 0));
			Assert.AreEqual(2, invoke("RemoteTimeRangeToIndex", "LAST_3_DAYS"));
			Assert.AreEqual("LAST_3_DAYS", invoke("RemoteTimeRangeFromIndex", 2));
			Assert.AreEqual(3, invoke("RemoteTimeRangeToIndex", "CURRENT_PATCH"));
			Assert.AreEqual("CURRENT_PATCH", invoke("RemoteTimeRangeFromIndex", 3));
			Assert.AreEqual(0, invoke("RemoteRankRangeToIndex", "DIAMOND_THROUGH_LEGEND"));
			Assert.AreEqual("LEGEND", invoke("RemoteRankRangeFromIndex", 1));
			Assert.AreEqual("TOP_1000_LEGEND", invoke("RemoteRankRangeFromIndex", 2));
			Assert.AreEqual("BRONZE_THROUGH_GOLD", invoke("RemoteRankRangeFromIndex", 7));
			Assert.IsNull(typeof(PluginConfig).GetProperty("LocalRecommendationRankRange"));
		}

		[TestMethod]
		public void SettingsActionBoundary_CatchesTechnicalFailureAndInvokesFriendlyHandler()
		{
			var settingsType = typeof(PluginConfig).Assembly.GetType(
				"MetaCompanion.SettingsWindow",
				true);
			var runAction = settingsType.GetMethod(
				"TryRunSettingsAction",
				BindingFlags.Static | BindingFlags.NonPublic);
			Assert.IsNotNull(runAction);

			var technicalFailure = new InvalidOperationException(
				"Process.Start failed with an English system error");
			Exception observedFailure = null;
			var failed = (bool)runAction.Invoke(null, new object[]
			{
				(Action)(() => { throw technicalFailure; }),
				(Action<Exception>)(error => observedFailure = error)
			});
			var succeeded = (bool)runAction.Invoke(null, new object[]
			{
				(Action)(() => { }),
				(Action<Exception>)(error => Assert.Fail("成功路径不应触发失败处理。"))
			});

			Assert.IsFalse(failed);
			Assert.AreSame(technicalFailure, observedFailure);
			Assert.IsTrue(succeeded);
		}

		private static string LegacySolve(string status, IEnumerable<string> codes, string detail)
		{
			return "{\"kind\":\"solve\",\"logged_at_utc\":\"2026-07-29T15:17:05Z\"," +
				"\"request\":{\"account_id\":\"private-account\"},\"result\":{" +
				"\"status\":\"" + status + "\",\"coverage\":{\"approximate_effects\":[" +
				string.Join(",", codes.Select(code =>
					"{\"code\":\"" + code + "\",\"detail\":\"" + EscapeJson(detail) + "\"}")) +
				"]},\"warnings\":[\"" + EscapeJson(detail) + "\"]}}";
		}

		private static string LegacySolveWithRawWarnings(
			string status,
			IEnumerable<string> warnings)
		{
			return "{\"kind\":\"solve\",\"result\":{\"status\":\"" + status +
				"\",\"warnings\":[" + string.Join(",", warnings.Select(value =>
					"\"" + EscapeJson(value) + "\"")) + "]}}";
		}

		private static string CurrentSolve(string status, IEnumerable<string> codes)
		{
			return "{\"kind\":\"solve\",\"log_schema\":\"advisor-training-log-v2\"," +
				"\"result\":{\"status\":\"" + status +
				"\",\"coverage\":{\"approximate_effects\":[" +
				string.Join(",", codes.Select(code =>
					"{\"code\":\"" + code + "\",\"detail\":\"English detail\"}")) + "]}}}";
		}

		private static string CurrentAction(
			string captureContract,
			string completeness,
			string transitionStatus,
			string actionIdentityStatus,
			bool trainingEligible)
		{
			return "{\"kind\":\"observation\",\"log_schema\":\"advisor-training-log-v2\"," +
				"\"observation\":{\"kind\":\"action\",\"metadata\":{" +
				"\"capture_contract\":\"" + captureContract + "\"," +
				"\"completeness\":\"" + completeness + "\"," +
				"\"transition_status\":\"" + transitionStatus + "\"," +
				"\"action_identity_status\":\"" + actionIdentityStatus + "\"," +
				"\"training_eligible\":" +
				(trainingEligible ? "true" : "false") + "}}}";
		}

		private static string EscapeJson(string value)
		{
			return (value ?? "")
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"")
				.Replace("\r", "\\r")
				.Replace("\n", "\\n");
		}

		private static void WithTemporaryDataDirectory(Action<string> action)
		{
			var root = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionSettingsTest-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				action(root);
			}
			finally
			{
				if (Directory.Exists(root))
					Directory.Delete(root, true);
			}
		}
	}
}
