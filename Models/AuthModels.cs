using System.ComponentModel.DataAnnotations;

namespace SessionApp.Models
{
    public class RegisterRequest
    {
        [Required(ErrorMessage = "Username is required")]
        [StringLength(100, MinimumLength = 3, ErrorMessage = "Username must be between 3 and 100 characters")]
        [RegularExpression(@"^[a-zA-Z0-9_-]+$", ErrorMessage = "Username can only contain letters, numbers, underscores, and hyphens")]
        public string Username { get; set; } = null!;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email address")]
        [StringLength(256)]
        public string Email { get; set; } = null!;

        [Required(ErrorMessage = "Password is required")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&#^()_+\-=\[\]{}|;:,.<>])[A-Za-z\d@$!%*?&#^()_+\-=\[\]{}|;:,.<>]{8,}$",
            ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one number, and one special character")]
        public string Password { get; set; } = null!;

        [Required(ErrorMessage = "Password confirmation is required")]
        [Compare(nameof(Password), ErrorMessage = "Passwords do not match")]
        public string ConfirmPassword { get; set; } = null!;

        [StringLength(100)]
        public string? DisplayName { get; set; }
    }

    public class LoginRequest
    {
        [Required(ErrorMessage = "Username or email is required")]
        public string UsernameOrEmail { get; set; } = null!;

        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; } = null!;
    }

    public class ChangePasswordRequest
    {
        [Required(ErrorMessage = "Current password is required")]
        public string CurrentPassword { get; set; } = null!;

        [Required(ErrorMessage = "New password is required")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&#^()_+\-=\[\]{}|;:,.<>])[A-Za-z\d@$!%*?&#^()_+\-=\[\]{}|;:,.<>]{8,}$",
            ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one number, and one special character")]
        public string NewPassword { get; set; } = null!;

        [Required(ErrorMessage = "Password confirmation is required")]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match")]
        public string ConfirmNewPassword { get; set; } = null!;
    }

    public class AuthResponse
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string Token { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }
    }

    public class UserProfile
    {
        public Guid UserId { get; set; }
        public string Username { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string? DisplayName { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
        public bool EmailConfirmed { get; set; }
    }
}
