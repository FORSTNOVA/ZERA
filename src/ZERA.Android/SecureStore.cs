using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text;

namespace ZERA.Android;

// The complete private state is authenticated and encrypted. Android backup is
// disabled because these keys cannot be transferred to another device.
internal sealed class SecureStore
{
    private const string Alias = "zera-state-v1";
    private readonly string _path;
    private readonly object _sync = new();
    public SecureStore(string path) => _path = path;
    private static IKey Key()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore");
        store.Load(null);
        if (store.ContainsAlias(Alias)) return store.GetKey(Alias, null);
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
        using var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm).SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).Build();
        generator.Init(spec);
        return generator.GenerateKey();
    }
    public string Read()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return null;
            byte[] bytes = File.ReadAllBytes(_path);
            if (bytes.Length < 29 || bytes[0] != 1) throw new IOException("本地账号资料格式无效，请保留备份后重试。");
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, bytes[1..13]));
            return Encoding.UTF8.GetString(cipher.DoFinal(bytes[13..]));
        }
    }
    public void Write(string value)
    {
        lock (_sync)
        {
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
            cipher.Init(CipherMode.EncryptMode, Key());
            byte[] iv = cipher.GetIV();
            if (iv.Length != 12) throw new IOException("本机凭据加密初始化失败。");
            byte[] encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(value));
            var data = new byte[13 + encrypted.Length]; data[0] = 1;
            iv.CopyTo(data, 1); encrypted.CopyTo(data, 13);
            string temporary = _path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(data); file.Flush(true); }
            File.Move(temporary, _path, true);
        }
    }
}
