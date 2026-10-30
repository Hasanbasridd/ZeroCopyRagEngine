using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Entities;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.VectorStores;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) Approximate Nearest Neighbor (ANN) vector store.
/// Combines a multi-layer proximity graph with TensorPrimitives.CosineSimilarity for sub-linear O(log N) search.
/// </summary>
public sealed class HnswVectorDatabase : IVectorDatabase
{
    private sealed class HnswNode
    {
        public readonly int Id;
        public readonly TextChunk Chunk;
        public readonly int MaxLayer;
        public readonly List<int>[] Connections;

        [SuppressMessage("Performance", "HAA0501", Justification = "Graph node construction allocates layer adjacency lists once at insertion time.")]
        [SuppressMessage("Performance", "HAA0502", Justification = "Graph node construction allocates layer adjacency lists once at insertion time.")]
        public HnswNode(int id, TextChunk chunk, int maxLayer, int maxConnectionsPerLayer)
        {
            Id = id;
            Chunk = chunk;
            MaxLayer = maxLayer;
            Connections = new List<int>[maxLayer + 1];
            for (int l = 0; l <= maxLayer; l++)
            {
                Connections[l] = new List<int>(maxConnectionsPerLayer + 1);
            }
        }
    }

    private readonly int _m;
    private readonly int _mMax0;
    private readonly int _efConstruction;
    private readonly int _efSearch;
    private readonly double _levelMult;
    private readonly Random _rng;
    private readonly object _syncLock;

    private readonly List<HnswNode> _nodes;
    private float[] _vectorStore = Array.Empty<float>();
    private int _dimension;
    private int _entryPointId = -1;
    private int _maxLayer = -1;
    private int _visitTagCounter = 1;
    private int[] _visitedTags = Array.Empty<int>();

    [SuppressMessage("Performance", "HAA0502", Justification = "One-time HNSW graph index initialization.")]
    public HnswVectorDatabase(int m = 16, int efConstruction = 64, int efSearch = 48)
    {
        _m = Math.Max(4, m);
        _mMax0 = _m * 2;
        _efConstruction = Math.Max(_m, efConstruction);
        _efSearch = Math.Max(8, efSearch);
        _levelMult = 1.0 / Math.Log(_m);
        _rng = new Random(42);
        _syncLock = new object();
        _nodes = new List<HnswNode>();
    }

    public int Count => _nodes.Count;
    public int Dimension => _dimension;

    private int SampleRandomLayer()
    {
        double u = Math.Max(1e-9, _rng.NextDouble());
        return (int)(-Math.Log(u) * _levelMult);
    }

    [SuppressMessage("Performance", "HAA0501", Justification = "Index construction path grows the contiguous vector store and visited tag table.")]
    [SuppressMessage("Performance", "HAA0502", Justification = "Index construction path creates HNSW graph nodes during ingestion.")]
    public Task UpsertChunksAsync(IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
            return Task.CompletedTask;

        lock (_syncLock)
        {
            if (_dimension == 0)
                _dimension = chunks[0].Embedding.Length;

            int dim = _dimension;
            int oldCount = _nodes.Count;
            int newTotal = oldCount + chunks.Count;

            if (_vectorStore.Length < newTotal * dim)
            {
                int newCap = Math.Max(newTotal, Math.Max(64, _vectorStore.Length / Math.Max(1, dim) * 2));
                float[] newVectors = new float[newCap * dim];
                if (oldCount > 0)
                {
                    Array.Copy(_vectorStore, newVectors, oldCount * dim);
                }
                _vectorStore = newVectors;

                int[] newVisited = new int[newCap];
                if (oldCount > 0)
                {
                    Array.Copy(_visitedTags, newVisited, oldCount);
                }
                _visitedTags = newVisited;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                int nodeId = _nodes.Count;
                Array.Copy(chunk.Embedding, 0, _vectorStore, nodeId * dim, dim);

                int level = SampleRandomLayer();
                var newNode = new HnswNode(nodeId, chunk, level, _mMax0);
                _nodes.Add(newNode);

                if (_entryPointId == -1)
                {
                    _entryPointId = nodeId;
                    _maxLayer = level;
                    continue;
                }

                InsertNodeIntoGraph(newNode);
            }
        }

        return Task.CompletedTask;
    }

