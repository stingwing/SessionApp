# Authentication Security Documentation

## Security Features Implemented

### 1. Brute Force Attack Protection

#### Account Lockout Mechanism
- **Failed Login Threshold**: 5 consecutive failed login attempts
- **Lockout Duration**: 15 minutes
- **Automatic Reset**: Failed attempts counter resets on successful login
- **Lockout Tracking**: `LockoutEndUtc` field tracks when account will be unlocked

#### Rate Limiting (Defense in Depth)
The "auth" rate limiting policy is applied to all authentication endpoints:
- **Limit**: 10 requests per minute per IP address
- **Queue Size**: Maximum 2 queued requests
- **Applies to**:
  - `/api/auth/register` - Registration endpoint
  - `/api/auth/login` - Login endpoint
  - `/api/auth/change-password` - Password change endpoint

#### Implementation Details
```csharp
// UserEntity.cs
public int FailedLoginAttempts { get; set; } = 0;
public DateTime? LockoutEndUtc { get; set; }
public bool IsLockedOut => LockoutEndUtc.HasValue && LockoutEndUtc.Value > DateTime.UtcNow;
```

### 2. Timing Attack Protection

#### Generic Error Messages
All authentication failures return the same generic message: **"Invalid username/email or password"**
- Prevents user enumeration attacks
- Makes it impossible to determine if a username/email exists

#### Constant-Time Response
When a user doesn't exist, the system performs a dummy password hash to ensure:
- Failed login attempts take similar time whether user exists or not
- Prevents timing analysis attacks

```csharp
if (user == null)
{
    // Perform a dummy hash verification to prevent timing attacks
    _passwordHasher.HashPassword(new UserEntity(), request.Password);
    return (false, genericErrorMessage, null);
}
```

### 3. Strong Password Requirements

