﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Connection;
using Raven.Abstractions.Counters;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Json;
using Raven.Abstractions.Util;
using Raven.Client.Connection;
using Raven.Imports.Newtonsoft.Json.Bson;
using Raven.Json.Linq;
using Raven.Client.Extensions;

namespace Raven.Client.Counters.Actions
{
	public class CountersBatchOperation : CountersActionsBase, ICountersBatchOperation
	{
		private Task _batchOperationTask;
		private readonly TaskCompletionSource<bool> _batchOperationTcs; 
		private readonly BlockingCollection<CounterChange> _changesQueue;
		private readonly CancellationTokenSource _cts;
		private bool disposed;
		private readonly CountersBatchOptions _options;
		private MemoryStream _tempStream;
		private long serverOperationId;
		private readonly string _singleAuthUrl;
		private readonly AsyncManualResetEvent _streamingStarted;
		private Timer _closeAndReopenStreamingTimer;

		internal CountersBatchOperation(ICounterStore parent, string counterStorageName, CountersBatchOptions options = null)
			: base(parent, counterStorageName)
		{

			if(options != null && options.BatchSizeLimit < 1)
				throw new ArgumentException("options");

			_streamingStarted = new AsyncManualResetEvent();
			_options = options ?? new CountersBatchOptions(); //defaults do exist
			_changesQueue = new BlockingCollection<CounterChange>();			
			_cts = new CancellationTokenSource();
			_batchOperationTcs = new TaskCompletionSource<bool>();
			_singleAuthUrl = string.Format("{0}/cs/{1}/singleAuthToken", serverUrl, counterStorageName);
			_batchOperationTask = StartBatchOperation();
			disposed = false;

			_closeAndReopenStreamingTimer = new Timer(CloseAndReopenStreaming, null,
				TimeSpan.FromMilliseconds(_options.ConnectionReopenTimingInMilliseconds),
				TimeSpan.FromMilliseconds(-1)); //fire timer only once -> then rescedule - handles ConnectionReopenTimingInMilliseconds changing use-case
		}

		private void CloseAndReopenStreaming(object state)
		{
			_cts.Cancel();
			if(_batchOperationTask.Status != TaskStatus.Faulted &&
				_batchOperationTask.Status != TaskStatus.Canceled)
			_batchOperationTask.Wait();

			_batchOperationTask = StartBatchOperation();

			_closeAndReopenStreamingTimer.Dispose();
			_closeAndReopenStreamingTimer = new Timer(CloseAndReopenStreaming, null,
				TimeSpan.FromMilliseconds(_options.ConnectionReopenTimingInMilliseconds),
				TimeSpan.FromMilliseconds(-1)); //fire timer only once -> then rescedule - handles ConnectionReopenTimingInMilliseconds changing use-case
		}

		public CountersBatchOptions Options
		{
			get { return _options; }
		}

		private async Task StartBatchOperation()
		{
			_tempStream = new MemoryStream();
			using (ConnectionOptions.Expect100Continue(serverUrl))
			{
				var authToken = await GetToken().ConfigureAwait(false);
				try
				{
					authToken = await ValidateThatWeCanUseAuthenticateTokens(authToken).ConfigureAwait(false);
				}
				catch (Exception e)
				{
					throw new InvalidOperationException("Could not authenticate token for bulk insert, if you are using ravendb in IIS make sure you have Anonymous Authentication enabled in the IIS configuration", e);
				}

				var requestUriString = String.Format("{0}/batch", counterStorageUrl);
				using (var request = CreateHttpJsonRequest(requestUriString, HttpMethods.Post, true, true)
										.AddOperationHeader("Single-Use-Auth-Token", authToken))
				{
					var token = _cts.Token;
					var response = await request.ExecuteRawRequestAsync((stream, source) => Task.Factory.StartNew(() =>
					{
						_streamingStarted.Set();
						try
						{
							ContinuouslyWriteQueueToServer(stream, token);
							source.TrySetResult(null);
						}
						catch (Exception e)
						{
							source.TrySetException(e);
							_batchOperationTcs.SetException(e);
						}
					}, TaskCreationOptions.LongRunning)).ConfigureAwait(false);

					await response.AssertNotFailingResponse();

					long operationId;
					using (response)
					{
						using (var stream = await response.GetResponseStreamWithHttpDecompression().ConfigureAwait(false))
						{
							var result = RavenJToken.TryLoad(stream); 
							if (result == null) //precaution - should prevent NRE in case the crap hits the fan
								throw new ApplicationException("Invalid response from server... maybe its not json?");

							operationId = result.Value<long>("OperationId");
						}
					}
					
					if (await IsOperationCompleted(operationId).ConfigureAwait(false))
						serverOperationId = operationId;
					_batchOperationTcs.SetResult(true);
				}
			}
		}

