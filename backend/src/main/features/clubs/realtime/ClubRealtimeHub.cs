using backend.main.application.features;
using backend.main.application.security;
using backend.main.features.clubs.discussions.replies;
using backend.main.features.clubs.posts.comments;
using backend.main.features.clubs.realtime.contracts.responses;
using backend.main.features.profile;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace backend.main.features.clubs.realtime;

/// <summary>
/// The single realtime hub for club content: discussion replies, post comments,
/// club-wide presence, and per-thread typing.
/// </summary>
/// <remarks>
/// Anonymous by default, matching the SSE endpoints it replaces — public clubs stream to
/// logged-out visitors. Every join re-runs the same authorization gate the REST endpoints
/// use, so access is re-checked far more often than the old once-per-stream check.
/// Anonymous connections receive events but never appear in the roster and cannot type.
/// </remarks>
[AllowAnonymous]
public sealed class ClubRealtimeHub : Hub
{
    private const string PresenceUserItemKey = "club-realtime:presence-user";

    private readonly IClubDiscussionReplyService _replyService;
    private readonly IPostCommentService _commentService;
    private readonly IClubPresenceStore _presence;
    private readonly IUserRepository _userRepository;
    private readonly IFeatureFlagEvaluator _featureFlags;

    public ClubRealtimeHub(
        IClubDiscussionReplyService replyService,
        IPostCommentService commentService,
        IClubPresenceStore presence,
        IUserRepository userRepository,
        IFeatureFlagEvaluator featureFlags)
    {
        _replyService = replyService;
        _commentService = commentService;
        _presence = presence;
        _userRepository = userRepository;
        _featureFlags = featureFlags;
    }

