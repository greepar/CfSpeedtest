using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

public class WebUiAuthService
{
    private const string CookieName = "cfst_webui_session";
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly TimeSpan _sessionLifetime = TimeSpan.FromDays(7);
    private readonly ILogger<WebUiAuthService> _logger;

    public WebUiAuthService(ILogger<WebUiAuthService> logger)
    {
        _logger = logger;
    }

    public void EnsureInitialized(DataStore store)
    {
        var config = store.GetConfig();
        var auth = config.WebUiAuth ?? new WebUiAuthConfig();
        var changed = false;

        if (string.IsNullOrWhiteSpace(auth.Username))
        {
            auth.Username = "admin";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(auth.PasswordSalt) || string.IsNullOrWhiteSpace(auth.PasswordHash))
        {
            SetCredentials(auth, auth.Username, "admin123456");
            changed = true;
            _logger.LogWarning("WebUI auth initialized with default credentials username={Username} password=admin123456. Change it immediately.", auth.Username);
        }

        if (changed)
        {
            config.WebUiAuth = auth;
            store.SaveConfig(config);
        }
    }

    public bool ValidateLogin(DataStore store, string username, string password)
    {
        var auth = store.GetConfig().WebUiAuth;
        if (!auth.Enabled)
            return true;

        if (!string.Equals(auth.Username, username?.Trim(), StringComparison.Ordinal))
            return false;

        var hashed = HashPassword(password ?? string.Empty, auth.PasswordSalt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hashed),
            Encoding.UTF8.GetBytes(auth.PasswordHash));
    }

    public string CreateSession(string username)
    {
        CleanupExpiredSessions();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new SessionInfo
        {
            Username = username,
            ExpiresAtUtc = DateTime.UtcNow.Add(_sessionLifetime)
        };
        return token;
    }

    public bool TryAuthenticate(HttpContext context, DataStore store, out string username)
    {
        username = string.Empty;
        var auth = store.GetConfig().WebUiAuth;
        if (!auth.Enabled)
        {
            username = auth.Username;
            return true;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return false;

        if (!_sessions.TryGetValue(token, out var session))
            return false;

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(_sessionLifetime);
        _sessions[token] = session;
        username = session.Username;
        return true;
    }

    public void SignIn(HttpContext context, string token)
    {
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.Add(_sessionLifetime)
        });
    }

    public void SignOut(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            _sessions.TryRemove(token, out _);
        }
        context.Response.Cookies.Delete(CookieName);
    }

    public bool ChangeCredentials(DataStore store, string currentPassword, string newUsername, string newPassword)
    {
        var config = store.GetConfig();
        var auth = config.WebUiAuth;
        if (auth.Enabled && !ValidateLogin(store, auth.Username, currentPassword))
            return false;

        if (string.IsNullOrWhiteSpace(newUsername) || string.IsNullOrWhiteSpace(newPassword))
            return false;

        SetCredentials(auth, newUsername.Trim(), newPassword);
        config.WebUiAuth = auth;
        store.SaveConfig(config);
        ClearAllSessions();
        return true;
    }

    private void ClearAllSessions()
    {
        _sessions.Clear();
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAtUtc <= now)
                _sessions.TryRemove(pair.Key, out _);
        }
    }

    private static void SetCredentials(WebUiAuthConfig auth, string username, string password)
    {
        auth.Username = username;
        auth.PasswordSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        auth.PasswordHash = HashPassword(password, auth.PasswordSalt);
    }

    private static string HashPassword(string password, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes($"{salt}:{password}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class SessionInfo
    {
        public string Username { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
}
