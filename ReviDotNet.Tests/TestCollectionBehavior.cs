// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Xunit;

// Run all tests in this assembly sequentially (no cross-collection parallelism).
//
// Why: the agent test harness wires fakes into PROCESS-WIDE STATIC registries
// (ProviderManager / ModelManager / AgentManager / ToolManager) and, by its own
// admission, cannot fully roll those mutations back on dispose — it leaks the
// agent/model/provider entries and relies on unique-suffix names for isolation.
// Model resolution can fall back to tier ("first registered tier-A"), so a test
// running concurrently with another that has just disposed its in-memory
// TestServer could resolve to a dead endpoint. Separately, the timing-sensitive
// inactivity/budget tests can be starved by CPU-heavy parallel tests (e.g. the
// concurrent crawl tests). Both failure modes require concurrency.
//
// Serializing the suite removes the shared-static-state races and CPU starvation
// entirely, making the run deterministic. The whole suite still completes quickly.
// (A future alternative is to add Remove APIs to the static managers and tear them
// down per test, which would make parallel execution safe again.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
