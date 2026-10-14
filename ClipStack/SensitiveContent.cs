using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClipStack
{
    /// <summary>
    /// Kopyalanan metnin geçmişe alınmaması gereken bir gizli bilgi olup olmadığını tahmin eder.
    ///
    /// Neden: Parola yöneticileri panoya "beni kaydetme" işareti koyar, ama tarayıcıların yerleşik parola
    /// yöneticileri, terminaller ve metin editörleri koymaz. Bu yüzden kaynağa ek olarak içeriğe de bakılır.
    /// </summary>
    public static class SensitiveContent
    {
        /// <summary>
        /// Bilinen anahtar/token biçimleri. Ön ekleri ve uzunlukları özgül olduğu için yanlış alarm nadirdir;
        /// varsayılan olarak açıktır.
        /// </summary>
        private static readonly (string Name, Regex Pattern)[] SecretPatterns =
        {
            ("özel anahtar (PEM)", R(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----")),
            ("AWS erişim anahtarı", R(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")),
            ("GitHub token", R(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})")),
            ("GitLab token", R(@"\bglpat-[A-Za-z0-9_\-]{20,}")),
            ("Slack token", R(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}")),
            ("Stripe gizli anahtarı", R(@"\b(?:sk|rk)_live_[A-Za-z0-9]{20,}")),
            ("API anahtarı (sk-…)", R(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{32,}")),
            ("Google API anahtarı", R(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
            ("npm token", R(@"\bnpm_[A-Za-z0-9]{36}\b")),
            ("JWT", R(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")),
            ("Azure bağlantı anahtarı", R(@"(?:AccountKey|SharedAccessKey)=[A-Za-z0-9+/=]{40,}")),
        };

        private static Regex R(string pattern) =>
            new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        /// <summary>
        /// Metin ayarlara göre atlanmalı mı? Atlanacaksa <paramref name="reason"/> nedeni söyler
        /// (log'a yazılır; içeriğin kendisi asla yazılmaz).
        /// </summary>
        public static bool ShouldSkip(string text, AppSettings settings, out string reason)
        {
            reason = "";
            if (settings.SkipSecrets)
            {
                string? secret = FindSecret(text);
                if (secret != null)
                {
                    reason = secret;
                    return true;
                }
            }
            if (settings.SkipPasswordLike && LooksLikePassword(text))
            {
                reason = "parolaya benzeyen metin";
                return true;
            }
            return false;
        }

        /// <summary>Metinde bilinen bir anahtar/token biçimi varsa adını, yoksa null döndürür.</summary>
        internal static string? FindSecret(string text)
        {
            foreach (var (name, pattern) in SecretPatterns)
            {
                try
                {
                    if (pattern.IsMatch(text)) return name;
                }
                catch (RegexMatchTimeoutException)
                {
                    // Çok uzun ve tuhaf metin: zaman aşımı bir sonraki desene geçmeye engel değil.
                }
            }
            return null;
        }

        /// <summary>
        /// Tek parça, boşluksuz, 8–64 karakterlik ve en az üç karakter sınıfı (küçük, büyük, rakam, simge)
        /// içeren metin. Tarayıcıdan kopyalanan parolaları yakalar; ama "Foo.Bar(1)" gibi kod parçalarını da
        /// yakalayabileceği için varsayılan olarak kapalıdır.
        /// </summary>
        internal static bool LooksLikePassword(string text)
        {
            string s = text.Trim();
            if (s.Length < 8 || s.Length > 64) return false;
            if (s.Any(char.IsWhiteSpace)) return false;
            if (s.Contains("://") || s.Contains('\\') || s.Contains('/')) return false; // adres, dosya yolu
            if (s.Contains('@') && s.Contains('.')) return false;                        // e-posta

            int classes = 0;
            if (s.Any(char.IsLower)) classes++;
            if (s.Any(char.IsUpper)) classes++;
            if (s.Any(char.IsDigit)) classes++;
            if (s.Any(c => !char.IsLetterOrDigit(c))) classes++;
            return classes >= 3;
        }
    }
}
