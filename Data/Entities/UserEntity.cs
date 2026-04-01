using System;
using System.ComponentModel.DataAnnotations;

namespace SessionApp.Data.Entities
{
    /// <summary>
    /// Represents a user account with authentication credentials.
    /// Users can be linked to hosts (when creating games) and participants (when playing).
    /// </summary>
    public class UserEntity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [MaxLength(100)]
        public string Username { get; set; } = null!;

        [Required]
        [MaxLength(256)]
        [EmailAddress]
        public string Email { get; set; } = null!;

        /// <summary>
        /// Hashed password using ASP.NET Core Identity PasswordHasher.
        /// Never store plain text passwords.
        /// </summary>
        [Required]
        public string PasswordHash { get; set; } = null!;

        /// <summary>
        /// Optional display name (defaults to username if not set)
        /// </summary>
        [MaxLength(100)]
        public string? DisplayName { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        
        public DateTime? LastLoginUtc { get; set; }

        /// <summary>
        /// Indicates if the user account is active
        /// </summary>
        public bool IsActive { get; set; } = true;

            /// <summary>
            /// Email verification status
            /// </summary>
            public bool EmailConfirmed { get; set; } = false;

            /// <summary>
            /// Number of consecutive failed login attempts
            /// </summary>
            public int FailedLoginAttempts { get; set; } = 0;

            /// <summary>
            /// Timestamp when the account was locked due to failed login attempts
            /// </summary>
            public DateTime? LockoutEndUtc { get; set; }

                    /// <summary>
                    /// Indicates if the account is currently locked
                    /// </summary>
                    public bool IsLockedOut => LockoutEndUtc.HasValue && LockoutEndUtc.Value > DateTime.UtcNow;

                    /// <summary>
                    /// Email verification token (hashed)
                    /// </summary>
                    public string? EmailVerificationToken { get; set; }

                    /// <summary>
                    /// When the email verification token expires
                    /// </summary>
                    public DateTime? EmailVerificationTokenExpiresUtc { get; set; }

                    /// <summary>
                    /// Password reset token (hashed)
                    /// </summary>
                    public string? PasswordResetToken { get; set; }

                    /// <summary>
                    /// When the password reset token expires
                    /// </summary>
                    public DateTime? PasswordResetTokenExpiresUtc { get; set; }
                }
            }
