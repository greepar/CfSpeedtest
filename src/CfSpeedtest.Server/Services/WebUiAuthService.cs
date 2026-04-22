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

        _sessions.Clear();
        foreach (var session in auth.Sessions.Where(s => s.ExpiresAtUtc > DateTime.UtcNow && !string.IsNullOrWhiteSpace(s.Token)))
        {
            _sessions[session.Token] = new SessionInfo
            {
                Username = session.Username,
                UserAgent = session.UserAgent,
                IpAddress = session.IpAddress,
                CreatedAtUtc = session.CreatedAtUtc,
                ExpiresAtUtc = session.ExpiresAtUtc,
                LastSeenAtUtc = session.LastSeenAtUtc,
            };
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

    public string CreateSession(DataStore store, HttpContext context, string username)
    {
        CleanupExpiredSessions(store);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new SessionInfo
        {
            Username = username,
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.Add(_sessionLifetime),
            LastSeenAtUtc = DateTime.UtcNow,
        };
        PersistSessions(store);
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
            PersistSessions(store);
            return false;
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(_sessionLifetime);
        session.LastSeenAtUtc = DateTime.UtcNow;
        _sessions[token] = session;
        PersistSessions(store);
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

    public void SignOut(HttpContext context, DataStore store)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            _sessions.TryRemove(token, out _);
        }
        PersistSessions(store);
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
        ClearAllSessions(store);
        return true;
    }

    public List<WebUiSessionOverview> GetSessionOverviews(DataStore store)
    {
        CleanupExpiredSessions(store);
        return _sessions.Values
            .OrderByDescending(s => s.LastSeenAtUtc)
            .Select(s => new WebUiSessionOverview
            {
                Username = s.Username,
                UserAgent = s.UserAgent,
                IpAddress = s.IpAddress,
                CreatedAtUtc = s.CreatedAtUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                LastSeenAtUtc = s.LastSeenAtUtc,
            })
            .ToList();
    }

    private void ClearAllSessions(DataStore store)
    {
        _sessions.Clear();
        PersistSessions(store);
    }

    private void CleanupExpiredSessions(DataStore store)
    {
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _sessions.TryRemove(pair.Key, out _);
                changed = true;
            }
        }
        if (changed)
            PersistSessions(store);
    }

    private void PersistSessions(DataStore store)
    {
        var config = store.GetConfig();
        config.WebUiAuth.Sessions = _sessions.Select(kv => new WebUiSessionInfo
        {
            Token = kv.Key,
            Username = kv.Value.Username,
            UserAgent = kv.Value.UserAgent,
            IpAddress = kv.Value.IpAddress,
            CreatedAtUtc = kv.Value.CreatedAtUtc,
            ExpiresAtUtc = kv.Value.ExpiresAtUtc,
            LastSeenAtUtc = kv.Value.LastSeenAtUtc,
        }).ToList();
        store.SaveConfig(config);
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
        public string UserAgent { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    }
}
