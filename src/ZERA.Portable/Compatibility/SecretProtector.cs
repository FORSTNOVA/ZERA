using System;

namespace ZERA.Core.Support
{
    // Windows DPAPI documents cannot be migrated by copying a config file.
    // Android persists its entire state using its own Keystore-backed store.
    public static class SecretProtector
    {
        public static bool IsProtected(string value) => value != null && value.StartsWith("enc:", StringComparison.Ordinal);
        public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : throw new PlatformNotSupportedException("请使用本机加密存储提供程序。");
        public static string ProtectRequired(string value) => Protect(value);
        public static string Unprotect(string value) => IsProtected(value) ? throw new PlatformNotSupportedException("Windows DPAPI 凭据无法在此平台解密，请重新登录。") : value ?? "";
    }
}
