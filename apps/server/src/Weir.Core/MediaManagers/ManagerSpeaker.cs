namespace Weir.Core.MediaManagers;

/// <summary>
/// Who said something about a copy Weir handed back: the kind of manager (<c>deluno</c>, <c>radarr</c>, <c>sonarr</c>), the
/// connection it came from when Weir could tell, and whether the message proved who sent it. A display name is not an identity:
/// two Deluno connections both read "Deluno".
/// </summary>
/// <param name="SourceKey">The intake source key of the manager's kind.</param>
/// <param name="ConnectionId">The connection the message was attributed to; null when Weir could not tell which one.</param>
/// <param name="Authenticated">The message carried a secret that proved its sender.</param>
public sealed record ManagerSpeaker(string SourceKey, long? ConnectionId, bool Authenticated)
{
    /// <summary>
    /// Both are the same connection of the same kind of manager, provably. When Weir could not tell which connection spoke, it
    /// cannot say two words came from one.
    /// </summary>
    public bool IsSameConnectionAs(ManagerSpeaker? other) =>
        other is not null && ConnectionId is not null && ConnectionId == other.ConnectionId &&
        string.Equals(SourceKey, other.SourceKey, StringComparison.Ordinal);

    /// <summary>The same caller as far as Weir can tell, so a repeat of its message is not a new word.</summary>
    public bool IsSameCallerAs(ManagerSpeaker? other) =>
        other is not null && ConnectionId == other.ConnectionId && string.Equals(SourceKey, other.SourceKey, StringComparison.Ordinal);
}
