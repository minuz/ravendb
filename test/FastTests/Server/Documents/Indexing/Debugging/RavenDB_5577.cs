﻿using System.Collections.Generic;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Client.Indexing;
using Raven.Server.Documents;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Debugging;
using Raven.Server.Documents.Indexes.MapReduce.Static;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Xunit;
using System.Linq;

namespace FastTests.Server.Documents.Indexing.Debugging
{
    public class RavenDB_5577 : RavenLowLevelTestBase
    {
        [Fact]
        public void Getting_identifiers_of_source_docs()
        {
            using (var database = CreateDocumentDatabase())
            {
                using (var index = MapReduceIndex.CreateNew(1, new IndexDefinition
                {
                    Name = "Users_ByCount_GroupByProduct",
                    Maps = {@"from order in docs.Orders
from line in order.Lines
select new { Product = line.Product, Count = 1, Total = line.Price }"},
                    Reduce = @"from result in mapResults
group result by result.Product into g
select new
{
    Product = g.Key,
    Count = g.Sum(x=> x.Count),
    Total = g.Sum(x=> x.Total)
}",
                }, database))
                {
                    var numberOfDocs = 100;

                    using (var context = DocumentsOperationContext.ShortTermSingleUse(database))
                    {
                        for (int i = 0; i < numberOfDocs; i++)
                        {
                            var order = CreateOrder();
                            PutOrder(database, order, context, i);
                        }

                        var firstRunStats = new IndexingRunStats();
                        var scope = new IndexingStatsScope(firstRunStats);
                        index.DoIndexingWork(scope, CancellationToken.None);

                        List<string> result;

                        IEnumerable<string> ids;
                        using (index.GetIdentifiersOfMappedDocuments(null, 0, 10, out ids))
                        {
                            result = ids.ToList();

                            Assert.Equal(10, result.Count);
                            Assert.Equal(result.Count, result.Distinct().Count());
                        }

                        using (index.GetIdentifiersOfMappedDocuments(null, 9, 1, out ids))
                        {
                            Assert.Equal(1, ids.Count());
                            Assert.Equal(result[9], ids.First());
                        }

                        using (index.GetIdentifiersOfMappedDocuments(null, 100, 10, out ids))
                        {
                            Assert.Empty(ids);
                        }

                        using (index.GetIdentifiersOfMappedDocuments("orders/3", 0, 1024, out ids))
                        {
                            result = ids.ToList();

                            Assert.Equal(11, result.Count);

                            Assert.Equal("orders/3", result[0]);
                            
                            for (var i = 0; i < 10; i++)
                            {
                                Assert.Equal($"orders/3{i}", result[i + 1]);
                            }
                        }

                        using (index.GetIdentifiersOfMappedDocuments("prod", 0, 100, out ids))
                        {
                            Assert.Empty(ids);
                        }
                    }
                }
            }
        }

        [Theory]
        [InlineData(1000, 2, 3)]
        [InlineData(10, 1, 1)] // nested section
        public void Getting_trees(int numberOfDocs, int expectedTreeDepth, int expectedPageCount)
        {
            using (var database = CreateDocumentDatabase())
            {
                using (var index = MapReduceIndex.CreateNew(1, new IndexDefinition
                {
                    Name = "Users_ByCount_GroupByProduct",
                    Maps = { @"from order in docs.Orders
from line in order.Lines
select new { Product = line.Product, Count = 1, Total = line.Price }" },
                    Reduce = @"from result in mapResults
group result by result.Product into g
select new
{
    Product = g.Key,
    Count = g.Sum(x=> x.Count),
    Total = g.Sum(x=> x.Total)
}",
                }, database))
                {
                    using (var context = DocumentsOperationContext.ShortTermSingleUse(database))
                    {
                        for (int i = 0; i < numberOfDocs; i++)
                        {
                            var order = CreateOrder();
                            PutOrder(database, order, context, i);
                        }

                        var firstRunStats = new IndexingRunStats();
                        var scope = new IndexingStatsScope(firstRunStats);
                        index.DoIndexingWork(scope, CancellationToken.None);

                        IEnumerable<ReduceTree> trees;
                        using (index.GetReduceTree("orders/1", out trees))
                        {
                            var result = trees.ToList();

                            Assert.Equal(2, result.Count);

                            for (int i = 0; i < 2; i++)
                            {
                                var tree = result[0];

                                Assert.Equal(expectedTreeDepth, tree.Depth);
                                Assert.Equal(numberOfDocs, tree.NumberOfEntries);
                                Assert.Equal(expectedPageCount, tree.PageCount);

                                var hasSource = false;

                                List<ReduceTreePage> pages;

                                if (tree.Depth > 1)
                                {
                                    // real tree

                                    Assert.True(tree.Root.Children.Any());
                                    Assert.Null(tree.Root.Entries);

                                    pages = tree.Root.Children;
                                }
                                else
                                {
                                    // nested section

                                    Assert.Null(tree.Root.Children);
                                    Assert.NotNull(tree.Root.Entries);

                                    pages = new List<ReduceTreePage>
                                    {
                                        tree.Root
                                    };
                                }
                                
                                Assert.NotNull(tree.Root.AggregationResult);
                                
                                foreach (var leafPage in pages)
                                {
                                    Assert.Null(leafPage.Children);
                                    Assert.NotNull(leafPage.AggregationResult);

                                    foreach (var entry in leafPage.Entries)
                                    {
                                        if (string.IsNullOrEmpty(entry.Source) == false)
                                            hasSource = true;

                                        Assert.NotNull(entry.Data);
                                    }
                                }

                                Assert.True(hasSource);

                                Assert.Equal(numberOfDocs, pages.Sum(x => x.Entries.Count));
                            }
                        }
                    }
                }
            }
        }

        private static DynamicJsonValue CreateOrder()
        {
            return new DynamicJsonValue
            {
                ["RefNumber"] = "123",
                ["Lines"] = new DynamicJsonArray
                {
                    new DynamicJsonValue
                    {
                        ["Product"] = "Milk",
                        ["Price"] = 10.5
                    },
                    new DynamicJsonValue
                    {
                        ["Product"] = "Bread",
                        ["Price"] = 10.7
                    }
                },
                [Constants.Metadata.Key] = new DynamicJsonValue
                {
                    [Constants.Headers.RavenEntityName] = "Orders"
                }
            };
        }

        private static void PutOrder(DocumentDatabase database, DynamicJsonValue dynamicOrder, DocumentsOperationContext context, int number)
        {
            using (var tx = context.OpenWriteTransaction())
            {
                using (var doc = CreateDocument(context, $"orders/{number}", dynamicOrder))
                {
                    database.DocumentsStorage.Put(context, $"orders/{number}", null, doc);
                }

                tx.Commit();
            }
        }
    }
}
