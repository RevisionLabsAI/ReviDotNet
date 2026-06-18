// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace ReviDotNet.Analyzers
{
    /// <summary>
    /// Analyzer REVI009: flags few-shot example halves in a <c>.pmt</c> file that are missing their
    /// counterpart — an <c>[[_exin_N]]</c> with no matching <c>[[_exout_N]]</c> (or vice versa).
    ///
    /// The runtime pairs examples by index and silently drops any half-pair, so an off-by-one or typo in
    /// the index removes a whole few-shot exemplar with no feedback. This rule surfaces that at build time.
    /// Reported as a Warning at the orphaned section's line.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class PromptExamplePairingAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic ID exposed for this analyzer.</summary>
        public const string DiagnosticId = "REVI009";

        private const string Category = "Configuration";

        private static readonly LocalizableString Title = "Unpaired few-shot example";
        private static readonly LocalizableString Message =
            "Example {0} is missing its {1} side ([[{2}]]); the runtime pairs examples by index and will drop this half-pair";
        private static readonly LocalizableString Description =
            "Each few-shot example needs both an [[_exin_N]] and an [[_exout_N]] with the same index N; "
            + "an orphaned half is silently dropped at runtime.";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            Message,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

        // Matches a raw example section header on its own line: [[_exin_3]] / [[_exout_3]].
        private static readonly Regex ExampleHeader =
            new Regex(@"^\s*\[\[_ex(?<side>in|out)_(?<index>\d+)\]\]\s*$", RegexOptions.Compiled);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationAction(AnalyzeCompilation);
        }

        private static void AnalyzeCompilation(CompilationAnalysisContext context)
        {
            foreach (AdditionalText file in context.Options.AdditionalFiles)
            {
                if (!file.Path.EndsWith(".pmt", StringComparison.OrdinalIgnoreCase))
                    continue;

                SourceText? text = file.GetText(context.CancellationToken);
                if (text == null)
                    continue;

                AnalyzeFile(context, file, text.ToString());
            }
        }

        private static void AnalyzeFile(CompilationAnalysisContext context, AdditionalText file, string content)
        {
            string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // index -> line number of the first occurrence of each side.
            Dictionary<int, int> inLines = new Dictionary<int, int>();
            Dictionary<int, int> outLines = new Dictionary<int, int>();

            for (int i = 0; i < lines.Length; i++)
            {
                Match m = ExampleHeader.Match(lines[i]);
                if (!m.Success)
                    continue;

                int index = int.Parse(m.Groups["index"].Value);
                bool isInput = m.Groups["side"].Value == "in";
                Dictionary<int, int> target = isInput ? inLines : outLines;
                if (!target.ContainsKey(index))
                    target[index] = i;
            }

            foreach (KeyValuePair<int, int> entry in inLines)
            {
                if (!outLines.ContainsKey(entry.Key))
                    Report(context, file, entry.Value, entry.Key, "output", $"_exout_{entry.Key}");
            }

            foreach (KeyValuePair<int, int> entry in outLines)
            {
                if (!inLines.ContainsKey(entry.Key))
                    Report(context, file, entry.Value, entry.Key, "input", $"_exin_{entry.Key}");
            }
        }

        private static void Report(
            CompilationAnalysisContext context,
            AdditionalText file,
            int line,
            int index,
            string missingSide,
            string missingKey)
        {
            LinePosition pos = new LinePosition(line, 0);
            Location loc = Location.Create(file.Path, default, new LinePositionSpan(pos, pos));
            context.ReportDiagnostic(Diagnostic.Create(Rule, loc, index, missingSide, missingKey));
        }
    }
}
