# 🔒 Authentication Security - Quick Reference

## ⚠️ CRITICAL: Apply Database Migration
```bash
dotnet ef database update
```

## 🛡️ Brute Force Protection

### Account Lockout
| Setting | Value |
|---------|-------|
| Max Failed Attempts | 5 |
| Lockout Duration | 15 minutes |
| Auto-Unlock | Yes |
| Reset on Success | Yes |

### Rate Limiting (Per IP)
| Endpoint | Limit | Window | Queue |
|----------|-------|--------|-------|
| `/api/auth/login` | 10 | 1 min | 2 |
| `/api/auth/register` | 10 | 1 min | 2 |
| `/api/auth/change-password` | 10 | 1 min | 2 |

## 🔐 Password Requirements

### New Requirements
✅ **Minimum 12 characters** (was 8)  
✅ **At least 1 uppercase letter** (A-Z)  
✅ **At least 1 lowercase letter** (a-z)  
✅ **At least 1 number** (0-9)  
✅ **At least 1 special character** (@$!%*?&#^()_+-=[]{}\|;:,.<>)

### Username Requirements
✅ **3-100 characters**  
✅ **Only: a-z, A-Z, 0-9, _, -**  
❌ No spaces or special characters

## 🔍 Security Features

| Feature | Status | Description |
|---------|--------|-------------|
| Password Hashing | ✅ | PBKDF2 with HMAC-SHA256/512 |
| Automatic Salting | ✅ | Unique salt per password |
| Account Lockout | ✅ | 5 failures = 15 min lockout |
| Rate Limiting | ✅ | 10 requests/min per IP |
| Timing Attack Protection | ✅ | Constant-time responses |
| User Enumeration Protection | ✅ | Generic error messages |
| Failed Login Logging | ✅ | All attempts logged |
| Auto Password Rehash | ✅ | On algorithm upgrade |

## 📊 HTTP Response Codes

| Code | Meaning | When |
|------|---------|------|
| 200 | Success | Login successful |
| 201 | Created | Registration successful |
| 400 | Bad Request | Invalid input, weak password |
| 401 | Unauthorized | Wrong credentials |
| 429 | Too Many Requests | Rate limit exceeded |

## 🔴 Common Error Messages

### Login Errors
```json
{
  "message": "Invalid username/email or password"
}
```
*Same message for: wrong username, wrong password, non-existent user*

```json
{
  "message": "Account is temporarily locked. Please try again in 14 minutes."
}
```
*After 5 failed attempts*

```json
{
  "message": "Account has been locked for 15 minutes due to multiple failed login attempts."
}
```
*On the 5th failed attempt*

### Registration Errors
```json
{
  "message": "Password must be at least 12 characters",
  "errors": [
    {
      "Field": "Password",
      "Errors": ["Password must be at least 12 characters"]
    }
  ]
}
```

```json
{
  "message": "Password must contain at least one uppercase letter, one lowercase letter, one number, and one special character"
}
```

### Rate Limit Error
```json
{
  "error": "Too many requests",
  "message": "Rate limit exceeded. Please try again later.",
  "retryAfter": 45.2
}
```
*Headers include: `Retry-After: 45`*

## 📈 Security Metrics to Monitor

### Daily
- Failed login attempt count
- Locked account count
- Rate limit 429 responses

### Weekly
- Unique IPs with multiple failed attempts
- Accounts with suspicious login patterns
- Password change frequency

### Monthly
- Average failed attempts per account
- Most commonly targeted usernames
- Peak authentication request times

## 🚨 Security Incident Response

### If Suspicious Activity Detected

1. **Check Logs**
   ```bash
   # Filter authentication warnings
   grep "Failed login attempt" logs/*.log
   grep "Account locked" logs/*.log
   ```

2. **Identify Affected Accounts**
   ```sql
   SELECT username, failed_login_attempts, lockout_end_utc
   FROM users
   WHERE failed_login_attempts >= 3
   ORDER BY failed_login_attempts DESC;
   ```

3. **Manual Account Lock (if needed)**
   ```sql
   UPDATE users 
   SET lockout_end_utc = NOW() + INTERVAL '1 hour',
       failed_login_attempts = 999
   WHERE username = 'suspicious_user';
   ```

4. **Review IP Addresses**
   - Check application logs for IP patterns
   - Consider blocking at firewall/CDN level

## ⚙️ Configuration Locations

| Setting | File | Line |
|---------|------|------|
| Max Failed Attempts | `Services/UserService.cs` | ~19 |
| Lockout Duration | `Services/UserService.cs` | ~20 |
| Auth Rate Limit | `Program.cs` | ~113-125 |
| Password Regex | `Models/AuthModels.cs` | ~17-18 |
| Username Regex | `Models/AuthModels.cs` | ~7 |

## 🧪 Testing Commands

### Test Rate Limiting
```bash
# Make 11 rapid requests (11th should fail)
for i in {1..11}; do
  curl -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"usernameOrEmail":"test","password":"test"}'
  echo ""
done
```

### Test Account Lockout
```bash
# Make 6 failed login attempts
for i in {1..6}; do
  curl -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"usernameOrEmail":"testuser","password":"wrongpass"}'
  echo "Attempt $i"
done
```

### Test Strong Password
```bash
# Should fail - too short
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"test","email":"test@test.com","password":"Short1!","confirmPassword":"Short1!"}'

# Should succeed
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"test","email":"test@test.com","password":"SecurePassword123!","confirmPassword":"SecurePassword123!"}'
```

## 📚 Additional Resources

- **Full Documentation**: `SECURITY.md`
- **Implementation Summary**: `SECURITY_FIXES_SUMMARY.md`
- **OWASP Auth Guide**: https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html
- **ASP.NET Core Security**: https://learn.microsoft.com/en-us/aspnet/core/security/

## ✅ Deployment Checklist

Before deploying to production:

- [ ] Run `dotnet ef database update`
- [ ] Test login with existing accounts
- [ ] Test registration with strong password
- [ ] Test registration with weak password (should fail)
- [ ] Test account lockout (5 failures)
- [ ] Test rate limiting (11 rapid requests)
- [ ] Verify logging is working
- [ ] Review security headers
- [ ] Enable HTTPS only
- [ ] Update documentation for users about new password requirements

## 💡 Tips

1. **Communicate Password Changes**: Notify existing users about new password requirements when they change passwords
2. **Monitor Lockouts**: Set up alerts for unusual lockout patterns
3. **Adjust Settings**: Start with these defaults, adjust based on your specific needs
4. **Consider Geography**: If attacks come from specific regions, consider geo-blocking
5. **Regular Reviews**: Review authentication logs weekly for suspicious patterns
