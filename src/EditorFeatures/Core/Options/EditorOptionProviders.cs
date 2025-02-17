﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Indentation;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;

namespace Microsoft.CodeAnalysis.Options;

internal static class EditorOptionProviders
{
    public static SyntaxFormattingOptions GetSyntaxFormattingOptions(this ITextBuffer textBuffer, IEditorOptionsFactoryService factory, IIndentationManagerService indentationManager, IGlobalOptionService globalOptions, HostLanguageServices languageServices, bool explicitFormat)
        => GetSyntaxFormattingOptionsImpl(textBuffer, factory.GetOptions(textBuffer), indentationManager, globalOptions, languageServices, explicitFormat);

    private static SyntaxFormattingOptions GetSyntaxFormattingOptionsImpl(ITextBuffer textBuffer, IEditorOptions editorOptions, IIndentationManagerService indentationManager, IGlobalOptionService globalOptions, HostLanguageServices languageServices, bool explicitFormat)
    {
        var configOptions = new EditorAnalyzerConfigOptions(editorOptions);
        var fallbackOptions = globalOptions.GetSyntaxFormattingOptions(languageServices);
        var options = configOptions.GetSyntaxFormattingOptions(fallbackOptions, languageServices);

        indentationManager.GetIndentation(textBuffer, explicitFormat, out var convertTabsToSpaces, out var tabSize, out var indentSize);

        return options.With(new LineFormattingOptions()
        {
            UseTabs = !convertTabsToSpaces,
            IndentationSize = indentSize,
            TabSize = tabSize,
            NewLine = editorOptions.GetNewLineCharacter(),
        });
    }

    public static IndentationOptions GetIndentationOptions(this ITextBuffer textBuffer, IEditorOptionsFactoryService factory, IIndentationManagerService indentationManager, IGlobalOptionService globalOptions, HostLanguageServices languageServices, bool explicitFormat)
    {
        var editorOptions = factory.GetOptions(textBuffer);
        var formattingOptions = GetSyntaxFormattingOptionsImpl(textBuffer, editorOptions, indentationManager, globalOptions, languageServices, explicitFormat);

        return new IndentationOptions(formattingOptions)
        {
            AutoFormattingOptions = globalOptions.GetAutoFormattingOptions(languageServices.Language),
            // TODO: Call editorOptions.GetIndentStyle() instead (see https://github.com/dotnet/roslyn/issues/62204):
            IndentStyle = globalOptions.GetOption(IndentationOptionsStorage.SmartIndent, languageServices.Language)
        };
    }

    public static IndentingStyle ToEditorIndentStyle(this FormattingOptions2.IndentStyle value)
        => value switch
        {
            FormattingOptions2.IndentStyle.Smart => IndentingStyle.Smart,
            FormattingOptions2.IndentStyle.Block => IndentingStyle.Block,
            _ => IndentingStyle.None,
        };

    public static FormattingOptions2.IndentStyle ToIndentStyle(this IndentingStyle value)
        => value switch
        {
            IndentingStyle.Smart => FormattingOptions2.IndentStyle.Smart,
            IndentingStyle.Block => FormattingOptions2.IndentStyle.Block,
            _ => FormattingOptions2.IndentStyle.None,
        };
}
