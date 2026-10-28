using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace SecureNotes.Api.Security;

public static class SessionToken
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(WebEncoders.Base64UrlDecode(token));
}
