using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Rasa.Web
{
    using Config;

    /// <summary>Who Discord says signed in.</summary>
    public class DiscordUser
    {
        /// <summary>Discord's permanent ID for the user. Never changes, so it's what to store.</summary>
        public string Id { get; set; }

        /// <summary>The unique username. The user can change it.</summary>
        public string UserName { get; set; }

        /// <summary>The name shown in Discord; null if they haven't set one.</summary>
        public string DisplayName { get; set; }
    }

    /// <summary>A finished sign-in, waiting for its game client to collect it.</summary>
    public class CompletedSignIn
    {
        public DiscordUser User { get; set; }
        public DateTime Expires { get; set; }
    }

    public class DiscordOAuth
    {
        private const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
        private const string TokenUrl = "https://discord.com/api/oauth2/token";
        private const string CurrentUserUrl = "https://discord.com/api/users/@me";

        /// <summary>The game client's token: 16 hex characters, all the login packet's password field holds.</summary>
        private const int NonceLength = 16;

        /// <summary>How long a sign-in has from /oauth/login to coming back from Discord.</summary>
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

        /// <summary>How long the game client has to collect a finished sign-in.</summary>
        private static readonly TimeSpan CompletedLifetime = TimeSpan.FromMinutes(10);

        // One HttpClient for the whole program, as Microsoft recommends; making one per request runs out of sockets.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly DiscordConfig _config;

        /// <summary>Nonces sent to Discord and not back yet, with when each expires.</summary>
        private readonly ConcurrentDictionary<string, DateTime> _pendingSignIns = new();

        /// <summary>Nonces back from Discord, with who signed in, waiting for the game client.</summary>
        private readonly ConcurrentDictionary<string, CompletedSignIn> _completedSignIns = new();

        public DiscordOAuth(DiscordConfig config)
        {
            _config = config;
        }

        public bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(_config?.ClientId)
                && !string.IsNullOrWhiteSpace(_config.ClientSecret)
                && !string.IsNullOrWhiteSpace(_config.RedirectUri);
        }

        /// <summary>Exactly 16 lowercase hex characters, as the game client makes them.</summary>
        public static bool IsValidNonce(string nonce)
        {
            if (nonce == null || nonce.Length != NonceLength)
                return false;

            foreach (var c in nonce)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;

            return true;
        }

        /// <summary>What the game client shows the player: the first 8 characters as "1A2B-3C4D".</summary>
        public static string GetDisplayCode(string nonce)
        {
            return nonce.Substring(0, 4).ToUpperInvariant() + "-" + nonce.Substring(4, 4).ToUpperInvariant();
        }

        /// <summary>
        /// Starts the sign-in for the game client's nonce: the Discord page to send the browser to.
        /// Null if the nonce is malformed or its sign-in has already finished. Clicking the same
        /// link twice before finishing is fine; it keeps its first expiry.
        /// </summary>
        public string CreateLoginUrl(string nonce)
        {
            if (!IsValidNonce(nonce))
                return null;

            RemoveExpired();

            if (_completedSignIns.ContainsKey(nonce))
                return null;

            _pendingSignIns.TryAdd(nonce, DateTime.UtcNow + PendingLifetime);

            return AuthorizeUrl
                + "?response_type=code"
                + "&client_id=" + Uri.EscapeDataString(_config.ClientId)
                + "&scope=identify"
                + "&redirect_uri=" + Uri.EscapeDataString(_config.RedirectUri)
                + "&state=" + Uri.EscapeDataString(nonce);
        }

        /// <summary>True if this nonce went to Discord through us and hasn't expired. Each works once.</summary>
        public bool TakePendingSignIn(string nonce)
        {
            if (nonce == null)
                return false;

            return _pendingSignIns.TryRemove(nonce, out var expires) && expires > DateTime.UtcNow;
        }

        /// <summary>Files who signed in under the nonce, for the game client to collect.</summary>
        public void RecordCompletedSignIn(string nonce, DiscordUser user)
        {
            _completedSignIns[nonce] = new CompletedSignIn
            {
                User = user,
                Expires = DateTime.UtcNow + CompletedLifetime
            };
        }

        /// <summary>
        /// For the game client: who signed in with this nonce, or null if nobody has (yet), or it
        /// expired. Each finished sign-in can be collected once.
        /// </summary>
        public DiscordUser TakeCompletedSignIn(string nonce)
        {
            if (nonce == null || !_completedSignIns.TryRemove(nonce, out var signIn))
                return null;

            return signIn.Expires > DateTime.UtcNow ? signIn.User : null;
        }

        /// <summary>Swaps the code from the callback for an access token.</summary>
        public string ExchangeCodeForToken(string code)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _config.RedirectUri,
                ["client_id"] = _config.ClientId,
                ["client_secret"] = _config.ClientSecret
            });

            using var response = Http.PostAsync(TokenUrl, form).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Discord refused the code ({(int)response.StatusCode}): {body}");

            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("access_token").GetString();
        }

        /// <summary>Asks Discord who the token belongs to.</summary>
        public DiscordUser GetCurrentUser(string accessToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, CurrentUserUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = Http.SendAsync(request).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Discord would not say who the user is ({(int)response.StatusCode}): {body}");

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            return new DiscordUser
            {
                Id = root.GetProperty("id").GetString(),
                UserName = root.GetProperty("username").GetString(),
                DisplayName = root.TryGetProperty("global_name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null
            };
        }

        private void RemoveExpired()
        {
            var now = DateTime.UtcNow;

            foreach (var pending in _pendingSignIns)
                if (pending.Value < now)
                    _pendingSignIns.TryRemove(pending.Key, out _);

            foreach (var completed in _completedSignIns)
                if (completed.Value.Expires < now)
                    _completedSignIns.TryRemove(completed.Key, out _);
        }
    }
}