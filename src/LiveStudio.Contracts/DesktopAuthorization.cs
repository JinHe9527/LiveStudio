namespace LiveStudio.Contracts;

public sealed record StartDesktopAuthorizationRequest(string DeviceName);

public sealed record StartDesktopAuthorizationResponse(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds);

public sealed record PollDesktopAuthorizationRequest(string DeviceCode);

public enum DesktopAuthorizationStatus
{
    Pending,
    Approved,
    Expired
}

public sealed record PollDesktopAuthorizationResponse(
    DesktopAuthorizationStatus Status,
    string? AccessToken,
    DateTimeOffset? AccessTokenExpiresAt);

public sealed record ApproveDesktopAuthorizationRequest(string UserCode);

public sealed record DesktopAuthorizationApproval(
    string DeviceName,
    string UserCode,
    DateTimeOffset ExpiresAt);
