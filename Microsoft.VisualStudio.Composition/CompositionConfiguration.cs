﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.CodeDom;
    using System.CodeDom.Compiler;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using System.Xml;
    using System.Xml.Linq;
    using Validation;
    using Task = System.Threading.Tasks.Task;

    public class CompositionConfiguration
    {
        private ImmutableDictionary<ComposablePartDefinition, string> effectiveSharingBoundaryOverrides;

        private CompositionConfiguration(ComposableCatalog catalog, ISet<ComposablePart> parts, ImmutableDictionary<ComposablePartDefinition, string> effectiveSharingBoundaryOverrides)
        {
            Requires.NotNull(catalog, "catalog");
            Requires.NotNull(parts, "parts");
            Requires.NotNull(effectiveSharingBoundaryOverrides, "effectiveSharingBoundaryOverrides");

            this.Catalog = catalog;
            this.Parts = parts;
            this.AdditionalReferenceAssemblies = ImmutableHashSet<Assembly>.Empty;
            this.effectiveSharingBoundaryOverrides = effectiveSharingBoundaryOverrides;
        }

        public ComposableCatalog Catalog { get; private set; }

        public ISet<ComposablePart> Parts { get; private set; }

        public ImmutableHashSet<Assembly> AdditionalReferenceAssemblies { get; private set; }

        public static CompositionConfiguration Create(ComposableCatalog catalog)
        {
            Requires.NotNull(catalog, "catalog");

            ValidateIndividualParts(catalog.Parts);

            // We consider all the parts in the catalog, plus the specially synthesized one
            // so that folks can import the ExportProvider itself.
            catalog = catalog.WithPart(ExportProvider.ExportProviderPartDefinition);

            // Construct our part builders, initialized with all their imports satisfied.
            var partBuilders = new Dictionary<ComposablePartDefinition, PartBuilder>();
            foreach (ComposablePartDefinition partDefinition in catalog.Parts)
            {
                var imports = partDefinition.ImportingMembers.Select(i => new Import(partDefinition, i.Value, i.Key));
                if (partDefinition.ImportingConstructor != null)
                {
                    imports = imports.Concat(partDefinition.ImportingConstructor.Select(i => new Import(partDefinition, i)));
                }

                var satisfyingImports = imports.ToImmutableDictionary(i => i, i => catalog.GetExports(i.ImportDefinition));
                partBuilders.Add(partDefinition, new PartBuilder(partDefinition, satisfyingImports));
            }

            // Create a lookup table that gets all immediate importers for each part.
            foreach (PartBuilder partBuilder in partBuilders.Values)
            {
                var importedPartsExcludingFactories =
                    (from entry in partBuilder.SatisfyingExports
                     where !entry.Key.ImportDefinition.IsExportFactory
                     from export in entry.Value
                     select export.PartDefinition).Distinct();
                foreach (var importedPartDefinition in importedPartsExcludingFactories)
                {
                    var importedPartBuilder = partBuilders[importedPartDefinition];
                    importedPartBuilder.ReportImportingPart(partBuilder);
                }
            }

            // Propagate sharing boundaries defined on parts to all importers (transitive closure).
            foreach (PartBuilder partBuilder in partBuilders.Values)
            {
                partBuilder.ApplySharingBoundary();
            }

            // Establish which sharing boundary each MEF part can be considered to truly belong to.
            // Note that a [Shared("someboundary")] attribute isn't reliable as a source because 
            // not all parts specify the argument, and even those that do might import other parts
            // that belong to other sharing boundaries.
            // And (ultimately) many parts use MEFv1 attributes, for which SharedAttribute isn't available.
            // So we have to be able to synthesize this data.
            var sharingBoundaryOverrides = ImmutableDictionary.CreateBuilder<ComposablePartDefinition, string>();
            foreach (PartBuilder partBuilder in partBuilders.Values)
            {
                if (partBuilder.PartDefinition.IsSharingBoundaryInferred)
                {
                    sharingBoundaryOverrides.Add(partBuilder.PartDefinition, ConstructInferredSharingBoundary(partBuilder.RequiredSharingBoundaries));
                }
            }

            // Build up our set of composed parts.
            var partsBuilder = ImmutableHashSet.CreateBuilder<ComposablePart>();
            foreach (var partBuilder in partBuilders.Values)
            {
                var composedPart = new ComposablePart(partBuilder.PartDefinition, partBuilder.SatisfyingExports, partBuilder.RequiredSharingBoundaries.ToImmutableHashSet());
                partsBuilder.Add(composedPart);
            }

            var parts = partsBuilder.ToImmutable();

            // Validate configuration.
            var exceptions = new List<Exception>();
            foreach (var part in parts)
            {
                try
                {
                    part.Validate();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            if (exceptions.Count > 0)
            {
                throw new CompositionFailedException("Catalog fails to create a well-formed configuration.", new AggregateException(exceptions));
            }

            // Detect loops of all non-shared parts.
            if (IsLoopPresent(parts))
            {
                Verify.FailOperation("Loop detected.");
            }

            return new CompositionConfiguration(catalog, parts, sharingBoundaryOverrides.ToImmutable());
        }

        public static CompositionConfiguration Create(PartDiscovery partDiscovery, params Type[] parts)
        {
            Requires.NotNull(partDiscovery, "partDiscovery");
            Requires.NotNull(parts, "parts");

            return Create(ComposableCatalog.Create(partDiscovery, parts));
        }

        public static IExportProviderFactory Load(AssemblyName assemblyRef)
        {
            return new CompiledExportProviderFactory(Assembly.Load(assemblyRef));
        }

        public static IExportProviderFactory Load(Assembly assembly)
        {
            return new CompiledExportProviderFactory(assembly);
        }

        public CompositionConfiguration WithReferenceAssemblies(ImmutableHashSet<Assembly> additionalReferenceAssemblies)
        {
            Requires.NotNull(additionalReferenceAssemblies, "additionalReferenceAssemblies");

            return new CompositionConfiguration(this.Catalog, this.Parts, this.effectiveSharingBoundaryOverrides)
            {
                AdditionalReferenceAssemblies = this.AdditionalReferenceAssemblies.Union(additionalReferenceAssemblies)
            };
        }

        public string GetEffectiveSharingBoundary(ComposablePartDefinition partDefinition)
        {
            Requires.NotNull(partDefinition, "partDefinition");
            Requires.That(partDefinition.IsShared, "partDefinition", "Part is not shared.");

            if (partDefinition.IsSharingBoundaryInferred)
            {
                return this.effectiveSharingBoundaryOverrides.GetValueOrDefault(partDefinition) ?? partDefinition.SharingBoundary;
            }
            else
            {
                return partDefinition.SharingBoundary;
            }
        }

        private static string ConstructInferredSharingBoundary(IEnumerable<string> discoveredSharingBoundaries)
        {
            Requires.NotNull(discoveredSharingBoundaries, "discoveredSharingBoundaries");

            return string.Join("-", discoveredSharingBoundaries);
        }

        private static bool IsLoopPresent(ImmutableHashSet<ComposablePart> parts)
        {
            var partsAndDirectImports = new Dictionary<ComposablePart, ImmutableHashSet<ComposablePart>>();

            // First create a map of each NonShared part and the NonShared parts it directly imports.
            foreach (var part in parts.Where(p => !p.Definition.IsShared))
            {
                var directlyImportedParts = (from exportList in part.SatisfyingExports.Values
                                             from export in exportList
                                             let exportingPart = parts.Single(p => p.Definition == export.PartDefinition)
                                             where !exportingPart.Definition.IsShared
                                             select exportingPart).ToImmutableHashSet();
                partsAndDirectImports.Add(part, directlyImportedParts);
            }

            // Now create a map of each part and all the parts it transitively imports.
            return IsLoopPresent(partsAndDirectImports.Keys, p => partsAndDirectImports[p]);
        }

        private static bool IsLoopPresent<T>(IEnumerable<T> values, Func<T, IEnumerable<T>> getDirectLinks)
        {
            Requires.NotNull(values, "values");
            Requires.NotNull(getDirectLinks, "getDirectLinks");

            var visitedNodes = new HashSet<T>();
            var queue = new Queue<T>();
            foreach (T value in values)
            {
                visitedNodes.Clear();
                queue.Clear();

                queue.Enqueue(value);
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    if (!visitedNodes.Add(node))
                    {
                        // Only claim to have detected a loop if we got back to the *original* part.
                        // This is because they may be multiple legit routes from the original part
                        // to the part we're looking at now.
                        if (value.Equals(node))
                        {
                            return true;
                        }
                    }

                    foreach (var directLink in getDirectLinks(node).Distinct())
                    {
                        queue.Enqueue(directLink);
                    }
                }
            }

            return false;
        }

        private static void ValidateIndividualParts(IImmutableSet<ComposablePartDefinition> parts)
        {
            Requires.NotNull(parts, "parts");
            var partsExportingExportProvider = parts.Where(p => p.ExportDefinitions.Any(ed => ExportProvider.ExportProviderContract.Equals(ed.Value.Contract)));
            if (partsExportingExportProvider.Any())
            {
                throw new CompositionFailedException();
            }
        }

        public XDocument CreateDgml()
        {
            return CreateDgml(this.Parts);
        }

        private static XDocument CreateDgml(ISet<ComposablePart> parts)
        {
            Requires.NotNull(parts, "parts");

            XElement nodes, links;
            var dgml = Dgml.Create(out nodes, out links, direction: "RightToLeft");

            foreach (var part in parts)
            {
                nodes.Add(Dgml.Node(part.Definition.Id, ReflectionHelpers.GetTypeName(part.Definition.Type, false, true, null)));
                foreach (var import in part.SatisfyingExports.Keys)
                {
                    foreach (Export export in part.SatisfyingExports[import])
                    {
                        string linkLabel = !export.ExportDefinition.Contract.Type.Equals(export.PartDefinition.Type)
                            ? export.ExportDefinition.Contract.ToString()
                            : null;
                        links.Add(Dgml.Link(export.PartDefinition.Id, part.Definition.Id, linkLabel));
                    }
                }
            }

            return dgml;
        }

        private class CompiledExportProviderFactory : IExportProviderFactory
        {
            private Func<ExportProvider> createFactory;

            internal CompiledExportProviderFactory(Assembly assembly)
            {
                Requires.NotNull(assembly, "assembly");

                var exportFactoryType = assembly.GetType("CompiledExportProvider");
                this.createFactory = () => (ExportProvider)Activator.CreateInstance(exportFactoryType);
            }

            public ExportProvider CreateExportProvider()
            {
                return this.createFactory();
            }
        }

        [DebuggerDisplay("{PartDefinition.Type.Name}")]
        private class PartBuilder
        {
            internal PartBuilder(ComposablePartDefinition partDefinition, IReadOnlyDictionary<Import, IReadOnlyList<Export>> importedParts)
            {
                Requires.NotNull(partDefinition, "partDefinition");
                Requires.NotNull(importedParts, "importedParts");

                this.PartDefinition = partDefinition;
                this.RequiredSharingBoundaries = ImmutableHashSet.CreateBuilder<string>();
                this.SatisfyingExports = importedParts;
                this.ImportingParts = new HashSet<PartBuilder>();
            }

            /// <summary>
            /// Gets the part definition tracked by this instance.
            /// </summary>
            public ComposablePartDefinition PartDefinition { get; set; }

            /// <summary>
            /// Gets the sharing boundaries required to instantiate this part.
            /// </summary>
            /// <remarks>
            /// This is the union of the part's own explicitly declared sharing boundary 
            /// and the boundaries of all parts it imports (transitively).
            /// </remarks>
            public ISet<string> RequiredSharingBoundaries { get; private set; }

            /// <summary>
            /// Gets the set of parts that import this one.
            /// </summary>
            public HashSet<PartBuilder> ImportingParts { get; private set; }

            /// <summary>
            /// Gets the set of parts imported by this one.
            /// </summary>
            public IReadOnlyDictionary<Import, IReadOnlyList<Export>> SatisfyingExports { get; private set; }

            public void ApplySharingBoundary()
            {
                this.ApplySharingBoundary(this.PartDefinition.SharingBoundary);
            }

            private void ApplySharingBoundary(string sharingBoundary)
            {
                if (!string.IsNullOrEmpty(sharingBoundary))
                {
                    if (this.RequiredSharingBoundaries.Add(sharingBoundary))
                    {
                        // Since this is new to us, be sure that all our importers belong to this sharing boundary as well.
                        foreach (var importingPart in this.ImportingParts)
                        {
                            importingPart.ApplySharingBoundary(sharingBoundary);
                        }
                    }
                }
            }

            public void ReportImportingPart(PartBuilder part)
            {
                this.ImportingParts.Add(part);
            }
        }

        [DebuggerDisplay("{Name}")]
        private class SharingBoundaryTree
        {
            public SharingBoundaryTree(string name, ImmutableHashSet<SharingBoundaryTree> children)
            {
                Requires.NotNull(name, "name");
                Requires.NotNull(children, "children");

                this.Name = name;
                this.Children = children;
            }

            public string Name { get; private set; }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public ImmutableHashSet<SharingBoundaryTree> Children { get; private set; }
        }
    }
}
