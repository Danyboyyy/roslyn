﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Transactions;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.SourceGeneration;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis;

using Aliases = ArrayBuilder<(string aliasName, string symbolName)>;

public partial struct SyntaxValueProvider
{
    private static readonly ObjectPool<Stack<string>> s_stackPool = new(static () => new());

    /// <summary>
    /// Returns all syntax nodes of that match <paramref name="predicate"/> if that node has an attribute on it that
    /// could possibly bind to the provided <paramref name="simpleName"/>. <paramref name="simpleName"/> should be the
    /// simple, non-qualified, name of the attribute, including the <c>Attribute</c> suffix, and not containing any
    /// generics, containing types, or namespaces.  For example <c>CLSCompliantAttribute</c> for <see
    /// cref="System.CLSCompliantAttribute"/>.
    /// <para/> This provider understands <see langword="using"/> (<c>Import</c> in Visual Basic) aliases and will find
    /// matches even when the attribute references an alias name.  For example, given:
    /// <code>
    /// using XAttribute = System.CLSCompliantAttribute;
    /// [X]
    /// class C { }
    /// </code>
    /// Then
    /// <c>context.SyntaxProvider.CreateSyntaxProviderForAttribute(nameof(CLSCompliantAttribute), (node, c) => node is ClassDeclarationSyntax)</c>
    /// will find the <c>C</c> class.
    /// </summary>
    internal IncrementalValuesProvider<SyntaxNode> ForAttributeWithSimpleName(
        string simpleName,
        Func<SyntaxNode, CancellationToken, bool> predicate)
    {
        var syntaxHelper = _context.SyntaxHelper;

        // Create a provider over all the syntax trees in the compilation.  This is better than CreateSyntaxProvider as
        // using SyntaxTrees is purely syntax and will not update the incremental node for a tree when another tree is
        // changed. CreateSyntaxProvider will have to rerun all incremental nodes since it passes along the
        // SemanticModel, and that model is updated whenever any tree changes (since it is tied to the compilation).
        var syntaxTreesProvider = _context.CompilationProvider
            .SelectMany(static (c, _) => c.SyntaxTrees)
            .WithTrackingName("compilationUnit_ForAttribute");

        // Create a provider that provides (and updates) the global aliases for any particular file when it is edited.
        var individualFileGlobalAliasesProvider = syntaxTreesProvider.Select(
            (s, c) => getGlobalAliasesInCompilationUnit(syntaxHelper, s.GetRoot(c))).WithTrackingName("individualFileGlobalAliases_ForAttribute");

        // Create an aggregated view of all global aliases across all files.  This should only update when an individual
        // file changes its global aliases or a file is added / removed from the compilation
        var collectedGlobalAliasesProvider = individualFileGlobalAliasesProvider
            .Collect()
            .WithComparer(ImmutableArrayValueComparer<GlobalAliases>.Instance)
            .WithTrackingName("collectedGlobalAliases_ForAttribute");

        var allUpGlobalAliasesProvider = collectedGlobalAliasesProvider
            .Select(static (arrays, _) => GlobalAliases.Create(arrays.SelectMany(a => a.AliasAndSymbolNames).ToImmutableArray()))
            .WithTrackingName("allUpGlobalAliases_ForAttribute");

        // Regenerate our data if the compilation options changed.  VB can supply global aliases with compilation options,
        // so we have to reanalyze everything if those changed.
        var compilationGlobalAliases = _context.CompilationOptionsProvider.Select(
            (o, _) =>
            {
                var aliases = Aliases.GetInstance();
                syntaxHelper.AddAliases(o, aliases);
                return GlobalAliases.Create(aliases.ToImmutableAndFree());
            }).WithTrackingName("compilationGlobalAliases_ForAttribute");

        allUpGlobalAliasesProvider = allUpGlobalAliasesProvider
            .Combine(compilationGlobalAliases)
            .Select((tuple, _) => GlobalAliases.Concat(tuple.Left, tuple.Right))
            .WithTrackingName("allUpIncludingCompilationGlobalAliases_ForAttribute");

        // Combine the two providers so that we reanalyze every file if the global aliases change, or we reanalyze a
        // particular file when it's compilation unit changes.
        var syntaxTreeAndGlobalAliasesProvider = syntaxTreesProvider
            .Combine(allUpGlobalAliasesProvider)
            .WithTrackingName("compilationUnitAndGlobalAliases_ForAttribute");

        // For each pair of compilation unit + global aliases, walk the compilation unit 
        var result = syntaxTreeAndGlobalAliasesProvider
            .SelectMany((globalAliasesAndCompilationUnit, cancellationToken) => GetMatchingNodes(
                syntaxHelper, globalAliasesAndCompilationUnit.Right, globalAliasesAndCompilationUnit.Left, simpleName, predicate, cancellationToken))
            .WithTrackingName("result_ForAttribute");

        return result;

        static GlobalAliases getGlobalAliasesInCompilationUnit(
            ISyntaxHelper syntaxHelper,
            SyntaxNode compilationUnit)
        {
            Debug.Assert(compilationUnit is ICompilationUnitSyntax);
            var globalAliases = Aliases.GetInstance();

            syntaxHelper.AddAliases(compilationUnit, globalAliases, global: true);

            return GlobalAliases.Create(globalAliases.ToImmutableAndFree());
        }
    }