		private async Task<bool> IsOperationCompleted(long operationId)
		{
			ErrorResponseException errorResponse;

			try
			{
				var status = await GetOperationStatusAsync(operationId);

				if (status == null) return true;

				if (status.Value<bool>("Completed"))
					return true;

				return false;
			}
			catch (ErrorResponseException e)
			{
				if (e.StatusCode != HttpStatusCode.Conflict)
					throw;

				errorResponse = e;
			}

			var conflictsDocument = RavenJObject.Load(new RavenJsonTextReader(new StringReader(errorResponse.ResponseString)));

			throw new ConcurrencyException(conflictsDocument.Value<string>("Error"));
		}

		private async Task<RavenJToken> GetOperationStatusAsync(long id)
		{
			var url = serverUrl + "/operation/status?id=" + id;
			using (var request = CreateHttpJsonRequest(url, HttpMethods.Get))
			{
				try
				{
					return await request.ReadResponseJsonAsync().ConfigureAwait(false);
				}
				catch (ErrorResponseException e)
				{
					if (e.StatusCode == HttpStatusCode.NotFound) return null;
					throw;
				}
			}
		}

		private void ContinuouslyWriteQueueToServer(Stream stream, CancellationToken token)
		{
			try
			{
				var batch = new List<CounterChange>();
				while (_changesQueue.IsCompleted == false)
				{
					batch.Clear();
					if (token.IsCancellationRequested)
					{
						FetchAllChangeQueue(batch);	
						FlushToServer(stream,batch);
						break;
					}

					TaskCompletionSource<object> tcs = null;
					try
					{
						CounterChange counterChange;
						while (_changesQueue.TryTake(out counterChange, Options.BatchReadTimeoutInMilliseconds, token))
						{
							if (counterChange.Done != null)
							{
								tcs = counterChange.Done;
								break;
							}

							batch.Add(counterChange);
							if (batch.Count >= Options.BatchSizeLimit)
								break;
						}
					}
					catch (OperationCanceledException)
					{
						if(_changesQueue.Count > 0)
							FetchAllChangeQueue(batch);
					}

					try
					{
						FlushToServer(stream, batch);
						if (tcs != null)
							tcs.TrySetResult(null);
					}
					catch (OperationCanceledException)
					{
						if (tcs != null)
							tcs.TrySetResult(null);						
					}
					catch (Exception e)
					{
						if (tcs != null)
							tcs.TrySetException(e);
					}
					
				}
			}
			finally
			{
				_tempStream.Dispose();
			}
		}

		private void FetchAllChangeQueue(List<CounterChange> batch)
		{
			CounterChange counterChange;
			while (_changesQueue.TryTake(out counterChange))
				batch.Add(counterChange);
		}

		public Task FlushAsync()
		{
			var taskCompletionSource = new TaskCompletionSource<object>();
			_changesQueue.Add(new CounterChange
			{
				Done = taskCompletionSource
			});

			return taskCompletionSource.Task.ContinueWith(t => CloseAndReopenStreaming(null));
		}