#### Current Requirements
- **Minimum Length**: 12 characters (increased from 8)
- **Uppercase**: At least one uppercase letter (A-Z)
- **Lowercase**: At least one lowercase letter (a-z)
- **Numbers**: At least one digit (0-9)
- **Special Characters**: At least one special character (@$!%*?&#^()_+-=[]{}\|;:,.<>)

#### Username Validation
- **Length**: 3-100 characters
- **Allowed Characters**: Letters, numbers, underscores, and hyphens only
- **Regex Pattern**: `^[a-zA-Z0-9_-]+$`

### 4. Password Hashing

#### ASP.NET Core Identity PasswordHasher
- Uses **PBKDF2** with HMAC-SHA256 or HMAC-SHA512
- **Automatic salting** - unique salt per password
- **Iteration count**: 10,000+ iterations (configurable)
- **Automatic rehashing** on successful login if algorithm is upgraded

```csharp
// Password hashing on registration
user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

// Password verification with rehash support
var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
{
    user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
    await _context.SaveChangesAsync();
}
```

### 5. Security Logging

#### Comprehensive Logging
All authentication events are logged with appropriate severity levels:
- **Information**: Successful logins, password changes
- **Warning**: Failed login attempts, locked account access attempts, disabled account access
- **Error**: Registration/update failures

#### Logged Events
```csharp
// Failed login tracking
_logger.LogWarning("Failed login attempt for user: {Username}. Attempt count: {FailedAttempts}", 
    user.Username, user.FailedLoginAttempts);

// Account lockout
_logger.LogWarning("Account locked due to failed login attempts: {Username}. Locked until: {LockoutEnd}", 
    user.Username, user.LockoutEndUtc);

// Successful login
_logger.LogInformation("User logged in successfully: {Username}", user.Username);
```

### 6. Defense in Depth

Multiple layers of security:
1. **Network Layer**: Rate limiting by IP address
2. **Application Layer**: Account lockout by user account
3. **Data Layer**: Secure password hashing
4. **Code Layer**: Timing attack protection

## Database Schema Changes

New fields added to `UserEntity`:
```sql
FailedLoginAttempts INT NOT NULL DEFAULT 0
LockoutEndUtc TIMESTAMP NULL
```

To apply migration:
```bash
dotnet ef database update
```

## Configuration Options

### Customizing Lockout Settings
Edit constants in `UserService.cs`:
```csharp
private const int MaxFailedLoginAttempts = 5;       // Adjust threshold
private const int LockoutDurationMinutes = 15;      // Adjust duration
```

### Customizing Rate Limiting
Edit in `Program.cs`:
```csharp
options.AddPolicy("auth", httpContext =>
    RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: partition => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,  // Adjust attempts per window
            Window = TimeSpan.FromMinutes(1),  // Adjust time window
            QueueLimit = 2
        }));
```

## Testing Recommendations

### Test Brute Force Protection
1. Make 5 failed login attempts with the same account
2. Verify account is locked for 15 minutes
3. Verify appropriate error message is returned
4. Wait 15 minutes and verify account is automatically unlocked
5. Make successful login and verify failed attempts counter resets

### Test Rate Limiting
1. Make 11 rapid login requests from the same IP
2. Verify 11th request returns 429 Too Many Requests
3. Verify `Retry-After` header is present
4. Wait for rate limit window to expire
5. Verify requests work again

### Test Timing Attack Protection
1. Measure response time for non-existent user
2. Measure response time for existing user with wrong password
3. Verify response times are similar (within ~100ms)
4. Verify both return same generic error message

## Security Best Practices

### Additional Recommendations

1. **HTTPS Only**: Always use HTTPS in production
   ```csharp
   app.UseHttpsRedirection();
   ```

2. **Email Verification**: Implement email confirmation before account activation
   - Current field exists: `EmailConfirmed`
   - Send verification email on registration
   - Require verification before allowing login

3. **Two-Factor Authentication (2FA)**: Consider adding 2FA for sensitive operations
   - TOTP (Time-based One-Time Password)
   - SMS verification
   - Email verification codes

4. **Session Management**: Implement proper session/token management
   - JWT tokens with short expiration
   - Refresh token rotation
   - Session invalidation on password change

5. **Security Headers**: Add security headers to responses
   ```csharp
   app.Use(async (context, next) =>
   {
       context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
       context.Response.Headers.Add("X-Frame-Options", "DENY");
       context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
       await next();
   });
   ```

6. **Password Reset**: Implement secure password reset flow
   - Time-limited reset tokens
   - Single-use reset tokens
   - Email verification required

7. **Audit Trail**: Maintain audit logs for:
   - Password changes
   - Email changes
   - Failed login attempts
   - Successful logins with IP and timestamp

8. **IP Blocking**: Consider automatic IP blocking for:
   - Excessive failed login attempts across multiple accounts
   - Suspicious patterns

## Compliance Considerations

These security measures help meet requirements for:
- **OWASP Top 10**: Protection against Broken Authentication (A07:2021)
- **GDPR**: Secure password storage and user data protection
- **PCI DSS**: Strong authentication mechanisms (if handling payments)
- **SOC 2**: Access control and authentication security controls

## Security Incident Response

If a security incident occurs:
1. Check logs for `UserService` with `LogLevel.Warning` or higher
2. Identify affected accounts from failed login attempts
3. Review IP addresses involved
4. Consider manual account lockout if needed:
   ```sql
   UPDATE users 
   SET lockout_end_utc = NOW() + INTERVAL '1 hour', 
       failed_login_attempts = 999
   WHERE username = 'affected_user';
   ```

## Regular Security Maintenance

### Monthly Tasks
- Review authentication logs for suspicious patterns
- Check for accounts with excessive failed login attempts
- Review rate limiting metrics

### Quarterly Tasks
- Update password hashing algorithm if new version available
- Review and update password complexity requirements
- Test brute force protection mechanisms

### Yearly Tasks
- Security audit of authentication system
- Penetration testing
- Update security documentation
