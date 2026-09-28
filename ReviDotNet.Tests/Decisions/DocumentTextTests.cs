using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Revi;
using Revi.Tests.Helpers;
using Xunit;

namespace ReviDotNet.Tests.Decisions;

/// <summary>
/// Text extraction shared by read-file, search-files and document-search: the host's registered extractor is
/// used everywhere, textual formats and untyped UTF-8 uploads are read, large or non-UTF-8 text degrades
/// instead of failing, genuine binary is never decoded, and one bad attachment cannot break a session index.
/// </summary>
public sealed class DocumentTextTests
{
    [Theory]
    [InlineData("config.yaml", "application/x-yaml", "key: value")]
    [InlineData("config.yml", "application/yaml", "key: value")]
    [InlineData("data.jsonld", "application/ld+json", "{\"@id\":\"x\"}")]
    [InlineData("feed.atom", "application/atom+xml; charset=utf-8", "<feed/>")]
    [InlineData("query.sql", "application/sql", "SELECT 1;")]
    [InlineData("table.csv", "application/csv", "a,b\n1,2")]
    [InlineData("notes.md", "application/octet-stream", "# Notes\nPlain markdown, caf\u00e9.")]
    [InlineData("run.log", "", "2026-09-28 INFO started\r\n\u001b[32mok\u001b[0m\r\n")]
    public void ExtractorReadsTextualFormatsAndUntypedUtf8Text(string name, string mediaType, string content)
    {
        new DocumentTextExtractor().Extract(File(name, mediaType, Encoding.UTF8.GetBytes(content))).Should().Be(content);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    public void ExtractorNeverDecodesUntypedBinaryAsText(byte[] bytes)
    {
        new DocumentTextExtractor().Extract(File("upload.bin", "application/octet-stream", bytes)).Should().BeNull();
        new DocumentTextExtractor().Extract(File("upload", "", bytes)).Should().BeNull();
    }

    [Fact]
    public void ExtractorDecodesNonUtf8AndByteOrderMarkedTextInsteadOfThrowing()
    {
        DocumentTextExtractor extractor = new();
        extractor.Extract(File("menu.txt", "text/plain", Encoding.Latin1.GetBytes("Caf\u00e9 au lait"))).Should().Be("Caf\uFFFD au lait");
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Wide text")];
        extractor.Extract(File("wide.txt", "text/plain", utf16)).Should().Be("Wide text");
    }

    [Fact]
    public async Task ReadFileUsesTheHostsRegisteredExtractor()
    {
        using Reader reader = new(new PdfOnlyExtractor());
        (ToolCallResult result, string request) = await reader.ReadAsync(File("report.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 binary")));
        result.Failed.Should().BeFalse();
        request.Should().Contain("HOST PDF TEXT");
        // A host extractor that declines plain text leaves the built-in text reading in place.
        (_, string text) = await reader.ReadAsync(File("notes.txt", "text/plain", Encoding.UTF8.GetBytes("PLAIN NOTE")));
        text.Should().Contain("PLAIN NOTE");
    }

    [Theory]
    [InlineData("config.yaml", "application/x-yaml")]
    [InlineData("schema.json", "application/schema+json")]
    [InlineData("notes.md", "application/octet-stream")]
    [InlineData("notes.md", "")]
    public async Task ReadFileReadsTextualUploadsTheReaderUsedToAccept(string name, string mediaType)
    {
        using Reader reader = new();
        (ToolCallResult result, string request) = await reader.ReadAsync(File(name, mediaType, Encoding.UTF8.GetBytes("FILE MARKER line")));
        result.Failed.Should().BeFalse();
        request.Should().Contain("FILE MARKER line");
    }

    [Fact]
    public async Task ReadFileSendsTheStartOfTextBeyondTheExtractionLimitAndDecodesLeniently()
    {
        using Reader reader = new();
        byte[] large = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0123456789abcdef", 17_000_000 / 16)));
        (ToolCallResult result, string request) = await reader.ReadAsync(File("big.log", "text/plain", large));
        result.Failed.Should().BeFalse();
        request.Should().Contain("truncated");
        request.Length.Should().BeLessThan(200_000);
        (result, request) = await reader.ReadAsync(File("latin.txt", "text/plain", Encoding.Latin1.GetBytes("Caf\u00e9 LATIN MARKER")));
        result.Failed.Should().BeFalse();
        request.Should().Contain("LATIN MARKER");
    }

