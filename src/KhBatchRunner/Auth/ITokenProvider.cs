namespace KhBatchRunner.Auth;

/// <summary>
/// Supplies a bearer token for Forma Data Management REST calls (the crawler).
/// Isolated behind an interface so the undocumented SSONET piggyback can later be
/// swapped for a real registered APS app / 3-legged flow without touching callers.
/// NOTE: opening a cloud model does NOT use this — that is authenticated by the
/// ambient signed-in Revit session (no token passed).
/// </summary>
public interface ITokenProvider
{
    /// <summary>Re-fetch a fresh bearer token per call; never cache the string.</summary>
    string GetAccessToken();
}
