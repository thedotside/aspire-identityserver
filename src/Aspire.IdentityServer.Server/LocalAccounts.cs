using Duende.IdentityServer.Test;

namespace Aspire.IdentityServer.Server;

/// <summary>
/// The accounts of this provider: those declared in the AppHost and those created on the sign-up
/// page. Each creation publishes a new snapshot that is never modified afterwards, so a request
/// reads a stable store, and every account created before it.
/// </summary>
internal sealed class LocalAccounts
{
    private readonly Lock gate = new();
    private Snapshot snapshot;

    internal LocalAccounts(IEnumerable<TestUser> users)
    {
        var list = users.ToList();
        snapshot = new Snapshot(list, new TestUserStore(list));
    }

    /// <summary>The store of the latest snapshot.</summary>
    internal TestUserStore Current => Volatile.Read(ref snapshot).Store;

    /// <summary>
    /// Adds an account, unless its username is taken ignoring case, as the sign-in page looks it
    /// up, or its subject is taken.
    /// </summary>
    internal bool TryAdd(TestUser user)
    {
        lock (gate)
        {
            if (
                snapshot.Users.Exists(u =>
                    string.Equals(u.Username, user.Username, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(u.SubjectId, user.SubjectId, StringComparison.Ordinal)
                )
            )
                return false;
            List<TestUser> users = [.. snapshot.Users, user];
            Volatile.Write(ref snapshot, new Snapshot(users, new TestUserStore(users)));
            return true;
        }
    }

    private sealed record Snapshot(List<TestUser> Users, TestUserStore Store);
}
