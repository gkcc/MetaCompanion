using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	/// <summary>
	/// The single boundary between worker/developer text and the live advisor UI.
	/// Worker payloads intentionally keep their original diagnostic text; only the
	/// short, localized values returned by this class may be rendered to the user.
	/// </summary>
	internal static class AdvisorUserMessages
	{
		internal const string DeveloperLogHint =
			"需要排查时，请在插件设置中查看开发者日志。";
		internal const string StateChanged = "局面已更新，旧建议已清除。";
		internal const string SearchCancelled = "本次建议计算已取消。";
		internal const string WorkerUnavailable =
			"本机求解器暂时不可用。HDT 可继续正常使用，插件会在后台重试。" +
			DeveloperLogHint;
		internal const string Searching = "正在搜索可行路线…";
		internal const string StaleResult = "局面已经变化，旧结果已忽略；正在等待新建议。";
		internal const string InitialResult = "首批建议已生成，正在继续校验更深路线。";
		internal const string SolveTimedOut =
			"本次建议计算超时，可能过时的路线不会显示。局面变化后会自动重试。" +
			DeveloperLogHint;
		internal const string SolveFailed =
			"当前局面暂时无法完成计算。HDT 可继续正常使用，插件会在后台重试。" +
			DeveloperLogHint;
		internal const string Unsupported =
			"该局面包含尚未完整支持的卡牌或机制。为避免误导，暂不展示行动线；这不是插件故障。";
		internal const string IncompleteState =
			"当前局面信息不完整。为避免误导，暂不展示行动线；局面更新后会自动重试。";
		internal const string NoRecommendations =
			"当前局面没有可可靠展示的行动线。你可以继续自行操作，局面变化后会自动重算。";
		internal const string PartialResult = "部分路线仍在校验，当前建议仅供参考。";
		internal const string FinalPartialStatus = "计算完成 · 近似路线，仅供参考";

		private const string GenericNotice = "部分求解细节已简化显示，完整信息已写入开发者日志。";
		private const string ApproximationNotice =
			"部分卡牌或机制尚未完整建模，当前结果使用了近似计算，仅供参考。";
		private const string UnsupportedNotice =
			"当前局面涉及尚未完整支持的卡牌或机制，相关路线未作为可靠建议展示；这不是插件故障。";
		private const string ResponseNotice =
			"对手回应搜索尚未完整完成，当前路线不能视为已经验证安全。";
		private const string HiddenInformationNotice =
			"当前路线受隐藏信息影响，实际结果可能随未知卡牌或随机结果变化。";
		private const string CaptureNotice =
			"当前局面信息不完整，建议准确性可能受到影响；局面更新后会自动重算。";

		private const string WarningCodeUnsupportedPlayable =
			"unsupported_solver_abstention";
		private const string WarningCodeUnsupportedRule = "unsupported_rule";
		private const string WarningCodeCounterplayLimit = "counterplay_limit";
		private const string WarningCodeHiddenInformation = "hidden_information";
		private const string WarningCodeCaptureGap = "capture_gap";
		private const string WarningCodeApproximateModel = "approximate_model";
		private const string WarningCodeOther = "other_model_notice";
		private const int MaximumDiagnosticWarningCategories = 3;

		private static readonly string[] DiagnosticWarningCategoryOrder =
		{
			WarningCodeUnsupportedPlayable,
			WarningCodeUnsupportedRule,
			WarningCodeCounterplayLimit,
			WarningCodeHiddenInformation,
			WarningCodeCaptureGap,
			WarningCodeApproximateModel,
			WarningCodeOther
		};

		private static readonly Dictionary<string, string> KnownMessages =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "Game state changed; previous recommendations were cleared.", StateChanged },
				{ "Advisor search cancelled.", SearchCancelled },
				{ "Advisor worker is not running.", WorkerUnavailable },
				{ "Advisor worker request timed out.", SolveTimedOut },
				{ "Game state snapshot is incomplete.", IncompleteState },
				{ "Searching candidate action lines...", Searching },
				{ "Worker response belonged to a different game state and was discarded.", StaleResult },
				{ "Final worker response belonged to a different game state and was discarded.", StaleResult },
				{ "Initial search result; deeper search is still running.", InitialResult },
				{ "Counterplay turn-pair solve completed.", "建议计算完成。" },
				{ "Recommendations include approximated mechanics.", PartialResult },
				{ "Live advisor disabled from settings.", "实战建议已关闭。" },
				{ "Live advisor worker exited unexpectedly.", WorkerUnavailable }
			};

		private static readonly Regex TechnicalDetailPattern = new Regex(
			@"(?ix)(" +
			@"https?://|file://|\bhttp\s*\d{3}\b|" +
			@"[a-z]:[\\/]|\\\\|/(?:users|home|var|tmp|appdata|program\s+files)/|" +
			@"\b(?:exception|traceback|stack\s+trace|inner\s+exception|socket|localhost|" +
			@"request[_\s-]?id|session[_\s-]?token|bearer|authorization|cookie|password|" +
			@"api[_\s-]?key|access\s+denied)\b|" +
			@"(?:[a-z_][a-z0-9_.]*)?(?:exception|error)\b|" +
			@"\bhresult\b\s*:?[\s]*(?:0x)?[0-9a-f]+|\b0x8[0-9a-f]{7}\b|" +
			@"(?:^|\s)--[a-z][a-z0-9-]*|[\{\}\[\]]" +
			@")",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex WhitespacePattern = new Regex(
			@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex LatinWordPattern = new Regex(
			@"[a-z]{3,}", RegexOptions.Compiled | RegexOptions.IgnoreCase |
			RegexOptions.CultureInvariant);

		private static readonly Regex CredentialHeaderPattern = new Regex(
			@"(?im)\b(authorization|cookie|set-cookie)\s*:\s*[^\r\n]+",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex CredentialValuePattern = new Regex(
			@"(?ix)\b(session[_\s-]?token|access[_\s-]?token|refresh[_\s-]?token|token|" +
			@"password|secret|api[_\s-]?key)\b(\s*[:=]\s*)" +
			@"(?:""[^""]*""|'[^']*'|[^\s;,]+)",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex BearerCredentialPattern = new Regex(
			@"(?i)\bbearer\s+[a-z0-9\-._~+/]+=*",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly Regex UriCredentialPattern = new Regex(
			@"(?i)(://)[^/@\s:]+:[^/@\s]+@",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		internal static string Status(string original, string fallback)
		{
			var fallbackCandidate = (fallback ?? "").Trim();
			var safeFallback = IsFriendlyChinese(fallbackCandidate)
				? Normalize(fallbackCandidate, 180)
				: SolveFailed;
			if (string.IsNullOrWhiteSpace(original))
				return safeFallback;

			var normalized = Normalize(original, 800);
			string translated;
			if (KnownMessages.TryGetValue(normalized, out translated))
				return translated;

			var lower = normalized.ToLowerInvariant();
			if (IsTimeoutMessage(lower))
			{
				LogHidden("超时详情", normalized);
				return SolveTimedOut;
			}
			if (IsIncompleteStateMessage(lower))
			{
				LogHidden("局面采集详情", normalized);
				return IncompleteState;
			}
			if (IsWorkerUnavailableMessage(lower))
			{
				LogHidden("求解器连接详情", normalized);
				return WorkerUnavailable;
			}
			if (lower.StartsWith("restarting live advisor worker", StringComparison.Ordinal) ||
				lower.StartsWith("live advisor worker exited", StringComparison.Ordinal))
				return "正在重新连接本机求解器…";
			if (lower.StartsWith("hdt observed a new game-state event", StringComparison.Ordinal))
				return StateChanged;
			if (lower.StartsWith("advisor game ended", StringComparison.Ordinal))
				return "本局已结束，实战建议已停止。";
			if (lower.Contains("recommendations include approximated mechanics"))
				return PartialResult;
			if (lower.Contains("solve completed") || lower.Contains("search completed"))
				return "建议计算完成。";

			if (IsFriendlyChinese(normalized))
				return Normalize(normalized, 180);

			LogHidden("状态消息", normalized);
			return safeFallback;
		}

		/// <summary>
		/// Converts a caught worker failure to safe UI copy. Ordinary logs should use one of the
		/// controlled diagnostic summaries below and must not serialize the raw exception object.
		/// </summary>
		internal static string Failure(Exception error, string fallback = null)
		{
			if (IsTimeoutException(error))
				return SolveTimedOut;
			if (IsExpectedCoverageFailure(error))
				return CoverageFailure(error);
			return Status(error?.Message, fallback ?? SolveFailed);
		}

		internal static string CoverageFailure(Exception error)
		{
			var workerError = FindWorkerError(error);
			switch ((workerError?.ErrorCode ?? "").Trim().ToLowerInvariant())
			{
				case "state_limit":
					return "当前局面的搜索分支达到安全上限，暂不展示未经验证的行动线；局面变化后会自动重算。";
				case "time_limit_reached":
					return "当前局面的计算达到时间上限，暂不展示未经验证的行动线；局面变化后会自动重算。";
				case "depth_limit_reached":
					return "当前局面的搜索深度达到安全上限，暂不展示未经验证的行动线；局面变化后会自动重算。";
				default:
					return Unsupported;
			}
		}

		internal static string ResponseStatus(
			string status, string original, bool isFinal, bool hasRecommendations)
		{
			var normalizedStatus = (status ?? "").Trim().ToLowerInvariant();
			var statusOverridesMessage = normalizedStatus == "stale" ||
				normalizedStatus == "cancelled" || normalizedStatus == "unsupported" ||
				normalizedStatus == "unavailable" || normalizedStatus == "error" ||
				!hasRecommendations;
			if (statusOverridesMessage)
				LogOverriddenStatus(original);
			if (normalizedStatus == "stale" || normalizedStatus == "cancelled")
				return StaleResult;
			if (normalizedStatus == "unsupported")
			{
				var controlled = Normalize(original, 180);
				return IsFriendlyChinese(controlled) ? controlled : Unsupported;
			}
			if (normalizedStatus == "unavailable" || normalizedStatus == "error")
				return IsTimeoutMessage(Normalize(original, 800).ToLowerInvariant())
					? SolveTimedOut
					: WorkerUnavailable;
			if (!hasRecommendations &&
				IsIncompleteStateMessage(Normalize(original, 800).ToLowerInvariant()))
				return IncompleteState;
			if (!hasRecommendations)
			{
				return normalizedStatus == "thinking" || normalizedStatus == "starting" || !isFinal
					? "首批合法路线生成后会自动显示。"
					: NoRecommendations;
			}
			return Status(original, isFinal ? "建议计算完成。" : InitialResult);
		}

		internal static IList<string> Notices(IEnumerable<string> originals, bool approximationOnly = false)
		{
			var result = new List<string>();
			foreach (var original in originals ?? Enumerable.Empty<string>())
			{
				var localized = Notice(original, approximationOnly);
				if (!string.IsNullOrWhiteSpace(localized) && !result.Contains(localized))
					result.Add(localized);
				if (result.Count == 2)
					break;
			}
			return result;
		}

		/// <summary>
		/// Worker warnings are diagnostic prose today, so classify them at the UI/log boundary
		/// into a small set of stable codes. The resulting log line intentionally contains no
		/// card names or raw warning text: a large unsupported-card flood stays one short,
		/// Chinese model-coverage summary and abstention is never presented as a failure.
		/// </summary>
		internal static string WarningDiagnosticSummary(IEnumerable<string> originals)
		{
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			var total = 0;
			foreach (var original in originals ?? Enumerable.Empty<string>())
			{
				if (string.IsNullOrWhiteSpace(original))
					continue;
				var code = WarningCode(original);
				int count;
				counts.TryGetValue(code, out count);
				counts[code] = count + 1;
				total++;
			}
			if (total == 0)
				return "";

			var categories = counts
				.Select(pair => new
				{
					Code = pair.Key,
					Count = pair.Value,
					Order = Array.IndexOf(DiagnosticWarningCategoryOrder, pair.Key)
				})
				.OrderByDescending(item => item.Count)
				.ThenBy(item => item.Order < 0 ? Int32.MaxValue : item.Order)
				.Take(MaximumDiagnosticWarningCategories)
				.Select(item => WarningCategoryLabel(item.Code) + " " + item.Count + " 条")
				.ToArray();

			return "顾问模型覆盖限制：" + string.Join("；", categories) +
				"（共 " + total + " 条）";
		}

		internal static string SolveDiagnosticSummary(
			AdvisorSolveResponse response, string stage, long fallbackElapsedMilliseconds)
		{
			if (response == null)
				return "顾问求解摘要：未收到结果。";

			var coverage = response.Coverage ?? new AdvisorCoverage();
			var recommendationCount = (response.Recommendations ??
				new List<AdvisorRecommendation>()).Count(item => item != null);
			var elapsed = response.ElapsedMilliseconds > 0
				? response.ElapsedMilliseconds
				: Math.Max(0, fallbackElapsedMilliseconds);
			var parts = new List<string>
			{
				"阶段=" + DiagnosticStage(stage),
				"状态=" + DiagnosticStatus(response.Status, coverage),
				"建议=" + recommendationCount.ToString(CultureInfo.InvariantCulture),
				"耗时=" + elapsed.ToString(CultureInfo.InvariantCulture) + "毫秒"
			};
			if (coverage.Overall.HasValue)
				parts.Add("总覆盖=" + DiagnosticPercent(coverage.Overall.Value));
			if (coverage.CardCoverage.HasValue)
				parts.Add("卡牌覆盖=" + DiagnosticPercent(coverage.CardCoverage.Value));
			if (coverage.RuleCoverage.HasValue)
				parts.Add("规则覆盖=" + DiagnosticPercent(coverage.RuleCoverage.Value));
			if (coverage.HasRootActionCoverageContract)
			{
				parts.Add("已生成首步=" + coverage.GeneratedFirstActionCount.ToString(
					CultureInfo.InvariantCulture) + "/" +
					coverage.LegalFirstActionCount.ToString(CultureInfo.InvariantCulture));
				parts.Add("已验证回应=" + coverage.ResponseVerifiedFirstActionCount.ToString(
					CultureInfo.InvariantCulture));
			}
			if (!string.IsNullOrWhiteSpace(coverage.DecisionRanker?.Status))
			{
				parts.Add("本方排序=" + DiagnosticOrderingStatus(
					coverage.DecisionRanker));
			}
			if (!string.IsNullOrWhiteSpace(coverage.BehaviorPrior?.Status))
			{
				parts.Add("对手排序=" + DiagnosticOrderingStatus(
					coverage.BehaviorPrior));
			}
			if (coverage.UnsupportedCount > 0)
				parts.Add("未覆盖项=" + coverage.UnsupportedCount.ToString(CultureInfo.InvariantCulture));
			var warningSummary = WarningDiagnosticSummary(response.Warnings);
			if (!string.IsNullOrWhiteSpace(warningSummary))
				parts.Add(warningSummary);
			return "顾问求解摘要：" + string.Join("；", parts) + "。";
		}

		private static string DiagnosticOrderingStatus(AdvisorSearchOrderingStatus model)
		{
			var normalized = (model?.Status ?? "").Trim().ToLowerInvariant();
			switch (normalized)
			{
				case "applied":
					return model.OrderingApplied && model.OrderingAttemptCount > 0
						? "已应用" : "状态不一致";
				case "available_not_applicable": return "可用但本局未适用";
				case "runtime_rejected": return "本局已安全停用";
				case "disabled": return "未启用";
				default: return "状态未知";
			}
		}

		internal static string SolveFailureDiagnostic(
			Exception error, string stage, bool preservingInitialResult = false)
		{
			var workerError = FindWorkerError(error);
			var category = IsTimeoutException(error)
				? "达到时间上限"
				: DiagnosticErrorCategory(workerError);
			var suffix = preservingInitialResult
				? "；已保留首批结果。"
				: "；本次未展示不可靠路线。";
			return "顾问求解摘要：阶段=" + DiagnosticStage(stage) +
				"；状态=" + category + suffix;
		}

		internal static string RuntimeFailureDiagnostic(Exception error, string operation)
		{
			var normalizedOperation = (operation ?? "").Trim().ToLowerInvariant();
			var workerError = FindWorkerError(error);
			var category = IsTimeoutException(error)
				? "达到时间上限"
				: RuntimeDiagnosticErrorCategory(workerError, normalizedOperation);
			string step;
			switch (normalizedOperation)
			{
				case "worker_start": step = "启动本机求解器"; break;
				case "resume": step = "恢复当前对局"; break;
				case "snapshot": step = "采集局面"; break;
				case "power": step = "读取动作轨迹"; break;
				case "observe": step = "写入训练观察"; break;
				case "behavior_outbox": step = "保存行为待发送记录"; break;
				case "behavior_flush": step = "同步行为记录"; break;
				case "result_outbox": step = "保存终局待发送记录"; break;
				case "result_flush": step = "同步终局结果"; break;
				default: step = "顾问运行"; break;
			}
			return "顾问运行摘要：环节=" + step + "；状态=" + category + "。";
		}

		private static string RuntimeDiagnosticErrorCategory(
			AdvisorWorkerException workerError, string operation)
		{
			if (workerError != null)
				return DiagnosticErrorCategory(workerError);
			switch (operation)
			{
				case "worker_start": return "本机求解器异常";
				case "behavior_outbox":
				case "result_outbox":
					return "本地待发送记录异常";
				case "behavior_flush":
				case "result_flush":
				case "observe":
					return "训练记录同步异常";
				default: return "运行环节异常";
			}
		}

		internal static bool IsExpectedCoverageFailure(Exception error)
		{
			var workerError = FindWorkerError(error);
			if (workerError == null || workerError.StatusCode !=
				(System.Net.HttpStatusCode)422)
			{
				return false;
			}
			var code = (workerError.ErrorCode ?? "").Trim().ToLowerInvariant();
			return code == "unsupported_scope" || code == "state_limit" ||
				code == "time_limit_reached" || code == "depth_limit_reached" ||
				code == "required_mechanic_unproven" || string.IsNullOrWhiteSpace(code);
		}

		private static string DiagnosticStage(string stage)
		{
			switch ((stage ?? "").Trim().ToLowerInvariant())
			{
				case "initial": return "首批";
				case "final": return "深化";
				case "single": return "单阶段";
				default: return "未知";
			}
		}

		private static string DiagnosticStatus(string status, AdvisorCoverage coverage)
		{
			if (coverage != null && coverage.ScopedLethal)
				return "局部斩杀证明";
			if (coverage != null && coverage.Exact)
				return "当前规则范围内完整";
			switch ((status ?? "").Trim().ToLowerInvariant())
			{
				case "ok": return "完成";
				case "partial": return "近似结果";
				case "unsupported": return "规则覆盖不足";
				case "cancelled": return "局面变化，已取消";
				case "stale": return "结果已过期";
				case "unavailable": return "求解器暂不可用";
				case "error": return "计算失败";
				default: return "未知";
			}
		}

		private static string DiagnosticPercent(double value)
		{
			if (Double.IsNaN(value) || Double.IsInfinity(value))
				return "未知";
			var normalized = Math.Max(0.0, Math.Min(1.0, value));
			return (normalized * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
		}

		private static string DiagnosticErrorCategory(AdvisorWorkerException error)
		{
			if (error == null)
				return "本机求解器异常";
			switch ((error.ErrorCode ?? "").Trim().ToLowerInvariant())
			{
				case "unsupported_scope":
				case "required_mechanic_unproven":
					return "规则覆盖不足";
				case "state_limit": return "搜索量达到安全上限";
				case "time_limit_reached": return "达到时间上限";
				case "depth_limit_reached": return "达到搜索深度上限";
				case "invalid_request":
				case "schema_error": return "局面数据不完整";
				default:
					return error.StatusCode.HasValue
						? "本机求解器返回异常状态"
						: "本机求解器连接异常";
			}
		}

		private static AdvisorWorkerException FindWorkerError(Exception error)
		{
			if (error == null)
				return null;
			var workerError = error as AdvisorWorkerException;
			if (workerError != null)
				return workerError;
			var aggregate = error as AggregateException;
			if (aggregate != null)
			{
				foreach (var inner in aggregate.Flatten().InnerExceptions)
				{
					var found = FindWorkerError(inner);
					if (found != null)
						return found;
				}
			}
			return FindWorkerError(error.InnerException);
		}

		internal static string WarningCode(string original)
		{
			var normalized = Normalize(RedactSecrets(original), 800).ToLowerInvariant();
			if (normalized.Contains("unsupported_solver_abstention") ||
				normalized.Contains("currently playable unsupported rule") ||
				normalized.Contains("ranked advice is withheld"))
				return WarningCodeUnsupportedPlayable;
			if (normalized.Contains("unsupported rule") ||
				normalized.Contains("unsupported solver") ||
				normalized.Contains("unsupported") ||
				normalized.Contains("card_text_not_parsed") ||
				normalized.Contains("card text") ||
				normalized.Contains("unknown mechanic") ||
				normalized.Contains("could not be applied") ||
				normalized.Contains("missing effect") ||
				normalized.Contains("尚未完整支持") ||
				normalized.Contains("规则未覆盖"))
				return WarningCodeUnsupportedRule;
			if (normalized.Contains("counterplay") ||
				normalized.Contains("opponent response") ||
				normalized.Contains("best-response") ||
				normalized.Contains("对手回应"))
				return WarningCodeCounterplayLimit;
			if (normalized.Contains("hidden") ||
				normalized.Contains("unknown burn") ||
				normalized.Contains("burned card") ||
				normalized.Contains("unknown card") ||
				normalized.Contains("隐藏信息") ||
				normalized.Contains("未知卡牌"))
				return WarningCodeHiddenInformation;
			if (normalized.Contains("snapshot") ||
				normalized.Contains("capture") ||
				normalized.Contains("entity snapshot") ||
				normalized.Contains("truncated") ||
				normalized.Contains("快照") ||
				normalized.Contains("局面信息不完整") ||
				normalized.Contains("截取"))
				return WarningCodeCaptureGap;
			if (normalized.Contains("approximate") ||
				normalized.Contains("approximated") ||
				normalized.Contains("generic model") ||
				normalized.Contains("outside this scope") ||
				normalized.Contains("outside this proof") ||
				normalized.Contains("近似"))
				return WarningCodeApproximateModel;
			return WarningCodeOther;
		}

		internal static string RecommendationSummary(AdvisorRecommendation recommendation)
		{
			if (recommendation == null)
				return "候选行动线";

			if (IsFriendlyChinese(recommendation.Summary))
				return Normalize(recommendation.Summary, 180);

			if (!string.IsNullOrWhiteSpace(recommendation.Summary))
				LogHidden("行动线说明", recommendation.Summary);

			var first = (recommendation.Actions ?? new List<AdvisorAction>())
				.Where(action => action != null &&
					!string.Equals(action.Type, "end_turn", StringComparison.OrdinalIgnoreCase))
				.OrderBy(action => action.Index <= 0 ? Int32.MaxValue : action.Index)
				.FirstOrDefault();
			string text;
			var type = (first?.Type ?? "").Trim().ToLowerInvariant();
			if (first == null)
				text = "直接结束回合，不再执行其他已建模动作。";
			else if (type == "attack")
				text = "先执行列出的攻击或交换，再按顺序继续。";
			else if (type == "play_card" || type == "play")
				text = "先打出列出的卡牌，再按顺序继续。";
			else if (type == "hero_power")
				text = "先使用英雄技能，再按顺序继续。";
			else if (type == "location_activate")
				text = "先激活列出的地点，再按顺序继续。";
			else
				text = "按列出的动作顺序执行。";

			if (recommendation.IsResponseVerified && recommendation.ResponseIsProvenLethal)
				return text + " 对手存在已建模的反杀回应。";
			if (recommendation.IsResponseVerified)
				return text + " 排序已考虑当前可见范围内的最差对手回应。";
			return text + " 该路线尚未完成对手回应验证。";
		}

		internal static string AlternativeKind(string kind)
		{
			switch ((kind ?? "").Trim().ToLowerInvariant())
			{
				case "co_optimal":
					return "完整搜索范围内共同最优";
				case "near_optimal":
					return "近优备选";
				case "best_found":
					return "当前已验证最佳";
				case "backup":
					return "已验证备选";
				case "fallback":
					return "未完整验证的兜底路线";
				default:
					return "";
			}
		}

		internal static string PortfolioCoverageSummary(AdvisorCoverage coverage)
		{
			if (coverage == null || !coverage.HasRootActionCoverageContract)
				return "";
			if (!coverage.RootActionCoverageContractValid)
				return "求解器返回的合法首步覆盖信息不完整或相互矛盾；" +
					"已关闭共同最优和近优结论。";
			if (coverage.RootActionCoverageComplete)
				return coverage.PortfolioOptimalityProven
					? "已覆盖全部 " + coverage.LegalFirstActionCount +
						" 种合法首步，并完成当前可见、已建模规则范围内的组合最优性证明。"
					: "已覆盖全部 " + coverage.LegalFirstActionCount +
						" 种合法首步；后续行动尚未穷尽，仅展示当前最佳。";
			if (coverage.ResponseVerifiedFirstActionCount == 0)
				return "首步组合尚未完成回应验证：0 / " +
					coverage.LegalFirstActionCount +
					" 种；当前仅为近似候选，不构成安全或最优证明。";
			return "首步组合尚未完整验证：已验证 " +
				coverage.ResponseVerifiedFirstActionCount + " / " +
				coverage.LegalFirstActionCount +
				" 种；当前排序只代表已经验证到的路线。";
		}

		internal static string Action(AdvisorAction action)
		{
			return Action(action, ResolveLocalizedCardName, null);
		}

		internal static string Action(AdvisorAction action, AdvisorGameState state)
		{
			return Action(action, ResolveLocalizedCardName, state);
		}

		internal static string Action(
			AdvisorAction action, Func<string, string> localizedCardNameResolver)
		{
			return Action(action, localizedCardNameResolver, null);
		}

		private static string Action(
			AdvisorAction action,
			Func<string, string> localizedCardNameResolver,
			AdvisorGameState state)
		{
			if (action == null)
				return "执行建议动作";

			var original = Normalize(action.Text, 240);
			if (ContainsTechnicalDetail(original))
				LogHidden("动作说明", original);

			var type = (action.Type ?? "").Trim().ToLowerInvariant();
			if (type == "end_turn" || type == "end turn" || type == "pass")
				return "结束回合";

			if (type == "play_card" || type == "play")
			{
				var source = FindEntity(state, action.SourceEntityId);
				var name = ResolveActionCardName(
					action.CardId, localizedCardNameResolver,
					FirstSafeActionName(source?.Name, AfterPrefix(original, "Play ")));
				var placement = action.BoardPosition.HasValue &&
					action.BoardPosition.Value >= 1 && action.BoardPosition.Value <= 7
					? "，放在从左数第 " + action.BoardPosition.Value + " 个位置"
					: "";
				var prefix = "打出" +
					(string.IsNullOrWhiteSpace(name) ? "列出的卡牌" : "「" + name + "」") +
					placement;
				return state == null
					? prefix + (action.TargetEntityId.HasValue ? "，选择指定目标" : "")
					: prefix + TargetSuffix(action, state, source, localizedCardNameResolver);
			}

			if (type == "hero_power")
			{
				var source = FindEntity(state, action.SourceEntityId);
				var name = ResolveActionCardName(
					action.CardId, localizedCardNameResolver,
					FirstSafeActionName(source?.Name, AfterPrefix(original, "Use ")));
				var prefix = "使用英雄技能" +
					(string.IsNullOrWhiteSpace(name) ? "" : "「" + name + "」");
				return state == null
					? prefix + (action.TargetEntityId.HasValue ? "，选择指定目标" : "")
					: prefix + TargetSuffix(action, state, source, localizedCardNameResolver);
			}

			if (type == "location_activate")
			{
				var source = FindEntity(state, action.SourceEntityId);
				var name = ResolveActionCardName(
					action.CardId, localizedCardNameResolver,
					FirstSafeActionName(source?.Name, AfterPrefix(original, "Activate ")));
				var prefix = (state == null ? "激活地点" : "点击地标") +
					(string.IsNullOrWhiteSpace(name) ? "" : "「" + name + "」");
				return state == null
					? prefix + (action.TargetEntityId.HasValue ? "，选择指定目标" : "")
					: prefix + TargetSuffix(action, state, source, localizedCardNameResolver);
			}

			if (type == "attack")
			{
				if (state != null)
				{
					var sourceDescription = DescribeEntity(
						state, action.SourceEntityId, localizedCardNameResolver);
					var targetDescription = DescribeEntity(
						state, action.TargetEntityId, localizedCardNameResolver);
					if (!string.IsNullOrWhiteSpace(sourceDescription) &&
						!string.IsNullOrWhiteSpace(targetDescription))
					{
						return "用" + sourceDescription + "攻击" + targetDescription;
					}
					if (!string.IsNullOrWhiteSpace(sourceDescription))
						return "用" + sourceDescription + "攻击当前指定目标";
				}
				string attacker;
				string target;
				if (TryParseAttack(original, out attacker, out target))
				{
					attacker = ResolveActionCardName(
						action.CardId, localizedCardNameResolver, SafeActionName(attacker));
					target = SafeActionName(target);
					if (!string.IsNullOrWhiteSpace(attacker) && !string.IsNullOrWhiteSpace(target))
					{
						if (IsSafeLocalizedActionName(target))
							return "用「" + attacker + "」攻击「" + target + "」";
						return "用「" + attacker + "」攻击指定目标";
					}
					if (!string.IsNullOrWhiteSpace(attacker))
						return "用「" + attacker + "」攻击指定目标";
				}
				return "用列出的角色攻击指定目标";
			}

			if (IsFriendlyChinese(original))
				return Normalize(original, 120);
			if (!string.IsNullOrWhiteSpace(original))
				LogHidden("动作说明", original);
			return "执行列出的建议动作";
		}

		private static string TargetSuffix(
			AdvisorAction action,
			AdvisorGameState state,
			AdvisorEntityState source,
			Func<string, string> localizedCardNameResolver)
		{
			if (action.TargetEntityId.HasValue)
			{
				var target = DescribeEntity(
					state, action.TargetEntityId, localizedCardNameResolver);
				return " → " + (string.IsNullOrWhiteSpace(target) ? "当前指定目标" : target);
			}
			var automatic = DescribeAutomaticHeroTarget(
				state, source, localizedCardNameResolver);
			return string.IsNullOrWhiteSpace(automatic)
				? ""
				: "（自动作用于" + automatic + "）";
		}

		private static string DescribeAutomaticHeroTarget(
			AdvisorGameState state,
			AdvisorEntityState source,
			Func<string, string> localizedCardNameResolver)
		{
			if (state == null || source == null)
				return "";
			var text = ((source.EnglishText ?? "") + " " + (source.CardText ?? ""))
				.ToLowerInvariant();
			var mentionsEnemyHero = text.Contains("enemy hero") || text.Contains("敌方英雄");
			var mentionsFriendlyHero = text.Contains("your hero") || text.Contains("你的英雄") ||
				text.Contains("己方英雄");
			if (mentionsEnemyHero == mentionsFriendlyHero)
				return "";
			return DescribeEntity(
				state,
				mentionsEnemyHero ? state.Opponent?.Hero?.EntityId : state.Player?.Hero?.EntityId,
				localizedCardNameResolver);
		}

		private static string DescribeEntity(
			AdvisorGameState state,
			int? entityId,
			Func<string, string> localizedCardNameResolver)
		{
			if (state == null || !entityId.HasValue || entityId.Value <= 0)
				return "";
			var entity = FindEntity(state, entityId);
			if (entity == null)
				return "";
			var owner = EntityBelongsToPlayer(state.Player, entityId.Value)
				? state.Player
				: EntityBelongsToPlayer(state.Opponent, entityId.Value)
					? state.Opponent
					: null;
			var side = owner == null
				? ""
				: ReferenceEquals(owner, state.Player) ? "我方" : "敌方";
			var name = ResolveActionCardName(
				entity.CardId,
				localizedCardNameResolver,
				SafeActionName(entity.Name));
			var quotedName = string.IsNullOrWhiteSpace(name) ? "" : "「" + name + "」";
			if (owner != null && owner.Hero != null && owner.Hero.EntityId == entityId.Value)
				return side + "英雄" + quotedName;
			if (owner != null && owner.HeroPower != null &&
				owner.HeroPower.EntityId == entityId.Value)
				return side + "英雄技能" + quotedName;
			if (owner != null && owner.Weapon != null && owner.Weapon.EntityId == entityId.Value)
				return side + "武器" + quotedName;
			var boardPosition = BoardPosition(owner, entityId.Value);
			if (boardPosition > 0)
			{
				var kind = string.Equals(
					entity.CardType, "LOCATION", StringComparison.OrdinalIgnoreCase)
					? "地标"
					: "随从";
				return side + "从左数第 " + boardPosition + " 个" + kind + quotedName;
			}
			return string.IsNullOrWhiteSpace(quotedName) ? "" : quotedName;
		}

		private static int BoardPosition(AdvisorPlayerState player, int entityId)
		{
			if (player?.Board == null)
				return 0;
			for (var index = 0; index < player.Board.Count; index++)
			{
				var entity = player.Board[index];
				if (entity == null || entity.EntityId != entityId)
					continue;
				return entity.ZonePosition >= 1 && entity.ZonePosition <= 7
					? entity.ZonePosition
					: index + 1;
			}
			return 0;
		}

		internal static AdvisorEntityState FindEntity(AdvisorGameState state, int? entityId)
		{
			if (state == null || !entityId.HasValue || entityId.Value <= 0)
				return null;
			return FindEntity(state.Player, entityId.Value) ??
				FindEntity(state.Opponent, entityId.Value) ??
				(state.OtherPublicEntities ?? new List<AdvisorEntityState>())
					.FirstOrDefault(entity => entity != null && entity.EntityId == entityId.Value) ??
				(state.GameEntity != null && state.GameEntity.EntityId == entityId.Value
					? state.GameEntity
					: null);
		}

		internal static AdvisorEntityState FindEntity(AdvisorPlayerState player, int entityId)
		{
			if (player == null)
				return null;
			foreach (var entity in PlayerEntities(player))
			{
				if (entity != null && entity.EntityId == entityId)
					return entity;
			}
			return null;
		}

		private static bool EntityBelongsToPlayer(AdvisorPlayerState player, int entityId)
		{
			return FindEntity(player, entityId) != null;
		}

		private static IEnumerable<AdvisorEntityState> PlayerEntities(AdvisorPlayerState player)
		{
			if (player == null)
				yield break;
			if (player.PlayerEntity != null)
				yield return player.PlayerEntity;
			if (player.Hero != null)
				yield return player.Hero;
			if (player.HeroPower != null)
				yield return player.HeroPower;
			if (player.Weapon != null)
				yield return player.Weapon;
			foreach (var entities in new[]
			{
				player.Hand, player.Board, player.Deck, player.Graveyard, player.Secrets,
				player.SetAside, player.RemovedFromGame, player.OtherEntities
			})
			{
				foreach (var entity in entities ?? new List<AdvisorEntityState>())
					yield return entity;
			}
		}

		internal static string Scope(string scope)
		{
			if (string.Equals(scope, "visible_generic_v2", StringComparison.OrdinalIgnoreCase))
				return "当前可见通用规则";
			if (string.Equals(scope, "visible_generic_turnpair_v1", StringComparison.OrdinalIgnoreCase))
				return "当前可见回合对规则";
			if (!string.IsNullOrWhiteSpace(scope))
				LogHidden("规则范围", scope);
			return "受支持的当前可见规则";
		}

		internal static bool ContainsTechnicalDetail(string value)
		{
			return !string.IsNullOrWhiteSpace(value) && TechnicalDetailPattern.IsMatch(value);
		}

		internal static string RedactSecrets(string value)
		{
			var redacted = value ?? "";
			redacted = CredentialHeaderPattern.Replace(redacted, match =>
				match.Groups[1].Value + ": [已隐藏]");
			redacted = CredentialValuePattern.Replace(redacted, match =>
				match.Groups[1].Value + match.Groups[2].Value + "[已隐藏]");
			redacted = BearerCredentialPattern.Replace(redacted, "Bearer [已隐藏]");
			redacted = UriCredentialPattern.Replace(redacted, "$1[已隐藏]@");
			return redacted;
		}

		private static string Notice(string original, bool approximationOnly)
		{
			if (string.IsNullOrWhiteSpace(original))
				return "";
			var normalized = Normalize(original, 800);
			if (IsFriendlyChinese(normalized))
				return Normalize(normalized, 180);

			var lower = normalized.ToLowerInvariant();
			string localized;
			if (lower.Contains("ranked advice is withheld") ||
				lower.Contains("unsupported solver") || lower.Contains("unsupported rule"))
				localized = UnsupportedNotice;
			else if ((lower.Contains("counterplay") || lower.Contains("opponent response") ||
				lower.Contains("best-response")) &&
				(lower.Contains("deadline") || lower.Contains("time") || lower.Contains("node") ||
				lower.Contains("depth") || lower.Contains("cancel") || lower.Contains("not evaluated") ||
				lower.Contains("incomplete") || lower.Contains("did not resolve")))
				localized = ResponseNotice;
			else if (lower.Contains("hidden") || lower.Contains("unknown burn") ||
				lower.Contains("burned card") || lower.Contains("unknown card"))
				localized = HiddenInformationNotice;
			else if (lower.Contains("snapshot") || lower.Contains("capture") ||
				lower.Contains("entity snapshot") || lower.Contains("truncated"))
				localized = CaptureNotice;
			else if (lower.Contains("card_text_not_parsed") || lower.Contains("card text") ||
				lower.Contains("unsupported") || lower.Contains("unknown mechanic") ||
				lower.Contains("could not be applied") || lower.Contains("missing effect"))
				localized = UnsupportedNotice;
			else if (lower.Contains("approximate") || lower.Contains("approximated") ||
				lower.Contains("generic model") || lower.Contains("outside this scope") ||
				lower.Contains("outside this proof"))
				localized = ApproximationNotice;
			else
				localized = approximationOnly ? ApproximationNotice : GenericNotice;

			return localized;
		}

		private static bool IsFriendlyChinese(string value)
		{
			if (string.IsNullOrWhiteSpace(value) || ContainsTechnicalDetail(value))
				return false;
			var containsChinese = value.Any(character =>
				(character >= '\u3400' && character <= '\u4DBF') ||
				(character >= '\u4E00' && character <= '\u9FFF'));
			if (!containsChinese)
				return false;
			var withoutKnownAbbreviations = Regex.Replace(
				value, @"\bHDT\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			return !LatinWordPattern.IsMatch(withoutKnownAbbreviations);
		}

		private static bool IsTimeoutMessage(string lower)
		{
			return !string.IsNullOrWhiteSpace(lower) &&
				(lower.Contains("timed out") || lower.Contains("timeout") ||
				lower.Contains("time limit") || lower.Contains("deadline exceeded") ||
				lower.Contains("计算超时") || lower.Contains("请求超时"));
		}

		private static bool IsIncompleteStateMessage(string lower)
		{
			return !string.IsNullOrWhiteSpace(lower) &&
				((lower.Contains("game state") || lower.Contains("snapshot") ||
				lower.Contains("capture")) &&
				(lower.Contains("incomplete") || lower.Contains("unavailable") ||
				lower.Contains("missing") || lower.Contains("truncated")) ||
				lower.Contains("局面信息不完整") || lower.Contains("局面采集不完整"));
		}

		private static bool IsWorkerUnavailableMessage(string lower)
		{
			return !string.IsNullOrWhiteSpace(lower) &&
				((lower.Contains("advisor worker") || lower.Contains("local advisor") ||
				lower.Contains("solver")) &&
				(lower.Contains("not running") || lower.Contains("unavailable") ||
				lower.Contains("unable to reach") || lower.Contains("connection refused") ||
				lower.Contains("failed to start") || lower.Contains("startup failed") ||
				lower.Contains("did not become healthy") || lower.Contains("exited")));
		}

		private static bool IsTimeoutException(Exception error)
		{
			var current = error;
			while (current != null)
			{
				if (current is TimeoutException)
					return true;
				var aggregate = current as AggregateException;
				if (aggregate != null)
				{
					foreach (var inner in aggregate.Flatten().InnerExceptions)
					{
						if (IsTimeoutException(inner))
							return true;
					}
					return false;
				}
				current = current.InnerException;
			}
			return false;
		}

		private static string SafeActionName(string value)
		{
			var normalized = Normalize(value, 80).Trim(' ', '.', ':', ';');
			if (string.IsNullOrWhiteSpace(normalized) || ContainsTechnicalDetail(normalized))
				return "";
			var lower = normalized.ToLowerInvariant();
			if (lower == "unknown" || lower == "unknown card" ||
				lower == "generated minion" || lower == "play card" ||
				lower == "hero power" || lower == "end turn" ||
				Regex.IsMatch(normalized, @"^[a-z]{2,8}[0-9_\-]{2,}[a-z0-9_\-]*$",
					RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
				return "";
			return normalized;
		}

		private static string FirstSafeActionName(params string[] values)
		{
			foreach (var value in values ?? new string[0])
			{
				var candidate = SafeActionName(value);
				if (!string.IsNullOrWhiteSpace(candidate))
					return candidate;
			}
			return "";
		}

		private static string ResolveActionCardName(
			string cardId, Func<string, string> localizedCardNameResolver, string fallback)
		{
			if (!string.IsNullOrWhiteSpace(cardId) && localizedCardNameResolver != null)
			{
				try
				{
					var localized = SafeActionName(localizedCardNameResolver(cardId.Trim()));
					if (IsSafeLocalizedActionName(localized) &&
						!string.Equals(localized, cardId.Trim(), StringComparison.OrdinalIgnoreCase))
						return localized;
				}
				catch
				{
					// HDT may still be loading its locale database. Use the controlled fallback.
				}
			}
			var controlledFallback = SafeActionName(fallback);
			return IsSafeLocalizedActionName(controlledFallback) ? controlledFallback : "";
		}

		private static bool IsSafeLocalizedActionName(string value)
		{
			return !string.IsNullOrWhiteSpace(value) &&
				!ContainsTechnicalDetail(value) &&
				value.Any(character =>
					(character >= '\u3400' && character <= '\u4DBF') ||
					(character >= '\u4E00' && character <= '\u9FFF'));
		}

		internal static string ResolveLocalizedCardName(string cardId)
		{
			if (string.IsNullOrWhiteSpace(cardId) ||
				!Regex.IsMatch(cardId, @"^[a-z0-9_\-]{1,80}$",
					RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
				return "";
			try
			{
				var card = Database.GetCardFromId(cardId.Trim());
				if (card == null)
					return "";
				var localized = SafeActionName(card.LocalizedName);
				return !string.IsNullOrWhiteSpace(localized)
					? localized
					: SafeActionName(card.Name);
			}
			catch
			{
				return "";
			}
		}

		private static string WarningCategoryLabel(string code)
		{
			switch (code)
			{
				case WarningCodeUnsupportedPlayable:
					return "可出牌规则未覆盖";
				case WarningCodeUnsupportedRule:
					return "规则未完整建模";
				case WarningCodeCounterplayLimit:
					return "对手回应搜索受限";
				case WarningCodeHiddenInformation:
					return "隐藏信息影响";
				case WarningCodeCaptureGap:
					return "局面采集不完整";
				case WarningCodeApproximateModel:
					return "近似模型";
				default:
					return "其他模型提示";
			}
		}

		private static string AfterPrefix(string value, string prefix)
		{
			return !string.IsNullOrWhiteSpace(value) &&
				value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
					? value.Substring(prefix.Length)
					: "";
		}

		private static bool TryParseAttack(string value, out string attacker, out string target)
		{
			attacker = "";
			target = "";
			if (string.IsNullOrWhiteSpace(value) ||
				!value.StartsWith("Attack ", StringComparison.OrdinalIgnoreCase))
				return false;
			var separator = value.LastIndexOf(" with ", StringComparison.OrdinalIgnoreCase);
			if (separator <= "Attack ".Length)
				return false;
			target = value.Substring("Attack ".Length, separator - "Attack ".Length);
			attacker = value.Substring(separator + " with ".Length);
			return true;
		}

		private static string Normalize(string value, int maximumLength)
		{
			var normalized = WhitespacePattern.Replace(value ?? "", " ").Trim();
			return normalized.Length <= maximumLength
				? normalized
				: normalized.Substring(0, maximumLength) + "…";
		}

		private static void LogHidden(string category, string original)
		{
			if (string.IsNullOrWhiteSpace(original))
				return;
			Log.Debug("顾问界面已隐藏原始" + category + "：" +
				Normalize(RedactSecrets(original), 2000));
		}

		private static void LogOverriddenStatus(string original)
		{
			if (string.IsNullOrWhiteSpace(original))
				return;
			var normalized = Normalize(original, 800);
			string translated;
			if (!KnownMessages.TryGetValue(normalized, out translated))
				LogHidden("求解状态", normalized);
		}
	}
}
