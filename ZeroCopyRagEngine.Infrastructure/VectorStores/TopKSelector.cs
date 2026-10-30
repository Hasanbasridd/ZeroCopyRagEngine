using System;
using System.Runtime.CompilerServices;

namespace ZeroCopyRagEngine.Infrastructure.VectorStores;

/// <summary>
/// Allocation-free Top-K selection primitives operating strictly on Span&lt;T&gt;.
/// Eliminates full O(N log N) sorting and Comparer&lt;T&gt; delegate allocations in vector search hot loops.
/// </summary>
internal static class TopKSelector
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InsertCandidate(
        float score,
        int index,
        Span<float> topScores,
        Span<int> topIndices,
        ref int currentCount,
        int maxK)
    {
        if (maxK <= 0)
            return;

        if (currentCount < maxK)
        {
            int pos = currentCount;
            while (pos > 0 && score > topScores[pos - 1])
            {
                topScores[pos] = topScores[pos - 1];
                topIndices[pos] = topIndices[pos - 1];
                pos--;
            }
            topScores[pos] = score;
            topIndices[pos] = index;
            currentCount++;
            return;
        }

        if (score <= topScores[maxK - 1])
            return;

        int insertPos = maxK - 1;
        while (insertPos > 0 && score > topScores[insertPos - 1])
        {
            topScores[insertPos] = topScores[insertPos - 1];
            topIndices[insertPos] = topIndices[insertPos - 1];
            insertPos--;
        }
        topScores[insertPos] = score;
        topIndices[insertPos] = index;
    }
}

