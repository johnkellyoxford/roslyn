﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols
{
    public static partial class SymbolFinder
    {
        /// <summary>
        /// Makes certain all namespace symbols returned by API are from the compilation.
        /// </summary>
        private static ImmutableArray<ISymbol> TranslateNamespaces(
            ImmutableArray<ISymbol> symbols, Compilation compilation)
        {
            var builder = ArrayBuilder<ISymbol>.GetInstance();
            foreach (var symbol in symbols)
            {
                var ns = symbol as INamespaceSymbol;
                if (ns != null)
                {
                    builder.Add(compilation.GetCompilationNamespace(ns));
                }
                else
                {
                    builder.Add(symbol);
                }
            }

            var result = builder.Count == symbols.Length
                ? symbols
                : builder.ToImmutable();

            builder.Free();

            return result;
        }

        private static Task AddCompilationDeclarationsWithNormalQueryAsync(
            Project project, SearchQuery query, SymbolFilter filter,
            ArrayBuilder<ISymbol> list, CancellationToken cancellationToken)
        {
            Debug.Assert(query.Kind != SearchKind.Custom);
            return AddCompilationDeclarationsWithNormalQueryAsync(
                project, query, filter, list,
                startingCompilation: null,
                startingAssembly: null,
                cancellationToken: cancellationToken);
        }

        private static async Task AddCompilationDeclarationsWithNormalQueryAsync(
            Project project,
            SearchQuery query,
            SymbolFilter filter,
            ArrayBuilder<ISymbol> list,
            Compilation startingCompilation,
            IAssemblySymbol startingAssembly,
            CancellationToken cancellationToken)
        {
            Debug.Assert(query.Kind != SearchKind.Custom);

            using (Logger.LogBlock(FunctionId.SymbolFinder_Project_AddDeclarationsAsync, cancellationToken))
            {
                if (!await project.ContainsSymbolsWithNameAsync(query.GetPredicate(), filter, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var symbolsWithName = compilation.GetSymbolsWithName(query.GetPredicate(), filter, cancellationToken)
                                                 .ToImmutableArray();

                if (startingCompilation != null && startingAssembly != null && compilation.Assembly != startingAssembly)
                {
                    // Return symbols from skeleton assembly in this case so that symbols have 
                    // the same language as startingCompilation.
                    symbolsWithName = symbolsWithName.Select(s => s.GetSymbolKey().Resolve(startingCompilation, cancellationToken: cancellationToken).Symbol)
                                                     .WhereNotNull()
                                                     .ToImmutableArray();
                }

                list.AddRange(FilterByCriteria(symbolsWithName, filter));
            }
        }

        private static async Task AddMetadataDeclarationsWithNormalQueryAsync(
            Solution solution, IAssemblySymbol assembly, PortableExecutableReference referenceOpt, 
            SearchQuery query, SymbolFilter filter, ArrayBuilder<ISymbol> list, CancellationToken cancellationToken)
        {
            // All entrypoints to this function are Find functions that are only searching
            // for specific strings (i.e. they never do a custom search).
            Debug.Assert(query.Kind != SearchKind.Custom);

            using (Logger.LogBlock(FunctionId.SymbolFinder_Assembly_AddDeclarationsAsync, cancellationToken))
            {
                if (referenceOpt != null)
                {
                    var info = await SymbolTreeInfo.TryGetInfoForMetadataReferenceAsync(
                        solution, referenceOpt, loadOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (info != null)
                    {
                        var symbols = await info.FindAsync(query, assembly, filter, cancellationToken).ConfigureAwait(false);
                        list.AddRange(symbols);
                    }
                }
            }
        }

        internal static ImmutableArray<ISymbol> FilterByCriteria(
            ImmutableArray<ISymbol> symbols, SymbolFilter criteria)
        {
            var builder = ArrayBuilder<ISymbol>.GetInstance();
            foreach (var symbol in symbols)
            {
                if (symbol.IsImplicitlyDeclared || symbol.IsAccessor())
                {
                    continue;
                }

                if (MeetCriteria(symbol, criteria))
                {
                    builder.Add(symbol);
                }
            }

            var result = builder.Count == symbols.Length
                ? symbols
                : builder.ToImmutable();

            builder.Free();
            return result;
        }

        private static bool MeetCriteria(ISymbol symbol, SymbolFilter filter)
        {
            if (IsOn(filter, SymbolFilter.Namespace) && symbol.Kind == SymbolKind.Namespace)
            {
                return true;
            }

            if (IsOn(filter, SymbolFilter.Type) && symbol is ITypeSymbol)
            {
                return true;
            }

            if (IsOn(filter, SymbolFilter.Member) && IsNonTypeMember(symbol))
            {
                return true;
            }

            return false;
        }

        private static bool IsNonTypeMember(ISymbol symbol)
        {
            return symbol.Kind == SymbolKind.Method ||
                   symbol.Kind == SymbolKind.Property ||
                   symbol.Kind == SymbolKind.Event ||
                   symbol.Kind == SymbolKind.Field;
        }

        private static bool IsOn(SymbolFilter filter, SymbolFilter flag)
        {
            return (filter & flag) == flag;
        }
    }
}