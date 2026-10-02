namespace SqlWright.DynamicQuery;

/// <summary>
/// The request was invalid: unknown or disallowed names, bad values, or exceeded limits. These are client errors
/// (HTTP 400); <see cref="ToDictionary"/> produces the shape ASP.NET Core's <c>Results.ValidationProblem</c> expects.
/// </summary>
public sealed class DynamicQueryException : SqlWrightException
{
    internal DynamicQueryException(IReadOnlyList<QueryError> errors)
        : base("The query request is invalid:" + string.Concat(errors.Select(e => $"\n  {e.Path}: {e.Message}")))
    {
        Errors = errors;
    }

    /// <summary>Every problem found, each with the JSON path of the offending part of the request.</summary>
    public IReadOnlyList<QueryError> Errors { get; }

    /// <summary>Errors grouped by path, for <c>Results.ValidationProblem(ex.ToDictionary())</c>.</summary>
    public IDictionary<string, string[]> ToDictionary() =>
        Errors.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
}

/// <summary>One problem with a query request.</summary>
/// <param name="Path">Where in the request, e.g. <c>where.conditions[1].field</c>.</param>
/// <param name="Message">What is wrong and, where possible, how to fix it.</param>
public sealed record QueryError(string Path, string Message);
