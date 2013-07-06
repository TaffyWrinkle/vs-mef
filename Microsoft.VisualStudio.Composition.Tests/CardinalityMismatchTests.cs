﻿namespace Microsoft.VisualStudio.Composition.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Composition;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;

    public class CardinalityMismatchTests
    {
        [Fact]
        public void MissingRequiredImport()
        {
            // The configuration is invalid, so prevent its creation.
            Assert.Throws<AggregateException>(() => CompositionConfiguration.Create(typeof(RequiredImportMissing)));
        }

        [MefFact(CompositionEngines.V2Compat, typeof(OptionalImportMissing))]
        public void MissingOptionalImport(IContainer container)
        {
            var export = container.GetExport<OptionalImportMissing>();
            Assert.NotNull(export);
            Assert.Null(export.MissingOptionalImport);
        }

        [Export]
        public class RequiredImportMissing
        {
            [Import]
            public ICustomFormatter MissingRequiredImport { get; set; }
        }

        [Export]
        public class OptionalImportMissing
        {
            [Import(AllowDefault = true)]
            public ICustomFormatter MissingOptionalImport { get; set; }
        }
    }
}
