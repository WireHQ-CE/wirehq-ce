namespace WireHQ.Infrastructure.Persistence;

/// <summary>
/// Thrown when the database is in a state the shipped migration chain cannot safely be applied to, BEFORE any
/// DDL runs. Carries a preformatted, operator-readable <see cref="OperatorMessage"/>: the migrate job is the
/// first thing a self-hoster meets when an update goes wrong, and a stack trace there reads as "the software
/// is broken" — which pushes people toward destructive guesses (docs/37 §3.4).
/// </summary>
public sealed class DatabaseMigrationPreconditionException(string operatorMessage, string summary)
    : Exception(summary)
{
    /// <summary>Plain, boxed text written straight to stderr by the <c>--migrate</c> call site. No stack.</summary>
    public string OperatorMessage { get; } = operatorMessage;

    /// <summary>Formats a refusal in the fixed order docs/37 §3.4 mandates: what was found · nothing was
    /// touched · how to read the logs · never <c>-v</c> · what restore is and is not for · the real fix.</summary>
    public static DatabaseMigrationPreconditionException Create(string summary, string found, string remedy)
    {
        var message = $"""

            ┌─────────────────────────────────────────────────────────────────────────────┐
            │  WireHQ could not update your database — and has changed NOTHING.           │
            └─────────────────────────────────────────────────────────────────────────────┘

            WHAT WE FOUND
            {Indent(found)}

            YOUR DATA IS UNTOUCHED
              No migration ran. No table was created, altered or dropped. Your database is
              exactly as it was before you started this update, and your previous version
              still runs against it.

            WHAT TO DO
            {Indent(remedy)}

            TO SEE THE FULL LOG
              docker compose -f deploy/docker-compose.yml logs migrate

            ⚠  Do NOT run 'docker compose down -v'. The -v flag DELETES your database
               volume. A stopped update is not a volume problem, and nothing here is fixed
               by starting over.

               'deploy/restore-db.sh' restores a backup to the SAME version it was taken
               from. It is not an upgrade path and will not resolve this on its own.

            """;

        return new DatabaseMigrationPreconditionException(message, summary);
    }

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? l : "  " + l));
}
