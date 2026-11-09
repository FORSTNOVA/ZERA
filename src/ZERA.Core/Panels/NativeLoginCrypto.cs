using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ZERA.Core.Panels
{
    internal static class NativeLoginCrypto
    {
        private static byte[] Read(BinaryReader reader, byte tag)
        {
            if (reader.ReadByte() != tag) throw new InvalidDataException();
            int length = reader.ReadByte();
            if (length >= 128)
            {
                int count = length & 127; if (count < 1 || count > 3) throw new InvalidDataException();
                length = 0; while (count-- > 0) length = (length << 8) | reader.ReadByte();
            }
            if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
            return reader.ReadBytes(length);
        }
        private static byte[] Unsigned(byte[] value) { int i = 0; while (i < value.Length - 1 && value[i] == 0) i++; var result = new byte[value.Length - i]; Buffer.BlockCopy(value, i, result, 0, result.Length); return result; }
        public static string Rsa(string publicKey, string value, bool base64)
        {
            try
            {
                byte[] key = Convert.FromBase64String(publicKey); if (key.Length > 16384) throw new InvalidDataException();
                using (var reader = new BinaryReader(new MemoryStream(key))) key = Read(reader, 0x30);
                if (key[0] == 0x30)
                {
                    using (var reader = new BinaryReader(new MemoryStream(key))) { Read(reader, 0x30); key = Read(reader, 0x03); }
                    if (key[0] != 0) throw new InvalidDataException();
                    using (var reader = new BinaryReader(new MemoryStream(key, 1, key.Length - 1))) key = Read(reader, 0x30);
                }
                using (var reader = new BinaryReader(new MemoryStream(key)))
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.ImportParameters(new RSAParameters { Modulus = Unsigned(Read(reader, 0x02)), Exponent = Unsigned(Read(reader, 0x02)) });
                    byte[] encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(value), false);
                    return base64 ? Convert.ToBase64String(encrypted) : BitConverter.ToString(encrypted).Replace("-", "");
                }
            }
            catch { throw new PanelApiException("登录服务的 RSA 公钥无效或加密失败，未发送账号密码。"); }
        }
    }
}
