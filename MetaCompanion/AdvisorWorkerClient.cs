using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanion
{
	public interface IAdvisorWorkerClient
	{
		Uri BaseUri { get; }
		Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
		Task<AdvisorSolveResponse> SolveAsync(AdvisorSolveRequest request, CancellationToken cancellationToken);
		Task<bool> CancelAsync(AdvisorCancelRequest request, CancellationToken cancellationToken);
		Task<AdvisorObservationResult> ObserveAsync(AdvisorObservation observation, CancellationToken cancellationToken);
	}

	public sealed class AdvisorWorkerClientOptions
	{
		public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(1);
		public TimeSpan SolveTimeout { get; set; } = TimeSpan.FromSeconds(12);
		public TimeSpan CancelTimeout { get; set; } = TimeSpan.FromSeconds(2);
		public TimeSpan ObserveTimeout { get; set; } = TimeSpan.FromSeconds(2);
		public TimeSpan BehaviorTimeout { get; set; } = TimeSpan.FromSeconds(3);
		public int MaximumResponseCharacters { get; set; } = 4 * 1024 * 1024;
	}

	public class AdvisorWorkerException : Exception
	{
		public AdvisorWorkerException(string message, Exception innerException = null)
			: base(message, innerException)
		{
		}

		public HttpStatusCode? StatusCode { get; internal set; }
		/// <summary>Stable worker error code, never a raw response body or stack trace.</summary>
		public string ErrorCode { get; internal set; } = "";
	}

	public sealed class AdvisorWorkerProtocolException : AdvisorWorkerException
	{
		public AdvisorWorkerProtocolException(string message, Exception innerException = null)
			: base(message, innerException)
		{
		}
	}

	/// <summary>
	/// Authenticated loopback-only worker client using .NET Framework 4.7.2 BCL APIs.
	/// All network operations are asynchronous and cancellation aborts the underlying request.
	/// </summary>
	public sealed class AdvisorWorkerClient : IAdvisorWorkerClient, IAdvisorBehaviorClient,
		IAdvisorResultClient
	{
		private readonly string _sessionToken;
		private readonly AdvisorWorkerClientOptions _options;

		public AdvisorWorkerClient(Uri baseUri, string sessionToken, AdvisorWorkerClientOptions options = null)
		{
			ValidateBaseUri(baseUri);
			if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length < 16)
				throw new ArgumentException("The worker session token must contain at least 16 characters.", nameof(sessionToken));
			BaseUri = NormalizeBaseUri(baseUri);
			_sessionToken = sessionToken;
			_options = options ?? new AdvisorWorkerClientOptions();
			ValidateOptions(_options);
		}

		public Uri BaseUri { get; }

		public async Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
		{
			var json = await SendAsync("GET", "v1/health", null, _options.HealthTimeout, cancellationToken)
				.ConfigureAwait(false);
			try
			{
				var health = AdvisorWireProtocol.DeserializeHealth(json);
				ValidateApiVersion(health.ApiVersion);
				return health;
			}
			catch (AdvisorWorkerException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException("Invalid health response from advisor worker.", ex);
			}
		}

		public async Task<AdvisorSolveResponse> SolveAsync(
			AdvisorSolveRequest request, CancellationToken cancellationToken)
		{
			if (request == null)
				throw new ArgumentNullException(nameof(request));
			var requestId = request.RequestId;
			var body = AdvisorWireProtocol.SerializeSolveRequest(request);
			string json;
			try
			{
				json = await SendAsync(
					"POST", "v1/solve", body, _options.SolveTimeout, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				await TryCancelSolveAsync(requestId).ConfigureAwait(false);
				throw;
			}
			catch (OperationCanceledException)
			{
				await TryCancelSolveAsync(requestId).ConfigureAwait(false);
				throw;
			}
			AdvisorSolveResponse response;
			try
			{
				response = AdvisorWireProtocol.DeserializeSolveResponse(json, request);
				ValidateApiVersion(response.ApiVersion);
			}
			catch (AdvisorWorkerException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException("Invalid solve response from advisor worker.", ex);
			}

			if (!string.IsNullOrWhiteSpace(response.RequestId) &&
				!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
			{
				throw new AdvisorWorkerProtocolException("Worker response request_id did not match the request.");
			}
			if (string.IsNullOrWhiteSpace(response.RequestId))
				response.RequestId = requestId;
			return response;
		}

		private async Task TryCancelSolveAsync(string requestId)
		{
			if (string.IsNullOrWhiteSpace(requestId))
				return;
			try
			{
				await CancelAsync(new AdvisorCancelRequest
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = requestId,
					StateId = ""
				}, CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				// This notification is deliberately best effort. The original timeout or
				// caller cancellation remains the only failure visible to the caller/UI.
			}
		}

		public async Task<bool> CancelAsync(
			AdvisorCancelRequest request, CancellationToken cancellationToken)
		{
			if (request == null)
				throw new ArgumentNullException(nameof(request));
			var body = AdvisorWireProtocol.SerializeCancelRequest(request);
			var json = await SendAsync("POST", "v1/cancel", body, _options.CancelTimeout, cancellationToken)
				.ConfigureAwait(false);
			try
			{
				var root = AdvisorWireProtocol.ParseObject(json);
				object statusValue;
				var status = root.TryGetValue("status", out statusValue)
					? Convert.ToString(statusValue, CultureInfo.InvariantCulture) ?? ""
					: "";
				return string.Equals(status, "cancellation_requested", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException("Invalid cancel response from advisor worker.", ex);
			}
		}

		public async Task<AdvisorObservationResult> ObserveAsync(
			AdvisorObservation observation, CancellationToken cancellationToken)
		{
			if (observation == null)
				throw new ArgumentNullException(nameof(observation));
			var body = AdvisorWireProtocol.SerializeObservation(observation);
			var json = await SendAsync("POST", "v1/observe", body, _options.ObserveTimeout, cancellationToken)
				.ConfigureAwait(false);
			try
			{
				var result = AdvisorWireProtocol.DeserializeObservationResult(json);
				ValidateApiVersion(result.ApiVersion);
				return result;
			}
			catch (AdvisorWorkerException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException("Invalid observation response from advisor worker.", ex);
			}
		}

		async Task<AdvisorBehaviorAppendResult> IAdvisorBehaviorClient.AppendBehaviorJsonAsync(
			string json,
			CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(json))
				throw new ArgumentException("Behavior payload is required.", nameof(json));
			if (json.Length > AdvisorBehaviorOutbox.MaximumRecordBytes)
				throw new AdvisorWorkerProtocolException("Behavior payload exceeded the configured size limit.");
			var responseJson = await SendAsync(
				"POST",
				"v1/behavior",
				json,
				_options.BehaviorTimeout,
				cancellationToken).ConfigureAwait(false);
			try
			{
				return AdvisorWireProtocol.DeserializeBehaviorAppendResult(responseJson);
			}
			catch (AdvisorWorkerException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException(
					"Invalid behavior response from advisor worker.", ex);
			}
		}

		async Task<AdvisorResultAppendResult> IAdvisorResultClient.AppendResultJsonAsync(
			string json,
			CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(json))
				throw new ArgumentException("Result payload is required.", nameof(json));
			if (Encoding.UTF8.GetByteCount(json) > AdvisorResultOutbox.MaximumRecordBytes)
				throw new AdvisorWorkerProtocolException("Result payload exceeded the configured size limit.");
			var responseJson = await SendAsync(
				"POST",
				"v1/observe",
				json,
				_options.ObserveTimeout,
				cancellationToken).ConfigureAwait(false);
			try
			{
				return AdvisorWireProtocol.DeserializeResultAppendResult(responseJson);
			}
			catch (AdvisorWorkerException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new AdvisorWorkerProtocolException(
					"Invalid terminal result response from advisor worker.", ex);
			}
		}

		private async Task<string> SendAsync(
			string method, string relativePath, string body, TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			var endpoint = new Uri(BaseUri, relativePath);
			if (!endpoint.IsLoopback)
				throw new AdvisorWorkerProtocolException("Refusing to send an advisor token to a non-loopback URI.");

			var request = (HttpWebRequest)WebRequest.Create(endpoint);
			request.Method = method;
			request.Proxy = null;
			request.AllowAutoRedirect = false;
			request.KeepAlive = true;
			request.Accept = "application/json";
			request.ContentType = "application/json; charset=utf-8";
			request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _sessionToken;
			request.Headers[AdvisorProtocol.TokenHeaderName] = _sessionToken;
			request.Headers[HttpRequestHeader.CacheControl] = "no-store";
			request.Timeout = ToTimeoutMilliseconds(timeout);
			request.ReadWriteTimeout = ToTimeoutMilliseconds(timeout);

			using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
			{
				timeoutSource.CancelAfter(timeout);
				using (timeoutSource.Token.Register(request.Abort))
				{
					try
					{
						if (body != null)
						{
							var bytes = Encoding.UTF8.GetBytes(body);
							request.ContentLength = bytes.Length;
							using (var stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
								await stream.WriteAsync(bytes, 0, bytes.Length, timeoutSource.Token).ConfigureAwait(false);
						}
						using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
							return await ReadBodyAsync(response, timeoutSource.Token).ConfigureAwait(false);
					}
					catch (WebException ex)
					{
						if (cancellationToken.IsCancellationRequested)
							throw new OperationCanceledException(cancellationToken);
						if (timeoutSource.IsCancellationRequested ||
							ex.Status == WebExceptionStatus.Timeout)
							throw new TimeoutException("Advisor worker request timed out after " + timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " seconds.", ex);

						var errorResponse = ex.Response as HttpWebResponse;
						if (errorResponse != null)
						{
							using (errorResponse)
							{
								var errorBody = await ReadBodyAsync(errorResponse, CancellationToken.None).ConfigureAwait(false);
								var detail = AdvisorWireProtocol.TryReadErrorMessage(errorBody);
								var errorCode = AdvisorWireProtocol.TryReadErrorCode(errorBody);
								var exception = new AdvisorWorkerException(
									"Advisor worker returned HTTP " + (int)errorResponse.StatusCode +
									(string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail), ex)
								{
									StatusCode = errorResponse.StatusCode,
									ErrorCode = errorCode
								};
								throw exception;
							}
						}
						throw new AdvisorWorkerException("Unable to reach the local advisor worker: " + ex.Message, ex);
					}
					catch (OperationCanceledException)
					{
						if (cancellationToken.IsCancellationRequested)
							throw;
						throw new TimeoutException("Advisor worker request timed out.");
					}
					catch (Exception ex)
					{
						// Aborting an HttpWebRequest while a response stream is being read can
						// surface as IOException/ObjectDisposedException rather than WebException.
						// Preserve the public cancellation/timeout contract in either case.
						if (cancellationToken.IsCancellationRequested)
							throw new OperationCanceledException(
								"Advisor worker request was cancelled.", ex, cancellationToken);
						if (timeoutSource.IsCancellationRequested)
							throw new TimeoutException("Advisor worker request timed out.", ex);
						throw;
					}
				}
			}
		}

		private async Task<string> ReadBodyAsync(HttpWebResponse response, CancellationToken cancellationToken)
		{
			if (response.ContentLength > _options.MaximumResponseCharacters * 4L)
				throw new AdvisorWorkerProtocolException("Advisor worker response was larger than the configured limit.");
			var stream = response.GetResponseStream();
			if (stream == null)
				return "";
			using (stream)
			using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
			{
				var result = new StringBuilder();
				var buffer = new char[4096];
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
					if (read == 0)
						break;
					result.Append(buffer, 0, read);
					if (result.Length > _options.MaximumResponseCharacters)
						throw new AdvisorWorkerProtocolException("Advisor worker response exceeded the configured character limit.");
				}
				return result.ToString();
			}
		}

		private static void ValidateBaseUri(Uri baseUri)
		{
			if (baseUri == null)
				throw new ArgumentNullException(nameof(baseUri));
			if (!baseUri.IsAbsoluteUri || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException("Advisor worker URI must be an absolute http URI.", nameof(baseUri));
			if (!baseUri.IsLoopback)
				throw new ArgumentException("Advisor worker URI must resolve to loopback.", nameof(baseUri));
			if (!string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
				throw new ArgumentException("Advisor worker URI may not contain credentials, query, or fragment.", nameof(baseUri));
			if (baseUri.Port <= 0 || baseUri.Port > 65535)
				throw new ArgumentOutOfRangeException(nameof(baseUri), "Advisor worker URI must contain a valid port.");
		}

		private static Uri NormalizeBaseUri(Uri baseUri)
		{
			var builder = new UriBuilder(baseUri)
			{
				Path = "/",
				Query = "",
				Fragment = ""
			};
			return builder.Uri;
		}

		private static void ValidateOptions(AdvisorWorkerClientOptions options)
		{
			if (options.HealthTimeout <= TimeSpan.Zero || options.SolveTimeout <= TimeSpan.Zero ||
				options.CancelTimeout <= TimeSpan.Zero || options.ObserveTimeout <= TimeSpan.Zero ||
				options.BehaviorTimeout <= TimeSpan.Zero)
				throw new ArgumentOutOfRangeException(nameof(options), "Worker timeouts must be positive.");
			if (options.MaximumResponseCharacters < 1024)
				throw new ArgumentOutOfRangeException(nameof(options), "Maximum response size is too small.");
		}

		private static int ToTimeoutMilliseconds(TimeSpan timeout)
		{
			return (int)Math.Max(1, Math.Min(int.MaxValue, timeout.TotalMilliseconds));
		}

		private static void ValidateApiVersion(string version)
		{
			if (!string.Equals(version, AdvisorProtocol.ApiVersion, StringComparison.Ordinal))
			{
				throw new AdvisorWorkerProtocolException(
					"Advisor worker API version " + version + " is incompatible with " + AdvisorProtocol.ApiVersion + ".");
			}
		}
	}
}
