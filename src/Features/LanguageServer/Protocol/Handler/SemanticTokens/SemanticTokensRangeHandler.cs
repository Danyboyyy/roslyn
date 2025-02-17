﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.ExternalAccess.Razor.Api;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Roslyn.Utilities;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens
{
    [Method(Methods.TextDocumentSemanticTokensRangeName)]
    internal class SemanticTokensRangeHandler : IRequestHandler<LSP.SemanticTokensRangeParams, LSP.SemanticTokens>, IDisposable
    {
        private readonly IGlobalOptionService _globalOptions;
        private readonly IAsynchronousOperationListener _asyncListener;

        private readonly CancellationTokenSource _disposalTokenSource;

        public bool MutatesSolutionState => false;
        public bool RequiresLSPSolution => true;

        #region Semantic Tokens Refresh state

        /// <summary>
        /// Lock over the mutable state that follows.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Mapping from project id to the workqueue for producing the corresponding compilation for it on the OOP server.
        /// </summary>
        private readonly Dictionary<ProjectId, CompilationAvailableEventSource> _projectIdToEventSource = new();

        /// <summary>
        /// Mapping from project id to the project-cone-checksum for it we were at when the project for it had its
        /// compilation produced on the oop server.
        /// </summary>
        private readonly Dictionary<ProjectId, Checksum> _projectIdToLastComputedChecksum = new();

        private readonly LspWorkspaceRegistrationService _lspWorkspaceRegistrationService;

        /// <summary>
        /// Debouncing queue so that we don't attempt to issue a semantic tokens refresh notification too often.
        /// 
        /// Null when the client does not support sending refresh notifications.
        /// </summary>
        private readonly AsyncBatchingWorkQueue<Uri?>? _semanticTokenRefreshQueue;

        #endregion

        public SemanticTokensRangeHandler(
            IGlobalOptionService globalOptions,
            IAsynchronousOperationListenerProvider asynchronousOperationListenerProvider,
            LspWorkspaceRegistrationService lspWorkspaceRegistrationService,
            LspWorkspaceManager lspWorkspaceManager,
            ILanguageServerNotificationManager notificationManager,
            ClientCapabilities clientCapabilities)
        {
            _globalOptions = globalOptions;
            _asyncListener = asynchronousOperationListenerProvider.GetListener(FeatureAttribute.Classification);

            _lspWorkspaceRegistrationService = lspWorkspaceRegistrationService;
            _disposalTokenSource = new();

            if (clientCapabilities.Workspace?.SemanticTokens?.RefreshSupport is true)
            {
                // Only send a refresh notification to the client every 2s (if needed) in order to avoid
                // sending too many notifications at once.  This ensures we batch up workspace notifications,
                // but also means we send soon enough after a compilation-computation to not make the user wait
                // an enormous amount of time.
                _semanticTokenRefreshQueue = new AsyncBatchingWorkQueue<Uri?>(
                    delay: TimeSpan.FromMilliseconds(2000),
                    processBatchAsync: (documentUris, cancellationToken)
                        => FilterLspTrackedDocumentsAsync(lspWorkspaceManager, notificationManager, documentUris, cancellationToken),
                    equalityComparer: EqualityComparer<Uri?>.Default,
                    asyncListener: _asyncListener,
                    _disposalTokenSource.Token);

                _lspWorkspaceRegistrationService.LspSolutionChanged += OnLspSolutionChanged;
            }
        }

        public LSP.TextDocumentIdentifier? GetTextDocumentIdentifier(LSP.SemanticTokensRangeParams request)
        {
            Contract.ThrowIfNull(request.TextDocument);
            return request.TextDocument;
        }

        private static ValueTask FilterLspTrackedDocumentsAsync(
            LspWorkspaceManager lspWorkspaceManager,
            ILanguageServerNotificationManager notificationManager,
            ImmutableSegmentedList<Uri?> documentUris,
            CancellationToken cancellationToken)
        {
            var trackedDocuments = lspWorkspaceManager.GetTrackedLspText();
            foreach (var documentUri in documentUris)
            {
                if (documentUri is null || !trackedDocuments.ContainsKey(documentUri))
                {
                    return notificationManager.SendNotificationAsync(Methods.WorkspaceSemanticTokensRefreshName, cancellationToken);
                }
            }

            // LSP is already tracking all changed documents so we don't need to send a refresh request.
            return ValueTaskFactory.CompletedTask;
        }

        private void OnLspSolutionChanged(object? sender, WorkspaceChangeEventArgs e)
        {
            if (e.DocumentId is not null && e.Kind is WorkspaceChangeKind.DocumentChanged)
            {
                var document = e.NewSolution.GetRequiredDocument(e.DocumentId);
                var documentUri = document.GetURI();

                // We enqueue the URI since there's a chance the client is already tracking the
                // document, in which case we don't need to send a refresh notification.
                // We perform the actual check when processing the batch to ensure we have the
                // most up-to-date list of tracked documents.
                EnqueueSemanticTokenRefreshNotification(documentUri);
            }
            else
            {
                EnqueueSemanticTokenRefreshNotification(documentUri: null);
            }
        }

        private void EnqueueSemanticTokenRefreshNotification(Uri? documentUri)
        {
            // We should have only gotten here if semantic tokens refresh is supported.
            Contract.ThrowIfNull(_semanticTokenRefreshQueue);
            _semanticTokenRefreshQueue.AddWork(documentUri);
        }

        public async Task<LSP.SemanticTokens> HandleRequestAsync(
            SemanticTokensRangeParams request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(request.TextDocument, "TextDocument is null.");
            Contract.ThrowIfNull(context.Document, "Document is null.");

            // If the full compilation is not yet available, we'll try getting a partial one. It may contain inaccurate
            // results but will speed up how quickly we can respond to the client's request.
            var document = context.Document.WithFrozenPartialSemantics(cancellationToken);
            var project = document.Project;
            var options = _globalOptions.GetClassificationOptions(project.Language) with { ForceFrozenPartialSemanticsForCrossProcessOperations = true };

            // The results from the range handler should not be cached since we don't want to cache
            // partial token results. In addition, a range request is only ever called with a whole
            // document request, so caching range results is unnecessary since the whole document
            // handler will cache the results anyway.
            var tokensData = await SemanticTokensHelpers.ComputeSemanticTokensDataAsync(
                document,
                SemanticTokensHelpers.TokenTypeToIndex,
                request.Range,
                options,
                includeSyntacticClassifications: context.Document.IsRazorDocument(),
                cancellationToken).ConfigureAwait(false);

            // The above call to get semantic tokens may be inaccurate (because we use frozen partial semantics).  Kick
            // off a request to ensure that the OOP side gets a fully up to compilation for this project.  Once it does
            // we can optionally choose to notify our caller to do a refresh if we computed a compilation for a new
            // solution snapshot.
            if (_semanticTokenRefreshQueue != null)
                await TryEnqueueRefreshComputationAsync(project, cancellationToken).ConfigureAwait(false);

            return new LSP.SemanticTokens { Data = tokensData };
        }

        private async Task TryEnqueueRefreshComputationAsync(Project project, CancellationToken cancellationToken)
        {
            // Determine the checksum for this project cone.  Note: this should be fast in practice because this is
            // the same project-cone-checksum we used to even call into OOP above when we computed semantic tokens.
            var projectChecksum = await project.Solution.State.GetChecksumAsync(project.Id, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                // If this checksum is the same as the last computed result, no need to continue, we would not produce a
                // different compilation.
                if (ChecksumIsUnchanged_NoLock(project, projectChecksum))
                    return;

                if (!_projectIdToEventSource.TryGetValue(project.Id, out var eventSource))
                {
                    eventSource = new CompilationAvailableEventSource(_asyncListener);
                    _projectIdToEventSource.Add(project.Id, eventSource);
                }

                eventSource.EnsureCompilationAvailability(project, () => OnCompilationAvailable(project, projectChecksum));
            }
        }

        private void OnCompilationAvailable(Project project, Checksum projectChecksum)
        {
            lock (_gate)
            {
                // Paranoia: It's technically possible (though unlikely) for two calls to compute the compilation for
                // the same project to come back and call into this.  This is because the
                // CompilationAvailableEventSource uses cooperative cancellation to cancel the in-flight request before
                // issuing the new one.  There is no requirement though that the inflight request actually stop (or run
                // to completion) prior to the next request running and completing.  In practice this should not happen
                // as cancellation is checked fairly regularly.  However, if it does, check and do not bother to issue a
                // refresh in this case.
                if (ChecksumIsUnchanged_NoLock(project, projectChecksum))
                    return;

                // keep track of this checksum.  That way we don't get into a loop where we send a refresh notification,
                // then we get called back into, causing us to compute the compilation, causing us to send the refresh
                // notification, etc. etc.
                _projectIdToLastComputedChecksum[project.Id] = projectChecksum;
            }

            EnqueueSemanticTokenRefreshNotification(documentUri: null);
        }

        private bool ChecksumIsUnchanged_NoLock(Project project, Checksum projectChecksum)
            => _projectIdToLastComputedChecksum.TryGetValue(project.Id, out var lastChecksum) && lastChecksum == projectChecksum;

        public void Dispose()
        {
            ImmutableArray<CompilationAvailableEventSource> eventSources;
            lock (_gate)
            {
                eventSources = _projectIdToEventSource.Values.ToImmutableArray();
                _projectIdToEventSource.Clear();

                _lspWorkspaceRegistrationService.LspSolutionChanged -= OnLspSolutionChanged;
            }

            foreach (var eventSource in eventSources)
                eventSource.Dispose();

            _disposalTokenSource.Cancel();
            _disposalTokenSource.Dispose();
        }
    }
}
