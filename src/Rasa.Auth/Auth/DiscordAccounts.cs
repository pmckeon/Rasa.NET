using System;
using System.Security.Cryptography;
using System.Text;

namespace Rasa.Auth
{
    using Data;
    using Repositories.Auth.Account;
    using Structures.Auth;
    using Web;

    /// <summary>
    /// The account that goes with a Discord user: the one already linked to their Discord ID, or
    /// a new one made the first time they sign in.
    /// </summary>
    public static class DiscordAccounts
    {
        /// <summary>Random digits added to a username that's already taken.</summary>
        private const int SuffixDigits = 4;

        /// <summary>How many usernames to try before giving up.</summary>
        private const int MaxNameAttempts = 10;

        /// <summary>
        /// The account for this Discord user, made if they have none. Null if one couldn't be made;
        /// the reason is in the log.
        /// </summary>
        public static AuthAccountEntry FindOrCreate(IAuthAccountRepository accounts, Action complete, DiscordUser user)
        {
            var existing = accounts.FindByDiscordId(user.Id);
            if (existing != null)
                return existing;

            var email = GetPlaceholderEmail(user);

            // Nobody is told this, and at 32 characters it's longer than the 16 the game client can
            // send: a Discord account can't be logged in to with a password at all.
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
            {
                var userName = GetCandidateUserName(user, attempt);

                if (accounts.FindByUserNameOrEmail(userName, null) != null)
                    continue; // taken: try again with random digits on the end

                try
                {
                    accounts.CreateForDiscord(email, userName, password, user.Id);
                    complete?.Invoke();
                }
                catch (Exception e)
                {
                    Logger.WriteLog(LogType.Error, $"Could not create an account for Discord user {user.UserName} ({user.Id}): {e.GetBaseException().Message}");
                    return null;
                }

                Logger.WriteLog(LogType.Initialize, $"Created account {userName} for Discord user {user.UserName} ({user.Id}).");

                return accounts.FindByDiscordId(user.Id);
            }

            Logger.WriteLog(LogType.Error, $"Could not find a free username for Discord user {user.UserName} ({user.Id}).");
            return null;
        }

        /// <summary>The table wants a unique e-mail; .invalid is reserved and can never be a real address.</summary>
        private static string GetPlaceholderEmail(DiscordUser user)
        {
            return $"{user.Id}@discord.invalid";
        }

        /// <summary>
        /// Attempt 0: the Discord username, cut to what the game client's username field holds.
        /// Later attempts: a shorter cut of it plus random digits.
        /// </summary>
        private static string GetCandidateUserName(DiscordUser user, int attempt)
        {
            var baseName = GetSafeUserName(user);

            if (attempt == 0)
                return Truncate(baseName, AccountRules.MaxUserNameBytes);

            var suffix = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");

            return Truncate(baseName, AccountRules.MaxUserNameBytes - SuffixDigits) + suffix;
        }

        /// <summary>
        /// The Discord username with anything but plain letters, digits, '_' and '.' left out, in
        /// lower case. Plain ASCII, so each character is one byte of the 14 the client allows.
        /// </summary>
        private static string GetSafeUserName(DiscordUser user)
        {
            var safe = new StringBuilder();

            foreach (var c in user.UserName ?? "")
                if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.')
                    safe.Append(char.ToLowerInvariant(c));

            return safe.Length > 0 ? safe.ToString() : "player";
        }

        private static string Truncate(string text, int maxLength)
        {
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}