    private void InsertNodeIntoGraph(HnswNode newNode)
    {
        int dim = _dimension;
        ReadOnlySpan<float> queryVec = new ReadOnlySpan<float>(_vectorStore, newNode.Id * dim, dim);

        int currObj = _entryPointId;
        float currSim = ComputeSimilarity(queryVec, currObj);

        for (int layer = _maxLayer; layer > newNode.MaxLayer; layer--)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                var neighbors = _nodes[currObj].Connections[layer];
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int candidateId = neighbors[i];
                    float sim = ComputeSimilarity(queryVec, candidateId);
                    if (sim > currSim)
                    {
                        currSim = sim;
                        currObj = candidateId;
                        changed = true;
                    }
                }
            }
        }

        int topConnectLayer = Math.Min(_maxLayer, newNode.MaxLayer);
        float[] candScores = ArrayPool<float>.Shared.Rent(_efConstruction);
        int[] candIds = ArrayPool<int>.Shared.Rent(_efConstruction);

        try
        {
            for (int layer = topConnectLayer; layer >= 0; layer--)
            {
                Span<float> scoresSpan = candScores.AsSpan(0, _efConstruction);
                Span<int> idsSpan = candIds.AsSpan(0, _efConstruction);

                int foundCount = SearchLayer(queryVec, currObj, _efConstruction, layer, scoresSpan, idsSpan, filter: null);
                int maxM = layer == 0 ? _mMax0 : _m;
                int connectCount = Math.Min(maxM, foundCount);

                for (int i = 0; i < connectCount; i++)
                {
                    int neighborId = idsSpan[i];
                    if (neighborId == newNode.Id)
                        continue;

                    newNode.Connections[layer].Add(neighborId);
                    var neighborConn = _nodes[neighborId].Connections[layer];
                    neighborConn.Add(newNode.Id);

                    if (neighborConn.Count > maxM)
                    {
                        PruneConnections(neighborId, neighborConn, maxM);
                    }
                }

                if (foundCount > 0)
                {
                    currObj = idsSpan[0];
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(candScores);
            ArrayPool<int>.Shared.Return(candIds);
        }

        if (newNode.MaxLayer > _maxLayer)
        {
            _maxLayer = newNode.MaxLayer;
            _entryPointId = newNode.Id;
        }
    }

    private void PruneConnections(int nodeId, List<int> connections, int maxM)
    {
        int count = connections.Count;
        float[] scores = ArrayPool<float>.Shared.Rent(count);
        int[] ids = ArrayPool<int>.Shared.Rent(count);

        try
        {
            ReadOnlySpan<float> nodeVec = new ReadOnlySpan<float>(_vectorStore, nodeId * _dimension, _dimension);
            Span<float> scoresSpan = scores.AsSpan(0, maxM);
            Span<int> idsSpan = ids.AsSpan(0, maxM);
            int selected = 0;

            for (int i = 0; i < count; i++)
            {
                int targetId = connections[i];
                float sim = ComputeSimilarity(nodeVec, targetId);
                TopKSelector.InsertCandidate(sim, targetId, scoresSpan, idsSpan, ref selected, maxM);
            }

            connections.Clear();
            for (int i = 0; i < selected; i++)
            {
                connections.Add(idsSpan[i]);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scores);
            ArrayPool<int>.Shared.Return(ids);
        }
    }

    private float ComputeSimilarity(ReadOnlySpan<float> queryVec, int nodeId)
    {
        ReadOnlySpan<float> targetVec = new ReadOnlySpan<float>(_vectorStore, nodeId * _dimension, _dimension);
        return TensorPrimitives.CosineSimilarity(queryVec, targetVec);
    }

    private int SearchLayer(
        ReadOnlySpan<float> queryVec,
        int entryPointId,
        int ef,
        int layer,
        Span<float> topScores,
        Span<int> topIds,
        MetadataFilter? filter)
    {
        int tag = ++_visitTagCounter;
        if (tag == int.MaxValue)
        {
            Array.Clear(_visitedTags, 0, _visitedTags.Length);
            _visitTagCounter = 1;
            tag = 1;
        }

        int[] frontierQueue = ArrayPool<int>.Shared.Rent(ef * 4);
        int frontierHead = 0;
        int frontierTail = 0;
        int selectedCount = 0;

        try
        {
            _visitedTags[entryPointId] = tag;
            float entrySim = ComputeSimilarity(queryVec, entryPointId);
            if (filter is null || filter.Matches(_nodes[entryPointId].Chunk.Metadata))
            {
                TopKSelector.InsertCandidate(entrySim, entryPointId, topScores, topIds, ref selectedCount, ef);
            }
            frontierQueue[frontierTail++] = entryPointId;

            while (frontierHead < frontierTail)
            {
                int currentId = frontierQueue[frontierHead++];
                var neighbors = _nodes[currentId].Connections[layer];

                for (int i = 0; i < neighbors.Count; i++)
                {
                    int neighborId = neighbors[i];
                    if (_visitedTags[neighborId] == tag)
                        continue;

                    _visitedTags[neighborId] = tag;
                    float sim = ComputeSimilarity(queryVec, neighborId);

                    bool shouldExplore = selectedCount < ef || sim > topScores[selectedCount - 1];
                    if (shouldExplore)
                    {
                        if (filter is null || filter.Matches(_nodes[neighborId].Chunk.Metadata))
                        {
                            TopKSelector.InsertCandidate(sim, neighborId, topScores, topIds, ref selectedCount, ef);
                        }

                        if (frontierTail < frontierQueue.Length)
                        {
                            frontierQueue[frontierTail++] = neighborId;
                        }
                    }
                }
            }

            return selectedCount;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(frontierQueue);
        }
    }

    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken cancellationToken = default)
        => SearchSimilarAsync(queryVector, topK, filter: null, cancellationToken);

    [SuppressMessage("Performance", "HAA0502", Justification = "Materializes the final List<ScoredChunk> return value; HNSW graph traversal uses pooled buffers and TensorPrimitives.CosineSimilarity without heap allocations in the inner loop.")]
    public Task<IReadOnlyList<ScoredChunk>> SearchSimilarAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter,
        CancellationToken cancellationToken = default)
    {
        if (_nodes.Count == 0 || topK <= 0 || _entryPointId < 0)
            return Task.FromResult<IReadOnlyList<ScoredChunk>>(Array.Empty<ScoredChunk>());

        lock (_syncLock)
        {
            ReadOnlySpan<float> querySpan = queryVector.Span;
            int currObj = _entryPointId;
            float currSim = ComputeSimilarity(querySpan, currObj);

            for (int layer = _maxLayer; layer >= 1; layer--)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    var neighbors = _nodes[currObj].Connections[layer];
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int candidateId = neighbors[i];
                        float sim = ComputeSimilarity(querySpan, candidateId);
                        if (sim > currSim)
                        {
                            currSim = sim;
                            currObj = candidateId;
                            changed = true;
                        }
                    }
                }
            }

            int ef = Math.Max(_efSearch, topK);
            float[] topScores = ArrayPool<float>.Shared.Rent(ef);
            int[] topIds = ArrayPool<int>.Shared.Rent(ef);

            try
            {
                Span<float> scoresSpan = topScores.AsSpan(0, ef);
                Span<int> idsSpan = topIds.AsSpan(0, ef);

                int found = SearchLayer(querySpan, currObj, ef, layer: 0, scoresSpan, idsSpan, filter);
                int resultCount = Math.Min(topK, found);

                var results = new List<ScoredChunk>(resultCount);
                for (int i = 0; i < resultCount; i++)
                {
                    var node = _nodes[idsSpan[i]];
                    results.Add(new ScoredChunk(node.Chunk, scoresSpan[i]));
                }

                return Task.FromResult<IReadOnlyList<ScoredChunk>>(results);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(topScores);
                ArrayPool<int>.Shared.Return(topIds);
            }
        }
    }
}
