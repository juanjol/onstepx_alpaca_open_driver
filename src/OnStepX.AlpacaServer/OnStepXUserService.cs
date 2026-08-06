using ASCOM.Alpaca;

namespace OnStepX.AlpacaServer;

/// <summary>
/// Server authentication, for when the server is exposed beyond localhost.
/// </summary>
/// <remarks>
/// <para>
/// The official REST layer requires a registered <see cref="IUserService"/>. With
/// authentication switched off, which is the normal case on a home network, this
/// implementation authorises nobody <b>and does not need to</b>, because the
/// authorisation filter is not applied at all.
/// </para>
/// <para>
/// The credential comparison is constant time on purpose, so that response timing does
/// not leak how many characters of a guess were correct.
/// </para>
/// </remarks>
public sealed class OnStepXUserService : IUserService
{
    /// <summary>
    /// Tells the REST layer whether to require authentication. It is this value, and not
    /// the result of <see cref="Authenticate"/>, that decides whether the filter runs.
    /// </summary>
    public bool UseAuth => ServerRuntime.Settings.Server.UseAuthentication;

    /// <summary>Validates a username and password.</summary>
    public Task<bool> Authenticate(string username, string password)
    {
        Core.Config.ServerSettings server = ServerRuntime.Settings.Server;

        if (!server.UseAuthentication)
        {
            // With authentication off the filter never runs, so nothing is granted here
            // either. Returning true would be a silent way in if this were ever called
            // by mistake.
            return Task.FromResult(false);
        }

        bool userMatches = FixedTimeEquals(username, server.UserName);
        bool passwordMatches = FixedTimeEquals(password, server.Password);

        // Both are always evaluated, with no short circuit, so that response timing does
        // not reveal whether the username exists.
        return Task.FromResult(userMatches & passwordMatches);
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        byte[] left = System.Text.Encoding.UTF8.GetBytes(a ?? string.Empty);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(b ?? string.Empty);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
