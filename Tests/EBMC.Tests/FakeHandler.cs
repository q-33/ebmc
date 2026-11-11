using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EBMC.Tests
{
	/// <summary>
	/// A scripted HttpMessageHandler: records each request (including the buffered body) and answers with the next
	/// scripted response or exception. When the script runs out, the last step is repeated.
	/// </summary>
	public class FakeHandler : HttpMessageHandler
	{
		private readonly Queue<Step> steps = new Queue<Step>();

		private Step lastStep;

		public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

		public FakeHandler Respond(HttpStatusCode status, string body = null, TimeSpan? delay = null, string contentType = "application/json")
		{
			steps.Enqueue(new Step { Status = status, Body = body, Delay = delay, ContentType = contentType });
			return this;
		}

		public FakeHandler Throw(Exception exception, TimeSpan? delay = null)
		{
			steps.Enqueue(new Step { Exception = exception, Delay = delay });
			return this;
		}

		/// <summary>
		/// Called after the request body has been read and before the response is returned.
		/// </summary>
		public Func<RecordedRequest, CancellationToken, Task> BeforeRespond { get; set; }

		/// <summary>
		/// When set, the request body is read like a slow network: each write of the body waits this long first.
		/// </summary>
		public TimeSpan? BodyWriteDelay { get; set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var record = new RecordedRequest
			{
				Method = request.Method.Method,
				Uri = request.RequestUri,
				Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
				TransferEncodingChunked = request.Headers.TransferEncodingChunked == true,
			};

			if (request.Content != null)
			{
				record.ContentLength = request.Content.Headers.ContentLength;
				record.ContentType = request.Content.Headers.ContentType?.ToString();
				if (BodyWriteDelay.HasValue)
				{
					var slow = new SlowStream(BodyWriteDelay.Value);
					await request.Content.CopyToAsync(slow, cancellationToken);
					record.Body = slow.ToArray();
				}
				else
				{
					record.Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
				}
			}

			Requests.Add(record);

			Step step = steps.Count > 0 ? steps.Dequeue() : lastStep;
			lastStep = step ?? throw new InvalidOperationException("No scripted response for " + request.RequestUri);

			if (BeforeRespond != null)
			{
				await BeforeRespond(record, cancellationToken);
			}

			if (step.Delay.HasValue)
			{
				await Task.Delay(step.Delay.Value, cancellationToken);
			}

			if (step.Exception != null)
			{
				throw step.Exception;
			}

			var response = new HttpResponseMessage(step.Status) { RequestMessage = request };
			response.Content = step.Body == null
				? new ByteArrayContent(Array.Empty<byte>())
				: new StringContent(step.Body, Encoding.UTF8, step.ContentType);
			return response;
		}

		private class SlowStream : MemoryStream
		{
			private readonly TimeSpan delay;

			public SlowStream(TimeSpan delay) => this.delay = delay;

			public override void Write(byte[] buffer, int offset, int count)
			{
				Thread.Sleep(delay);
				base.Write(buffer, offset, count);
			}

			public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			{
				await Task.Delay(delay, cancellationToken);
				await base.WriteAsync(buffer, offset, count, cancellationToken);
			}

			public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
			{
				await Task.Delay(delay, cancellationToken);
				await base.WriteAsync(buffer, cancellationToken);
			}
		}

		private class Step
		{
			public HttpStatusCode Status { get; set; }

			public string Body { get; set; }

			public string ContentType { get; set; }

			public TimeSpan? Delay { get; set; }

			public Exception Exception { get; set; }
		}
	}

	public class RecordedRequest
	{
		public string Method { get; set; }

		public Uri Uri { get; set; }

		public Dictionary<string, string> Headers { get; set; }

		public bool TransferEncodingChunked { get; set; }

		public long? ContentLength { get; set; }

		public string ContentType { get; set; }

		public byte[] Body { get; set; }

		public string BodyText => Body == null ? null : Encoding.UTF8.GetString(Body);
	}
}
