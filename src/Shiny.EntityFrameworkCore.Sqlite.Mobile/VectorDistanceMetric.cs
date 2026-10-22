namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>
/// The distance function used by <c>EF.Functions.VectorDistance</c> and by a <c>vec0</c> vector index. Smaller is
/// always closer, so order ascending for nearest-first.
/// </summary>
public enum VectorDistanceMetric
{
    /// <summary>Cosine distance (<c>1 - cosine similarity</c>), in [0, 2]. The usual choice for text embeddings.</summary>
    Cosine,

    /// <summary>Euclidean (L2) distance.</summary>
    Euclidean,

    /// <summary>Manhattan (L1) distance - the sum of absolute differences.</summary>
    Manhattan
}
