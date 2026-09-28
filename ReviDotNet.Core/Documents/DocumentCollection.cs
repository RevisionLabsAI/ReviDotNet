using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Revi;

/// <summary>Immutable extracted source text. Offsets always refer to this exact version of this text.</summary>
public sealed record SearchDocument(string Id, string Text, string Reference, string? Version = null);

/// <summary>An exact source substring with stable identity and UTF-16 offsets.</summary>
public sealed record DocumentPassage(string Id, string DocumentId, string SourceVersion, string Reference,
    int Start, int Length, int StartLine, string Text)
{
    /// <summary>Whether offsets refer to the extracted document or an externally supplied chunk.</summary>
    public string OffsetBasis { get; init; } = "document";
}

/// <summary>Immutable per-run or tenant-authorized index. There is no process-wide current collection.</summary>
public sealed class DocumentCollection
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> _terms;
    private readonly IReadOnlyDictionary<string, int> _documentFrequency;
    private readonly float[][]? _vectors;
    private readonly double _averageLength;
    private bool _retrievedCandidates;
    /// <summary>Collection identity supplied by its owner.</summary>
    public string Id { get; }
    /// <summary>Stable original passages.</summary>
    public IReadOnlyList<DocumentPassage> Passages { get; }
    /// <summary>Embedding model used when indexing, or null for lexical-only retrieval.</summary>
    public string? EmbeddingModel { get; }

    /// <summary>Builds a reusable text index. Pass only documents the collection's owner may read.</summary>
    public DocumentCollection(string id, IEnumerable<SearchDocument> documents, int chunkCharacters = 1800)
        : this(id, Chunk(documents, chunkCharacters), null, null) { }

    /// <summary>Wraps an already authorized retrieval shortlist without re-filtering it lexically or changing IDs.</summary>
    public static DocumentCollection FromRetrievedPassages(string id, IEnumerable<DocumentPassage> passages)
    {
        DocumentPassage[] snapshot = passages.ToArray();
        if (snapshot.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.DocumentId) || string.IsNullOrWhiteSpace(p.SourceVersion) || p.Start < 0 || p.Length != p.Text.Length))
            throw new ArgumentException("Passages require stable identities and consistent source spans.");
        return new DocumentCollection(id, snapshot, null, null) { _retrievedCandidates = true };
    }

    /// <summary>Creates an immutable lexical/vector index.</summary>
    private DocumentCollection(string id, IReadOnlyList<DocumentPassage> passages, string? embeddingModel, float[][]? vectors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Passages = new ReadOnlyCollection<DocumentPassage>(passages.ToArray());
        EmbeddingModel = embeddingModel;
        _vectors = vectors?.Select(v => v.ToArray()).ToArray();
        Dictionary<string, IReadOnlyDictionary<string, int>> terms = [];
        Dictionary<string, int> frequency = new(StringComparer.Ordinal);
        foreach (DocumentPassage passage in Passages)
        {
            Dictionary<string, int> counts = Tokens(passage.Text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            terms.Add(passage.Id, counts);
            foreach (string term in counts.Keys) frequency[term] = frequency.GetValueOrDefault(term) + 1;
        }
        _terms = terms;
        _documentFrequency = frequency;
        _averageLength = Math.Max(1, terms.Values.Select(c => c.Values.Sum()).DefaultIfEmpty(1).Average());
    }

    /// <summary>Embeds passages once, returning a new index; the original remains lexical-only.</summary>
    public async Task<DocumentCollection> WithEmbeddingsAsync(IEmbedService embeddings, string model, CancellationToken token = default)
    {
        if (Passages.Count == 0) return new DocumentCollection(Id, Passages, model, []);
        List<float[]>? vectors = await embeddings.GenerateBatch(Passages.Select(p => p.Text), model, token).ConfigureAwait(false);
        if (vectors is null || vectors.Count != Passages.Count || vectors.Any(v => v.Length == 0 || v.Any(x => !float.IsFinite(x))) || vectors.Select(v => v.Length).Distinct().Count() != 1)
            throw new InvalidDataException("Embedding provider did not return one valid vector per passage.");
        return new DocumentCollection(Id, Passages, model, vectors.ToArray());
    }

    /// <summary>Fuses BM25 and optional vector ranks with reciprocal rank fusion.</summary>
    internal IReadOnlyList<(DocumentPassage Passage, double Score)> Retrieve(string query, int count, float[]? vector)
    {
        if (_retrievedCandidates) return Passages.Take(count).Select((p, i) => (p, 1d / (61 + i))).ToArray();
        string[] queryTerms = Tokens(query).Distinct().ToArray();
        List<(int Index, double Score)> lexical = [];
        for (int i = 0; i < Passages.Count; i++)
        {
            IReadOnlyDictionary<string, int> terms = _terms[Passages[i].Id];
            int length = terms.Values.Sum();
            double score = 0;
            foreach (string term in queryTerms)
            {
                if (!terms.TryGetValue(term, out int frequency)) continue;
                double idf = Math.Log(1 + (Passages.Count - _documentFrequency[term] + 0.5) / (_documentFrequency[term] + 0.5));
                score += idf * frequency * 2.2 / (frequency + 1.2 * (0.25 + 0.75 * length / _averageLength));
            }
            if (score > 0) lexical.Add((i, score));
        }
        Dictionary<int, double> fused = [];
        AddRanks(lexical.OrderByDescending(x => x.Score).ThenBy(x => x.Index).Take(count), fused);
        if (vector is not null && _vectors is not null && _vectors.Length > 0)
        {
            if (vector.Length != _vectors[0].Length || vector.Any(x => !float.IsFinite(x))) throw new ArgumentException("Query vector does not match the index.");
            AddRanks(_vectors.Select((v, i) => (Index: i, Score: Cosine(v, vector))).Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Index).Take(count), fused);
        }
        return fused.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(count).Select(p => (Passages[p.Key], p.Value)).ToArray();
    }

    /// <summary>Adds ranks without comparing incompatible vector and BM25 score scales.</summary>
    private static void AddRanks(IEnumerable<(int Index, double Score)> ranked, Dictionary<int, double> fused)
    {
        int rank = 0;
        foreach ((int index, double _) in ranked) fused[index] = fused.GetValueOrDefault(index) + 1d / (60 + ++rank);
    }
    /// <summary>Computes finite cosine similarity.</summary>
    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb);
    }
    /// <summary>Splits at line boundaries when practical, copying exact substrings without normalization.</summary>
    private static IReadOnlyList<DocumentPassage> Chunk(IEnumerable<SearchDocument> documents, int size)
    {
        if (size < 128 || size > 32000) throw new ArgumentOutOfRangeException(nameof(size));
        List<DocumentPassage> passages = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (SearchDocument doc in documents)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(doc.Id);
            if (!ids.Add(doc.Id)) throw new ArgumentException("Document IDs must be unique within a collection.");
            string version = doc.Version ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Text))).ToLowerInvariant();
            int start = 0, line = 1;
            while (start < doc.Text.Length)
            {
                int end = Math.Min(doc.Text.Length, start + size);
                if (end < doc.Text.Length)
                {
                    int boundary = doc.Text.LastIndexOf('\n', end - 1, end - start);
                    if (boundary > start + size / 2) end = boundary + 1;
                    if (end > start && char.IsHighSurrogate(doc.Text[end - 1])) end--;
                }
                string text = doc.Text[start..end];
                string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{doc.Id}\0{version}\0{start}\0{end}"))).ToLowerInvariant();
                passages.Add(new DocumentPassage(id, doc.Id, version, doc.Reference, start, end - start, line, text));
                line += text.Count(c => c == '\n');
                start = end;
            }
        }
        return passages;
    }
    /// <summary>Deterministic Unicode word tokens.</summary>
    private static IEnumerable<string> Tokens(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant).Select(m => m.Value);
}
