//-----------------------------------------------------------------------
// <copyright file="ShardedDocumentSession.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Raven.Abstractions.Data;
#if !NET_3_5
using Raven.Abstractions.Extensions;
using Raven.Client.Connection.Async;
using Raven.Client.Document.Batches;
#endif
using Raven.Client.Connection;
using Raven.Client.Document;
using Raven.Client.Document.SessionOperations;
using Raven.Client.Indexes;
using Raven.Client.Linq;
using Raven.Client.Shard.ShardStrategy;
using Raven.Client.Shard.ShardStrategy.ShardResolution;
using System;
using Raven.Client.Util;
using Raven.Json.Linq;

namespace Raven.Client.Shard
{
#if !SILVERLIGHT
	/// <summary>
	/// Implements Unit of Work for accessing a set of sharded RavenDB servers
	/// </summary>
	public class ShardedDocumentSession : InMemoryDocumentSessionOperations, IDocumentSessionImpl, ITransactionalDocumentSession,
		ISyncAdvancedSessionOperation, IDocumentQueryGenerator
#if !NET_3_5
		, ILazySessionOperations, IEagerSessionOperations
#endif
	{
#if !NET_3_5
		//private readonly IDictionary<string, IAsyncDatabaseCommands> asyncShardDbCommands;
		private readonly List<ILazyOperation> pendingLazyOperations = new List<ILazyOperation>();
		private readonly Dictionary<ILazyOperation, Action<object>> onEvaluateLazy = new Dictionary<ILazyOperation, Action<object>>();
#endif
		private readonly IShardStrategy shardStrategy;
		private readonly IDictionary<string, IDatabaseCommands> shardDbCommands;
		private readonly ShardedDocumentStore documentStore;

		/// <summary>
		/// Initializes a new instance of the <see cref="ShardedDocumentSession"/> class.
		/// </summary>
		/// <param name="shardStrategy">The shard strategy.</param>
		/// <param name="shardDbCommands">The shard IDatabaseCommands.</param>
		/// <param name="id"></param>
		/// <param name="documentStore"></param>
		/// <param name="listeners"></param>
		public ShardedDocumentSession(ShardedDocumentStore documentStore, DocumentSessionListeners listeners, Guid id,
			IShardStrategy shardStrategy, IDictionary<string, IDatabaseCommands> shardDbCommands
//#if !NET_3_5
//, IDictionary<string, IAsyncDatabaseCommands> asyncDatabaseCommands
//#endif
			)
			: base(documentStore, listeners, id)
		{
			this.shardStrategy = shardStrategy;
			this.shardDbCommands = shardDbCommands;
			this.documentStore = documentStore;
//#if !NET_3_5
//            this.asyncShardDbCommands = asyncDatabaseCommands;
//#endif
		}

		private IList<IDatabaseCommands> GetShardsToOperateOn(ShardRequestData resultionData)
		{
			var shardIds = shardStrategy.ShardResolutionStrategy.PotentialShardsFor(resultionData);
			
			if (shardIds != null)
				return shardDbCommands.Where(cmd => shardIds.Contains(cmd.Key)).Select(x => x.Value).ToList();
			
			return shardDbCommands.Values.ToList();
		}

		protected override JsonDocument GetJsonDocument(string documentKey)
		{
			var dbCommands = GetShardsToOperateOn(new ShardRequestData
			{
				EntityType = typeof(object),
				Key = documentKey
			});

			foreach (var dbCmd in dbCommands)
			{
				var jsonDocument = dbCmd.Get(documentKey);
				if (jsonDocument != null)
					return jsonDocument;
			}

			throw new InvalidOperationException("Document '" + documentKey + "' no longer exists and was probably deleted");
		}

		public override void Commit(Guid txId)
		{
			IncrementRequestCount();
			foreach (var databaseCommands in shardDbCommands.Values)
			{
				databaseCommands.Commit(txId);
			}
			ClearEnlistment();
		}

		public override void Rollback(Guid txId)
		{
			IncrementRequestCount();
			foreach (var databaseCommands in shardDbCommands.Values)
			{
				databaseCommands.Rollback(txId);
			}
			ClearEnlistment();
		}

		public override byte[] PromoteTransaction(Guid fromTxId)
		{
			throw new NotImplementedException();
		}

		public void StoreRecoveryInformation(Guid resourceManagerId, Guid txId, byte[] recoveryInformation)
		{
			IncrementRequestCount();
			foreach (var databaseCommands in shardDbCommands.Values)
			{
				databaseCommands.StoreRecoveryInformation(resourceManagerId, txId, recoveryInformation);
			}
		}

		public ISyncAdvancedSessionOperation Advanced
		{
			get { return this; }
		}

#if !NET_3_5
		public Lazy<TResult[]> Load<TResult>(IEnumerable<string> ids, Action<TResult[]> onEval)
		{
			throw new NotImplementedException();
			//return LazyLoadInternal(ids.ToArray(), new string[0], onEval);
		}

		Lazy<TResult> ILazySessionOperations.Load<TResult>(string id)
		{
			throw new NotImplementedException();
		}

		public Lazy<TResult> Load<TResult>(string id, Action<TResult> onEval)
		{
			//var lazyLoadOperation = new LazyLoadOperation<TResult>(id, new LoadOperation(this, DatabaseCommands.DisableAllCaching, id));
			//return AddLazyOperation(lazyLoadOperation, onEval);
			throw new NotImplementedException();
		}

		Lazy<TResult> ILazySessionOperations.Load<TResult>(ValueType id)
		{
			throw new NotImplementedException();
		}

		public Lazy<TResult> Load<TResult>(ValueType id, Action<TResult> onEval)
		{
			throw new NotImplementedException();
		}
#endif
		public T Load<T>(string id)
		{
			object existingEntity;
			if (entitiesByKey.TryGetValue(id, out existingEntity))
			{
				return (T)existingEntity;
			}

			IncrementRequestCount();
			var dbCommands = GetShardsToOperateOn(new ShardRequestData
			                                      	{
			                                      		EntityType = typeof (T),
			                                      		Key = id
			                                      	});
			foreach (var dbCmd in dbCommands)
			{
				var loadOperation = new LoadOperation(this, dbCmd.DisableAllCaching, id);
				bool retry;
				do
				{
					loadOperation.LogOperation();
					using (loadOperation.EnterLoadContext())
					{
						retry = loadOperation.SetResult(dbCmd.Get(id));
					}
				} while (retry);
				var result = loadOperation.Complete<T>();
				
				if (!Equals(result, default(T)))
					return result;
			}

			return default(T);
		}

#if !NET_3_5
		ILazyLoaderWithInclude<T> ILazySessionOperations.Include<T>(Expression<Func<T, object>> path)
		{
			throw new NotImplementedException();
		}
		Lazy<TResult[]> ILazySessionOperations.Load<TResult>(params string[] ids)
		{
			throw new NotImplementedException();
		}
#endif

		public T[] Load<T>(params string[] ids)
		{
			return LoadInternal<T>(ids, null);
		}

		public T[] Load<T>(IEnumerable<string> ids)
		{
			return ((IDocumentSessionImpl)this).LoadInternal<T>(ids.ToArray(), null);
		}

		public T Load<T>(ValueType id)
		{
			var documentKey = Conventions.FindFullDocumentKeyFromNonStringIdentifier(id, typeof(T), false);
			return Load<T>(documentKey);
		}

		public IRavenQueryable<T> Query<T>(string indexName)
		{
			throw new NotImplementedException();
		}

		/// <summary>
		/// Query RavenDB dynamically using LINQ
		/// </summary>
		/// <typeparam name="T">The result of the query</typeparam>
		public IRavenQueryable<T> Query<T>()
		{
			var indexName = "dynamic";
			if (typeof(T).IsEntityType())
			{
				indexName += "/" + Conventions.GetTypeTagName(typeof(T));
			}
			return Query<T>(indexName);
		}

		public IRavenQueryable<T> Query<T, TIndexCreator>() where TIndexCreator : AbstractIndexCreationTask, new()
		{
			throw new NotImplementedException();
		}

		public ILoaderWithInclude<object> Include(string path)
		{
			return new MultiLoaderWithInclude<object>(this).Include(path);
		}
#if !NET_3_5
		ILazyLoaderWithInclude<object> ILazySessionOperations.Include(string path)
		{
			throw new NotImplementedException();
		}
#endif
		public ILoaderWithInclude<T> Include<T>(Expression<Func<T, object>> path)
		{
			return new MultiLoaderWithInclude<T>(this).Include(path);
		}

		public override void Defer(params Abstractions.Commands.ICommandData[] commands)
		{
			throw new NotSupportedException("You cannot defer commands using the sharded document session, because we don't know which shard to use");
		}

		/// <summary>
		/// Saves all the changes to the Raven server.
		/// </summary>
		public void SaveChanges()
		{
			using (EntitiesToJsonCachingScope())
			{
				var data = PrepareForSaveChanges();
				if (data.Commands.Count == 0)
					return; // nothing to do here

				IncrementRequestCount();
				LogBatch(data);
				
				// split by shards
				var saveChangesPerShard = new Dictionary<string, SaveChangesData>();
				for (int index = 0; index < data.Entities.Count; index++)
				{
					var entity = data.Entities[index];
					var metadata = GetMetadataFor(entity);
					var shardId = metadata.Value<string>(Constants.RavenShardId);

					if (shardId == null)
					{
						shardId = shardStrategy.ShardResolutionStrategy.GenerateShardIdFor(entity);
						metadata[Constants.RavenShardId] = shardId;
					}

					var shardSaveChangesData = saveChangesPerShard.GetOrAdd(shardId);
					shardSaveChangesData.Entities.Add(entity);
					shardSaveChangesData.Commands.Add(data.Commands[index]);
				}

				// execute on all shards
				foreach (var shardAndObjects in saveChangesPerShard)
				{
					var shardId = shardAndObjects.Key;

					IDatabaseCommands databaseCommands;
					if (shardDbCommands.TryGetValue(shardId, out databaseCommands) == false)
						throw new InvalidOperationException(string.Format("ShardedDocumentStore cannot found a DatabaseCommands for shard id '{0}'.", shardId));

					var results = databaseCommands.Batch(shardAndObjects.Value.Commands);
					UpdateBatchResults(results, shardAndObjects.Value);
				}
			}
		}

#if !NET_3_5
		IAsyncDocumentQuery<T> IDocumentQueryGenerator.AsyncQuery<T>(string indexName)
		{
			throw new NotSupportedException("Shared document store doesn't support async operations");
		}
#endif

		IDocumentQuery<T> IDocumentQueryGenerator.Query<T>(string indexName)
		{
			throw new NotImplementedException();
		}

		public void Refresh<T>(T entity)
		{
			DocumentMetadata value;
			if (entitiesAndMetadata.TryGetValue(entity, out value) == false)
				throw new InvalidOperationException("Cannot refresh a transient instance");
			IncrementRequestCount();


			var dbCommands = GetShardsToOperateOn(new ShardRequestData
			{
				EntityType = typeof(T),
				Key = value.Key
			});
			foreach (var dbCmd in dbCommands)
			{
				var jsonDocument = dbCmd.Get(value.Key);
				if (jsonDocument == null)
					continue;

				value.Metadata = jsonDocument.Metadata;
				value.OriginalMetadata = (RavenJObject)jsonDocument.Metadata.CloneToken();
				value.ETag = jsonDocument.Etag;
				value.OriginalValue = jsonDocument.DataAsJson;
				var newEntity = ConvertToEntity<T>(value.Key, jsonDocument.DataAsJson, jsonDocument.Metadata);
				foreach (var property in entity.GetType().GetProperties())
				{
					if (!property.CanWrite || !property.CanRead || property.GetIndexParameters().Length != 0)
						continue;
					property.SetValue(entity, property.GetValue(newEntity, null), null);
				}
			}

			throw new InvalidOperationException("Document '" + value.Key + "' no longer exists and was probably deleted");
		}

		IDatabaseCommands ISyncAdvancedSessionOperation.DatabaseCommands
		{
			get { throw new NotSupportedException("Not supported in a sharded session"); }
		}

#if !NET_3_5
		/// <summary>
		/// Gets the async database commands.
		/// </summary>
		/// <value>The async database commands.</value>
		public IAsyncDatabaseCommands AsyncDatabaseCommands
		{
			get { throw new NotSupportedException("Not supported in a sharded session"); }
		}

		/// <summary>
		/// Access the lazy operations
		/// </summary>
		public ILazySessionOperations Lazily
		{
			get { return this; }
		}

		/// <summary>
		/// Access the eager operations
		/// </summary>
		public IEagerSessionOperations Eagerly
		{
			get { return this; }
		}
#endif

		public IDocumentQuery<T> LuceneQuery<T, TIndexCreator>() where TIndexCreator : AbstractIndexCreationTask, new()
		{
			throw new NotImplementedException();
		}

		public IDocumentQuery<T> LuceneQuery<T>(string indexName)
		{
#if !NET_3_5
			return new ShardedDocumentQuery<T>(this, SelectShardsByQuery, shardStrategy, indexName, null, listeners.QueryListeners);
#else
			return new ShardedDocumentQuery<T>(this, SelectShardsByQuery, null, indexName, null, listeners.QueryListeners);
#endif
		}

		protected IList<IDatabaseCommands> SelectShardsByQuery(Type type, IndexQuery query)
		{
			return GetShardsToOperateOn(new ShardRequestData
			{
				EntityType = type,
				Query = query
			});
		}

		public IDocumentQuery<T> LuceneQuery<T>()
		{
			throw new NotImplementedException();
		}

		public string GetDocumentUrl(object entity)
		{
			throw new NotImplementedException();
		}

		public T[] LoadInternal<T>(string[] ids, string[] includes)
		{
			if (ids.Length == 0)
				return new T[0];

			IncrementRequestCount();
			var idsAndShards = ids.Select(id => new
			                                    	{
			                                    		id,
			                                    		shards = GetShardsToOperateOn(new ShardRequestData
			                                    		                              	{
			                                    		                              		EntityType = typeof (T),
			                                    		                              		Key = id
			                                    		                              	})
			                                    	})
				.GroupBy(x => x.shards, new DbCmdsListComparer());

			var results = new T[ids.Length];
			foreach (var shard in idsAndShards)
			{
				var currentShardIds = shard.Select(x => x.id).ToArray();
				var multiLoadOperations = shardStrategy.ShardAccessStrategy.Apply(shard.Key, (dbCmd, i) =>
				{
					var multiLoadOperation = new MultiLoadOperation(this, dbCmd.DisableAllCaching, currentShardIds);
					MultiLoadResult multiLoadResult;
					do
					{
						multiLoadOperation.LogOperation();
						using (multiLoadOperation.EnterMultiLoadContext())
						{
							multiLoadResult = dbCmd.Get(currentShardIds, includes);
						}
					} while (multiLoadOperation.SetResult(multiLoadResult));
					return multiLoadOperation;
				});
				foreach (var multiLoadOperation in multiLoadOperations)
				{
					var loadResults = multiLoadOperation.Complete<T>();
					for (int i = 0; i < loadResults.Length; i++)
					{
						if(ReferenceEquals(loadResults[i], null))
							continue;
						results[Array.IndexOf(ids, currentShardIds[i])] = loadResults[i];
					}
				}
			}
			return results;
		}

		public T[] LoadInternal<T>(string[] ids)
		{
			throw new NotImplementedException();
		}

		internal class DbCmdsListComparer : IEqualityComparer<IList<IDatabaseCommands>>
		{
			public bool Equals(IList<IDatabaseCommands> x, IList<IDatabaseCommands> y)
			{
				if (x.Count != y.Count)
					return false;

				return !x.Where((t, i) => t != y[i]).Any();
			}

			public int GetHashCode(IList<IDatabaseCommands> obj)
			{
				return obj.Aggregate(obj.Count, (current, item) => (current * 397) ^ item.GetHashCode());
			}

		}

		public void ExecuteAllPendingLazyOperations()
		{
			throw new NotImplementedException();
		}
	}

#endif
}