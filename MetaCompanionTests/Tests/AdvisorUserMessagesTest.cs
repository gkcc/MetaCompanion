using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Hearthstone_Deck_Tracker.Hearthstone;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class AdvisorUserMessagesTest
	{
		[TestMethod]
		public void Status_TranslatesKnownWorkerTextAndHidesTechnicalDetails()
		{
			Assert.AreEqual(
				AdvisorUserMessages.PartialResult,
				AdvisorUserMessages.Status(
					"Recommendations include approximated mechanics.",
					AdvisorUserMessages.SolveFailed));

			var original =
				"本机求解器暂不可用：HTTP 500 InvalidOperationException at C:\\Users\\someone\\worker.py; " +
				"session_" + "token=" + "sample-value";
			var visible = AdvisorUserMessages.Status(original, AdvisorUserMessages.WorkerUnavailable);
			Assert.AreEqual(AdvisorUserMessages.WorkerUnavailable, visible);
			Assert.IsFalse(visible.Contains("HTTP"));
			Assert.IsFalse(visible.Contains("Exception"));
			Assert.IsFalse(visible.Contains("C:\\"));
			Assert.IsFalse(visible.Contains("token"));
			Assert.IsFalse(visible.Contains("secret"));

			Assert.AreEqual(
				AdvisorUserMessages.WorkerUnavailable,
				AdvisorUserMessages.Status(
					"读取失败：InvalidOperationException",
					AdvisorUserMessages.WorkerUnavailable));
			Assert.AreEqual(
				AdvisorUserMessages.WorkerUnavailable,
				AdvisorUserMessages.Status(
					"读取失败：HRESULT 0x80070005",
					AdvisorUserMessages.WorkerUnavailable));
			Assert.IsTrue(AdvisorUserMessages.ContainsTechnicalDetail(
				"读取失败：System.IO.IOException"));
			Assert.IsTrue(AdvisorUserMessages.ContainsTechnicalDetail(
				"读取失败：HRESULT 0x80070005"));
			Assert.AreEqual(
				AdvisorUserMessages.WorkerUnavailable,
				AdvisorUserMessages.Status(
					"本机求解器暂不可用；将在后台重试。Connection refused",
					AdvisorUserMessages.WorkerUnavailable));
		}

		[TestMethod]
		public void Status_SummarizesStartupTimeoutAndIncompleteStateWithActionableChinese()
		{
			var startup = AdvisorUserMessages.Status(
				"Advisor worker failed to start: System.IO.FileNotFoundException at " +
				@"C:\Users\Player\AdvisorWorker\launch_solver.py",
				AdvisorUserMessages.WorkerUnavailable);
			Assert.AreEqual(AdvisorUserMessages.WorkerUnavailable, startup);
			StringAssert.Contains(startup, "后台重试");
			StringAssert.Contains(startup, "开发者日志");

			var timeout = AdvisorUserMessages.Status(
				"Advisor worker request timed out after 12 seconds. request_id=private-id",
				AdvisorUserMessages.SolveFailed);
			Assert.AreEqual(AdvisorUserMessages.SolveTimedOut, timeout);
			StringAssert.Contains(timeout, "自动重试");
			StringAssert.Contains(timeout, "开发者日志");

			var incomplete = AdvisorUserMessages.ResponseStatus(
				"partial",
				"Game state snapshot is incomplete: missing entity payload at /tmp/state.json",
				true,
				false);
			Assert.AreEqual(AdvisorUserMessages.IncompleteState, incomplete);
			StringAssert.Contains(incomplete, "局面更新后会自动重试");

			var unsupported = AdvisorUserMessages.ResponseStatus(
				"unsupported",
				"Wound Prey has a currently playable unsupported rule; ranked advice is withheld.",
				true,
				false);
			Assert.AreEqual(AdvisorUserMessages.Unsupported, unsupported);
			StringAssert.Contains(unsupported, "不是插件故障");

			var visible = string.Join("\n", new[] { startup, timeout, incomplete, unsupported });
			Assert.IsFalse(visible.Contains("Advisor"));
			Assert.IsFalse(visible.Contains("Exception"));
			Assert.IsFalse(visible.Contains("request_id"));
			Assert.IsFalse(visible.Contains(@"C:\Users"));
			Assert.IsFalse(visible.Contains("/tmp"));
			Assert.IsFalse(visible.Contains("Wound Prey"));
		}

		[TestMethod]
		public void Notices_CollapseKnownEnglishFloodToAtMostTwoChineseLimits()
		{
			Assert.AreEqual("模型覆盖提示（不是故障）：", AdvisorPanel.WarningHeading);
			var notices = AdvisorUserMessages.Notices(new[]
			{
				"Arcane Bolt: card_text_not_parsed",
				"Card text for Arcane Bolt is unavailable.",
				"Arcane Bolt has a currently playable unsupported rule; ranked advice is withheld.",
				"No opponent response was attached because the counterplay deadline was exhausted.",
				"Unknown worker detail from /tmp/advisor.json with " + "token=" + "sample-value"
			});

			Assert.AreEqual(2, notices.Count);
			Assert.IsTrue(notices.Any(item => item.Contains("尚未完整支持")));
			Assert.IsTrue(notices.Any(item => item.Contains("对手回应搜索")));
			var visible = string.Join("；", notices.ToArray());
			Assert.IsFalse(visible.Contains("Arcane Bolt"));
			Assert.IsFalse(visible.Contains("card_text_not_parsed"));
			Assert.IsFalse(visible.Contains("/tmp"));
			Assert.IsFalse(visible.Contains("token"));
		}

		[TestMethod]
		public void Action_RendersOneBasedBoardPositionInChinese()
		{
			var visible = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "play_card",
					CardId = "UNKNOWN_TEST_MINION",
					Text = "Play 安全随从名",
					BoardPosition = 2,
					TargetEntityId = 8
				},
				cardId => "");

			Assert.AreEqual(
				"打出「安全随从名」，放在从左数第 2 个位置，选择指定目标",
				visible);

			var invalid = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "play_card",
					Text = "Play 安全随从名",
					BoardPosition = 0
				},
				cardId => "");
			Assert.AreEqual("打出「安全随从名」", invalid);
		}

		[TestMethod]
		public void Action_PrefersHdtCurrentLanguageCardNameOverWorkerText()
		{
			var hdtCard = Database.GetCardFromId("CS2_029");
			Assert.IsNotNull(hdtCard, "The HDT card database should contain Fireball.");
			var expectedName = string.IsNullOrWhiteSpace(hdtCard.LocalizedName)
				? hdtCard.Name
				: hdtCard.LocalizedName;
			Assert.IsFalse(string.IsNullOrWhiteSpace(expectedName));

			var visible = AdvisorUserMessages.Action(new AdvisorAction
			{
				Type = "play_card",
				CardId = "CS2_029",
				Text = "Play Deliberately Wrong Worker Name",
				TargetEntityId = 8
			});

			var expectedIsChinese = expectedName.Any(character =>
				(character >= '\u3400' && character <= '\u4DBF') ||
				(character >= '\u4E00' && character <= '\u9FFF'));
			if (expectedIsChinese)
				StringAssert.Contains(visible, "打出「" + expectedName + "」");
			else
				StringAssert.Contains(visible, "打出列出的卡牌");
			StringAssert.Contains(visible, "选择指定目标");
			Assert.IsFalse(visible.Contains("Deliberately Wrong Worker Name"));
		}

		[TestMethod]
		public void CompactEntityLabel_PrefersHdtLocalizedNameOverEnglishSnapshotName()
		{
			const string cardId = "CS2_029";
			var expected = AdvisorUserMessages.ResolveLocalizedCardName(cardId);
			Assert.IsFalse(string.IsNullOrWhiteSpace(expected));
			var entity = new AdvisorEntityState
			{
				EntityId = 20,
				CardId = cardId,
				Name = "Deliberately Wrong English Snapshot Name",
				CardType = "SPELL"
			};
			var state = new AdvisorGameState
			{
				Player = new AdvisorPlayerState
				{
					Hand = new List<AdvisorEntityState> { entity }
				}
			};

			var label = AdvisorPanel.BuildCompactEntityLabel(state, entity, cardId, "卡牌");

			Assert.AreEqual(expected, label);
			Assert.IsFalse(label.Contains("English Snapshot"));
		}

		[TestMethod]
		public void Action_MissingLocalizedNameUsesOnlyControlledFallbackOrChinesePlaceholder()
		{
			var controlled = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "hero_power",
					CardId = "UNKNOWN_TEST_CARD",
					Text = "Use 安全技能名"
				},
				cardId => "");
			Assert.AreEqual("使用英雄技能「安全技能名」", controlled);

			var hidden = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "play_card",
					CardId = "UNKNOWN_TEST_CARD",
					Text = "Play HTTP 500 at C:\\Users\\someone\\worker.py"
				},
				cardId => "");
			Assert.AreEqual("打出列出的卡牌", hidden);
			Assert.IsFalse(hidden.Contains("HTTP"));
			Assert.IsFalse(hidden.Contains("C:\\"));
		}

		[TestMethod]
		public void Action_UnsafeOrUnavailableResolverNeverLeaksWorkerEnglishName()
		{
			var action = new AdvisorAction
			{
				Type = "play_card",
				CardId = "CS2_029",
				Text = "Play Worker English Card Name"
			};
			var visible = new[]
			{
				AdvisorUserMessages.Action(action, cardId => ""),
				AdvisorUserMessages.Action(action, cardId => cardId),
				AdvisorUserMessages.Action(action, cardId => "Localized English Card Name"),
				AdvisorUserMessages.Action(action, cardId =>
					{ throw new InvalidOperationException("locale database is loading"); })
			};

			foreach (var text in visible)
			{
				Assert.AreEqual("打出列出的卡牌", text);
				Assert.IsFalse(text.Contains("Worker English"));
				Assert.IsFalse(text.Contains("Localized English"));
				Assert.IsFalse(text.Contains("CS2_029"));
			}

			var attack = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "attack",
					CardId = "EX1_001",
					Text = "Attack English Target with English Attacker"
				},
				cardId => "English Localized Attacker");
			Assert.AreEqual("用列出的角色攻击指定目标", attack);
		}

		[TestMethod]
		public void Action_WithMatchingSnapshotNamesSourcesTargetsLocationsAndFixedHeroEffects()
		{
			var state = new AdvisorGameState
			{
				StateId = "named-actions",
				Player = new AdvisorPlayerState
				{
					Hero = new AdvisorEntityState
					{
						EntityId = 10,
						CardId = "TEST_FRIENDLY_HERO",
						Name = "雷克萨",
						CardType = "HERO"
					},
					HeroPower = new AdvisorEntityState
					{
						EntityId = 11,
						CardId = "TEST_STEADY_SHOT",
						Name = "稳固射击",
						CardType = "HERO_POWER",
						EnglishText = "Deal 2 damage to the enemy hero."
					},
					Hand = new List<AdvisorEntityState>
					{
						new AdvisorEntityState
						{
							EntityId = 20,
							CardId = "TEST_ROCK",
							Name = "石头",
							CardType = "SPELL"
						}
					},
					Board = new List<AdvisorEntityState>
					{
						new AdvisorEntityState
						{
							EntityId = 40,
							CardId = "TEST_WOLF",
							Name = "森林狼",
							CardType = "MINION",
							ZonePosition = 1
						},
						new AdvisorEntityState
						{
							EntityId = 41,
							CardId = "TEST_LOCATION",
							Name = "下水道管网",
							CardType = "LOCATION",
							ZonePosition = 2
						}
					}
				},
				Opponent = new AdvisorPlayerState
				{
					Hero = new AdvisorEntityState
					{
						EntityId = 30,
						CardId = "TEST_ENEMY_HERO",
						Name = "古尔丹",
						CardType = "HERO"
					},
					Board = new List<AdvisorEntityState>
					{
						new AdvisorEntityState
						{
							EntityId = 50,
							CardId = "TEST_VOIDWALKER",
							Name = "虚空行者",
							CardType = "MINION",
							ZonePosition = 1
						}
					}
				}
			};

			var attack = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "attack",
					SourceEntityId = 40,
					TargetEntityId = 50,
					CardId = "TEST_WOLF"
				},
				state);
			var spell = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "play_card",
					SourceEntityId = 20,
					TargetEntityId = 30,
					CardId = "TEST_ROCK"
				},
				state);
			var location = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "location_activate",
					SourceEntityId = 41,
					CardId = "TEST_LOCATION"
				},
				state);
			var heroPower = AdvisorUserMessages.Action(
				new AdvisorAction
				{
					Type = "hero_power",
					SourceEntityId = 11,
					CardId = "TEST_STEADY_SHOT"
				},
				state);

			Assert.AreEqual(
				"用我方从左数第 1 个随从「森林狼」攻击敌方从左数第 1 个随从「虚空行者」",
				attack);
			Assert.AreEqual("打出「石头」 → 敌方英雄「古尔丹」", spell);
			Assert.AreEqual("点击地标「下水道管网」", location);
			Assert.AreEqual(
				"使用英雄技能「稳固射击」（自动作用于敌方英雄「古尔丹」）",
				heroPower);
			var visible = string.Join("\n", new[] { attack, spell, location, heroPower });
			Assert.IsFalse(visible.Contains("指定目标"));
			Assert.IsFalse(visible.Contains("entity"));
			Assert.IsFalse(visible.Contains("TEST_"));
		}

		[TestMethod]
		public void ResponseStatus_PrioritizesStatusOverGenericWorkerMessage()
		{
			const string generic = "Recommendations include approximated mechanics.";
			Assert.AreEqual(
				AdvisorUserMessages.Unsupported,
				AdvisorUserMessages.ResponseStatus("unsupported", generic, true, false));
			Assert.AreEqual(
				AdvisorUserMessages.StaleResult,
				AdvisorUserMessages.ResponseStatus("cancelled", generic, true, false));
			Assert.AreEqual(
				AdvisorUserMessages.NoRecommendations,
				AdvisorUserMessages.ResponseStatus("ok", generic, true, false));
		}

		[TestMethod]
		public void RedactSecrets_PreservesDiagnosticsButNeverCredentialValues()
		{
			var original =
				"HTTP 500 InvalidOperationException at C:\\Users\\someone\\worker.py\r\n" +
				"session_" + "token=" + "sample-token-value\r\n" +
				"Cook" + "ie: sample-name=sample-cookie-value\r\n" +
				"Author" + "ization: " + "Bear" + "er sample-bearer-value\r\n" +
				"https://sample-user:" + "sample-password@example.invalid/";
			var redacted = AdvisorUserMessages.RedactSecrets(original);

			StringAssert.Contains(redacted, "HTTP 500");
			StringAssert.Contains(redacted, "InvalidOperationException");
			StringAssert.Contains(redacted, "C:\\Users\\someone\\worker.py");
			StringAssert.Contains(redacted, "[已隐藏]");
			Assert.IsFalse(redacted.Contains("sample-token-value"));
			Assert.IsFalse(redacted.Contains("sample-cookie-value"));
			Assert.IsFalse(redacted.Contains("sample-bearer-value"));
			Assert.IsFalse(redacted.Contains("sample-user:sample-password"));
		}

		[TestMethod]
		public void RecommendationContent_LocalizesTemplatesActionsScopesAndMetrics()
		{
			var recommendation = new AdvisorRecommendation
			{
				Summary = "Start by playing the listed card, then follow the modeled sequence.",
				IsResponseVerified = true,
				ResponseScope = "visible_generic_turnpair_v1",
				ExpectedWinRate = 0.61,
				WorstCaseScore = 0.55,
				MinimaxValue = 123.5,
				Visits = 42,
				Actions = new List<AdvisorAction>
				{
					new AdvisorAction
					{
						Index = 1,
						Type = "play_card",
						Text = "Play Fireball",
						TargetEntityId = 8
					},
					new AdvisorAction { Index = 2, Type = "end_turn", Text = "End turn" }
				}
			};

			var summary = AdvisorUserMessages.RecommendationSummary(recommendation);
			StringAssert.Contains(summary, "先打出列出的卡牌");
			Assert.IsFalse(summary.Contains("Start by"));
			Assert.IsFalse(summary.Contains("modeled sequence"));

			var actions = AdvisorPanel.BuildActionLine(recommendation.Actions);
			StringAssert.Contains(actions, "打出列出的卡牌");
			StringAssert.Contains(actions, "选择指定目标");
			StringAssert.Contains(actions, "结束回合");
			Assert.IsFalse(actions.Contains("Play "));
			Assert.IsFalse(actions.Contains("Fireball"));
			Assert.IsFalse(actions.Contains("End turn"));

			var metrics = AdvisorPanel.BuildMetrics(recommendation);
			StringAssert.Contains(metrics, "最差回应战术值 123.5");
			StringAssert.Contains(metrics, "当前可见回合对规则");
			StringAssert.Contains(metrics, "42 次搜索");
			Assert.IsFalse(metrics.Contains("minimax"));
			Assert.IsFalse(metrics.Contains("visits"));
			Assert.IsFalse(metrics.Contains("visible_generic"));
		}

		[TestMethod]
		public void PortfolioLabels_AreShortChineseAndDoNotOverclaimIncompleteCoverage()
		{
			Assert.AreEqual(
				"完整搜索范围内共同最优",
				AdvisorUserMessages.AlternativeKind("co_optimal"));
			Assert.AreEqual("近优备选", AdvisorUserMessages.AlternativeKind("near_optimal"));
			Assert.AreEqual("当前已验证最佳", AdvisorUserMessages.AlternativeKind("best_found"));
			Assert.AreEqual("", AdvisorUserMessages.AlternativeKind("unknown_kind"));

			var coverage = new AdvisorCoverage
			{
				HasRootActionCoverageContract = true,
				RootActionCoverageContractValid = true,
				RootActionCoverageComplete = false,
				LegalFirstActionCount = 5,
				ResponseVerifiedFirstActionCount = 3
			};
			var summary = AdvisorUserMessages.PortfolioCoverageSummary(coverage);
			StringAssert.Contains(summary, "尚未完整验证");
			StringAssert.Contains(summary, "3 / 5");
			Assert.IsFalse(summary.Contains("optimal"));
			Assert.IsFalse(summary.Contains("coverage"));

			coverage.RootActionCoverageContractValid = false;
			var invalidSummary = AdvisorUserMessages.PortfolioCoverageSummary(coverage);
			StringAssert.Contains(invalidSummary, "相互矛盾");
			StringAssert.Contains(invalidSummary, "已关闭共同最优和近优结论");
		}

		[TestMethod]
		public void RecommendationCards_UsePlainPriorityLabelsAndOnlyShowCriticalBadges()
		{
			Assert.AreEqual("首选", AdvisorPanel.BuildPriorityLabel(1));
			Assert.AreEqual("备选一", AdvisorPanel.BuildPriorityLabel(2));
			Assert.AreEqual("备选二", AdvisorPanel.BuildPriorityLabel(3));
			Assert.AreEqual(
				"",
				AdvisorPanel.BuildRecommendationBadge(new AdvisorRecommendation
				{
					ExpectedWinRate = 0.987,
					IsResponseVerified = true
				}));
			Assert.AreEqual(
				"斩杀",
				AdvisorPanel.BuildRecommendationBadge(new AdvisorRecommendation
				{
					IsProvenLethal = true
				}));
			Assert.AreEqual(
				"有反杀",
				AdvisorPanel.BuildRecommendationBadge(new AdvisorRecommendation
				{
					IsResponseVerified = true,
					ResponseIsProvenLethal = true
				}));
		}

		[TestMethod]
		public void RecommendationCards_UseCompactArtFlowWithExactBoardLabels()
		{
			Assert.AreEqual(380, AdvisorPanel.CompactPanelWidth);
			var friendlyMinion = new AdvisorEntityState
			{
				EntityId = 11,
				CardId = "TEST_FRIENDLY",
				Name = "测试随从",
				CardType = "MINION",
				ZonePosition = 2
			};
			var state = new AdvisorGameState
			{
				StateId = "visual-state",
				Player = new AdvisorPlayerState
				{
					Hero = new AdvisorEntityState { EntityId = 1, CardType = "HERO" },
					Board = new List<AdvisorEntityState> { friendlyMinion }
				},
				Opponent = new AdvisorPlayerState
				{
					Hero = new AdvisorEntityState
					{
						EntityId = 2,
						CardId = "TEST_ENEMY_HERO",
						CardType = "HERO"
					}
				}
			};

			Assert.AreEqual(
				"我2·测试随从",
				AdvisorPanel.BuildCompactEntityLabel(state, friendlyMinion, "", "随从"));
			Assert.AreEqual(
				"敌方英雄",
				AdvisorPanel.BuildCompactEntityLabel(
					state, state.Opponent.Hero, "", "英雄"));

			var panel = new AdvisorPanel(null);
			panel.Update(
				new AdvisorSolveResponse
				{
					StateId = state.StateId,
					Status = "ok",
					IsFinal = true,
					Recommendations = new List<AdvisorRecommendation>
					{
						new AdvisorRecommendation
						{
							Rank = 1,
							Actions = new List<AdvisorAction>
							{
								new AdvisorAction
								{
									Index = 1,
									Type = "attack",
									SourceEntityId = 11,
									TargetEntityId = 2
								}
							}
						}
					}
				},
				false,
				state);

			var recommendation = (Border)panel.RecommendationsPanel.Children[0];
			var content = (StackPanel)recommendation.Child;
			Assert.AreEqual(1, content.Children.OfType<WrapPanel>().Count());
			Assert.IsFalse(content.Children.OfType<TextBlock>()
				.Any(text => text.Text.StartsWith("行动：", StringComparison.Ordinal)));

			panel.Measure(new Size(AdvisorPanel.CompactPanelWidth, 700));
			var renderHeight = Math.Max(120, (int)Math.Ceiling(panel.DesiredSize.Height));
			panel.Arrange(new Rect(0, 0, AdvisorPanel.CompactPanelWidth, renderHeight));
			panel.UpdateLayout();
			var bitmap = new RenderTargetBitmap(
				(int)AdvisorPanel.CompactPanelWidth,
				renderHeight,
				96,
				96,
				PixelFormats.Pbgra32);
			bitmap.Render(panel);
			var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
			bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
			Assert.IsTrue(pixels.Where((value, index) => index % 4 == 3).Any(alpha => alpha > 0));
		}

		[TestMethod]
		public void CardArtLookup_PrefersLocalTilesAndRejectsPathTraversal()
		{
			var root = Path.Combine(Path.GetTempPath(), "MetaCompanion-card-art-" + Guid.NewGuid());
			try
			{
				var tileDirectory = Path.Combine(
					root, "HearthstoneDeckTracker", "Images", "CardTiles");
				Directory.CreateDirectory(tileDirectory);
				var tile = Path.Combine(tileDirectory, "SAFE_CARD.jpg");
				File.WriteAllText(tile, "fixture");

				Assert.AreEqual(tile, AdvisorPanel.FindCardArtPath("SAFE_CARD", root));
				Assert.AreEqual("", AdvisorPanel.FindCardArtPath("..\\secret", root));
				Assert.AreEqual("", AdvisorPanel.FindCardArtPath("folder/card", root));
			}
			finally
			{
				if (Directory.Exists(root))
					Directory.Delete(root, true);
			}
		}

		[TestMethod]
		public void BuildMetrics_NeverShowsPortfolioRegretForUnverifiedLine()
		{
			var recommendation = new AdvisorRecommendation
			{
				AlternativeKind = "fallback",
				VerifiedPortfolioRegret = 25,
				IsResponseVerified = false,
				IsProvenLethal = false
			};

			var metrics = AdvisorPanel.BuildMetrics(recommendation);
			StringAssert.Contains(metrics, "未完整验证的兜底路线");
			Assert.IsFalse(metrics.Contains("与已验证最佳"));
			Assert.IsFalse(metrics.Contains("战术值差距"));
		}

		[TestMethod]
		public void SolveDiagnosticSummary_IsCompactChineseAndReportsCoverage()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r\",\"state_id\":\"s\"," +
				"\"status\":\"partial\",\"elapsed_ms\":321," +
				"\"coverage\":{\"exact\":false,\"exact_scope\":\"visible-response-v1\"," +
				"\"scoped_lethal\":false,\"unsupported_count\":7," +
				"\"planner_model\":\"rust-visible-response-v1\"," +
				"\"rules_model\":\"visible-combat-v2\",\"overall\":0.25," +
				"\"card_coverage\":0.5,\"rule_coverage\":0.125}," +
				"\"warnings\":[\"Card text for Unknown Card is unsupported.\"]," +
				"\"recommendations\":[{\"rank\":1,\"actions\":[]}]}");

			Assert.IsFalse(response.Coverage.Exact);
			Assert.AreEqual("visible-response-v1", response.Coverage.ExactScope);
			Assert.AreEqual(7, response.Coverage.UnsupportedCount);
			Assert.AreEqual("rust-visible-response-v1", response.Coverage.PlannerModel);
			var summary = AdvisorUserMessages.SolveDiagnosticSummary(response, "initial", 999);
			StringAssert.Contains(summary, "阶段=首批");
			StringAssert.Contains(summary, "状态=近似结果");
			StringAssert.Contains(summary, "建议=1");
			StringAssert.Contains(summary, "总覆盖=25%");
			StringAssert.Contains(summary, "卡牌覆盖=50%");
			StringAssert.Contains(summary, "规则覆盖=12.5%");
			StringAssert.Contains(summary, "未覆盖项=7");
			StringAssert.Contains(summary, "规则未完整建模 1 条");
			Assert.IsFalse(summary.Contains("partial"));
			Assert.IsFalse(summary.Contains("unsupported"));
			Assert.IsFalse(summary.Contains("Card text"));
			Assert.IsFalse(summary.Contains("rust"));
		}

		[TestMethod]
		public void SolveFailureDiagnostic_UsesStableErrorCodeWithoutStackTrace()
		{
			var error = new AdvisorWorkerException(
				"Advisor worker returned HTTP 422 at C:\\Users\\someone\\worker.py")
			{
				StatusCode = (HttpStatusCode)422,
				ErrorCode = "unsupported_scope"
			};

			Assert.IsTrue(AdvisorUserMessages.IsExpectedCoverageFailure(error));
			Assert.AreEqual(AdvisorUserMessages.Unsupported, AdvisorUserMessages.Failure(error));
			var summary = AdvisorUserMessages.SolveFailureDiagnostic(error, "final", true);
			Assert.AreEqual(
				"顾问求解摘要：阶段=深化；状态=规则覆盖不足；已保留首批结果。",
				summary);
			Assert.IsFalse(summary.Contains("HTTP"));
			Assert.IsFalse(summary.Contains("Exception"));
			Assert.IsFalse(summary.Contains("C:\\"));
			Assert.IsFalse(summary.Contains("unsupported_scope"));
			Assert.AreEqual(
				"unsupported_scope",
				AdvisorWireProtocol.TryReadErrorCode(
					"{\"error\":{\"code\":\"unsupported_scope\",\"message\":\"不支持\"}}"));
		}

		[TestMethod]
		public void RuntimeFailureDiagnostic_IsChineseAndNeverIncludesRawExceptionDetails()
		{
			var error = new InvalidOperationException(
				"session_" + "token=do-not-log C:\\Users\\someone\\worker.py",
				new Exception("raw inner stack detail"));
			var summary = AdvisorUserMessages.RuntimeFailureDiagnostic(
				error, "worker_start");

			Assert.AreEqual(
				"顾问运行摘要：环节=启动本机求解器；状态=本机求解器异常。",
				summary);
			Assert.IsFalse(summary.Contains("token"));
			Assert.IsFalse(summary.Contains("worker.py"));
			Assert.IsFalse(summary.Contains("Exception"));
			Assert.IsFalse(summary.Contains("stack"));
		}

		[TestMethod]
		public void RuntimeFailureDiagnostic_IdentifiesBehaviorAndResultPersistenceStages()
		{
			var error = new IOException("private path and payload must not be shown");
			var cases = new[]
			{
				new[] { "behavior_outbox", "保存行为待发送记录", "本地待发送记录异常" },
				new[] { "behavior_flush", "同步行为记录", "训练记录同步异常" },
				new[] { "result_outbox", "保存终局待发送记录", "本地待发送记录异常" },
				new[] { "result_flush", "同步终局结果", "训练记录同步异常" },
				new[] { "observe", "写入训练观察", "训练记录同步异常" }
			};

			foreach (var item in cases)
			{
				var summary = AdvisorUserMessages.RuntimeFailureDiagnostic(error, item[0]);
				StringAssert.Contains(summary, "环节=" + item[1]);
				StringAssert.Contains(summary, "状态=" + item[2]);
				Assert.IsFalse(summary.Contains("求解器异常"));
				Assert.IsFalse(summary.Contains("private"));
				Assert.IsFalse(summary.Contains("IOException"));
			}
		}

		[TestMethod]
		public void RecommendationController_CoverageAbstentionKeepsRustEligibleAndUsesUnsupportedUi()
		{
			var original = new AdvisorWorkerException(
				"Advisor worker returned HTTP 422 at C:\\Users\\someone\\worker.py")
			{
				StatusCode = (HttpStatusCode)422,
				ErrorCode = "unsupported_scope"
			};
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				new ThrowingAdvisorClient(original),
				TimeSpan.Zero,
				callbackContext: new ImmediateSynchronizationContext()))
			{
				AdvisorRecommendationUpdateEventArgs update = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations || args.Error == null)
						return;
					update = args;
					signal.Set();
				};

				controller.SubmitSnapshot(NewState(), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected coverage abstention event.");
				Assert.AreSame(original, update.Error);
				Assert.IsNotNull(update.Response);
				Assert.AreEqual(AdvisorProtocol.StatusUnsupported, update.Response.Status);
				Assert.AreEqual("safe-state", update.Response.StateId);
				Assert.AreEqual(AdvisorUserMessages.Unsupported, update.Message);
				Assert.AreEqual(AdvisorUserMessages.Unsupported, update.Response.Message);
				Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
					update,
					true,
					AdvisorWorkerBackendMode.Auto,
					AdvisorWorkerBackendKind.Rust));
				Assert.IsFalse(update.Message.Contains("HTTP"));
				Assert.IsFalse(update.Message.Contains("C:\\"));
			}
		}

		[TestMethod]
		public void RecommendationController_FailureKeepsExceptionForLogsButPublishesSafeChineseMessage()
		{
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				new ThrowingAdvisorClient(),
				TimeSpan.Zero,
				callbackContext: new ImmediateSynchronizationContext()))
			{
				AdvisorRecommendationUpdateEventArgs failure = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.WorkerUnavailable)
						return;
					failure = args;
					signal.Set();
				};

				controller.SubmitSnapshot(NewState(), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected worker failure event.");
				Assert.IsNotNull(failure);
				Assert.IsNotNull(failure.Error, "The original exception must remain available to diagnostics.");
				Assert.AreEqual(AdvisorUserMessages.SolveFailed, failure.Message);
				Assert.IsFalse(failure.Message.Contains("HTTP"));
				Assert.IsFalse(failure.Message.Contains("Exception"));
				Assert.IsFalse(failure.Message.Contains("C:\\"));
				Assert.IsFalse(failure.Message.Contains("token"));
			}
		}

		[TestMethod]
		public void RecommendationController_TimeoutKeepsExceptionButPublishesTimeoutSummary()
		{
			var original = new TimeoutException(
				"Advisor worker request timed out at C:\\Users\\someone\\worker.py; request_id=private");
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				new ThrowingAdvisorClient(original),
				TimeSpan.Zero,
				callbackContext: new ImmediateSynchronizationContext()))
			{
				AdvisorRecommendationUpdateEventArgs failure = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.WorkerUnavailable)
						return;
					failure = args;
					signal.Set();
				};

				controller.SubmitSnapshot(NewState(), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected timeout event.");
				Assert.AreSame(original, failure.Error);
				Assert.AreEqual(AdvisorUserMessages.SolveTimedOut, failure.Message);
				Assert.IsFalse(failure.Message.Contains("Advisor"));
				Assert.IsFalse(failure.Message.Contains("C:\\"));
				Assert.IsFalse(failure.Message.Contains("request_id"));
			}
		}

		private static AdvisorGameState NewState()
		{
			return new AdvisorGameState
			{
				StateId = "safe-state",
				StateHash = "hash",
				CapturedAtUtc = DateTime.UtcNow,
				IsRunning = true,
				IsLocalPlayerTurn = true,
				IsMulliganDone = true,
				Phase = new AdvisorGamePhaseState { CanLocalPlayerAct = true },
				Player = new AdvisorPlayerState { PlayerId = 1 },
				Opponent = new AdvisorPlayerState { PlayerId = 2 }
			};
		}

		private sealed class ThrowingAdvisorClient : IAdvisorWorkerClient
		{
			private readonly Exception _error;

			public ThrowingAdvisorClient(Exception error = null)
			{
				_error = error ?? new InvalidOperationException(
					"HTTP 500 at C:\\Users\\someone\\worker.py; session_" +
					"token=" + "sample-value");
			}

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				var completion = new TaskCompletionSource<AdvisorSolveResponse>();
				completion.SetException(_error);
				return completion.Task;
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
			}
		}

		private sealed class ImmediateSynchronizationContext : SynchronizationContext
		{
			public override void Post(SendOrPostCallback callback, object state)
			{
				callback(state);
			}
		}
	}
}
