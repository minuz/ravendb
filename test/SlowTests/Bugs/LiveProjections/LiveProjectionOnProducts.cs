using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FastTests;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Xunit;

namespace SlowTests.Bugs.LiveProjections
{
    public class LiveProjectionOnProducts : RavenTestBase
    {
        [Fact]
        public void ComplexLiveProjection()
        {
            using (var documentStore = GetDocumentStore())
            {
                new ProductDetailsReport_ByProductId().Execute((IDocumentStore)documentStore);

                using (var session = documentStore.OpenSession())
                {
                    var product = new Product()
                    {
                        Name = "product 1",
                        Variants = new List<ProductSku>()
                                {
                                    new ProductSku()
                                        {
                                            ArticleNumber = "v1",
                                            Name = "variant 1",
                                            Packing = "packing"
                                        },
                                    new ProductSku()
                                        {
                                            ArticleNumber = "v2",
                                            Name = "variant 2",
                                            Packing = "packing"
                                        }
                                }
                    };

                    session.Store(product);
                    session.SaveChanges();
                }
                WaitForUserToContinueTheTest(documentStore);
                using (var session = documentStore.OpenSession())
                {
                    var rep = session.Advanced.DocumentQuery<ProductDetailsReport>()
                        .WaitForNonStaleResultsAsOfNow()
                        .RawQuery(@"
declare function mapVariants(v) {
    v.Name = v.Name.toUpperCase();
    v.IsInStock = v.QuantityInWarehouse > 0;
    return v;
}
from index 'ProductDetailsReport/ByProductId' as p
select {
    Name: p.Name,
    Variants: p.Variants.map(mapVariants)
}
")
                        .ToList();

                    var first = rep.FirstOrDefault();

                    Assert.Equal(first.Name, "product 1");
                    Assert.Equal(first.Id, "products/1-A");
                    Assert.Equal(first.Variants[0].Name, "VARIANT 1");
                }
            }
        }

        private class Product
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public ICollection<ProductSku> Variants { get; set; }
        }

        private class ProductSku
        {
            public string Id { get; set; }

            public string ArticleNumber { get; set; }

            public string Name { get; set; }

            public string Packing { get; set; }

            public int QuantityInWarehouse { get; set; }
        }

        private class ProductDetailsReport
        {
            public string Id { get; set; }

            public string Name { get; set; }

            public IList<ProductVariant> Variants { get; set; }
        }

        private class ProductVariant
        {
            public string ArticleNumber { get; set; }

            public string Name { get; set; }

            public string Packing { get; set; }

            public bool IsInStock { get; set; }
        }

        private class ProductDetailsReport_ByProductId : AbstractIndexCreationTask<Product, ProductDetailsReport>
        {
            public ProductDetailsReport_ByProductId()
            {
                Map = products => from product in products
                                  select new
                                  {
                                      ProductId = product.Id,
                                  };
            }
        }
    }
}