		private void FlushToServer(Stream requestStream, ICollection<CounterChange> batchItems)
		{
			if (batchItems.Count == 0)
				return;

			_tempStream.SetLength(0);
			long bytesWritten;
			WriteCollectionToBuffer(_tempStream, AggregateItems(batchItems), out bytesWritten);

			var requestBinaryWriter = new BinaryWriter(requestStream);
			requestBinaryWriter.Write((int)_tempStream.Position);

			_tempStream.WriteTo(requestStream);
			requestStream.Flush();
		}

		private ICollection<CounterChange> AggregateItems(IEnumerable<CounterChange> batchItems)
		{
			var aggregationResult = from item in batchItems
									group item by new { item.Name, item.Group }
										into g
										select new CounterChange
										{
											Name = g.Key.Name,
											Group = g.Key.Group,
											Delta = g.Sum(x => x.Delta)
										};

			var aggregateItems = aggregationResult.ToList();
			return aggregateItems;
		}

		private void WriteCollectionToBuffer(Stream targetStream, ICollection<CounterChange> items, out long bytesWritten)
		{
			using (var gzip = new GZipStream(targetStream, CompressionMode.Compress, leaveOpen: true))
			using (var stream = new CountingStream(gzip))
			{
				var binaryWriter = new BinaryWriter(stream);
				binaryWriter.Write(items.Count);
				var bsonWriter = new BsonWriter(binaryWriter)
				{
					DateTimeKindHandling = DateTimeKind.Unspecified
				};

				foreach (var doc in items.Select(RavenJObject.FromObject))
					doc.WriteTo(bsonWriter);

				bsonWriter.Flush();
				binaryWriter.Flush();
				stream.Flush();
				bytesWritten = stream.NumberOfWrittenBytes;
			}
		}

		public void ScheduleChange(string groupName, string counterName, long delta)
		{
			_streamingStarted.WaitAsync().Wait();
			_changesQueue.Add(new CounterChange
			{
				Delta = delta,
				Group = groupName,
				Name = counterName
			});
		}

		public void ScheduleIncrement(string groupName, string counterName)
		{
			ScheduleChange(groupName, counterName, 1);
		}

		public void ScheduleDecrement(string groupName, string counterName)
		{
			ScheduleChange(groupName, counterName, -1);
		}

		public virtual void Dispose()
		{
			if (!disposed)
			{
				_closeAndReopenStreamingTimer.Dispose();
				_changesQueue.CompleteAdding();

				_batchOperationTcs.Task.Wait();
				if (_batchOperationTask.Status != TaskStatus.RanToCompletion ||
					_batchOperationTask.Status != TaskStatus.Canceled)
					_cts.Cancel();

				if (serverOperationId != default(long))
				{
					while (true)
					{
						var serverSideOperationWaitingTask = IsOperationCompleted(serverOperationId);
						serverSideOperationWaitingTask.Wait();

						if (serverSideOperationWaitingTask.Result)
							break;

						Thread.Sleep(100);
					}
				}

				_tempStream.Dispose(); //precaution
				disposed = true;
			}
		}

		private async Task<string> GetToken()
		{
			// this will force the HTTP layer to authenticate, meaning that our next request won't have to
			var jsonToken = await GetAuthToken().ConfigureAwait(false);

			return jsonToken.Value<string>("Token");
		}

		private async Task<RavenJToken> GetAuthToken()
		{
			using (var request = CreateHttpJsonRequest(_singleAuthUrl, HttpMethods.Get, disableRequestCompression: true))
			{
				var response = await request.ReadResponseJsonAsync().ConfigureAwait(false);
				return response;
			}
		}

		private async Task<string> ValidateThatWeCanUseAuthenticateTokens(string token)
		{
			using (var request = CreateHttpJsonRequest(_singleAuthUrl, HttpMethods.Get, disableRequestCompression: true, disableAuthentication: true))
			{
				request.AddOperationHeader("Single-Use-Auth-Token", token);
				var result = await request.ReadResponseJsonAsync().ConfigureAwait(false);
				return result.Value<string>("Token");
			}
		}
	}
}
