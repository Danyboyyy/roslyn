﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Implementation.CallHierarchy.Finders;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.Language.CallHierarchy;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServices.Implementation.Utilities;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.CallHierarchy
{
    [Export(typeof(CallHierarchyProvider))]
    internal partial class CallHierarchyProvider
    {
        public readonly IAsynchronousOperationListener AsyncListener;
        public readonly IUIThreadOperationExecutor ThreadOperationExecutor;

        public IThreadingContext ThreadingContext { get; }
        public IGlyphService GlyphService { get; }

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CallHierarchyProvider(
            IThreadingContext threadingContext,
            IUIThreadOperationExecutor threadOperationExecutor,
            IAsynchronousOperationListenerProvider listenerProvider,
            IGlyphService glyphService)
        {
            AsyncListener = listenerProvider.GetListener(FeatureAttribute.CallHierarchy);
            ThreadingContext = threadingContext;
            ThreadOperationExecutor = threadOperationExecutor;
            this.GlyphService = glyphService;
        }

        public async Task<ICallHierarchyMemberItem> CreateItemAsync(
            ISymbol symbol, Project project, ImmutableArray<Location> callsites, CancellationToken cancellationToken)
        {
            if (symbol.Kind is SymbolKind.Method or
                               SymbolKind.Property or
                               SymbolKind.Event or
                               SymbolKind.Field)
            {
                symbol = GetTargetSymbol(symbol);

                var finders = await CreateFindersAsync(symbol, project, cancellationToken).ConfigureAwait(false);

                ICallHierarchyMemberItem item = new CallHierarchyItem(
                    this,
                    symbol,
                    project.Id,
                    finders,
                    () => symbol.GetGlyph().GetImageSource(GlyphService),
                    callsites,
                    project.Solution.Workspace);

                return item;
            }

            return null;
        }

        private static ISymbol GetTargetSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                methodSymbol = methodSymbol.ReducedFrom ?? methodSymbol;
                methodSymbol = methodSymbol.ConstructedFrom ?? methodSymbol;
                return methodSymbol;
            }

            return symbol;
        }

        public FieldInitializerItem CreateInitializerItem(IEnumerable<CallHierarchyDetail> details)
        {
            return new FieldInitializerItem(EditorFeaturesResources.Initializers,
                                            "__" + EditorFeaturesResources.Initializers,
                                            Glyph.FieldPublic.GetImageSource(GlyphService),
                                            details);
        }

        public async Task<IEnumerable<AbstractCallFinder>> CreateFindersAsync(ISymbol symbol, Project project, CancellationToken cancellationToken)
        {
            if (symbol.Kind is SymbolKind.Property or
                    SymbolKind.Event or
                    SymbolKind.Method)
            {
                var finders = new List<AbstractCallFinder>();

                finders.Add(new MethodCallFinder(symbol, project.Id, AsyncListener, this));

                if (symbol.IsVirtual || symbol.IsAbstract)
                {
                    finders.Add(new OverridingMemberFinder(symbol, project.Id, AsyncListener, this));
                }

                var @overrides = await SymbolFinder.FindOverridesAsync(symbol, project.Solution, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (overrides.Any())
                {
                    finders.Add(new CallToOverrideFinder(symbol, project.Id, AsyncListener, this));
                }

                if (symbol.GetOverriddenMember() != null)
                {
                    finders.Add(new BaseMemberFinder(symbol.GetOverriddenMember(), project.Id, AsyncListener, this));
                }

                var implementedInterfaceMembers = await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, project.Solution, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var implementedInterfaceMember in implementedInterfaceMembers)
                {
                    finders.Add(new InterfaceImplementationCallFinder(implementedInterfaceMember, project.Id, AsyncListener, this));
                }

                if (symbol.IsImplementableMember())
                {
                    finders.Add(new ImplementerFinder(symbol, project.Id, AsyncListener, this));
                }

                return finders;
            }

            if (symbol.Kind == SymbolKind.Field)
            {
                return SpecializedCollections.SingletonEnumerable(new FieldReferenceFinder(symbol, project.Id, AsyncListener, this));
            }

            return null;
        }

        public async Task NavigateToAsync(SymbolKey id, Project project, CancellationToken cancellationToken)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var resolution = id.Resolve(compilation, cancellationToken: cancellationToken);
            var workspace = project.Solution.Workspace;
            var options = NavigationOptions.Default with { PreferProvisionalTab = true };
            var symbolNavigationService = workspace.Services.GetService<ISymbolNavigationService>();

            var location = await symbolNavigationService.GetNavigableLocationAsync(
                resolution.Symbol, project, cancellationToken).ConfigureAwait(false);
            await location.TryNavigateToAsync(this.ThreadingContext, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
