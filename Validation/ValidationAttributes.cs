using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace SessionApp.Validation
{
    /// <summary>
    /// Validates that a string contains only alphanumeric characters and safe symbols
    /// Allows colons for URIs, timestamps, and identifiers
    /// </summary>
    public class SafeStringAttribute : ValidationAttribute
    {
        private static readonly Regex SafePattern = new Regex(@"^[a-zA-Z0-9\s\-_\.,'!@#&()\[\]:]+$", RegexOptions.Compiled);
        
        public int MinLength { get; set; } = 0;
        public int MaxLength { get; set; } = int.MaxValue;

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
            {
                return ValidationResult.Success; // Let [Required] handle null/empty
            }

            var stringValue = value.ToString()!;

            if (stringValue.Length < MinLength)
            {
                return new ValidationResult($"The field {validationContext.MemberName} must be at least {MinLength} characters.");
            }

            if (stringValue.Length > MaxLength)
            {
                return new ValidationResult($"The field {validationContext.MemberName} must not exceed {MaxLength} characters.");
            }

            if (!SafePattern.IsMatch(stringValue))
            {
                return new ValidationResult($"The field {validationContext.MemberName} contains invalid characters. Only alphanumeric characters and basic punctuation (including colons) are allowed.");
            }

            return ValidationResult.Success;
        }
    }

    /// <summary>
    /// Validates that a string is a valid room code format (uppercase alphanumeric)
    /// </summary>
    public class RoomCodeAttribute : ValidationAttribute
    {
        private static readonly Regex RoomCodePattern = new Regex(@"^[A-Z0-9]{4,10}$", RegexOptions.Compiled);

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.ToString()))
            {
                return new ValidationResult("Room code is required.");
            }

            var stringValue = value.ToString()!.ToUpperInvariant();

            if (!RoomCodePattern.IsMatch(stringValue))
            {
                return new ValidationResult("Room code must be 4-10 uppercase alphanumeric characters.");
            }

            return ValidationResult.Success;
        }
    }

    /// <summary>
    /// Validates that a dictionary doesn't exceed size limits
    /// </summary>
    public class DictionarySizeLimitAttribute : ValidationAttribute
    {
        public int MaxKeys { get; set; } = 50;
        public int MaxValueLength { get; set; } = 1000;

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (value == null)
            {
                return ValidationResult.Success;
            }

            if (value is not Dictionary<string, object> dictionary)
            {
                return new ValidationResult("Value must be a dictionary.");
            }

            if (dictionary.Count > MaxKeys)
            {
                return new ValidationResult($"Dictionary cannot contain more than {MaxKeys} keys.");
            }

            foreach (var kvp in dictionary)
            {
                var valueStr = kvp.Value?.ToString() ?? "";
                if (valueStr.Length > MaxValueLength)
                {
                    return new ValidationResult($"Dictionary value for key '{kvp.Key}' exceeds maximum length of {MaxValueLength}.");
                }
            }

            return ValidationResult.Success;
        }
    }

    /// <summary>
    /// Validates TimeSpan ranges
    /// </summary>
    public class TimeSpanRangeAttribute : ValidationAttribute
    {
        public int MinHours { get; set; }
        public int MaxHours { get; set; }

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            if (value == null)
            {
                return ValidationResult.Success;
            }

            if (value is not TimeSpan timeSpan)
            {
                return new ValidationResult("Value must be a TimeSpan.");
            }

            if (timeSpan.TotalHours < MinHours)
            {
                return new ValidationResult($"Duration must be at least {MinHours} hours.");
            }

            if (timeSpan.TotalHours > MaxHours)
            {
                return new ValidationResult($"Duration cannot exceed {MaxHours} hours.");
            }

            return ValidationResult.Success;
        }
    }
}