    /// <summary>
    /// Subscribes to a club: discussion reply events plus the club-wide presence roster.
    /// </summary>
    public async Task JoinClub(int clubId)
    {
        EnsureFeature(FeatureFlagKeys.Clubs);
        var (userId, userRole) = GetOptionalUser();
        await _replyService.EnsureCanReadClubAsync(clubId, userId, userRole);

        var group = ClubRealtimeGroups.Club(clubId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        var user = await ResolvePresenceUserAsync(userId);
        var cameOnline = _presence.JoinClub(clubId, Context.ConnectionId, user);

        await Clients.Caller.SendAsync(
            ClubRealtimeEvents.PresenceSnapshot, _presence.Snapshot(clubId));

        if (cameOnline && user is not null)
        {
            await Clients.OthersInGroup(group).SendAsync(
                ClubRealtimeEvents.PresenceChanged,
                new PresenceDiff(clubId, user, null, _presence.Snapshot(clubId).TotalOnline));
        }
    }

    /// <summary>
    /// Re-sends the full roster to the caller.
    /// </summary>
    /// <remarks>
    /// Presence is broadcast as diffs, and the roster on the wire is capped. In a club with
    /// more online members than the cap, members outside it are never named by a diff, so a
    /// client whose visible list has drained needs a way to refill it.
    /// </remarks>
    public async Task RequestPresence(int clubId)
    {
        EnsureFeature(FeatureFlagKeys.Clubs);
        var (userId, userRole) = GetOptionalUser();
        await _replyService.EnsureCanReadClubAsync(clubId, userId, userRole);

        await Clients.Caller.SendAsync(
            ClubRealtimeEvents.PresenceSnapshot, _presence.Snapshot(clubId));
    }

    public async Task LeaveClub(int clubId)
    {
        var group = ClubRealtimeGroups.Club(clubId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        await ReleaseClubAsync(clubId, group);
    }

    /// <summary>Subscribes to a discussion's typing group. Reply events arrive via the club group.</summary>
    public async Task JoinDiscussion(int clubId, int discussionId)
    {
        EnsureFeature(FeatureFlagKeys.ClubsDiscussions);
        var (userId, userRole) = GetOptionalUser();

        // The typing group is keyed on the discussion alone, so authorizing only the
        // caller-supplied club would let someone pair a club they can read with a discussion
        // from a private one. This proves the discussion actually belongs to that club.
        await _replyService.EnsureCanReadDiscussionAsync(clubId, discussionId, userId, userRole);

        var threadKey = ClubRealtimeGroups.DiscussionThread(discussionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, threadKey);
        _presence.JoinThread(Context.ConnectionId, threadKey);

        await Clients.Caller.SendAsync(ClubRealtimeEvents.TypingChanged, _presence.Typing(threadKey));
    }

    public Task LeaveDiscussion(int discussionId) =>
        ReleaseThreadAsync(ClubRealtimeGroups.DiscussionThread(discussionId));

    /// <summary>Subscribes to a post's comment events and its typing group.</summary>
    public async Task JoinPost(int clubId, int postId)
    {
        EnsureFeature(FeatureFlagKeys.ClubsPosts);
        var (userId, userRole) = GetOptionalUser();
        await _commentService.EnsureCanReadPostAsync(clubId, postId, userId, userRole);

        await Groups.AddToGroupAsync(Context.ConnectionId, ClubRealtimeGroups.Post(clubId, postId));

        var threadKey = ClubRealtimeGroups.PostThread(postId);
        await Groups.AddToGroupAsync(Context.ConnectionId, threadKey);
        _presence.JoinThread(Context.ConnectionId, threadKey);

        await Clients.Caller.SendAsync(ClubRealtimeEvents.TypingChanged, _presence.Typing(threadKey));
    }

    public async Task LeavePost(int clubId, int postId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ClubRealtimeGroups.Post(clubId, postId));
        await ReleaseThreadAsync(ClubRealtimeGroups.PostThread(postId));
    }

    /// <summary>
    /// Marks the caller as typing (or not) in a thread it has already joined.
    /// Clients refresh this every ~2s while composing; the entry expires on its own if they stop.
    /// </summary>
    public async Task Typing(string kind, int threadId, bool isTyping)
    {
        if (kind is not (ClubRealtimeGroups.DiscussionKind or ClubRealtimeGroups.PostKind))
            throw new HubException("Unknown thread kind.");

        // The join that precedes this is already gated, but typing is the one hub method that
        // carried no check of its own; disabling a surface should silence it here too.
        EnsureFeature(kind == ClubRealtimeGroups.DiscussionKind
            ? FeatureFlagKeys.ClubsDiscussions
            : FeatureFlagKeys.ClubsPosts);

        var (userId, _) = GetOptionalUser();
        if (!userId.HasValue)
            throw new HubException("Authentication is required to broadcast typing.");

        var threadKey = ClubRealtimeGroups.Thread(kind, threadId);

        // Authorized against the join rather than the database: the client refreshes this
        // on a timer, and re-querying membership on every tick would be a query per keystroke.
        if (!_presence.IsInThread(Context.ConnectionId, threadKey))
            throw new HubException("Join the thread before broadcasting typing.");

        var user = await ResolvePresenceUserAsync(userId);
        if (user is null)
            return;

        if (_presence.SetTyping(threadKey, Context.ConnectionId, user, isTyping, DateTimeOffset.UtcNow))
            await BroadcastTypingAsync(threadKey);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;

        foreach (var threadKey in _presence.ThreadsFor(connectionId))
        {
            if (_presence.LeaveThread(connectionId, threadKey))
                await BroadcastTypingAsync(threadKey);
        }

        foreach (var clubId in _presence.ClubsFor(connectionId))
            await ReleaseClubAsync(clubId, ClubRealtimeGroups.Club(clubId));

        await base.OnDisconnectedAsync(exception);
    }

    private async Task ReleaseClubAsync(int clubId, string group)
    {
        if (!_presence.LeaveClub(clubId, Context.ConnectionId, out var user) || user is null)
            return;

        await Clients.Group(group).SendAsync(
            ClubRealtimeEvents.PresenceChanged,
            new PresenceDiff(clubId, null, user.UserId, _presence.Snapshot(clubId).TotalOnline));
    }

    private async Task ReleaseThreadAsync(string threadKey)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, threadKey);
        if (_presence.LeaveThread(Context.ConnectionId, threadKey))
            await BroadcastTypingAsync(threadKey);
    }

    private Task BroadcastTypingAsync(string threadKey) =>
        Clients.Group(threadKey).SendAsync(ClubRealtimeEvents.TypingChanged, _presence.Typing(threadKey));

    private void EnsureFeature(string featureKey)
    {
        // FeatureGateConvention is an MVC application-model convention and does not reach
        // hubs, so the flag has to be checked by hand here.
        if (!_featureFlags.IsEnabled(featureKey))
            throw new HubException($"The '{featureKey}' feature is disabled.");
    }

    private (int? UserId, string? UserRole) GetOptionalUser()
    {
        if (Context.User?.Identity?.IsAuthenticated != true)
            return (null, null);
        var user = Context.User.GetUserPayload();
        return (user.Id, user.Role);
    }

    /// <summary>
    /// Loads the display fields presence broadcasts need, once per connection rather than
    /// once per join.
    /// </summary>
    private async Task<PresenceUser?> ResolvePresenceUserAsync(int? userId)
    {
        if (!userId.HasValue)
            return null;

        if (Context.Items.TryGetValue(PresenceUserItemKey, out var cached) && cached is PresenceUser existing)
            return existing;

        var records = await _userRepository.GetByIdsAsync([userId.Value]);
        var record = records.FirstOrDefault();
        if (record is null)
            return null;

        var user = new PresenceUser(
            record.Id,
            record.Name,
            record.Username,
            record.Avatar,
            record.UsernameDisplay ?? record.Username);
        Context.Items[PresenceUserItemKey] = user;
        return user;
    }
}
