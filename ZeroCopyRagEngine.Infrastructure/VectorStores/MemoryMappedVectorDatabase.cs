using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics.Tensors;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.VectorStores;

/// <summary>
/// Persistent zero-copy vector database backed by an OS MemoryMappedFile.
/// Vector payloads are stored contiguously after a 16-byte binary header (Magic 'ZCRV', Version, Count, Dimension)
/// and read directly from the OS page cache as a ReadOnlySpan&lt;float&gt; without copying into managed arrays.
/// </summary>
public sealed unsafe class MemoryMappedVectorDatabase : IVectorDatabase, IDisposable
{
    private const uint MagicHeader = 0x5652435A; // 'ZCRV'
    private const int FormatVersion = 1;
    private const int HeaderSizeBytes = 16;

    private readonly string _vectorFilePath;
    private readonly string _metadataFilePath;
    private readonly object _syncLock;
    private readonly Dictionary<string, TextChunk> _chunkCatalog;
    private readonly List<string> _vectorIds;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private int _vectorCount;
    private int _vectorDimension;
    private bool _disposed;

    [SuppressMessage("Performance", "HAA0502", Justification = "Constructor initializes file paths and loads existing index metadata once.")]
    public MemoryMappedVectorDatabase(string vectorFilePath)
    {
        if (string.IsNullOrWhiteSpace(vectorFilePath))
            throw new ArgumentException("Vector file path cannot be empty.", nameof(vectorFilePath));

        _syncLock = new object();
        _chunkCatalog = new Dictionary<string, TextChunk>(StringComparer.Ordinal);
        _vectorIds = new List<string>();
        _vectorFilePath = Path.GetFullPath(vectorFilePath);
        _metadataFilePath = _vectorFilePath + ".meta";

        string? dir = Path.GetDirectoryName(_vectorFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        LoadExistingIfPresent();
    }

    public int Count => _vectorCount;
    public int Dimension => _vectorDimension;

    [SuppressMessage("Performance", "HAA0501", Justification = "Cold startup path: reads persisted metadata and vector headers from disk.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "Cold startup path: reconstructs metadata dictionary and opens MemoryMappedFile.")]
    private void LoadExistingIfPresent()
    {
        if (!File.Exists(_vectorFilePath) || !File.Exists(_metadataFilePath))
            return;

        using (var fs = new FileStream(_vectorFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length < HeaderSizeBytes)
                return;

            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            uint magic = br.ReadUInt32();
            int version = br.ReadInt32();
            int count = br.ReadInt32();
            int dim = br.ReadInt32();

            if (magic != MagicHeader || version != FormatVersion || count <= 0 || dim <= 0)
                return;

            _vectorCount = count;
            _vectorDimension = dim;
        }

        using (var metaFs = new FileStream(_metadataFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var metaReader = new BinaryReader(metaFs, Encoding.UTF8))
        {
            int metaCount = metaReader.ReadInt32();
            _vectorIds.Clear();
            _chunkCatalog.Clear();

            for (int i = 0; i < metaCount; i++)
            {
                string chunkId = metaReader.ReadString();
                string content = metaReader.ReadString();
                string fileId = metaReader.ReadString();
                string fileName = metaReader.ReadString();
                int pageNumber = metaReader.ReadInt32();
                int chunkIndex = metaReader.ReadInt32();
                string source = metaReader.ReadString();
                long createdTicks = metaReader.ReadInt64();
                int tagCount = metaReader.ReadInt32();
                string[]? tags = tagCount > 0 ? new string[tagCount] : null;
                for (int t = 0; t < tagCount; t++)
                {
                    tags![t] = metaReader.ReadString();
                }

                var meta = new DocumentMetadata(
                    fileId,
                    fileName,
                    pageNumber,
                    chunkIndex,
                    string.IsNullOrEmpty(source) ? null : source,
                    createdTicks > 0 ? new DateTimeOffset(createdTicks, TimeSpan.Zero) : null,
                    tags);

                var chunk = new TextChunk(chunkId, content.AsMemory(), Array.Empty<float>(), meta);
                _vectorIds.Add(chunkId);
                _chunkCatalog[chunkId] = chunk;
            }
        }

        OpenMemoryMapping();
    }

    [SuppressMessage("Performance", "HAA0502", Justification = "Creates the OS memory mapping once after an upsert or load.")]
    private void OpenMemoryMapping()
    {
        CloseMemoryMapping();
        if (_vectorCount <= 0 || _vectorDimension <= 0 || !File.Exists(_vectorFilePath))
            return;

        var fileStream = new FileStream(
            _vectorFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        _mmf = MemoryMappedFile.CreateFromFile(
            fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: false);

        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    private void CloseMemoryMapping()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    [SuppressMessage("Performance", "HAA0501", Justification = "Disk ingestion path: appends vectors to the binary file and refreshes the memory mapping.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "Disk ingestion path: uses FileStream and BinaryWriter outside the search hot loop.")]
    public Task UpsertChunksAsync(IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return Task.CompletedTask;

        lock (_syncLock)
        {
            if (_vectorDimension == 0)
                _vectorDimension = chunks[0].Embedding.Length;

            int dim = _vectorDimension;
            int existingCount = _vectorCount;
            int newTotal = existingCount + chunks.Count;

            float[] existingVectors = Array.Empty<float>();
            if (existingCount > 0 && _accessor is not null)
            {
                existingVectors = new float[existingCount * dim];
                byte* basePtr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                try
                {
                    float* floatPtr = (float*)(basePtr + _accessor.PointerOffset + HeaderSizeBytes);
                    new ReadOnlySpan<float>(floatPtr, existingCount * dim).CopyTo(existingVectors);
                }
                finally
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            CloseMemoryMapping();

            using (var fs = new FileStream(_vectorFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(MagicHeader);
                bw.Write(FormatVersion);
                bw.Write(newTotal);
                bw.Write(dim);

                for (int i = 0; i < existingVectors.Length; i++)
                {
                    bw.Write(existingVectors[i]);
                }

                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    float[] emb = chunk.Embedding;
                    for (int j = 0; j < dim; j++)
                    {
                        bw.Write(j < emb.Length ? emb[j] : 0f);
                    }
                    _vectorIds.Add(chunk.ChunkId);
                    _chunkCatalog[chunk.ChunkId] = chunk;
                }

                bw.Flush();
            }

            _vectorCount = newTotal;
            SaveMetadataFile();
            OpenMemoryMapping();
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Performance", "HAA0502", Justification = "Cold persistence path: writes chunk metadata companion file during ingestion.")]
    private void SaveMetadataFile()
    {
        using var metaFs = new FileStream(_metadataFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(metaFs, Encoding.UTF8);

        bw.Write(_vectorIds.Count);
        for (int i = 0; i < _vectorIds.Count; i++)
        {
            string id = _vectorIds[i];
            var chunk = _chunkCatalog[id];
            var meta = chunk.Metadata;

            bw.Write(chunk.ChunkId);
            bw.Write(chunk.Content.Span.ToString());
            bw.Write(meta.FileId);
            bw.Write(meta.FileName);
            bw.Write(meta.PageNumber);
            bw.Write(meta.ChunkIndex);
            bw.Write(meta.Source ?? string.Empty);
            bw.Write(meta.CreatedAt?.UtcDateTime.Ticks ?? 0L);

            int tagCount = meta.Tags?.Count ?? 0;
            bw.Write(tagCount);
            for (int t = 0; t < tagCount; t++)
            {
                bw.Write(meta.Tags![t]);
            }
        }
        bw.Flush();
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken cancellationToken = default)
        => SearchSimilarAsync(queryVector, topK, filter: null, cancellationToken);

    [SuppressMessage("Performance", "HAA0502", Justification = "Result materialization allocates the final List<ScoredChunk> and ScoredChunk items; the memory-mapped pointer scan and SIMD CosineSimilarity loop itself performs 0 heap allocations.")]
    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter,
        CancellationToken cancellationToken = default)
    {
        if (_vectorCount == 0 || topK <= 0 || _accessor is null)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        lock (_syncLock)
        {
            if (_vectorCount == 0 || _accessor is null)
                return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

            int count = _vectorCount;
            int dim = _vectorDimension;
            int effectiveK = Math.Min(topK, count);
            ReadOnlySpan<float> querySpan = queryVector.Span;

            float[] topScores = ArrayPool<float>.Shared.Rent(effectiveK);
            int[] topIndices = ArrayPool<int>.Shared.Rent(effectiveK);

            byte* basePtr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            try
            {
                float* vectorBasePtr = (float*)(basePtr + _accessor.PointerOffset + HeaderSizeBytes);
                Span<float> scoresSpan = topScores.AsSpan(0, effectiveK);
                Span<int> indicesSpan = topIndices.AsSpan(0, effectiveK);
                int selectedCount = 0;

                for (int i = 0; i < count; i++)
                {
                    if (filter is not null)
                    {
                        string candidateId = _vectorIds[i];
                        if (!_chunkCatalog.TryGetValue(candidateId, out var candidateChunk) || !filter.Matches(candidateChunk.Metadata))
                            continue;
                    }

                    ReadOnlySpan<float> mappedVector = new ReadOnlySpan<float>(vectorBasePtr + (i * dim), dim);
                    float score = TensorPrimitives.CosineSimilarity(querySpan, mappedVector);
                    TopKSelector.InsertCandidate(score, i, scoresSpan, indicesSpan, ref selectedCount, effectiveK);
                }

                var results = new List<ScoredChunk>(selectedCount);
                for (int i = 0; i < selectedCount; i++)
                {
                    int idx = indicesSpan[i];
                    string id = _vectorIds[idx];
                    if (_chunkCatalog.TryGetValue(id, out var chunk))
                    {
                        results.Add(new ScoredChunk(chunk, scoresSpan[i]));
                    }
                }

                return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
            }
            finally
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                ArrayPool<float>.Shared.Return(topScores);
                ArrayPool<int>.Shared.Return(topIndices);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_syncLock)
        {
            CloseMemoryMapping();
            _disposed = true;
        }
    }
}
