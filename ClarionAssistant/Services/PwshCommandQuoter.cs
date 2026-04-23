using System;
using System.Collections.Generic;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Builds safe pwsh `-Command` payloads from user-configurable settings.
    /// The launch path composes a single PowerShell command line that is then
    /// passed as the `-Command` argument to pwsh.exe. Any user-controlled
    /// string concatenated into that payload must be tokenized and wrapped
    /// so shell metacharacters cannot escape into the enclosing context.
    ///
    /// Why this class exists: settings values (Claude.Commands, Copilot.Commands,
    /// Copilot.ExtraFlags) flow from the user's settings.txt into the pwsh
    /// `-Command` string. Without tokenize-and-quote, a value like
    /// <c>--verbose; Remove-Item C:\ -Recurse</c> would chain commands.
    /// </summary>
    public static class PwshCommandQuoter
    {
        /// <summary>
        /// Wrap a literal string for use inside a pwsh single-quoted literal.
        /// Doubles any embedded single quote per PowerShell rules.
        /// Does NOT validate content — caller must ensure no stray <c>"</c>.
        /// </summary>
        public static string QuoteLiteral(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        /// <summary>
        /// Tokenize a user-configured command line (e.g. <c>copilot --allow-all-tools</c>
        /// or <c>"C:\\Program Files\\gh\\gh.exe" copilot</c>) and rebuild it as
        /// a pwsh invocation using the call operator <c>&amp;</c> so the first
        /// token is treated as an executable even when it's a quoted path.
        /// Each token is wrapped in a pwsh single-quoted literal.
        /// Throws <see cref="ArgumentException"/> if any token contains a
        /// double quote (those would escape the outer CreateProcess-parsed
        /// <c>-Command "..."</c> context and cannot be represented safely here).
        /// </summary>
        public static string BuildInvocation(string userCommand)
        {
            var tokens = Tokenize(userCommand);
            if (tokens.Count == 0) return string.Empty;
            ValidateTokens(tokens, "command");
            var sb = new StringBuilder();
            sb.Append("& ");
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteLiteral(tokens[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tokenize a user-configured flag string (e.g. <c>--verbose --log-level debug</c>)
        /// and rebuild it as space-separated pwsh single-quoted literals. No
        /// call operator is prefixed — the caller appends this to an existing
        /// invocation. Empty / whitespace-only input returns an empty string.
        /// </summary>
        public static string BuildFlags(string userFlags)
        {
            var tokens = Tokenize(userFlags);
            if (tokens.Count == 0) return string.Empty;
            ValidateTokens(tokens, "flag");
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteLiteral(tokens[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Basic shell-style tokenizer: splits on whitespace, honors paired
        /// single and double quotes as token delimiters (their content is
        /// preserved, the delimiters are consumed). Does not support escape
        /// sequences — a literal quote character inside a token is not
        /// representable and must be handled by the caller's validation.
        /// </summary>
        public static List<string> Tokenize(string input)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return result;

            var cur = new StringBuilder();
            char? quote = null;
            foreach (char c in input)
            {
                if (quote.HasValue)
                {
                    if (c == quote.Value) { quote = null; }
                    else { cur.Append(c); }
                }
                else if (c == '\'' || c == '"')
                {
                    quote = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (cur.Length > 0)
                    {
                        result.Add(cur.ToString());
                        cur.Clear();
                    }
                }
                else
                {
                    cur.Append(c);
                }
            }
            if (cur.Length > 0) result.Add(cur.ToString());
            return result;
        }

        private static void ValidateTokens(List<string> tokens, string kind)
        {
            foreach (string t in tokens)
            {
                if (t.IndexOf('"') >= 0)
                {
                    throw new ArgumentException(
                        "Settings " + kind + " token contains a literal double-quote character, " +
                        "which cannot be safely represented inside the pwsh -Command payload: [" + t + "]");
                }
            }
        }
    }
}
