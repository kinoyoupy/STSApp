namespace STSApp.Backend.Services.Rag;

/// <summary>
/// Embedding同士の近さを比べるための小さな計算集です。
/// 保存時と検索時にどちらも正規化することで、内積をコサイン類似度として扱えます。
/// </summary>
public static class VectorMath
{
    public static float[] Normalize(IReadOnlyList<float> vector)
    {
        if (vector.Count == 0)
        {
            throw new InvalidOperationException("Embeddingベクトルが空です。");
        }

        double sumOfSquares = 0;
        foreach (var value in vector)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException("Embeddingベクトルに計算できない値が含まれています。");
            }

            sumOfSquares += value * value;
        }

        var length = Math.Sqrt(sumOfSquares);
        if (length <= double.Epsilon)
        {
            throw new InvalidOperationException("Embeddingベクトルの長さが0です。");
        }

        return vector.Select(value => (float)(value / length)).ToArray();
    }

    public static double CosineSimilarityOfNormalizedVectors(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        if (left.Count != right.Count)
        {
            throw new InvalidOperationException($"Embedding次元数が一致しません。left={left.Count}, right={right.Count}");
        }

        double dotProduct = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dotProduct += left[index] * right[index];
        }

        return dotProduct;
    }
}
