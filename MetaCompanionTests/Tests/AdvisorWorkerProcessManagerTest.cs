using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class AdvisorWorkerProcessManagerTest
	{
		private string _testRoot;

		[TestInitialize]
		public void Initialize()
		{
			_testRoot = Path.Combine(
				Path.GetTempPath(), "MetaCompanionTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_testRoot);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (string.IsNullOrWhiteSpace(_testRoot) || !Directory.Exists(_testRoot))
				return;
			try { Directory.Delete(_testRoot, true); }
			catch { }
		}

		[TestMethod]
		public void StartAsync_ConcurrentCallsOwnOnlyOneWorker()
		{
			string pidFile;
			using (var manager = CreateManager(out pidFile))
			using (var startGate = new ManualResetEventSlim())
			{
				var first = Task.Run(async () =>
				{
					startGate.Wait();
					return await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);
				});
				var second = Task.Run(async () =>
				{
					startGate.Wait();
					return await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);
				});
				startGate.Set();
				Task.WaitAll(first, second);

				Assert.AreSame(first.Result, second.Result);
				Assert.IsTrue(manager.IsRunning);
				Assert.IsNotNull(manager.LastHealth);
				Assert.IsTrue(manager.LastHealth.IsReady);
				Assert.AreEqual(1, ReadWorkerProcessIds(pidFile).Length);
			}
		}

		[TestMethod]
		public void StopAsync_CancelledTokenStillTerminatesOwnedWorker()
		{
			string pidFile;
			using (var manager = CreateManager(out pidFile))
			{
				manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
				var processId = ReadWorkerProcessIds(pidFile).Single();
				var cancellationObserved = false;
				using (var cancellation = new CancellationTokenSource())
				{
					cancellation.Cancel();
					try
					{
						manager.StopAsync(cancellation.Token).GetAwaiter().GetResult();
					}
					catch (OperationCanceledException)
					{
						cancellationObserved = true;
					}
				}

				Assert.IsTrue(cancellationObserved);
				Assert.IsFalse(manager.IsRunning);
				Assert.IsNull(manager.LastHealth);
				Assert.IsTrue(WaitUntilProcessIsGone(processId, TimeSpan.FromSeconds(3)));
			}
		}

		[TestMethod]
		public void Stop_WithoutProcessAlwaysRestoresStoppingState()
		{
			string ignored;
			using (var manager = CreateManager(out ignored))
			{
				manager.Stop();
				var stopping = (bool)typeof(AdvisorWorkerProcessManager)
					.GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)
					.GetValue(manager);
				Assert.IsFalse(stopping);
			}
		}

		[TestMethod]
		public void WorkerHealth_RequiresBothRuntimeReadinessAndProductionParity()
		{
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(null));
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = false,
					IsProductionReady = true
				}));
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = false,
					Backend = "rust",
					ParityProfile = "combat-v1",
					SupportsCounterplayTurnpair = false
				}));
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = "rust",
					ParityProfile = "combat-v1",
					SupportsCounterplayTurnpair = true
				}), "Self-declared production readiness cannot bypass the fixed full profile.");
			Assert.IsTrue(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = "rust",
					ParityProfile = "full",
					SupportsCounterplayTurnpair = true
				}));
			Assert.IsTrue(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = "python",
					ParityProfile = "full"
				}));
			Assert.IsTrue(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = ""
				},
				AdvisorWorkerBackendKind.Python),
				"Legacy Python health without a backend marker remains compatible.");
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = "",
					ParityProfile = "full",
					SupportsCounterplayTurnpair = true
				},
				AdvisorWorkerBackendKind.Rust),
				"An executable target cannot bypass Rust identity attestation.");
			Assert.IsFalse(AdvisorWorkerProcessManager.IsWorkerHealthReady(
				new AdvisorWorkerHealth
				{
					ApiVersion = "2.0",
					Status = "ready",
					IsReady = true,
					IsProductionReady = true,
					Backend = "python"
				},
				AdvisorWorkerBackendKind.Python));
		}

		[TestMethod]
		public void LaunchOptions_RejectProtectedAdditionalArguments()
		{
			var protectedArguments = new[]
			{
				"--port 4321",
				"\"--host\" 0.0.0.0",
				"--data-dir=C:\\untrusted",
				"--session-token stolen",
				"--config config.json",
				"--po\"rt=4321"
			};
			foreach (var arguments in protectedArguments)
			{
				var rejected = false;
				try
				{
					using (new AdvisorWorkerProcessManager(
						Path.Combine(_testRoot, "plugin"),
						Path.Combine(_testRoot, "data"),
						new AdvisorWorkerLaunchOptions { AdditionalArguments = arguments }))
					{
					}
				}
				catch (ArgumentException)
				{
					rejected = true;
				}
				Assert.IsTrue(rejected, "Protected argument was accepted: " + arguments);
			}
		}

		[TestMethod]
		public void ResolveTargets_OnlyTreatsNamedOrExplicitNativeExecutableAsRust()
		{
			var pluginDirectory = Path.Combine(_testRoot, "plugin");
			var dataDirectory = Path.Combine(_testRoot, "data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var rustPath = Path.Combine(workerDirectory, "metacompanion-solver.exe");
			var legacyPath = Path.Combine(workerDirectory, "MetaCompanion.Advisor.Worker.exe");
			File.WriteAllBytes(rustPath, new byte[] { 0 });
			File.WriteAllBytes(legacyPath, new byte[] { 0 });

			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory, dataDirectory, new AdvisorWorkerLaunchOptions()))
			{
				var targets = ResolveTargets(manager);
				Assert.AreEqual(2, targets.Length);
				Assert.AreEqual(rustPath, GetTargetPath(targets[0]));
				Assert.AreEqual(
					AdvisorWorkerBackendKind.Rust, GetTargetBackend(targets[0]));
				Assert.AreEqual(legacyPath, GetTargetPath(targets[1]));
				Assert.AreEqual(
					AdvisorWorkerBackendKind.Python, GetTargetBackend(targets[1]));
			}

			var customNativePath = Path.Combine(workerDirectory, "custom-native.exe");
			File.WriteAllBytes(customNativePath, new byte[] { 0 });
			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					WorkerPath = customNativePath,
					BackendMode = AdvisorWorkerBackendMode.RustOnly
				}))
			{
				var targets = ResolveTargets(manager);
				Assert.AreEqual(1, targets.Length);
				Assert.AreEqual(
					AdvisorWorkerBackendKind.Rust, GetTargetBackend(targets[0]));
			}
		}

		[TestMethod]
		public void StartAsync_TrainingEnabledClearsInheritedDisableFlag()
		{
			const string variable = "METACOMPANION_SOLVER_NO_TRAINING_LOG";
			var original = Environment.GetEnvironmentVariable(variable);
			var envFile = Path.Combine(_testRoot, "worker-env.txt");
			try
			{
				Environment.SetEnvironmentVariable(variable, "1");
				string ignored;
				using (var manager = CreateManager(
					out ignored,
					"--env-file " + QuoteArgument(envFile),
					true))
				{
					manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
					Assert.IsTrue(SpinWait.SpinUntil(
						() => File.Exists(envFile), TimeSpan.FromSeconds(3)));
					Assert.AreEqual("", File.ReadAllText(envFile));
				}
			}
			finally
			{
				Environment.SetEnvironmentVariable(variable, original);
			}
		}

		[TestMethod]
		public void BackendModes_AutoFallsBackWhileOnlyModesStayStrict()
		{
			string autoPidFile;
			using (var manager = CreateMixedBackendManager(
				"auto", AdvisorWorkerBackendMode.Auto, out autoPidFile))
			{
				manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
				Assert.AreEqual(AdvisorWorkerBackendKind.Python, manager.ActiveBackend);
				Assert.AreEqual("launch_solver.py", Path.GetFileName(manager.ActiveWorkerPath));
				Assert.IsTrue(manager.LastStartUsedFallback);
				Assert.AreEqual(
					"Rust 求解器暂不可用，已自动切换到 Python 兼容求解器。",
					manager.LastStartUserMessage);
				Assert.IsTrue(manager.HasQuarantinedRustWorker);
				Assert.AreEqual(1, ReadWorkerProcessIds(autoPidFile).Length);
			}

			string rustOnlyPidFile;
			using (var manager = CreateMixedBackendManager(
				"rust-only", AdvisorWorkerBackendMode.RustOnly, out rustOnlyPidFile))
			{
				var failed = false;
				try
				{
					manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
				}
				catch (InvalidOperationException)
				{
					failed = true;
				}
				Assert.IsTrue(failed);
				Assert.IsFalse(manager.IsRunning);
				Assert.IsTrue(manager.HasQuarantinedRustWorker);
				Assert.IsFalse(File.Exists(rustOnlyPidFile));
			}

			string pythonOnlyPidFile;
			using (var manager = CreateMixedBackendManager(
				"python-only", AdvisorWorkerBackendMode.PythonOnly, out pythonOnlyPidFile))
			{
				manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
				Assert.AreEqual(AdvisorWorkerBackendKind.Python, manager.ActiveBackend);
				Assert.IsFalse(manager.LastStartUsedFallback);
				Assert.IsFalse(manager.HasQuarantinedRustWorker);
				Assert.AreEqual(1, ReadWorkerProcessIds(pythonOnlyPidFile).Length);
			}
		}

		[TestMethod]
		public void CompatibilityFallback_QuarantinesOneLiveAutoRustWorkerOnlyOnce()
		{
			var pluginDirectory = Path.Combine(_testRoot, "compat-once-plugin");
			var dataDirectory = Path.Combine(_testRoot, "compat-once-data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var rustPath = Path.Combine(workerDirectory, "metacompanion-solver.exe");
			File.WriteAllBytes(rustPath, new byte[] { 0 });
			File.WriteAllText(Path.Combine(workerDirectory, "launch_solver.py"), WorkerScript);

			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					BackendMode = AdvisorWorkerBackendMode.Auto
				}))
			using (var process = StartLongRunningProcess())
			{
				AssignOwnedRustProcess(manager, process, rustPath);

				Assert.IsTrue(manager.TryBeginCompatibilityFallback());
				Assert.IsFalse(
					manager.TryBeginCompatibilityFallback(),
					"Repeated errors from the same state/process must not start another fallback.");
				Assert.IsTrue(manager.HasQuarantinedRustWorker);
				var remainingTargets = ResolveTargets(manager);
				Assert.AreEqual(1, remainingTargets.Length);
				Assert.AreEqual(
					AdvisorWorkerBackendKind.Python,
					GetTargetBackend(remainingTargets[0]));
				manager.Stop();
			}
		}

		[TestMethod]
		public void CompatibilityFallback_WithoutPythonKeepsCurrentRustRunning()
		{
			var pluginDirectory = Path.Combine(_testRoot, "compat-no-python-plugin");
			var dataDirectory = Path.Combine(_testRoot, "compat-no-python-data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var rustPath = Path.Combine(workerDirectory, "metacompanion-solver.exe");
			File.WriteAllBytes(rustPath, new byte[] { 0 });

			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					BackendMode = AdvisorWorkerBackendMode.Auto
				}))
			using (var process = StartLongRunningProcess())
			{
				AssignOwnedRustProcess(manager, process, rustPath);

				Assert.IsFalse(manager.TryBeginCompatibilityFallback());
				Assert.IsFalse(manager.HasQuarantinedRustWorker);
				Assert.IsTrue(manager.IsRunning);
				Assert.IsFalse(process.HasExited);
				manager.Stop();
			}
		}

		[TestMethod]
		public void CompatibilityFallback_FailedPythonStartNeverReenablesRust()
		{
			var pluginDirectory = Path.Combine(_testRoot, "compat-python-fails-plugin");
			var dataDirectory = Path.Combine(_testRoot, "compat-python-fails-data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var rustPath = Path.Combine(workerDirectory, "metacompanion-solver.exe");
			File.WriteAllBytes(rustPath, new byte[] { 0 });
			File.WriteAllText(
				Path.Combine(workerDirectory, "launch_solver.py"),
				"raise SystemExit(23)\n");

			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					BackendMode = AdvisorWorkerBackendMode.Auto,
					StartupTimeout = TimeSpan.FromSeconds(2)
				}))
			using (var process = StartLongRunningProcess())
			{
				AssignOwnedRustProcess(manager, process, rustPath);
				Assert.IsTrue(manager.TryBeginCompatibilityFallback());
				manager.Stop();

				for (var attempt = 0; attempt < 2; attempt++)
				{
					var failed = false;
					try
					{
						manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
					}
					catch (InvalidOperationException)
					{
						failed = true;
					}
					Assert.IsTrue(failed);
					Assert.IsFalse(manager.IsRunning);
					Assert.IsTrue(manager.HasQuarantinedRustWorker);
					var remainingTargets = ResolveTargets(manager);
					Assert.AreEqual(1, remainingTargets.Length);
					Assert.AreEqual(
						AdvisorWorkerBackendKind.Python,
						GetTargetBackend(remainingTargets[0]));
				}
			}
		}

		[TestMethod]
		public void ExitedCallback_IgnoresStaleProcessWithoutClearingCurrentWorker()
		{
			string pidFile;
			using (var manager = CreateManager(out pidFile))
			{
				var client = manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
				var notifications = 0;
				manager.Exited += (sender, args) => Interlocked.Increment(ref notifications);

				using (var stale = Process.Start(new ProcessStartInfo
				{
					FileName = Environment.GetEnvironmentVariable("ComSpec"),
					Arguments = "/d /c exit 0",
					UseShellExecute = false,
					CreateNoWindow = true,
					WindowStyle = ProcessWindowStyle.Hidden
				}))
				{
					stale.WaitForExit();
					InvokeExitedCallback(manager, stale);
				}

				Assert.AreEqual(0, notifications);
				Assert.IsTrue(manager.IsRunning);
				Assert.AreSame(client, manager.Client);
			}
		}

		[TestMethod]
		public void UnexpectedExit_ClearsOwnershipNotifiesOnceAndDisposesProcess()
		{
			var pluginDirectory = Path.Combine(_testRoot, "exit-plugin");
			var dataDirectory = Path.Combine(_testRoot, "exit-data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var rustPath = Path.Combine(workerDirectory, "metacompanion-solver.exe");
			var pythonPath = Path.Combine(workerDirectory, "launch_solver.py");
			File.WriteAllBytes(rustPath, new byte[] { 0 });
			File.WriteAllText(pythonPath, WorkerScript);
			using (var manager = new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					BackendMode = AdvisorWorkerBackendMode.Auto
				}))
			using (var ownedProcess = Process.Start(new ProcessStartInfo
			{
				FileName = Environment.GetEnvironmentVariable("ComSpec"),
				Arguments = "/d /c exit 17",
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			}))
			{
				ownedProcess.WaitForExit();
				typeof(AdvisorWorkerProcessManager)
					.GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
					.SetValue(manager, ownedProcess);
				typeof(AdvisorWorkerProcessManager)
					.GetField("_activeTarget", BindingFlags.Instance | BindingFlags.NonPublic)
					.SetValue(manager, CreateRustTarget(rustPath));
				AdvisorWorkerExitedEventArgs notification = null;
				var notifications = 0;
				manager.Exited += (sender, args) =>
				{
					notification = args;
					Interlocked.Increment(ref notifications);
				};

				InvokeExitedCallback(manager, ownedProcess);

				Assert.AreEqual(1, notifications);
				Assert.IsNotNull(notification);
				Assert.AreEqual(17, notification.ExitCode);
				Assert.IsFalse(notification.Expected);
				Assert.AreEqual(AdvisorWorkerBackendKind.Rust, notification.Backend);
				Assert.IsTrue(notification.FallbackAvailable);
				Assert.IsTrue(manager.HasQuarantinedRustWorker);
				Assert.IsFalse(manager.IsRunning);
				Assert.IsNull(manager.Client);
				Assert.IsTrue(IsDisposed(ownedProcess));
			}
		}

		private AdvisorWorkerProcessManager CreateManager(
			out string pidFile, string additionalArguments = "", bool enableTrainingLog = true)
		{
			var pluginDirectory = Path.Combine(_testRoot, "plugin");
			var dataDirectory = Path.Combine(_testRoot, "data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var workerPath = Path.Combine(workerDirectory, "launch_solver.py");
			File.WriteAllText(workerPath, WorkerScript);
			pidFile = Path.Combine(_testRoot, "worker-pids.txt");
			return new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					WorkerPath = workerPath,
					StartupTimeout = TimeSpan.FromSeconds(5),
					StopTimeout = TimeSpan.FromMilliseconds(50),
					AdditionalArguments =
						"--pid-file " + QuoteArgument(pidFile) +
						(string.IsNullOrWhiteSpace(additionalArguments)
							? ""
							: " " + additionalArguments),
					EnableTrainingLog = enableTrainingLog,
					ClientOptions = new AdvisorWorkerClientOptions
					{
						HealthTimeout = TimeSpan.FromSeconds(1)
					}
				});
		}

		private AdvisorWorkerProcessManager CreateMixedBackendManager(
			string name, AdvisorWorkerBackendMode backendMode, out string pidFile)
		{
			var root = Path.Combine(_testRoot, name);
			var pluginDirectory = Path.Combine(root, "plugin");
			var dataDirectory = Path.Combine(root, "data");
			var workerDirectory = Path.Combine(pluginDirectory, "AdvisorWorker");
			Directory.CreateDirectory(workerDirectory);
			Directory.CreateDirectory(dataDirectory);
			var systemExecutable = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
			Assert.IsTrue(File.Exists(systemExecutable));
			File.Copy(
				systemExecutable,
				Path.Combine(workerDirectory, "metacompanion-solver.exe"));
			File.WriteAllText(Path.Combine(workerDirectory, "launch_solver.py"), WorkerScript);
			pidFile = Path.Combine(root, "worker-pids.txt");
			return new AdvisorWorkerProcessManager(
				pluginDirectory,
				dataDirectory,
				new AdvisorWorkerLaunchOptions
				{
					BackendMode = backendMode,
					StartupTimeout = TimeSpan.FromSeconds(3),
					StopTimeout = TimeSpan.FromMilliseconds(50),
					AdditionalArguments = "--pid-file " + QuoteArgument(pidFile),
					ClientOptions = new AdvisorWorkerClientOptions
					{
						HealthTimeout = TimeSpan.FromSeconds(1)
					}
				});
		}

		private static int[] ReadWorkerProcessIds(string pidFile)
		{
			Assert.IsTrue(SpinWait.SpinUntil(
				() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0,
				TimeSpan.FromSeconds(3)));
			return File.ReadAllLines(pidFile)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.Select(int.Parse)
				.ToArray();
		}

		private static bool WaitUntilProcessIsGone(int processId, TimeSpan timeout)
		{
			return SpinWait.SpinUntil(() =>
			{
				try
				{
					using (var process = Process.GetProcessById(processId))
						return process.HasExited;
				}
				catch (ArgumentException)
				{
					return true;
				}
			}, timeout);
		}

		private static bool IsDisposed(Process process)
		{
			try
			{
				var ignored = process.Handle;
				return false;
			}
			catch (ObjectDisposedException)
			{
				return true;
			}
			catch (InvalidOperationException)
			{
				return true;
			}
		}

		private static void InvokeExitedCallback(
			AdvisorWorkerProcessManager manager, Process process)
		{
			typeof(AdvisorWorkerProcessManager)
				.GetMethod("OnProcessExited", BindingFlags.Instance | BindingFlags.NonPublic)
				.Invoke(manager, new object[] { process, EventArgs.Empty });
		}

		private static object[] ResolveTargets(AdvisorWorkerProcessManager manager)
		{
			return ((System.Collections.IEnumerable)typeof(AdvisorWorkerProcessManager)
				.GetMethod("ResolveTargets", BindingFlags.Instance | BindingFlags.NonPublic)
				.Invoke(manager, null)).Cast<object>().ToArray();
		}

		private static string GetTargetPath(object target)
		{
			return (string)target.GetType().GetProperty("EntryPath").GetValue(target, null);
		}

		private static AdvisorWorkerBackendKind GetTargetBackend(object target)
		{
			return (AdvisorWorkerBackendKind)target.GetType()
				.GetProperty("Backend").GetValue(target, null);
		}

		private static object CreateRustTarget(string entryPath)
		{
			var managerType = typeof(AdvisorWorkerProcessManager);
			var targetType = managerType.GetNestedType(
				"WorkerTarget", BindingFlags.NonPublic);
			var targetKindType = managerType.GetNestedType(
				"WorkerTargetKind", BindingFlags.NonPublic);
			var executableKind = Enum.Parse(targetKindType, "Executable");
			return Activator.CreateInstance(
				targetType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				new[]
				{
					(object)entryPath,
					Path.GetDirectoryName(entryPath),
					executableKind,
					"",
					AdvisorWorkerBackendKind.Rust
				},
				null);
		}

		private static Process StartLongRunningProcess()
		{
			var process = Process.Start(new ProcessStartInfo
			{
				FileName = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.System),
					"ping.exe"),
				Arguments = "127.0.0.1 -n 30",
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			Assert.IsNotNull(process);
			Assert.IsFalse(process.HasExited);
			return process;
		}

		private static void AssignOwnedRustProcess(
			AdvisorWorkerProcessManager manager, Process process, string rustPath)
		{
			typeof(AdvisorWorkerProcessManager)
				.GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
				.SetValue(manager, process);
			typeof(AdvisorWorkerProcessManager)
				.GetField("_activeTarget", BindingFlags.Instance | BindingFlags.NonPublic)
				.SetValue(manager, CreateRustTarget(rustPath));
			typeof(AdvisorWorkerProcessManager)
				.GetProperty("ActiveBackend")
				.SetValue(manager, AdvisorWorkerBackendKind.Rust, null);
		}

		private static string QuoteArgument(string value)
		{
			return "\"" + value.Replace("\"", "\\\"") + "\"";
		}

		private const string WorkerScript = @"import argparse
import http.server
import json
import os

parser = argparse.ArgumentParser()
parser.add_argument('command')
parser.add_argument('--port', type=int, required=True)
parser.add_argument('--data-dir')
parser.add_argument('--pid-file', required=True)
parser.add_argument('--env-file')
args, unknown = parser.parse_known_args()

with open(args.pid_file, 'a', encoding='utf-8') as pid_stream:
    pid_stream.write(str(os.getpid()) + '\n')

if args.env_file:
    with open(args.env_file, 'w', encoding='utf-8') as env_stream:
        env_stream.write(os.environ.get('METACOMPANION_SOLVER_NO_TRAINING_LOG', ''))

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/v1/health':
            self.send_response(404)
            self.end_headers()
            return
        payload = json.dumps({'api_version': '1.0', 'status': 'ready', 'is_ready': True}).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format, *values):
        pass

server = http.server.HTTPServer(('127.0.0.1', args.port), Handler)
server.serve_forever()
";
	}
}
