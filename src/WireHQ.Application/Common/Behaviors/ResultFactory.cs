using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Builds a failed <c>TResponse</c> (either <see cref="Result"/> or
/// <c>Result&lt;T&gt;</c>) from an <see cref="Error"/>. Lets pipeline behaviors short-circuit a
/// request with a typed failure without knowing the concrete response shape — so validation and
/// authorization stay as values in the Result flow, not thrown exceptions.
/// </summary>
internal static class ResultFactory
{
    public static TResponse Failure<TResponse>(Error error)
        where TResponse : Result
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return (TResponse)Result.Failure(error);
        }

        // Result<TValue>: invoke the generic Result.Failure<TValue>(Error) — unambiguous.
        var valueType = typeof(TResponse).GetGenericArguments()[0];
        var failure = typeof(Result)
            .GetMethods()
            .Single(m => m is { Name: nameof(Result.Failure), IsGenericMethod: true })
            .MakeGenericMethod(valueType)
            .Invoke(null, [error]);

        return (TResponse)failure!;
    }
}
