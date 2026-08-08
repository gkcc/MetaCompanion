using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanion
{
	public enum AdvisorWorkerBackendMode
	{
		Auto,
		RustOnly,
		PythonOnly
	}

	public enum AdvisorWorkerBackendKind
	{
		Unknown,
		Rust,
		Python
	}

	public sealed class AdvisorWorkerLaunchOptions
	{
		/// <summary>Optional .exe/.py/package __main__.py path, restricted to a configured root.</summary>
		public string WorkerPath { get; set; } = "";
		/// <summary>Python executable or command name. Defaults to python when the target is Python.</summary>
		public string PythonExecutablePath { get; set; } = "";
		/// <summary>Zero selects a free ephemeral loopback port.</summary>
		public int Port { get; set; }
		public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(12);
		public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(2);
		public string AdditionalArguments { get; set; } = "";
		public bool EnableTrainingLog { get; set; } = true;
		public AdvisorWorkerBackendMode BackendMode { get; set; } =
			AdvisorWorkerBackendMode.Auto;
		public AdvisorWorkerClientOptions ClientOptions { get; set; } = new AdvisorWorkerClientOptions();
	}

	public sealed class AdvisorWorkerExitedEventArgs : EventArgs
	{
		public AdvisorWorkerExitedEventArgs(
			int? exitCode,
			bool expected,
			AdvisorWorkerBackendKind backend = AdvisorWorkerBackendKind.Unknown,
			bool fallbackAvailable = false)
		{
			ExitCode = exitCode;
			Expected = expected;
			Backend = backend;
			FallbackAvailable = fallbackAvailable;
		}

		public int? ExitCode { get; }
		public bool Expected { get; }
		public AdvisorWorkerBackendKind Backend { get; }
		public bool FallbackAvailable { get; }
	}

	/// <summary>
	/// Owns exactly one hidden local worker process. It only launches entrypoints located below
	/// the supplied plugin/data roots and always connects through 127.0.0.1.
	/// </summary>
	public sealed class AdvisorWorkerProcessManager : IDisposable
	{
		private readonly object _sync = new object();
		private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
		private readonly CancellationTokenSource _disposeCancellation = new CancellationTokenSource();
		private readonly string _pluginDirectory;
		private readonly string _dataDirectory;
		private readonly AdvisorWorkerLaunchOptions _options;
		private readonly HashSet<string> _quarantinedRustPaths =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private Process _process;
		private WorkerTarget _activeTarget;
		private CancellationTokenSource _activeStartCancellation;
		private long _stopRevision;
		private int _pendingStopCount;
		private bool _stopping;
		private bool _disposed;

		public AdvisorWorkerProcessManager(
			string pluginDirectory, string dataDirectory, AdvisorWorkerLaunchOptions options = null)
		{
			_pluginDirectory = NormalizeRoot(pluginDirectory, nameof(pluginDirectory));
			_dataDirectory = NormalizeRoot(dataDirectory, nameof(dataDirectory));
			_options = options ?? new AdvisorWorkerLaunchOptions();
			ValidateOptions(_options);
		}

		public event EventHandler<AdvisorWorkerExitedEventArgs> Exited;

		public Uri BaseUri { get; private set; }
		public string ActiveWorkerPath { get; private set; } = "";
		public AdvisorWorkerBackendKind ActiveBackend { get; private set; } =
			AdvisorWorkerBackendKind.Unknown;
		public bool LastStartUsedFallback { get; private set; }
		public string LastStartUserMessage { get; private set; } = "";
		public AdvisorWorkerClient Client { get; private set; }
		public AdvisorWorkerHealth LastHealth { get; private set; }

		public bool HasQuarantinedRustWorker
		{
			get
			{
				lock (_sync)
					return _quarantinedRustPaths.Count > 0;
			}
		}

		public bool IsRunning
		{
			get
			{
				lock (_sync)
					return _process != null && !SafeHasExited(_process);
			}
		}

		/// <summary>
		/// Atomically quarantines the currently owned Rust worker after an explicit solve-contract
		/// incompatibility. The caller still owns stopping it and starting the compatibility target.
		/// A Python target must already be available, and the operation is one-shot per Rust path.
		/// </summary>
		internal bool TryBeginCompatibilityFallback()
		{
			Process process;
			WorkerTarget target;
			string rustPath;
			lock (_sync)
			{
				if (_disposed || _stopping ||
					_options.BackendMode != AdvisorWorkerBackendMode.Auto ||
					_process == null || SafeHasExited(_process) ||
					_activeTarget == null ||
					_activeTarget.Backend != AdvisorWorkerBackendKind.Rust)
				{
					return false;
				}
				process = _process;
				target = _activeTarget;
				rustPath = Path.GetFullPath(target.EntryPath);
				if (_quarantinedRustPaths.Contains(rustPath))
					return false;
			}

			if (!HasPythonFallbackCandidate())
				return false;

			lock (_sync)
			{
				if (_disposed || _stopping ||
					_options.BackendMode != AdvisorWorkerBackendMode.Auto ||
					!ReferenceEquals(_process, process) || SafeHasExited(process) ||
					!ReferenceEquals(_activeTarget, target) ||
					_quarantinedRustPaths.Contains(rustPath))
				{
					return false;
				}
				_quarantinedRustPaths.Add(rustPath);
				return true;
			}
		}

		public async Task<AdvisorWorkerClient> StartAsync(CancellationToken cancellationToken)
		{
			ThrowIfDisposed();
			long stopRevision;
			lock (_sync)
				stopRevision = _stopRevision;

			using (var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken, _disposeCancellation.Token))
			{
				await _lifecycleGate.WaitAsync(startCancellation.Token).ConfigureAwait(false);
				try
				{
					startCancellation.Token.ThrowIfCancellationRequested();
					Process precedingProcess = null;
					AdvisorWorkerClient existingClient = null;
					lock (_sync)
					{
						if (_disposed)
							throw new ObjectDisposedException(nameof(AdvisorWorkerProcessManager));
						if (_stopping || stopRevision != _stopRevision)
							throw new OperationCanceledException(
								"Advisor worker startup was superseded by a stop request.",
								startCancellation.Token);
						if (_process != null && !SafeHasExited(_process) && Client != null)
						{
							existingClient = Client;
						}
						else if (_process != null)
						{
							precedingProcess = _process;
							ClearOwnedProcessLocked(_process);
						}
						_activeStartCancellation = startCancellation;
					}

					if (existingClient != null)
						return existingClient;
					StopAndDisposeProcess(precedingProcess);

					var targets = ResolveTargets();
					Exception lastError = null;
					var rustFailed = false;
					lock (_sync)
					{
						LastStartUsedFallback = false;
						LastStartUserMessage = "";
					}

					foreach (var target in targets)
					{
						try
						{
							var client = await StartTargetAsync(
								target, stopRevision, startCancellation)
								.ConfigureAwait(false);
							if (rustFailed && target.Backend == AdvisorWorkerBackendKind.Python)
							{
								lock (_sync)
								{
									LastStartUsedFallback = true;
									LastStartUserMessage =
										"Rust 求解器暂不可用，已自动切换到 Python 兼容求解器。";
								}
							}
							return client;
						}
						catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
						{
							throw;
						}
						catch (Exception ex)
						{
							lastError = ex;
							if (target.Backend == AdvisorWorkerBackendKind.Rust)
							{
								rustFailed = true;
								QuarantineRustTarget(target.EntryPath);
							}
							Log.Warn(
								"顾问工作进程启动检查失败：后端=" + target.Backend + "；" +
								AdvisorUserMessages.RuntimeFailureDiagnostic(
									ex, "worker_start"));
						}
					}

					throw new InvalidOperationException(
						"No configured advisor worker candidate passed startup and health validation.",
						lastError);
				}
				finally
				{
					lock (_sync)
					{
						if (ReferenceEquals(_activeStartCancellation, startCancellation))
							_activeStartCancellation = null;
					}
					_lifecycleGate.Release();
				}
			}
		}

		private async Task<AdvisorWorkerClient> StartTargetAsync(
			WorkerTarget target,
			long stopRevision,
			CancellationTokenSource startCancellation)
		{
			var port = _options.Port == 0 ? FindFreeLoopbackPort() : _options.Port;
			var token = CreateSessionToken();
			var baseUri = new Uri("http://127.0.0.1:" + port + "/", UriKind.Absolute);
			var process = new Process
			{
				StartInfo = CreateStartInfo(target, port, token),
				EnableRaisingEvents = true
			};
			try
			{
				startCancellation.Token.ThrowIfCancellationRequested();
				if (!process.Start())
					throw new InvalidOperationException("The advisor worker process did not start.");
				var client = new AdvisorWorkerClient(baseUri, token, _options.ClientOptions);
				var health = await WaitUntilReadyAsync(
					process, client, target, _options.StartupTimeout, startCancellation.Token)
					.ConfigureAwait(false);
				startCancellation.Token.ThrowIfCancellationRequested();

				process.Exited += OnProcessExited;
				lock (_sync)
				{
					if (_disposed || _stopping || stopRevision != _stopRevision ||
						startCancellation.IsCancellationRequested)
					{
						throw new OperationCanceledException(
							"Advisor worker startup was superseded before ownership was committed.",
							startCancellation.Token);
					}
					if (SafeHasExited(process))
						throw new InvalidOperationException(
							"Advisor worker exited before startup ownership was committed.");
					_process = process;
					_activeTarget = target;
					BaseUri = baseUri;
					ActiveWorkerPath = target.EntryPath;
					ActiveBackend = target.Backend;
					Client = client;
					LastHealth = health;
				}

				lock (_sync)
				{
					if (!ReferenceEquals(_process, process) ||
						!ReferenceEquals(Client, client) || SafeHasExited(process))
					{
						throw new InvalidOperationException(
							"Advisor worker exited before startup ownership was confirmed.");
					}
				}
				return client;
			}
			catch
			{
				lock (_sync)
					ClearOwnedProcessLocked(process);
				StopAndDisposeProcess(process);
				throw;
			}
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			var activeStart = BeginStop();
			TryCancel(activeStart);
			var cancellationObserved = false;
			await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				var process = DetachOwnedProcess();
				if (process != null)
				{
					DetachExitedHandler(process);
					try
					{
						var exited = SafeHasExited(process);
						if (!exited && !cancellationToken.IsCancellationRequested)
						{
							try { process.CloseMainWindow(); }
							catch { }
							try
							{
								exited = await WaitForExitAsync(
									process, _options.StopTimeout, cancellationToken)
									.ConfigureAwait(false);
							}
							catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
							{
								cancellationObserved = true;
							}
						}
						else if (cancellationToken.IsCancellationRequested)
						{
							cancellationObserved = true;
						}
						if (!exited)
							StopOwnedProcess(process);
					}
					finally
					{
						StopOwnedProcess(process);
						DisposeProcess(process);
					}
				}
				else if (cancellationToken.IsCancellationRequested)
				{
					cancellationObserved = true;
				}
			}
			finally
			{
				EndStop();
				_lifecycleGate.Release();
			}
			if (cancellationObserved)
				throw new OperationCanceledException(cancellationToken);
		}

		/// <summary>Immediate, exact-process fallback for synchronous HDT unload paths.</summary>
		public void Stop()
		{
			var activeStart = BeginStop();
			TryCancel(activeStart);
			_lifecycleGate.Wait();
			try
			{
				var process = DetachOwnedProcess();
				if (process == null)
					return;
				DetachExitedHandler(process);
				StopOwnedProcess(process);
				DisposeProcess(process);
			}
			finally
			{
				EndStop();
				_lifecycleGate.Release();
			}
		}

		public void Dispose()
		{
			lock (_sync)
			{
				if (_disposed)
					return;
				_disposed = true;
			}
			try { _disposeCancellation.Cancel(); }
			catch (ObjectDisposedException) { }
			Stop();
		}

		private async Task<AdvisorWorkerHealth> WaitUntilReadyAsync(
			Process process, AdvisorWorkerClient client, WorkerTarget target, TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + timeout;
			Exception lastError = null;
			while (DateTime.UtcNow < deadline)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (SafeHasExited(process))
					throw new InvalidOperationException("Advisor worker exited during startup with code " + SafeExitCode(process) + ".", lastError);
				AdvisorWorkerHealth health = null;
				try
				{
					health = await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					lastError = ex;
				}

				if (health != null)
				{
					if (IsWorkerHealthReady(health, target.Backend))
						return health;
					if (health.IsReady ||
						string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
					{
						throw new AdvisorWorkerProtocolException(
							DescribeHealthGateFailure(health, target.Backend));
					}
					lastError = new InvalidOperationException(
						"Worker health status: " + (health.Status ?? "") + " " +
						(health.Message ?? ""));
				}
				await Task.Delay(150, cancellationToken).ConfigureAwait(false);
			}
			throw new TimeoutException("Advisor worker did not become healthy within " + timeout.TotalSeconds + " seconds.", lastError);
		}

		internal static bool IsWorkerHealthReady(AdvisorWorkerHealth health)
		{
			var backend = health != null &&
				string.Equals(health.Backend, "rust", StringComparison.OrdinalIgnoreCase)
					? AdvisorWorkerBackendKind.Rust
					: AdvisorWorkerBackendKind.Python;
			return IsWorkerHealthReady(health, backend);
		}

		internal static bool IsWorkerHealthReady(
			AdvisorWorkerHealth health, AdvisorWorkerBackendKind targetBackend)
		{
			if (health == null ||
				!string.Equals(health.ApiVersion, AdvisorProtocol.ApiVersion, StringComparison.Ordinal) ||
				!health.IsReady ||
				!string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase) ||
				!health.IsProductionReady)
				return false;
			if (targetBackend == AdvisorWorkerBackendKind.Rust)
			{
				return string.Equals(health.Backend, "rust", StringComparison.OrdinalIgnoreCase) &&
					health.SupportsCounterplayTurnpair &&
					string.Equals(health.ParityProfile, "full", StringComparison.OrdinalIgnoreCase);
			}
			if (targetBackend == AdvisorWorkerBackendKind.Python)
			{
				return string.IsNullOrWhiteSpace(health.Backend) ||
					string.Equals(health.Backend, "python", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static string DescribeHealthGateFailure(
			AdvisorWorkerHealth health, AdvisorWorkerBackendKind targetBackend)
		{
			if (!string.Equals(health.ApiVersion, AdvisorProtocol.ApiVersion, StringComparison.Ordinal))
				return "Advisor worker API version did not match " + AdvisorProtocol.ApiVersion + ".";
			if (!health.IsReady ||
				!string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
				return "Advisor worker returned inconsistent readiness fields.";
			if (!health.IsProductionReady)
				return "Advisor worker has not passed the production parity gate.";
			if (targetBackend == AdvisorWorkerBackendKind.Rust)
			{
				if (!string.Equals(health.Backend, "rust", StringComparison.OrdinalIgnoreCase))
					return "Native advisor worker did not identify itself as the rust backend.";
				if (!string.Equals(health.ParityProfile, "full", StringComparison.OrdinalIgnoreCase))
					return "Native advisor worker has not completed the full parity profile.";
				if (!health.SupportsCounterplayTurnpair)
					return "Native advisor worker does not advertise counterplay turn-pair support.";
			}
			if (targetBackend == AdvisorWorkerBackendKind.Python &&
				!string.IsNullOrWhiteSpace(health.Backend) &&
				!string.Equals(health.Backend, "python", StringComparison.OrdinalIgnoreCase))
				return "Compatibility advisor worker reported an unexpected backend identity.";
			return "Advisor worker did not pass production health validation.";
		}

		private ProcessStartInfo CreateStartInfo(WorkerTarget target, int port, string token)
		{
			ValidateAdditionalArguments(_options.AdditionalArguments);
			string executable;
			var arguments = new StringBuilder();
			if (target.Kind == WorkerTargetKind.Executable)
			{
				executable = target.EntryPath;
				arguments.Append("serve ");
			}
			else
			{
				executable = ResolvePythonExecutable();
				if (target.Kind == WorkerTargetKind.Module)
					arguments.Append("-m ").Append(target.ModuleName).Append(" serve ");
				else
					arguments.Append(QuoteArgument(target.EntryPath)).Append(" serve ");
			}

			arguments.Append("--port ").Append(port).Append(' ');
			arguments.Append("--data-dir ").Append(QuoteArgument(Path.Combine(_dataDirectory, "AdvisorWorker")));
			if (!_options.EnableTrainingLog)
				arguments.Append(" --no-training-log");
			if (!string.IsNullOrWhiteSpace(_options.AdditionalArguments))
				arguments.Append(' ').Append(_options.AdditionalArguments.Trim());

			var info = new ProcessStartInfo
			{
				FileName = executable,
				Arguments = arguments.ToString(),
				WorkingDirectory = target.WorkingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				RedirectStandardInput = false,
				RedirectStandardOutput = false,
				RedirectStandardError = false
			};
			info.EnvironmentVariables["METACOMPANION_ADVISOR_HOST"] = "127.0.0.1";
			info.EnvironmentVariables["METACOMPANION_ADVISOR_PORT"] = port.ToString();
			info.EnvironmentVariables["METACOMPANION_ADVISOR_TOKEN"] = token;
			info.EnvironmentVariables["METACOMPANION_SOLVER_TOKEN"] = token;
			info.EnvironmentVariables["METACOMPANION_SOLVER_HOST"] = "127.0.0.1";
			info.EnvironmentVariables["METACOMPANION_ADVISOR_DATA_DIR"] = Path.Combine(_dataDirectory, "AdvisorWorker");
			info.EnvironmentVariables["METACOMPANION_SOLVER_PORT"] = port.ToString();
			info.EnvironmentVariables["METACOMPANION_SOLVER_DATA_DIR"] = Path.Combine(_dataDirectory, "AdvisorWorker");
			if (!_options.EnableTrainingLog)
				info.EnvironmentVariables["METACOMPANION_SOLVER_NO_TRAINING_LOG"] = "1";
			else
				info.EnvironmentVariables.Remove("METACOMPANION_SOLVER_NO_TRAINING_LOG");
			info.EnvironmentVariables["METACOMPANION_PLUGIN_DIR"] = _pluginDirectory;
			return info;
		}

		private List<WorkerTarget> ResolveTargets()
		{
			var candidates = new List<string>();
			var hasExplicitWorkerPath = !string.IsNullOrWhiteSpace(_options.WorkerPath);
			if (hasExplicitWorkerPath)
				candidates.Add(Path.IsPathRooted(_options.WorkerPath)
					? _options.WorkerPath
					: Path.Combine(_pluginDirectory, _options.WorkerPath));
			else
			{
				foreach (var root in new[] { _dataDirectory, _pluginDirectory })
				{
					candidates.Add(Path.Combine(root, "AdvisorWorker", "metacompanion-solver.exe"));
					candidates.Add(Path.Combine(root, "AdvisorWorker", "launch_solver.py"));
					candidates.Add(Path.Combine(root, "AdvisorWorker", "metacompanion_solver", "__main__.py"));
					candidates.Add(Path.Combine(root, "AdvisorWorker", "MetaCompanion.Advisor.Worker.exe"));
					candidates.Add(Path.Combine(root, "AdvisorWorker", "advisor_worker.exe"));
					candidates.Add(Path.Combine(root, "AdvisorWorker", "advisor_worker.py"));
					candidates.Add(Path.Combine(root, "MetaCompanion.Advisor.Worker.exe"));
					candidates.Add(Path.Combine(root, "advisor_worker.py"));
				}
			}

			var targets = new List<WorkerTarget>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var unresolvedCandidate in candidates)
			{
				var candidate = Path.GetFullPath(unresolvedCandidate);
				if (!seen.Add(candidate))
					continue;
				if (!File.Exists(candidate) || !IsBelowAllowedRoot(candidate))
					continue;
				var extension = Path.GetExtension(candidate);
				WorkerTarget target = null;
				if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
				{
					var backend = IsExplicitRustExecutable(candidate, hasExplicitWorkerPath)
						? AdvisorWorkerBackendKind.Rust
						: AdvisorWorkerBackendKind.Python;
					target = new WorkerTarget(
						candidate,
						Path.GetDirectoryName(candidate),
						WorkerTargetKind.Executable,
						"",
						backend);
				}
				else if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase) &&
					string.Equals(Path.GetFileName(candidate), "__main__.py", StringComparison.OrdinalIgnoreCase))
				{
					var packageDirectory = Path.GetDirectoryName(candidate);
					var packageParent = Directory.GetParent(packageDirectory);
					if (packageParent != null)
					{
						target = new WorkerTarget(
							candidate,
							packageParent.FullName,
							WorkerTargetKind.Module,
							Path.GetFileName(packageDirectory),
							AdvisorWorkerBackendKind.Python);
					}
				}
				else if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase))
				{
					target = new WorkerTarget(
						candidate,
						Path.GetDirectoryName(candidate),
						WorkerTargetKind.Script,
						"",
						AdvisorWorkerBackendKind.Python);
				}

				if (target == null || !BackendModeAllows(target.Backend))
					continue;
				if (target.Backend == AdvisorWorkerBackendKind.Rust &&
					IsRustTargetQuarantined(target.EntryPath))
					continue;
				targets.Add(target);
			}

			if (_options.BackendMode == AdvisorWorkerBackendMode.Auto)
			{
				targets = targets
					.OrderBy(target => target.Backend == AdvisorWorkerBackendKind.Rust ? 0 : 1)
					.ToList();
			}
			if (targets.Count == 0)
			{
				throw new FileNotFoundException(
					"No advisor worker entrypoint matched backend mode " +
					_options.BackendMode + " below the plugin or data directory. Searched: " +
					string.Join(", ", candidates));
			}
			return targets;
		}

		private bool IsExplicitRustExecutable(string candidate, bool hasExplicitWorkerPath)
		{
			if (string.Equals(
				Path.GetFileName(candidate),
				"metacompanion-solver.exe",
				StringComparison.OrdinalIgnoreCase))
				return true;
			return hasExplicitWorkerPath &&
				_options.BackendMode == AdvisorWorkerBackendMode.RustOnly;
		}

		private bool BackendModeAllows(AdvisorWorkerBackendKind backend)
		{
			switch (_options.BackendMode)
			{
				case AdvisorWorkerBackendMode.Auto:
					return backend == AdvisorWorkerBackendKind.Rust ||
						backend == AdvisorWorkerBackendKind.Python;
				case AdvisorWorkerBackendMode.RustOnly:
					return backend == AdvisorWorkerBackendKind.Rust;
				case AdvisorWorkerBackendMode.PythonOnly:
					return backend == AdvisorWorkerBackendKind.Python;
				default:
					return false;
			}
		}

		private bool IsRustTargetQuarantined(string entryPath)
		{
			var fullPath = Path.GetFullPath(entryPath);
			lock (_sync)
				return _quarantinedRustPaths.Contains(fullPath);
		}

		private void QuarantineRustTarget(string entryPath)
		{
			if (string.IsNullOrWhiteSpace(entryPath))
				return;
			var fullPath = Path.GetFullPath(entryPath);
			lock (_sync)
				_quarantinedRustPaths.Add(fullPath);
		}

		private bool HasPythonFallbackCandidate()
		{
			if (_options.BackendMode != AdvisorWorkerBackendMode.Auto)
				return false;
			try
			{
				return ResolveTargets().Any(
					target => target.Backend == AdvisorWorkerBackendKind.Python);
			}
			catch
			{
				return false;
			}
		}

		private string ResolvePythonExecutable()
		{
			if (string.IsNullOrWhiteSpace(_options.PythonExecutablePath))
				return "python";
			var value = _options.PythonExecutablePath.Trim();
			if (!Path.IsPathRooted(value))
				return value;
			var fullPath = Path.GetFullPath(value);
			if (!File.Exists(fullPath))
				throw new FileNotFoundException("Configured Python executable was not found.", fullPath);
			return fullPath;
		}

		private bool IsBelowAllowedRoot(string path)
		{
			return IsBelowRoot(path, _pluginDirectory) || IsBelowRoot(path, _dataDirectory);
		}

		private static bool IsBelowRoot(string path, string root)
		{
			var fullPath = Path.GetFullPath(path);
			var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
		}

		private void OnProcessExited(object sender, EventArgs args)
		{
			var process = sender as Process;
			if (process == null)
				return;
			var exitCode = SafeExitCode(process);
			bool expected;
			WorkerTarget activeTarget;
			AdvisorWorkerBackendKind backend;
			lock (_sync)
			{
				// A detached process is already owned by the start/stop cleanup path. Its
				// delayed Exited callback must never clear or report the replacement worker.
				if (!ReferenceEquals(_process, process))
					return;
				expected = _stopping || _disposed;
				activeTarget = _activeTarget;
				backend = activeTarget == null
					? ActiveBackend
					: activeTarget.Backend;
				if (!expected && backend == AdvisorWorkerBackendKind.Rust &&
					activeTarget != null)
				{
					_quarantinedRustPaths.Add(Path.GetFullPath(activeTarget.EntryPath));
				}
				ClearOwnedProcessLocked(process);
			}
			var fallbackAvailable = !expected &&
				backend == AdvisorWorkerBackendKind.Rust &&
				HasPythonFallbackCandidate();
			DetachExitedHandler(process);
			try
			{
				if (!expected)
				{
					Log.Warn(
						"顾问工作进程意外退出：后端=" + backend +
						"；退出码=" + (exitCode.HasValue ? exitCode.Value.ToString() : "未知") +
						"；可切换兼容进程=" + (fallbackAvailable ? "是" : "否") + "。");
				}
				var callback = Exited;
				if (callback != null)
				{
					try
					{
						callback(
							this,
							new AdvisorWorkerExitedEventArgs(
								exitCode, expected, backend, fallbackAvailable));
					}
					catch { }
				}
			}
			finally
			{
				DisposeProcess(process);
			}
		}

		private static async Task<bool> WaitForExitAsync(
			Process process, TimeSpan timeout, CancellationToken cancellationToken)
		{
			if (SafeHasExited(process))
				return true;
			var timer = Stopwatch.StartNew();
			while (timer.Elapsed < timeout)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (SafeHasExited(process))
					return true;
				var remaining = timeout - timer.Elapsed;
				var delayMilliseconds = (int)Math.Max(
					1, Math.Min(50, Math.Ceiling(remaining.TotalMilliseconds)));
				await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
			}
			return SafeHasExited(process);
		}

		private CancellationTokenSource BeginStop()
		{
			lock (_sync)
			{
				_stopRevision++;
				_pendingStopCount++;
				_stopping = true;
				return _activeStartCancellation;
			}
		}

		private void EndStop()
		{
			lock (_sync)
			{
				if (_pendingStopCount > 0)
					_pendingStopCount--;
				_stopping = _pendingStopCount > 0;
			}
		}

		private Process DetachOwnedProcess()
		{
			lock (_sync)
			{
				var process = _process;
				ClearOwnedProcessLocked(process);
				return process;
			}
		}

		private void ClearOwnedProcessLocked(Process process)
		{
			if (process == null || !ReferenceEquals(_process, process))
				return;
			_process = null;
			_activeTarget = null;
			Client = null;
			LastHealth = null;
			BaseUri = null;
			ActiveWorkerPath = "";
			ActiveBackend = AdvisorWorkerBackendKind.Unknown;
		}

		private void StopAndDisposeProcess(Process process)
		{
			if (process == null)
				return;
			DetachExitedHandler(process);
			StopOwnedProcess(process);
			DisposeProcess(process);
		}

		private void DetachExitedHandler(Process process)
		{
			if (process == null)
				return;
			try { process.Exited -= OnProcessExited; }
			catch (ObjectDisposedException) { }
			catch (InvalidOperationException) { }
		}

		private static void DisposeProcess(Process process)
		{
			if (process == null)
				return;
			try { process.Dispose(); }
			catch { }
		}

		private static void TryCancel(CancellationTokenSource source)
		{
			if (source == null)
				return;
			try { source.Cancel(); }
			catch (ObjectDisposedException) { }
		}

		private static void StopOwnedProcess(Process process)
		{
			if (process == null || SafeHasExited(process))
				return;
			try { process.Kill(); }
			catch (InvalidOperationException) { }
			catch (System.ComponentModel.Win32Exception) { }
		}

		private static bool SafeHasExited(Process process)
		{
			try { return process == null || process.HasExited; }
			catch { return true; }
		}

		private static int? SafeExitCode(Process process)
		{
			try { return process != null && process.HasExited ? (int?)process.ExitCode : null; }
			catch { return null; }
		}

		private static int FindFreeLoopbackPort()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			try
			{
				listener.Start();
				return ((IPEndPoint)listener.LocalEndpoint).Port;
			}
			finally
			{
				listener.Stop();
			}
		}

		public static string CreateSessionToken()
		{
			var bytes = new byte[32];
			using (var random = RandomNumberGenerator.Create())
				random.GetBytes(bytes);
			return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		}

		private static string QuoteArgument(string value)
		{
			if (string.IsNullOrEmpty(value))
				return "\"\"";
			return "\"" + value.Replace("\"", "\\\"") + "\"";
		}

		private static string NormalizeRoot(string path, string parameterName)
		{
			if (string.IsNullOrWhiteSpace(path))
				throw new ArgumentException("Directory is required.", parameterName);
			return Path.GetFullPath(path);
		}

		private static void ValidateOptions(AdvisorWorkerLaunchOptions options)
		{
			if (options.Port < 0 || options.Port > 65535)
				throw new ArgumentOutOfRangeException(nameof(options.Port));
			if (options.StartupTimeout <= TimeSpan.Zero || options.StopTimeout < TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(options), "Worker lifecycle timeouts are invalid.");
			if (!Enum.IsDefined(typeof(AdvisorWorkerBackendMode), options.BackendMode))
				throw new ArgumentOutOfRangeException(nameof(options), "Worker backend mode is invalid.");
			ValidateAdditionalArguments(options.AdditionalArguments);
		}

		private static void ValidateAdditionalArguments(string arguments)
		{
			if (string.IsNullOrWhiteSpace(arguments))
				return;
			var normalized = arguments.Replace("\"", "");
			var tokens = normalized.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			var reservedOptions = new[]
			{
				"--host",
				"--port",
				"--data-dir",
				"--session-token",
				"--token",
				"--bind",
				"--listen",
				"--address",
				"--config",
				"--config-file"
			};
			foreach (var token in tokens)
			{
				var option = token;
				var equalsIndex = option.IndexOf('=');
				if (equalsIndex >= 0)
					option = option.Substring(0, equalsIndex);
				if (reservedOptions.Any(
					reserved => string.Equals(option, reserved, StringComparison.OrdinalIgnoreCase)))
				{
					throw new ArgumentException(
						"Additional worker arguments cannot override protected launch option " +
						option + ".",
						nameof(AdvisorWorkerLaunchOptions.AdditionalArguments));
				}
			}
		}

		private void ThrowIfDisposed()
		{
			lock (_sync)
			{
				if (_disposed)
					throw new ObjectDisposedException(nameof(AdvisorWorkerProcessManager));
			}
		}

		private enum WorkerTargetKind
		{
			Executable,
			Script,
			Module
		}

		private sealed class WorkerTarget
		{
			public WorkerTarget(
				string entryPath,
				string workingDirectory,
				WorkerTargetKind kind,
				string moduleName,
				AdvisorWorkerBackendKind backend)
			{
				EntryPath = entryPath;
				WorkingDirectory = workingDirectory;
				Kind = kind;
				ModuleName = moduleName;
				Backend = backend;
			}

			public string EntryPath { get; }
			public string WorkingDirectory { get; }
			public WorkerTargetKind Kind { get; }
			public string ModuleName { get; }
			public AdvisorWorkerBackendKind Backend { get; }
		}
	}
}
