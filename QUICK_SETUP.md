# ⚡ Quick Setup Commands

## 🚀 Get Started in 3 Commands

### Step 1: Generate JWT Secret
```powershell
# Run in PowerShell to generate a random 64-character secret
-join ((33..126) | Get-Random -Count 64 | % {[char]$_})
```

### Step 2: Set JWT Secret (Copy the output from Step 1)
```bash
dotnet user-secrets set "Jwt:Secret" "paste-your-generated-secret-here"
```

### Step 3: Run the Application
```bash
dotnet run
```

---

## ✅ Verify Setup

```bash
# Check if secret is set
dotnet user-secrets list

# Should show:
# Jwt:Secret = your-secret-key
```

---

## 🧪 Test It Works

### Test Login:
```bash
curl -X POST https://localhost:7086/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"youruser","password":"YourPassword123!"}' \
  -k

# Should return a token
```

### Test Protected Endpoint (Password Change):
```bash
# First get a token from login
$TOKEN = "your-token-from-login"

# Then try to change password
curl -X POST https://localhost:7086/api/auth/change-password \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{
    "currentPassword": "YourPassword123!",
    "newPassword": "NewPassword456!",
    "confirmNewPassword": "NewPassword456!"
  }' \
  -k
```

---

## 🔒 Production Deployment

### For Azure App Service:
1. Go to **Configuration** → **Application settings**
2. Add these environment variables:
   ```
   Name: Jwt__Secret
   Value: <generate a NEW production secret>
   
   Name: Jwt__Issuer
   Value: SessionApp
   
   Name: Jwt__Audience
   Value: SessionApp
   ```

### For Docker:
```yaml
environment:
  - Jwt__Secret=${JWT_SECRET}
  - Jwt__Issuer=SessionApp
  - Jwt__Audience=SessionApp
```

---

## ⚠️ Troubleshooting

### "JWT Secret is not configured" warning:
```bash
# Ensure you've set the secret
dotnet user-secrets set "Jwt:Secret" "your-64-char-secret"

# Verify
dotnet user-secrets list
```

### Build errors about missing packages:
```bash
dotnet restore
dotnet build
```

### 401 Unauthorized on protected endpoints:
- Make sure you're including the `Authorization: Bearer {token}` header
- Check that the token hasn't expired (7 days default)
- Verify the token is valid by decoding it at https://jwt.io

---

## 📝 Quick Reference

### Endpoints that now REQUIRE authentication:
- `POST /api/auth/change-password` - Change password
- `GET /api/auth/profile/me` - Get my profile with email

### Endpoints that are PUBLIC (no auth needed):
- `POST /api/auth/register` - Register new user
- `POST /api/auth/login` - Login
- `GET /api/auth/profile/{userId}` - Get public profile (no email)
- `GET /api/auth/profile/username/{username}` - Get public profile by username
- `GET /api/auth/{userId}/hosted-games` - Get user's hosted games
- `GET /api/auth/{userId}/played-games` - Get user's played games
- `GET /api/auth/{userId}/all-games` - Get all user's games

---

## 🎯 Success Checklist

- [ ] JWT secret generated and set in user secrets
- [ ] Application runs without warnings
- [ ] Login returns a JWT token
- [ ] Password change requires authentication
- [ ] Public profiles don't show email
- [ ] `/api/auth/profile/me` returns email for authenticated users

---

**Need help?** See `SECURITY_SETUP_GUIDE.md` for detailed instructions.
