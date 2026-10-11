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
    /// This is the connection that <paramref name="other"/> spoke for, as far as Weir can tell: the same kind of manager, and
    /// either the same connection or one <paramref name="other"/> was never attributed to (a global secret with several
    /// connections, or a word recorded before connections were kept), which would otherwise lock the word in for good.
    /// </summary>
    public bool IsSameConnectionAs(ManagerSpeaker? other) =>
        other is not null && string.Equals(SourceKey, other.SourceKey, StringComparison.Ordinal) &&
        (other.ConnectionId is null || ConnectionId == other.ConnectionId);

    /// <summary>The same caller as far as Weir can tell, so a repeat of its message is not a new word.</summary>
    public bool IsSameCallerAs(ManagerSpeaker? other) =>
        other is not null && ConnectionId == other.ConnectionId && string.Equals(SourceKey, other.SourceKey, StringComparison.Ordinal);
}
