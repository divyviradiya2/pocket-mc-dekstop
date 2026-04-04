using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketMC.Desktop.Utils
{
    public static class LogSanitizer
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        private static readonly Regex PlayitClaimUrlRegex = new(
            @"https://playit\.gg/claim/[A-Za-z0-9\-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            RegexTimeout);

        private static readonly Regex SecretAssignmentRegex = new(
            @"(?i)\b(secret|token)\b(\s*[:=]\s*)([^\s,;]+)",
            RegexOptions.Compiled,
            RegexTimeout);

        public static string SanitizeConsoleLine(string? line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(line.Length);
            foreach (char character in line)
            {
                if (!char.IsControl(character) || character == '\t')
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        public static string SanitizePlayitLine(string? line)
        {
            string sanitized = SanitizeConsoleLine(line);
            sanitized = PlayitClaimUrlRegex.Replace(sanitized, "https://playit.gg/claim/[REDACTED]");
            sanitized = SecretAssignmentRegex.Replace(
                sanitized,
                match => $"{match.Groups[1].Value}{match.Groups[2].Value}[REDACTED]");
            return sanitized;
        }
    }
}
