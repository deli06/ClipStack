using System;
using System.IO;
using System.Security.Cryptography;

namespace ClipStack
{
    /// <summary>
    /// Veritabanı şifreleme anahtarı. Rastgele 256 bitlik anahtar, Windows DPAPI ile (CurrentUser kapsamı)
    /// korunarak <c>db.key</c> dosyasında saklanır. Böylece anahtar sadece bu Windows hesabında, oturum
    /// açılmışken çözülebilir; dosyayı başka kullanıcı veya başka bilgisayar okuyamaz. Kullanıcıdan parola istenmez.
    /// </summary>
    public static class DatabaseKey
    {
        public const int KeyBytes = 32;

        /// <summary>DPAPI'ye ek gizli veri; başka bir uygulamanın DPAPI ile koruduğu veriyle karışmasın.</summary>
        private static readonly byte[] Entropy = "ClipStack.DatabaseKey.v1"u8.ToArray();

        public static string DefaultPath => Path.Combine(Settings.DataFolder, "db.key");

        /// <summary>Anahtarı okur; dosya yoksa yenisini üretip kaydeder.</summary>
        /// <exception cref="CryptographicException">Dosya var ama bu hesapta çözülemiyor (başka kullanıcı/bilgisayar).</exception>
        public static byte[] LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                if (key.Length != KeyBytes)
                    throw new CryptographicException("Anahtar dosyası bozuk.");
                return key;
            }

            byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);
            byte[] protectedKey = ProtectedData.Protect(created, Entropy, DataProtectionScope.CurrentUser);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, protectedKey);
            File.Move(tmp, path, overwrite: true);
            return created;
        }

        /// <summary>SQLCipher'ın ham anahtar biçimi: <c>x'…64 onaltılık…'</c> (PBKDF2 türetmesi atlanır, açılış hızlı olur).</summary>
        internal static string ToSqlLiteral(byte[] key) => "\"x'" + Convert.ToHexString(key) + "'\"";
    }
}
