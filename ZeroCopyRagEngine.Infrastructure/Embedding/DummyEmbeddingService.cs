using System;
using System.Collections.Generic;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using ZeroCopyRagEngine.Domain.Ports;

namespace ZeroCopyRagEngine.Infrastructure.Embedding;

/// <summary>
/// Deterministic, zero-dependency feature-hashing embedding service (384 dimensions).
/// Projects words and character 3-grams into a L2-normalized vector space so semantically/lexically
/// related passages have high cosine similarity in local tests, evaluations, and offline runs.
/// </summary>
public sealed class DummyEmbeddingService : IEmbeddingService
{
    public const int DefaultDimension = 384;
    private readonly int _dimension;

    public DummyEmbeddingService(int dimension = DefaultDimension)
    {
        _dimension = Math.Max(8, dimension);
    }

    public Task<float[]> GenerateEmbeddingAsync(ReadOnlyMemory<char> text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float[] vec = new float[_dimension];
        ReadOnlySpan<char> span = text.Span;

        if (span.IsEmpty)
        {
            vec[0] = 1.0f;
            return Task.FromResult(vec);
        }

        int tokenStart = -1;
        for (int i = 0; i <= span.Length; i++)
        {
            bool isWordChar = i < span.Length && char.IsLetterOrDigit(span[i]);
            if (isWordChar)
            {
                if (tokenStart < 0)
                    tokenStart = i;
            }
            else if (tokenStart >= 0)
            {
                int len = i - tokenStart;
                if (len >= 2)
                {
                    ReadOnlySpan<char> word = span.Slice(tokenStart, len);
                    AccumulateFeature(vec, word, weight: 2.0f);

                    // Add character 3-grams for morphological / subword robustness
                    for (int g = 0; g <= word.Length - 3; g++)
                    {
                        AccumulateFeature(vec, word.Slice(g, 3), weight: 0.75f);
                    }
                }
                tokenStart = -1;
            }
        }

        float norm = TensorPrimitives.Norm(vec);
        if (norm > 1e-8f)
        {
            TensorPrimitives.Divide(vec, norm, vec);
        }
        else
        {
            vec[0] = 1.0f;
        }

        return Task.FromResult(vec);
    }

    public async Task<List<float[]>> GenerateEmbeddingsBatchAsync(IReadOnlyList<ReadOnlyMemory<char>> texts, CancellationToken cancellationToken = default)
    {
        var list = new List<float[]>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            list.Add(await GenerateEmbeddingAsync(texts[i], cancellationToken));
        }
        return list;
    }

    private static void AccumulateFeature(float[] vector, ReadOnlySpan<char> token, float weight)
    {
        uint hash1 = 2166136261u;
        uint hash2 = 2166136261u ^ 0x9E3779B9u;

        for (int i = 0; i < token.Length; i++)
        {
            char c = char.ToLowerInvariant(token[i]);
            hash1 = (hash1 ^ c) * 16777619u;
            hash2 = (hash2 ^ (uint)(c * 31)) * 16777619u;
        }

        int dim = vector.Length;
        int idx1 = (int)(hash1 % (uint)dim);
        float sign1 = (hash1 & 1u) == 0 ? 1f : -1f;
        vector[idx1] += sign1 * weight;

        int idx2 = (int)(hash2 % (uint)dim);
        float sign2 = (hash2 & 1u) == 0 ? 1f : -1f;
        vector[idx2] += sign2 * (weight * 0.5f);
    }
}
