using Microsoft.EntityFrameworkCore;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Authentication;

/// <summary>
/// SQL Server-specific row-lock helpers used by authentication mutations.
/// Callers MUST invoke these inside an active explicit transaction; the
/// UPDLOCK acquired here is released only when that transaction commits or
/// rolls back.
/// </summary>
internal static class SqlServerAuthenticationLocks
{
    /// <summary>
    /// Acquires a SQL Server UPDLOCK on the AspNetUsers row for the given
    /// <paramref name="userId"/> inside the caller's current transaction. Any
    /// other transaction that requests an update-compatible lock on the same
    /// row blocks until the caller commits or rolls back, which serializes
    /// same-user authentication writers and eliminates the classic
    /// shared-to-exclusive lock conversion deadlock. ROWLOCK is a granularity
    /// preference for row-level locks; it does not guarantee that lock
    /// escalation cannot occur. The correctness mechanism is the UPDLOCK held
    /// by the explicit transaction, not ROWLOCK.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no explicit transaction is active on the database context;
    /// the UPDLOCK would otherwise be released as soon as the statement
    /// completes and would provide no serialization guarantee.
    /// </exception>
    public static Task AcquireUserUpdateLockAsync(
        OpsFlowDbContext db,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An authentication user lock can only be acquired inside an active transaction.");
        }

        return db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM AspNetUsers WITH (UPDLOCK, ROWLOCK) WHERE Id = {userId}",
            cancellationToken);
    }
}
