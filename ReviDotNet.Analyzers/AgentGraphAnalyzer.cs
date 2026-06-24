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
    /// Analyzer REVI011: validates the state graph of a <c>.agent</c> file at build time, mirroring the
    /// runtime checks in <c>AgentProfile.ValidateGraph</c>.
    ///
    /// Warnings:
    /// <list type="bullet">
    /// <item>A state name (in a <c>[[state.X]]</c> header or a <c>[[_loop]]</c> edge) containing an underscore — state discovery does not support underscores.</item>
    /// <item>A loop node or transition target referencing a state with no <c>[[state.*]]</c> section.</item>
    /// <item>A loop entry state with no matching <c>[[state.*]]</c> section.</item>
    /// <item>A transition after an unconditional (no <c>[when:]</c>) fallback — an unreachable dead edge.</item>
    /// <item>A signal declared more than once within a single state.</item>
    /// </list>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class AgentGraphAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic ID exposed for this analyzer.</summary>
        public const string DiagnosticId = "REVI011";

        private const string Category = "Configuration";

        private static readonly DiagnosticDescriptor StateNameRule = Rule(
            "Invalid state name",
            "State name '{0}' contains an underscore; state names must be letters, digits, and hyphens (underscores break state discovery)");

        private static readonly DiagnosticDescriptor UndefinedNodeRule = Rule(
            "Loop state has no definition",
            "Loop declares state '{0}' which has no matching [[state.{0}]] section");

        private static readonly DiagnosticDescriptor UndefinedTargetRule = Rule(
            "Transition to undefined state",
            "Loop transition target '{0}' has no matching [[state.{0}]] section");

        private static readonly DiagnosticDescriptor UndefinedEntryRule = Rule(
            "Loop entry has no definition",
            "Loop entry state '{0}' has no matching [[state.{0}]] section");

        private static readonly DiagnosticDescriptor DeadEdgeRule = Rule(
            "Unreachable transition (dead edge)",
            "Transition to '{0}' is unreachable — it follows an unconditional (no [when:]) fallback");

        private static readonly DiagnosticDescriptor DuplicateSignalRule = Rule(
            "Duplicate signal",
            "Signal '{0}' is declared more than once in this state — only the first transition for it is reachable");

        private static DiagnosticDescriptor Rule(string title, string message) =>
            new DiagnosticDescriptor(DiagnosticId, title, message, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly Regex SectionHeader =
            new Regex(@"^\s*\[\[(?<name>[^\]]+)\]\]\s*$", RegexOptions.Compiled);
        private static readonly Regex EntryKey =
            new Regex(@"^\s*entry\s*=\s*(?<val>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Mirrors LoopDslParser.TransitionRegex.
        private static readonly Regex TransitionRegex = new Regex(
            @"^\s*->\s*(?<target>\[end\]|self|\w[\w-]*)\s*(?:\[when:\s*(?<signal>[A-Z0-9_]+)\])?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ValidStateName =
            new Regex(@"^[A-Za-z][A-Za-z0-9-]*$", RegexOptions.Compiled);

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(StateNameRule, UndefinedNodeRule, UndefinedTargetRule, UndefinedEntryRule, DeadEdgeRule, DuplicateSignalRule);

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
                if (!file.Path.EndsWith(".agent", StringComparison.OrdinalIgnoreCase))
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

            // declared state name -> header line (first occurrence)
            var declared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? entry = null;
            int entryLine = -1;
            int loopStart = -1, loopEnd = -1; // body line range [loopStart, loopEnd)
            string? currentSection = null;

            for (int i = 0; i < lines.Length; i++)
            {
                Match h = SectionHeader.Match(lines[i]);
                if (h.Success)
                {
                    if (string.Equals(currentSection, "_loop", StringComparison.OrdinalIgnoreCase))
                        loopEnd = i;

                    currentSection = h.Groups["name"].Value.Trim();
                    string? stateName = ExtractStateName(currentSection);
                    if (stateName != null && !declared.ContainsKey(stateName))
                        declared[stateName] = i;
                    if (string.Equals(currentSection, "_loop", StringComparison.OrdinalIgnoreCase))
                        loopStart = i + 1;
                    continue;
                }

                if (string.Equals(currentSection, "loop", StringComparison.OrdinalIgnoreCase) && entry == null)
                {
                    Match e = EntryKey.Match(lines[i]);
                    if (e.Success) { entry = e.Groups["val"].Value.Trim(); entryLine = i; }
                }
            }
            if (loopStart >= 0 && loopEnd < 0)
                loopEnd = lines.Length;

            // Grammar: any declared state name with an underscore.
            foreach (KeyValuePair<string, int> d in declared)
            {
                if (!ValidStateName.IsMatch(d.Key))
                    context.ReportDiagnostic(Diagnostic.Create(StateNameRule, LineLoc(file.Path, d.Value), d.Key));
            }

            // Entry must resolve to a declared state.
            if (!string.IsNullOrEmpty(entry) && !declared.ContainsKey(entry!))
                context.ReportDiagnostic(Diagnostic.Create(UndefinedEntryRule, LineLoc(file.Path, entryLine), entry));

            if (loopStart < 0)
                return;

            AnalyzeLoop(context, file, lines, loopStart, loopEnd, declared);
        }

        private static void AnalyzeLoop(
            CompilationAnalysisContext context, AdditionalText file, string[] lines,
            int start, int end, Dictionary<string, int> declared)
        {
            string? currentState = null;
            bool sawUnconditional = false;
            HashSet<string> seenSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = start; i < end; i++)
            {
                string line = StripComment(lines[i]);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Match t = TransitionRegex.Match(line);
                if (t.Success)
                {
                    string target = t.Groups["target"].Value.Trim();
                    string? signal = t.Groups["signal"].Success ? t.Groups["signal"].Value.Trim().ToUpperInvariant() : null;

                    if (sawUnconditional)
                        context.ReportDiagnostic(Diagnostic.Create(DeadEdgeRule, LineLoc(file.Path, i), target));

                    if (!IsSpecial(target) && ValidStateName.IsMatch(target) && !declared.ContainsKey(target))
                        context.ReportDiagnostic(Diagnostic.Create(UndefinedTargetRule, LineLoc(file.Path, i), target));

                    if (string.IsNullOrEmpty(signal))
                        sawUnconditional = true;
                    else if (!seenSignals.Add(signal!))
                        context.ReportDiagnostic(Diagnostic.Create(DuplicateSignalRule, LineLoc(file.Path, i), signal));
                }
                else if (!line.StartsWith(" ") && !line.StartsWith("\t"))
                {
                    // State declaration line.
                    currentState = line.Trim();
                    sawUnconditional = false;
                    seenSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(currentState) && !declared.ContainsKey(currentState))
                        context.ReportDiagnostic(Diagnostic.Create(UndefinedNodeRule, LineLoc(file.Path, i), currentState));
                }
            }
        }

        /// <summary>Extracts the state name from a section header name, or null if it isn't a state section.</summary>
        private static string? ExtractStateName(string section)
        {
            // _state.<X>.instruction / _state.<X>.settings
            if (section.StartsWith("_state.", StringComparison.OrdinalIgnoreCase))
            {
                string rest = section.Substring("_state.".Length);
                int dot = rest.LastIndexOf('.');
                return dot > 0 ? rest.Substring(0, dot) : rest;
            }

            // state.<X>  or  state.<X>.guardrails
            if (section.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
            {
                string rest = section.Substring("state.".Length);
                int g = rest.IndexOf(".guardrails", StringComparison.OrdinalIgnoreCase);
                return g >= 0 ? rest.Substring(0, g) : rest;
            }

            return null;
        }

        private static bool IsSpecial(string target) =>
            target.Equals("[end]", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("self", StringComparison.OrdinalIgnoreCase);

        private static string StripComment(string line)
        {
            int c = line.IndexOf('#');
            return c >= 0 ? line.Substring(0, c) : line;
        }

        private static Location LineLoc(string filePath, int line)
        {
            LinePosition pos = new LinePosition(Math.Max(0, line), 0);
            return Location.Create(filePath, default, new LinePositionSpan(pos, pos));
        }
    }
}
