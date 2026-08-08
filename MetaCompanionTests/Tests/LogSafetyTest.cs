using System;
using System.IO;
using System.Text.RegularExpressions;
using Hearthstone_Deck_Tracker.Utility.Logging;
using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PluginLog = MetaCompanion.Log;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class LogSafetyTest
	{
		[TestMethod]
		public void Warn_CommonEnglishPrefixesBecomeMeaningfulChineseSummaries()
		{
			var cases = new[]
			{
				new[] { "Download failed, check hdt log for error.", "插件更新下载失败" },
				new[] { "Unable to read HDT replay info: System.IO.IOException", "无法读取 HDT 对局回放信息" },
				new[] { "Meta dashboard panel update failed: boom", "数据面板更新失败" },
				new[] { "No deck code snapshot found; source=C:\\Users\\person\\deck.tsv", "未找到牌组代码快照" },
				new[] { "Post-game local meta refresh failed: timeout", "赛后本地环境刷新失败" },
				new[] { "Failed to load HDT DeckStats local meta from C:\\Users\\person\\stats.xml", "无法加载 HDT 本地对局统计" },
				new[] { "Refresh scheduled task status check failed: access denied", "自动刷新计划任务状态检查失败" },
				new[] { "Open match history failed: no association", "无法打开对局历史" },
				new[] { "Settings training log snapshot failed: parse error", "设置页读取训练日志状态失败" },
				new[] { "Meta deck library load failed: bad payload", "牌组库加载失败" }
			};

			foreach (var item in cases)
			{
				PluginLog.Warn(item[0], "Load", @"C:\Workspace\AuditSource.cs");
				var message = MessagePart(PluginLog.PrevLine);
				StringAssert.Contains(message, item[1]);
				AssertSafeOperationalMessage(message);
			}
		}

		[TestMethod]
		public void Warn_UnknownEnglishMessageFailsClosedInChinese()
		{
			PluginLog.Warn(
				"Completely unknown failure, token=plain-secret at https://example.invalid/v1",
				"Inspect",
				@"D:\src\UnknownComponent.cs");

			var message = MessagePart(PluginLog.PrevLine);
			Assert.AreEqual(
				"运行警告：无法安全展示原始技术信息；请根据调用来源排查。",
				message);
			AssertSafeOperationalMessage(message);
		}

		[TestMethod]
		public void Info_KnownEnglishPrefixBecomesMeaningfulChineseWithoutLocations()
		{
			PluginLog.Info(
				"Meta deck library loaded: 143 decks from C:\\Users\\person\\decks.tsv " +
				"at https://example.invalid/library",
				"StartMetaDeckLoad",
				@"D:\src\MetaCompanionPlugin.cs");

			var message = MessagePart(PluginLog.PrevLine);
			Assert.AreEqual("牌组库加载完成。", message);
			AssertSafeOperationalMessage(message);
		}

		[TestMethod]
		public void Info_UnknownEnglishMessageFailsClosedWithoutPathUrlOrCredential()
		{
			PluginLog.Info(
				"Background probe source=cache/startup.json endpoint=https://example.invalid/v1 " +
				"token=plain-secret",
				"Probe",
				@"C:\src\StartupProbe.cs");

			var message = MessagePart(PluginLog.PrevLine);
			Assert.AreEqual("运行信息：原始技术内容已隐藏。", message);
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("plain-secret"));
		}

		[TestMethod]
		public void Info_ChineseMessageRemovesEveryLocationAndTechnicalTail()
		{
			PluginLog.Info(
				"牌组库检查完成：地址=https://example.invalid/v1；" +
				@"绝对路径=C:\Users\person\decks.tsv；" +
				"Unix 路径=/opt/metacompanion/decks.csv；" +
				"相对路径=cache/decks.json；文件=Loader.cs:42；" +
				"System.IO.IOException: raw-message-do-not-echo",
				"Inspect",
				@"C:\src\DeckLibrary.cs");

			var message = MessagePart(PluginLog.PrevLine);
			StringAssert.Contains(message, "牌组库检查完成");
			StringAssert.Contains(message, "[地址已隐藏]");
			StringAssert.Contains(message, "[路径已隐藏]");
			StringAssert.Contains(message, "[文件已隐藏]");
			StringAssert.Contains(message, "技术细节已隐藏");
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("raw-message-do-not-echo"));
		}

		[TestMethod]
		public void StartupAndDeckLibraryInfoSourcesDoNotConcatenateLocations()
		{
			var repositoryRoot = FindRepositoryRoot();
			var pluginSource = File.ReadAllText(Path.Combine(
				repositoryRoot, "MetaCompanion", "MetaCompanionPlugin.cs"));
			var retrieverSource = File.ReadAllText(Path.Combine(
				repositoryRoot, "MetaCompanion", "MetaRetriever.cs"));

			StringAssert.Contains(pluginSource, "Log.Info(\"插件已启动（版本 0.1.0）。\")");
			StringAssert.Contains(pluginSource, "Log.Info(\"插件数据目录已就绪。\")");
			StringAssert.Contains(pluginSource, "Log.Info(\"牌组库正在后台加载。\")");
			Assert.IsFalse(
				Regex.IsMatch(
					pluginSource,
					@"Log\.Info\s*\([^;]*(?:Assembly\.Location|PluginDirectory|DataDirectory)",
					RegexOptions.Singleline),
				"常规 Info 不得拼接插件组件或数据目录位置。");

			StringAssert.Contains(retrieverSource, "Log.Info(\"未找到牌组代码数据源。\")");
			StringAssert.Contains(retrieverSource, "Log.Info(\"正在读取 \" + existingFiles.Count");
			Assert.IsFalse(
				retrieverSource.Contains("Log.Info(\"No deck code file found in \" + dataDirectory)"));
			Assert.IsFalse(
				retrieverSource.Contains("Log.Info(\"Reading deck codes from preferred sources: \" + " +
					"string.Join(\", \", existingFiles))"));
		}

		[TestMethod]
		public void Error_ExceptionUsesChineseCategoryAndCallerWithoutRawException()
		{
			PluginLog.Error(
				new InvalidOperationException(
					"raw-message-do-not-echo Authorization: Bearer private-value C:\\Users\\person\\secret.txt"),
				"LoadConfig",
				@"D:\Workspace\PluginConfig.cs");

			StringAssert.Contains(PluginLog.PrevLine, "PluginConfig.LoadConfig >>");
			var message = MessagePart(PluginLog.PrevLine);
			StringAssert.Contains(message, "检测到运行状态异常");
			StringAssert.Contains(message, "调用来源见日志定位");
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("raw-message-do-not-echo"));
			Assert.IsFalse(message.Contains("private-value"));
			Assert.IsFalse(message.Contains("InvalidOperationException"));
		}

		[TestMethod]
		public void Error_ExceptionCategoriesRemainUsefulWithoutMessages()
		{
			var cases = new[]
			{
				new { Error = (Exception)new UnauthorizedAccessException("do-not-echo"), Category = "权限访问异常" },
				new { Error = (Exception)new FileNotFoundException("do-not-echo", @"C:\\private\\file.json"), Category = "文件缺失异常" },
				new { Error = (Exception)new TimeoutException("do-not-echo"), Category = "等待超时异常" },
				new { Error = (Exception)new ArgumentException("do-not-echo"), Category = "输入参数异常" },
				new { Error = (Exception)new NotSupportedException("do-not-echo"), Category = "功能兼容异常" },
				new { Error = (Exception)null, Category = "未分类运行异常" }
			};

			foreach (var item in cases)
			{
				PluginLog.Error(item.Error, "Run", @"C:\src\SafeSource.cs");
				var message = MessagePart(PluginLog.PrevLine);
				StringAssert.Contains(message, item.Category);
				Assert.IsFalse(message.Contains("do-not-echo"));
				AssertSafeOperationalMessage(message);
			}
		}

		[TestMethod]
		public void Warn_ChineseMessageRemovesCredentialsLocationsAndStack()
		{
			PluginLog.Warn(
				"读取缓存失败：token=plain-secret；地址=https://user:pass@example.invalid/v1；" +
				@"文件=C:\Users\person\AppData\cache.json" +
				"\r\n   at Hidden.Namespace.Loader() in C:\\Workspace\\Loader.cs:line 42",
				"ReadCache",
				@"C:\src\CacheReader.cs");

			var message = MessagePart(PluginLog.PrevLine);
			StringAssert.Contains(message, "读取缓存失败");
			StringAssert.Contains(message, "凭据=[已隐藏]");
			StringAssert.Contains(message, "[地址已隐藏]");
			StringAssert.Contains(message, "[路径已隐藏]");
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("plain-secret"));
			Assert.IsFalse(message.Contains("user:pass"));
			Assert.IsFalse(message.Contains("Hidden.Namespace"));
		}

		[TestMethod]
		public void Warn_MixedChineseCredentialsAndDotEnvNamesAreHidden()
		{
			PluginLog.Warn(
				"读取本地设置失败：账户token=x7；密码：短值；配置=.env；备用=.env.local。",
				"ReadSettings",
				@"C:\src\SettingsReader.cs");

			var message = MessagePart(PluginLog.PrevLine);
			StringAssert.Contains(message, "读取本地设置失败");
			StringAssert.Contains(message, "凭据=[已隐藏]");
			StringAssert.Contains(message, "[文件已隐藏]");
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("x7"));
			Assert.IsFalse(message.Contains("短值"));
			Assert.IsFalse(message.Contains(".env"));
		}

		[TestMethod]
		public void Warn_InlineExceptionTailIsNeverEchoed()
		{
			PluginLog.Warn(
				"读取设置失败：System.InvalidOperationException: raw-message-do-not-echo at Hidden.Loader()",
				"ReadSettings",
				@"C:\src\SettingsReader.cs");

			var message = MessagePart(PluginLog.PrevLine);
			StringAssert.Contains(message, "读取设置失败");
			StringAssert.Contains(message, "技术细节已隐藏");
			AssertSafeOperationalMessage(message);
			Assert.IsFalse(message.Contains("raw-message-do-not-echo"));
		}

		[TestMethod]
		public void Debug_KeepsDevelopmentContextButRedactsEveryCredentialForm()
		{
			var longSecret = new string('x', 64);
			var message = PluginLog.PrepareMessage(
				"worker endpoint=https://localhost:1234/v1/health " +
				"token=plain-secret path=C:\\Workspace\\worker.log long=" + longSecret +
				" Authorization: Bearer header-secret 中文密码：短值 账户token=x7",
				LogType.Debug);

			StringAssert.Contains(message, "worker endpoint=https://localhost:1234/v1/health");
			StringAssert.Contains(message, @"path=C:\Workspace\worker.log");
			StringAssert.Contains(message, "凭据=[已隐藏]");
			Assert.IsFalse(message.Contains("plain-secret"));
			Assert.IsFalse(message.Contains("header-secret"));
			Assert.IsFalse(message.Contains("短值"));
			Assert.IsFalse(message.Contains("x7"));
			Assert.IsFalse(message.Contains(longSecret));
			Assert.IsFalse(message.Contains("\r"));
			Assert.IsFalse(message.Contains("\n"));
		}

		[TestMethod]
		public void WriteLine_DirectWarningAndErrorCallsStillUseSafetyPolicy()
		{
			PluginLog.WriteLine(
				"unknown warning https://example.invalid token=plain-secret",
				LogType.Warning,
				"Check",
				@"C:\src\DirectWriter.cs");
			Assert.AreEqual(
				"运行警告：无法安全展示原始技术信息；请根据调用来源排查。",
				MessagePart(PluginLog.PrevLine));

			PluginLog.WriteLine(
				"unknown error C:\\Users\\person\\private.txt password=plain-secret",
				LogType.Error,
				"Check",
				@"C:\src\DirectWriter.cs");
			Assert.AreEqual(
				"运行错误：无法安全展示原始技术信息；请根据调用来源排查。",
				MessagePart(PluginLog.PrevLine));
		}

		[TestMethod]
		public void Warn_CallerMetadataCannotInjectPathsCredentialsOrNewLines()
		{
			PluginLog.Warn(
				"数据检查失败。",
				"token=member-secret\r\nforged",
				@"C:\Users\person\password=source-secret.cs");

			StringAssert.Contains(PluginLog.PrevLine, "未知来源.未知调用 >>");
			Assert.IsFalse(PluginLog.PrevLine.Contains("member-secret"));
			Assert.IsFalse(PluginLog.PrevLine.Contains("source-secret"));
			Assert.IsFalse(PluginLog.PrevLine.Contains("\r"));
			Assert.IsFalse(PluginLog.PrevLine.Contains("\n"));
			AssertSafeOperationalMessage(MessagePart(PluginLog.PrevLine));
		}

		private static string MessagePart(string line)
		{
			var marker = " >> ";
			var index = (line ?? "").IndexOf(marker, StringComparison.Ordinal);
			return index < 0 ? (line ?? "") : line.Substring(index + marker.Length);
		}

		private static void AssertSafeOperationalMessage(string message)
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(message));
			Assert.IsTrue(Regex.IsMatch(message, @"[\u3400-\u9fff]"));
			Assert.IsFalse(message.Contains("\r"));
			Assert.IsFalse(message.Contains("\n"));
			Assert.IsFalse(Regex.IsMatch(message, @"(?i)\b(?:https?|ftp|file)://|\bwww\."));
			Assert.IsFalse(Regex.IsMatch(message, @"(?i)\b[A-Z]:[\\/]|\\\\|/(?:Users|home|var|tmp)/"));
			Assert.IsFalse(Regex.IsMatch(
				message,
				@"(?i)(?<![\p{L}\p{N}_])(?:[^\\/\s，；;]+[\\/])+(?:[^\\/\s，；;]+)"));
			Assert.IsFalse(Regex.IsMatch(
				message,
				@"(?i)(?<![\p{L}\p{N}_])(?:\.env(?:\.[\w.-]+)?|[\w.-]+\.(?:cs|rs|py|ps1|cmd|bat|exe|dll|pdb|log|txt|jsonl?|csv|tsv|ya?ml|toml|xml|zip|config|ini|db|sqlite|env))(?::\d+)?"));
			Assert.IsFalse(Regex.IsMatch(message, @"(?i)\b[A-Za-z_][A-Za-z0-9_.+`]*(?:Exception|Error)\b"));
			Assert.IsFalse(Regex.IsMatch(message, @"(?i)\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+"));
			Assert.IsFalse(Regex.IsMatch(message, @"(?i)\b(?:token|password|secret|api[_-]?key)\b\s*[:=]\s*(?!\[已隐藏\])"));
			Assert.IsFalse(Regex.IsMatch(message, @"(?:令牌|密码|口令|密钥|凭据|授权|认证)\s*[:=：]\s*(?!\[已隐藏\])"));
		}

		private static string FindRepositoryRoot()
		{
			var candidates = new[]
			{
				Directory.GetCurrentDirectory(),
				Path.GetDirectoryName(typeof(LogSafetyTest).Assembly.Location),
				Path.GetDirectoryName(typeof(PluginLog).Assembly.Location),
				AppDomain.CurrentDomain.BaseDirectory
			};
			foreach (var candidate in candidates)
			{
				var current = string.IsNullOrWhiteSpace(candidate)
					? null
					: new DirectoryInfo(candidate);
				while (current != null)
				{
					if (File.Exists(Path.Combine(current.FullName, "MetaCompanion.sln")))
						return current.FullName;
					current = current.Parent;
				}
			}
			Assert.Fail("无法定位 MetaCompanion 仓库根目录。");
			return "";
		}
	}
}