    [Fact]
    public async Task OneFailingAttachmentLeavesTheRestOfTheSessionSearchable()
    {
        DocumentSearchTool tool = new(new DocumentSearchService(new DecisionIntegrationTests.FakeDecisions((_, _, _) => throw new IOException()), Embeddings()),
            new ThrowingExtractor());
        SessionFileRegistry files = new([
            File("broken.pdf", "application/pdf", [1, 2, 3]),
            File("notes.txt", "text/plain", Encoding.UTF8.GetBytes("Refunds are available within thirty days."))
        ]);
        using IDisposable scope = AgentRunContext.Push(new AgentRunContext { Files = files, DocumentSearchOptions = new() { DecisionPrompt = null } });
        ToolCallResult result = await tool.ExecuteAsync("refunds", CancellationToken.None);
        result.Failed.Should().BeFalse();
        using JsonDocument output = JsonDocument.Parse(result.Output!);
        output.RootElement.GetProperty("unsupportedFiles").EnumerateArray().Select(e => e.GetString()).Should().Equal("broken.pdf");
        output.RootElement.GetProperty("passages").EnumerateArray().Should().ContainSingle();
    }

    private static SessionFile File(string name, string mediaType, byte[] bytes) => new() { Id = name, Name = name, MediaType = mediaType, Bytes = bytes };

    private static IEmbedService Embeddings()
    {
        ProviderManagerService providers = new(new RecordingReviLogger<ProviderManagerService>());
        return new EmbedService(new EmbeddingManagerService(providers, new RecordingReviLogger<EmbeddingManagerService>()), new RecordingReviLogger<EmbedService>());
    }

    /// <summary>The DI-built read-file tool with a reader model whose HTTP layer records the prompt it was sent.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CapturingHandler _handler = new();
        private readonly IToolManager _tools;

        public Reader(IDocumentTextExtractor? extractor = null)
        {
            ServiceCollection services = new();
            services.AddReviDotNet(typeof(DocumentTextTests).Assembly);
            if (extractor is not null) services.AddSingleton<IDocumentTextExtractor>(extractor);
            _provider = services.BuildServiceProvider();
            ProviderProfile provider = new() { Name = "reader-provider", Enabled = true };
            provider.InferenceClient = new InferClient(apiUrl: "https://reader.invalid/", apiKey: "test", protocol: Protocol.OpenAI, defaultModel: "reader",
                retryAttemptLimit: 1, retryInitialDelaySeconds: 0, httpClientOverride: new HttpClient(_handler) { BaseAddress = new Uri("https://reader.invalid/") });
            _provider.GetRequiredService<IModelManager>().Add(new ModelProfile { Name = "reader", Enabled = true, ModelString = "reader", ProviderName = provider.Name, Provider = provider });
            _tools = _provider.GetRequiredService<IToolManager>();
        }

        /// <summary>Runs read-file on one attachment; returns the tool result and the reader request body ("" when none was sent).</summary>
        public async Task<(ToolCallResult Result, string Request)> ReadAsync(SessionFile file)
        {
            int before = _handler.Bodies.Count;
            using IDisposable scope = AgentRunContext.Push(AgentRunContext.Root(new SessionFileRegistry([file])));
            ToolCallResult result = await _tools.GetBuiltIn("read-file")!.ExecuteAsync(JsonSerializer.Serialize(new { file = file.Name, query = "Summarise." }), CancellationToken.None);
            return (result, _handler.Bodies.Count > before ? _handler.Bodies[^1] : "");
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>Records request bodies and answers like an OpenAI chat endpoint.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>A host extractor that only handles PDF, as a vetted PDF library registration would.</summary>
    private sealed class PdfOnlyExtractor : IDocumentTextExtractor
    {
        public string? Extract(SessionFile file) => file.MediaType == "application/pdf" ? "HOST PDF TEXT" : null;
    }

    /// <summary>A host extractor that fails on PDF with its own exception type and delegates everything else.</summary>
    private sealed class ThrowingExtractor : IDocumentTextExtractor
    {
        public string? Extract(SessionFile file) =>
            file.MediaType == "application/pdf" ? throw new NotSupportedException("Encrypted PDF.") : new DocumentTextExtractor().Extract(file);
    }
}
