using STSApp.Backend.Services.Rag;

namespace STSApp.Backend.Tests;

public sealed class VectorMathTests
{
    [Fact]
    public void Normalize_makes_vector_length_one()
    {
        var vector = VectorMath.Normalize([3f, 4f]);

        Assert.Equal(1d, Math.Sqrt(vector.Sum(value => value * value)), precision: 6);
    }

    [Fact]
    public void Cosine_similarity_of_normalized_vectors_orders_near_vectors_first()
    {
        var query = VectorMath.Normalize([1f, 0f]);
        var near = VectorMath.Normalize([0.9f, 0.1f]);
        var far = VectorMath.Normalize([0f, 1f]);

        var nearScore = VectorMath.CosineSimilarityOfNormalizedVectors(query, near);
        var farScore = VectorMath.CosineSimilarityOfNormalizedVectors(query, far);

        Assert.True(nearScore >= 0.70d);
        Assert.True(farScore < 0.70d);
        Assert.True(nearScore > farScore);
    }

    [Fact]
    public void Cosine_similarity_rejects_dimension_mismatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            VectorMath.CosineSimilarityOfNormalizedVectors([1f], [1f, 0f]));
    }
}
