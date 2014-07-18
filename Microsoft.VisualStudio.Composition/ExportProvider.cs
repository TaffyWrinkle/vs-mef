﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;
    using DefaultMetadataType = System.Collections.Generic.IDictionary<string, object>;

    public abstract class ExportProvider : IDisposableObservable
    {
        internal static readonly ExportDefinition ExportProviderExportDefinition = new ExportDefinition(
            ContractNameServices.GetTypeIdentity(typeof(ExportProvider)),
            PartCreationPolicyConstraint.GetExportMetadata(CreationPolicy.Shared).AddRange(ExportTypeIdentityConstraint.GetExportMetadata(typeof(ExportProvider))));

        internal static readonly ComposablePartDefinition ExportProviderPartDefinition = new ComposablePartDefinition(
            typeof(ExportProviderAsExport),
            new[] { ExportProviderExportDefinition },
            ImmutableDictionary<MemberInfo, IReadOnlyList<ExportDefinition>>.Empty,
            ImmutableList<ImportDefinitionBinding>.Empty,
            null,
            null,
            CreationPolicy.Shared);

        protected static readonly LazyPart<object> NotInstantiablePartLazy = new LazyPart<object>(() => CannotInstantiatePartWithNoImportingConstructor());

        protected static readonly object[] EmptyObjectArray = new object[0];

        /// <summary>
        /// A metadata template used by the generated code.
        /// </summary>
        protected static readonly ImmutableDictionary<string, object> EmptyMetadata = ImmutableDictionary.Create<string, object>();

        /// <summary>
        /// An array of manifest modules required for access by reflection.
        /// </summary>
        /// <remarks>
        /// This field is initialized to an array of appropriate size by the derived code-gen'd class.
        /// Its elements are individually lazily initialized.
        /// </remarks>
        protected Module[] cachedManifests;

        /// <summary>
        /// An array of types required for access by reflection.
        /// </summary>
        /// <remarks>
        /// This field is initialized to an array of appropriate size by the derived code-gen'd class.
        /// Its elements are individually lazily initialized.
        /// </remarks>
        protected Type[] cachedTypes;

        private readonly object syncObject = new object();

        /// <summary>
        /// A map of shared boundary names to their shared instances.
        /// The value is a dictionary of types to their Lazy{T} factories.
        /// </summary>
        private readonly ImmutableDictionary<string, Dictionary<Type, object>> sharedInstantiatedExports = ImmutableDictionary.Create<string, Dictionary<Type, object>>();

        /// <summary>
        /// The disposable objects whose lifetimes are controlled by this instance.
        /// </summary>
        private readonly HashSet<IDisposable> disposableInstantiatedParts = new HashSet<IDisposable>();

        private bool isDisposed;

        protected ExportProvider(ExportProvider parent, string[] freshSharingBoundaries)
        {
            if (parent == null)
            {
                this.sharedInstantiatedExports = this.sharedInstantiatedExports.Add(string.Empty, new Dictionary<Type, object>());
            }
            else
            {
                this.sharedInstantiatedExports = parent.sharedInstantiatedExports;
            }

            if (freshSharingBoundaries != null)
            {
                foreach (string freshSharingBoundary in freshSharingBoundaries)
                {
                    this.sharedInstantiatedExports = this.sharedInstantiatedExports.SetItem(freshSharingBoundary, new Dictionary<Type, object>());
                }
            }

            var nonDisposableWrapper = (this as ExportProviderAsExport) ?? new ExportProviderAsExport(this);
            this.NonDisposableWrapper = LazyPart.Wrap(nonDisposableWrapper);
            this.NonDisposableWrapperExportAsListOfOne = ImmutableList.Create(
                new Export(ExportProviderExportDefinition, this.NonDisposableWrapper));
        }

        bool IDisposableObservable.IsDisposed
        {
            get { return this.isDisposed; }
        }

        protected ILazy<DelegatingExportProvider> NonDisposableWrapper { get; private set; }

        protected ImmutableList<Export> NonDisposableWrapperExportAsListOfOne { get; private set; }

        public Lazy<T> GetExport<T>()
        {
            return this.GetExport<T>(null);
        }

        public Lazy<T> GetExport<T>(string contractName)
        {
            return this.GetExport<T, DefaultMetadataType>(contractName);
        }

        public Lazy<T, TMetadataView> GetExport<T, TMetadataView>()
        {
            return this.GetExport<T, TMetadataView>(null);
        }

        public Lazy<T, TMetadataView> GetExport<T, TMetadataView>(string contractName)
        {
            return this.GetExports<T, TMetadataView>(contractName, ImportCardinality.ExactlyOne).Single();
        }

        public T GetExportedValue<T>()
        {
            return this.GetExport<T>().Value;
        }

        public T GetExportedValue<T>(string contractName)
        {
            return this.GetExport<T>(contractName).Value;
        }

        public IEnumerable<Lazy<T>> GetExports<T>()
        {
            return this.GetExports<T>(null);
        }

        public IEnumerable<Lazy<T>> GetExports<T>(string contractName)
        {
            return this.GetExports<T, DefaultMetadataType>(contractName);
        }

        public IEnumerable<Lazy<T, TMetadataView>> GetExports<T, TMetadataView>()
        {
            return this.GetExports<T, TMetadataView>(null);
        }

        public IEnumerable<Lazy<T, TMetadataView>> GetExports<T, TMetadataView>(string contractName)
        {
            return this.GetExports<T, TMetadataView>(contractName, ImportCardinality.ZeroOrMore);
        }

        public IEnumerable<T> GetExportedValues<T>()
        {
            return this.GetExports<T>().Select(l => l.Value);
        }

        public IEnumerable<T> GetExportedValues<T>(string contractName)
        {
            return this.GetExports<T>(contractName).Select(l => l.Value);
        }

        public virtual IEnumerable<Export> GetExports(ImportDefinition importDefinition)
        {
            Requires.NotNull(importDefinition, "importDefinition");

            IEnumerable<Export> exports = importDefinition.ContractName == ExportProviderExportDefinition.ContractName
                ? this.NonDisposableWrapperExportAsListOfOne
                : this.GetExportsCore(importDefinition);

            string genericTypeDefinitionContractName;
            Type[] genericTypeArguments;
            if (ComposableCatalog.TryGetOpenGenericExport(importDefinition, out genericTypeDefinitionContractName, out genericTypeArguments))
            {
                var genericTypeImportDefinition = new ImportDefinition(genericTypeDefinitionContractName, importDefinition.Cardinality, importDefinition.Metadata, importDefinition.ExportConstraints);
                var openGenericExports = this.GetExportsCore(genericTypeImportDefinition);
                var closedGenericExports = openGenericExports.Select(export => export.CloseGenericExport(genericTypeArguments));
                exports = exports.Concat(closedGenericExports);
            }

            var filteredExports = from export in exports
                                  where importDefinition.ExportConstraints.All(c => c.IsSatisfiedBy(export.Definition))
                                  select export;

            var exportsSnapshot = filteredExports.ToArray(); // avoid redoing the above work during multiple enumerations of our result.
            if (importDefinition.Cardinality == ImportCardinality.ExactlyOne && exportsSnapshot.Length != 1)
            {
                throw new CompositionFailedException();
            }

            return exportsSnapshot;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.isDisposed = true;

                // Snapshot the contents of the collection within the lock,
                // then dispose of the values outside the lock to avoid
                // executing arbitrary 3rd-party code within our lock.
                List<IDisposable> disposableSnapshot;
                lock (this.syncObject)
                {
                    disposableSnapshot = new List<IDisposable>(this.disposableInstantiatedParts);
                    this.disposableInstantiatedParts.Clear();
                }

                foreach (var item in disposableSnapshot)
                {
                    item.Dispose();
                }
            }
        }

        protected static object CannotInstantiatePartWithNoImportingConstructor()
        {
            throw new CompositionFailedException("No importing constructor");
        }

        /// <summary>
        /// When implemented by a derived class, returns an <see cref="IEnumerable&lt;ILazy&lt;T&gt;&gt;"/> of values that
        /// satisfy the contract name of the specified <see cref="ImportDefinition"/>.
        /// </summary>
        /// <remarks>
        /// The derived type is *not* expected to filter the exports based on the import definition constraints.
        /// </remarks>
        protected abstract IEnumerable<Export> GetExportsCore(ImportDefinition importDefinition);

        protected Export CreateExport(ImportDefinition importDefinition, IReadOnlyDictionary<string, object> metadata, Type partOpenGenericType, string valueFactoryMethodName, string partSharingBoundary, bool nonSharedInstanceRequired, MemberInfo exportingMember)
        {
            Requires.NotNull(importDefinition, "importDefinition");
            Requires.NotNull(metadata, "metadata");
            Requires.NotNull(partOpenGenericType, "partOpenGenericType");

            var typeArgs = (Type[])importDefinition.Metadata[CompositionConstants.GenericParametersMetadataName];
            var valueFactoryOpenGenericMethodInfo = this.GetMethodWithArity(valueFactoryMethodName, typeArgs.Length);
            var valueFactoryMethodInfo = valueFactoryOpenGenericMethodInfo.MakeGenericMethod(typeArgs);
            var valueFactory = (Func<Dictionary<Type, object>, object>)valueFactoryMethodInfo.CreateDelegate(typeof(Func<Dictionary<Type, object>, object>), this);
            var partType = partOpenGenericType.MakeGenericType(typeArgs);
            return this.CreateExport(importDefinition, metadata, partType, valueFactory, partSharingBoundary, nonSharedInstanceRequired, exportingMember);
        }

        protected Export CreateExport(ImportDefinition importDefinition, IReadOnlyDictionary<string, object> metadata, Type partType, Func<Dictionary<Type, object>, object> valueFactory, string partSharingBoundary, bool nonSharedInstanceRequired, MemberInfo exportingMember)
        {
            Requires.NotNull(importDefinition, "importDefinition");
            Requires.NotNull(metadata, "metadata");
            Requires.NotNull(partType, "partType");
            Requires.NotNull(valueFactory, "valueFactory");

            var provisionalSharedObjects = new Dictionary<Type, object>();
            ILazy<object> lazy = this.GetOrCreateShareableValue(partType, valueFactory, provisionalSharedObjects, partSharingBoundary, nonSharedInstanceRequired);
            Func<object> memberValueFactory;
            if (exportingMember == null)
            {
                memberValueFactory = lazy.ValueFactory;
            }
            else
            {
                memberValueFactory = () => GetValueFromMember(lazy.Value, exportingMember);
            }

            return new Export(importDefinition.ContractName, metadata, memberValueFactory);
        }

        private object GetValueFromMember(object instance, MemberInfo member)
        {
            Requires.NotNull(instance, "instance");
            Requires.NotNull(member, "member");

            var field = member as FieldInfo;
            if (field != null) {
                return field.GetValue(instance);
            }

            var property = member as PropertyInfo;
            if (property != null) {
                return property.GetValue(instance);
            }

            var method = member as MethodInfo;
            if (method != null)
            {
                // If the method came from a property, return the result of the property getter rather than return the delegate.
                if (method.IsSpecialName && method.GetParameters().Length == 0 && method.Name.StartsWith("get_"))
                {
                    return method.Invoke(instance, EmptyObjectArray);
                }

                return method.CreateDelegate(ExportDefinitionBinding.GetContractTypeForDelegate(method), instance);
            }

            throw new NotSupportedException();
        }

        protected ILazy<object> GetOrCreateShareableValue(Type partType, Func<Dictionary<Type, object>, object> valueFactory, Dictionary<Type, object> provisionalSharedObjects, string partSharingBoundary, bool nonSharedInstanceRequired)
        {
            ILazy<System.Object> lazyResult;
            if (!nonSharedInstanceRequired)
            {
                if (TryGetProvisionalSharedExport(provisionalSharedObjects, partType, out lazyResult) ||
                    this.TryGetSharedInstanceFactory(partSharingBoundary, partType, out lazyResult))
                {
                    return lazyResult;
                }
            }

            lazyResult = new LazyPart<object>(() => valueFactory(provisionalSharedObjects));

            if (!nonSharedInstanceRequired)
            {
                lazyResult = this.GetOrAddSharedInstanceFactory(partSharingBoundary, partType, lazyResult);
            }

            return lazyResult;
        }

        protected bool TryGetSharedInstanceFactory<T>(string partSharingBoundary, Type type, out ILazy<T> value)
        {
            Requires.NotNull(type, "type");
            Assumes.True(typeof(T).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo()));

            lock (this.syncObject)
            {
                var sharingBoundary = AcquireSharingBoundaryInstances(partSharingBoundary);
                object valueObject;
                bool result = sharingBoundary.TryGetValue(type, out valueObject);
                value = (ILazy<T>)valueObject;
                return result;
            }
        }

        protected ILazy<T> GetOrAddSharedInstanceFactory<T>(string partSharingBoundary, Type type, ILazy<T> value)
        {
            Requires.NotNull(type, "type");
            Requires.NotNull(value, "value");
            Assumes.True(typeof(T).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo()));

            lock (this.syncObject)
            {
                var sharingBoundary = AcquireSharingBoundaryInstances(partSharingBoundary);
                object priorValue;
                if (sharingBoundary.TryGetValue(type, out priorValue))
                {
                    return (ILazy<T>)priorValue;
                }

                sharingBoundary.Add(type, value);
                return value;
            }
        }

        protected void TrackDisposableValue(IDisposable value)
        {
            Requires.NotNull(value, "value");

            lock (this.syncObject)
            {
                this.disposableInstantiatedParts.Add(value);
            }
        }

        protected MethodInfo GetMethodWithArity(string methodName, int arity)
        {
            return this.GetType().GetTypeInfo().GetDeclaredMethods(methodName)
                .Single(m => m.GetGenericArguments().Length == arity);
        }

        /// <summary>
        /// Gets the manifest module for an assembly.
        /// </summary>
        /// <param name="assemblyId">The index into the cached manifest array.</param>
        /// <returns>The manifest module.</returns>
        protected Module GetAssemblyManifest(int assemblyId)
        {
            Module result = cachedManifests[assemblyId];
            if (result == null)
            {
                // We don't need to worry about thread-safety here because if two threads assign the
                // reference to the loaded assembly to the array slot, that's just fine.
                result = Assembly.Load(new AssemblyName(this.GetAssemblyName(assemblyId))).ManifestModule;
                cachedManifests[assemblyId] = result;
            }

            return result;
        }

        /// <summary>
        /// Gets a type for reflection.
        /// </summary>
        /// <param name="typeId">The index into the cached type array.</param>
        /// <returns>The type.</returns>
        protected Type GetType(int typeId)
        {
            Type result = cachedTypes[typeId];
            if (result == null)
            {
                // We don't need to worry about thread-safety here because if two threads assign the
                // reference to the type to the array slot, that's just fine.
                result = this.GetTypeCore(typeId);
                cachedTypes[typeId] = result;
            }

            return result;
        }

        /// <summary>
        /// When overridden in the derived code-gen'd class, this method gets the full name
        /// of an assembly for an integer that the code-gen knows about.
        /// </summary>
        protected virtual string GetAssemblyName(int assemblyId)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// When overridden in the derived code-gen'd class, this method gets the type
        /// for an integer that the code-gen knows about.
        /// </summary>
        protected virtual Type GetTypeCore(int typeId)
        {
            throw new NotImplementedException();
        }

        private static bool TryGetProvisionalSharedExport(IReadOnlyDictionary<Type, object> provisionalSharedObjects, Type type, out ILazy<object> value)
        {
            object valueObject;
            if (provisionalSharedObjects.TryGetValue(type, out valueObject))
            {
                value = LazyPart.Wrap(valueObject, type);
                return true;
            }

            value = null;
            return false;
        }

        private IEnumerable<Lazy<T, TMetadataView>> GetExports<T, TMetadataView>(string contractName, ImportCardinality cardinality)
        {
            Verify.NotDisposed(this);
            contractName = string.IsNullOrEmpty(contractName) ? ContractNameServices.GetTypeIdentity(typeof(T)) : contractName;

            var constraints = ImmutableHashSet<IImportSatisfiabilityConstraint>.Empty
                .Union(PartDiscovery.GetExportTypeIdentityConstraints(typeof(T)));

            if (typeof(TMetadataView) != typeof(DefaultMetadataType))
            {
                constraints = constraints.Add(new ImportMetadataViewConstraint(typeof(TMetadataView)));
            }

            var importMetadata = PartDiscovery.GetImportMetadataForGenericTypeImport(typeof(T));
            var importDefinition = new ImportDefinition(contractName, cardinality, importMetadata, constraints);
            IEnumerable<Export> results = this.GetExports(importDefinition);
            return results.Select(result => new LazyPart<T, TMetadataView>(() => result.Value, (TMetadataView)result.Metadata));
        }

        private Dictionary<Type, object> AcquireSharingBoundaryInstances(string sharingBoundaryName)
        {
            Requires.NotNull(sharingBoundaryName, "sharingBoundaryName");

            var sharingBoundary = this.sharedInstantiatedExports.GetValueOrDefault(sharingBoundaryName);
            if (sharingBoundary == null)
            {
                // This means someone is trying to create a part
                // that belongs to a sharing boundary that has not yet been created.
                throw new CompositionFailedException("Inappropriate request for export from part that belongs to another sharing boundary.");
            }

            return sharingBoundary;
        }

        private class ExportProviderAsExport : DelegatingExportProvider
        {
            internal ExportProviderAsExport(ExportProvider inner)
                : base(inner)
            {
            }

            protected override void Dispose(bool disposing)
            {
                throw new InvalidOperationException("This instance is an import and cannot be directly disposed.");
            }
        }
    }
}