    private static ImmutableArray<SyntaxNode> GetMatchingNodes(
        ISyntaxHelper syntaxHelper,
        GlobalAliases globalAliases,
        SyntaxTree syntaxTree,
        string name,
        Func<SyntaxNode, CancellationToken, bool> predicate,
        CancellationToken cancellationToken)
    {
        var compilationUnit = syntaxTree.GetRoot(cancellationToken);

        Debug.Assert(compilationUnit is ICompilationUnitSyntax);

        var isCaseSensitive = syntaxHelper.IsCaseSensitive;
        var comparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // As we walk down the compilation unit and nested namespaces, we may encounter additional using aliases local
        // to this file. Keep track of them so we can determine if they would allow an attribute in code to bind to the
        // attribute being searched for.
        var localAliases = Aliases.GetInstance();
        var nameHasAttributeSuffix = name.HasAttributeSuffix(isCaseSensitive);

        // Used to ensure that as we recurse through alias names to see if they could bind to attributeName that we
        // don't get into cycles.
        var seenNames = s_stackPool.Allocate();
        var results = ArrayBuilder<SyntaxNode>.GetInstance();
        var attributeTargets = ArrayBuilder<SyntaxNode>.GetInstance();

        try
        {
            recurse(compilationUnit);
        }
        finally
        {
            localAliases.Free();
            seenNames.Clear();
            s_stackPool.Free(seenNames);
            attributeTargets.Free();
        }

        results.RemoveDuplicates();
        return results.ToImmutableAndFree();

        void recurse(SyntaxNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node is ICompilationUnitSyntax)
            {
                syntaxHelper.AddAliases(node, localAliases, global: false);

                recurseChildren(node);
            }
            else if (syntaxHelper.IsAnyNamespaceBlock(node))
            {
                var localAliasCount = localAliases.Count;
                syntaxHelper.AddAliases(node, localAliases, global: false);

                recurseChildren(node);

                // after recursing into this namespace, dump any local aliases we added from this namespace decl itself.
                localAliases.Count = localAliasCount;
            }
            else if (syntaxHelper.IsAttributeList(node))
            {
                foreach (var attribute in syntaxHelper.GetAttributesOfAttributeList(node))
                {
                    // Have to lookup both with the name in the attribute, as well as adding the 'Attribute' suffix.
                    // e.g. if there is [X] then we have to lookup with X and with XAttribute.
                    var simpleAttributeName = syntaxHelper.GetUnqualifiedIdentifierOfName(
                        syntaxHelper.GetNameOfAttribute(attribute)).ValueText;
                    if (matchesAttributeName(simpleAttributeName, withAttributeSuffix: false) ||
                        matchesAttributeName(simpleAttributeName, withAttributeSuffix: true))
                    {
                        attributeTargets.Clear();
                        syntaxHelper.AddAttributeTargets(node, attributeTargets);

                        foreach (var target in attributeTargets)
                        {
                            if (predicate(target, cancellationToken))
                                results.Add(target);
                        }

                        return;
                    }
                }

                // attributes can't have attributes inside of them.  so no need to recurse when we're done.
            }
            else
            {
                // For any other node, just keep recursing deeper to see if we can find an attribute. Note: we cannot
                // terminate the search anywhere as attributes may be found on things like local functions, and that
                // means having to dive deep into statements and expressions.
                recurseChildren(node);
            }

            return;

            void recurseChildren(SyntaxNode node)
            {
                foreach (var child in node.ChildNodesAndTokens())
                {
                    if (child.IsNode)
                        recurse(child.AsNode()!);
                }
            }
        }

        // Checks if `name` is equal to `matchAgainst`.  if `withAttributeSuffix` is true, then
        // will check if `name` + "Attribute" is equal to `matchAgainst`
        bool matchesName(string name, string matchAgainst, bool withAttributeSuffix)
        {
            if (withAttributeSuffix)
            {
                return name.Length + "Attribute".Length == matchAgainst.Length &&
                    matchAgainst.HasAttributeSuffix(isCaseSensitive) &&
                    matchAgainst.StartsWith(name, comparison);
            }
            else
            {
                return name.Equals(matchAgainst, comparison);
            }
        }

        bool matchesAttributeName(string currentAttributeName, bool withAttributeSuffix)
        {
            // If the names match, we're done.
            if (withAttributeSuffix)
            {
                if (nameHasAttributeSuffix &&
                    matchesName(currentAttributeName, name, withAttributeSuffix))
                {
                    return true;
                }
            }
            else
            {
                if (matchesName(currentAttributeName, name, withAttributeSuffix: false))
                    return true;
            }

            // Otherwise, keep searching through aliases.  Check that this is the first time seeing this name so we
            // don't infinite recurse in error code where aliases reference each other.
            //
            // note: as we recurse up the aliases, we do not want to add the attribute suffix anymore.  aliases must
            // reference the actual real name of the symbol they are aliasing.
            if (seenNames.Contains(currentAttributeName))
                return false;

            seenNames.Push(currentAttributeName);

            foreach (var (aliasName, symbolName) in localAliases)
            {
                // see if user wrote `[SomeAlias]`.  If so, if we find a `using SomeAlias = ...` recurse using the
                // ... name portion to see if it might bind to the attr name the caller is searching for.
                if (matchesName(currentAttributeName, aliasName, withAttributeSuffix) &&
                    matchesAttributeName(symbolName, withAttributeSuffix: false))
                {
                    return true;
                }
            }

            foreach (var (aliasName, symbolName) in globalAliases.AliasAndSymbolNames)
            {
                if (matchesName(currentAttributeName, aliasName, withAttributeSuffix) &&
                    matchesAttributeName(symbolName, withAttributeSuffix: false))
                {
                    return true;
                }
            }

            seenNames.Pop();
            return false;
        }
    }
}
