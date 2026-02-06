using System.Text;
using System.Text.RegularExpressions;

namespace SessionApp.Validation
{
    /// <summary>
    /// Provides methods to sanitize user input to prevent XSS and injection attacks
    /// </summary>
    public static class InputSanitizer
    {
        private static readonly Regex HtmlTagPattern = new Regex(@"<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex SqlKeywordPattern = new Regex(@"\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|EXECUTE|UNION|DECLARE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ScriptPattern = new Regex(@"<script[^>]*>.*?</script>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        
        /// <summary>
        /// Sanitizes string input by removing HTML tags and trimming
        /// </summary>
        public static string SanitizeString(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Remove script tags
            var sanitized = ScriptPattern.Replace(input, string.Empty);
            
            // Remove HTML tags
            sanitized = HtmlTagPattern.Replace(sanitized, string.Empty);
            
            // Trim and normalize whitespace
            sanitized = sanitized.Trim();
            sanitized = Regex.Replace(sanitized, @"\s+", " ");

            return sanitized;
        }

        /// <summary>
        /// Validates that a string doesn't contain SQL keywords (basic protection)
        /// </summary>
        public static bool ContainsSqlKeywords(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            return SqlKeywordPattern.IsMatch(input);
        }

        /// <summary>
        /// Sanitizes a dictionary by sanitizing all string values
        /// </summary>
        public static Dictionary<string, object> SanitizeDictionary(Dictionary<string, object>? input)
        {
            if (input == null)
                return new Dictionary<string, object>();

            var sanitized = new Dictionary<string, object>();
            
            foreach (var kvp in input)
            {
                var key = SanitizeString(kvp.Key);
                
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var value = kvp.Value;
                
                if (value is string stringValue)
                {
                    sanitized[key] = SanitizeString(stringValue);
                }
                else if (value is int || value is long || value is double || value is bool || value is DateTime)
                {
                    sanitized[key] = value;
                }
                else
                {
                    // For other types, convert to string and sanitize
                    sanitized[key] = SanitizeString(value?.ToString());
                }
            }

            return sanitized;
        }

        /// <summary>
        /// Truncates a string to a maximum length
        /// </summary>
        public static string Truncate(string? input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            return input.Length <= maxLength ? input : input.Substring(0, maxLength);
        }
    }
}