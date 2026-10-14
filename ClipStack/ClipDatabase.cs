using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace ClipStack
{
    public sealed class ClipItem
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "text";

        /// <summary>
        /// Metnin sadece başı (liste önizlemesi için). Tam metin bellekte tutulmaz;
        /// yapıştırırken <see cref="ClipDatabase.GetText"/> ile okunur.
        /// </summary>
        public string Head { get; init; } = "";

        /// <summary>Tam metnin karakter sayısı.</summary>
        public int Length { get; init; }

        /// <summary>Tam metindeki satır sayısı.</summary>
        public int LineCount { get; init; }

        public string? SourceApp { get; init; }
        public DateTime LastUsed { get; init; }
        public bool Pinned { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>Küçük resmi gerektiğinde (liste satırı görününce) yükler.</summary>
        internal ClipDatabase? Owner { get; init; }

        /// <summary>HTML veya RTF biçimi saklanmış mı (web, Word, Outlook kopyaları).</summary>
        public bool HasFormat { get; init; }

        public bool IsImage => Kind == ClipDatabase.KindImage;
        public bool IsFiles => Kind == ClipDatabase.KindFiles;
        public bool IsText => Kind == ClipDatabase.KindText;

        /// <summary>Listede metin satırı mı gösterilecek (metin ve dosya kayıtları), yoksa küçük resim mi.</summary>
        public bool ShowsText => !IsImage;

        /// <summary>Hızlı yapıştırma numarası (listenin ilk 9 öğesi için "1"…"9", diğerleri boş).</summary>
        public string QuickKey { get; set; } = "";

        /// <summary>Listede gösterilecek tek satırlık önizleme.</summary>
        public string Preview
        {
            get
            {
                if (IsImage) return $"Görsel  {Width}×{Height}";
                if (IsFiles)
                {
                    // Dosya kayıtlarında metin, satır başına bir tam yol tutar; listede sadece adlar.
                    var names = Head.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(p => Path.GetFileName(p.TrimEnd('\r', '\\')))
                                    .Select(n => n.Length == 0 ? "?" : n);
                    string joined = string.Join(", ", names);
                    string more = Length > Head.Length ? "…" : "";
                    return $"📁 {joined}{more}";
                }
                string s = Head.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\t', ' ').Trim();
                return s.Length > 200 || Length > Head.Length ? s.Substring(0, Math.Min(s.Length, 200)) + "…" : s;
            }
        }

        public string Meta
        {
            get
            {
                string when = LastUsed.Date == DateTime.Today
                    ? LastUsed.ToString("HH:mm")
                    : LastUsed.ToString("dd.MM.yyyy HH:mm");
                if (IsImage)
                    return $"{when} · {SourceApp ?? "?"} · {Width}×{Height} görsel";
                if (IsFiles)
                    return $"{when} · {SourceApp ?? "?"} · {LineCount} dosya";
                string lines = LineCount > 1 ? $" · {LineCount} satır" : "";
                string fmt = HasFormat ? " · biçimli" : "";
                return $"{when} · {SourceApp ?? "?"}{lines} · {Length} karakter{fmt}";
            }
        }

        public string PinGlyph => Pinned ? "📌" : "";

        public ImageSource? Thumbnail => IsImage ? Owner?.GetThumbnail(Id) : null;
    }

    public sealed class ClipStats
    {
        public long TotalCopies { get; init; }
        public long TotalPastes { get; init; }
        public DateTime Since { get; init; }
        public long Items { get; init; }
        public long Texts { get; init; }
        public long Images { get; init; }
        public long Files { get; init; }
        public long Formatted { get; init; }
        public long Pinned { get; init; }
        public long AddedToday { get; init; }
        public DateTime? Oldest { get; init; }
        public long DatabaseBytes { get; init; }
    }

    /// <summary>
    /// Veritabanı dosyası elimizdeki anahtarla açılamıyor: başka bir Windows hesabında/bilgisayarda şifrelenmiş
    /// ya da anahtar dosyası (db.key) kaybolmuş/değişmiş.
    /// </summary>
    public sealed class DatabaseKeyException : Exception
    {
        public DatabaseKeyException(string path, Exception inner)
            : base("Veritabanı bu anahtarla açılamadı (başka bir hesapta şifrelenmiş veya anahtar dosyası değişmiş): " + path, inner)
        {
            Path = path;
        }

        public string Path { get; }
    }

    /// <summary>
    /// Silinen bir kaydın tüm sütunları; "geri al" ile aynı kimlikle (id) geri yazılır.
    /// </summary>
    public sealed class DeletedClip
    {
        internal DeletedClip(long id, IReadOnlyDictionary<string, object> values)
        {
            Id = id;
            Values = values;
        }

        public long Id { get; }
        internal IReadOnlyDictionary<string, object> Values { get; }
    }

    /// <summary>Pano geçmişini SQLite'ta saklar.</summary>
    public sealed class ClipDatabase : IDisposable
    {
        public const string KindText = "text";
        public const string KindImage = "image";
        public const string KindFiles = "files";

        /// <summary>Yedekten içe aktarılabilmesi için bulunması zorunlu sütunlar.</summary>
        private static readonly string[] RequiredColumns = { "text", "hash", "created_utc", "last_used_utc" };

        private const string StatCopies = "copies";
        private const string StatPastes = "pastes";
        private const string StatSince = "since_utc";

        /// <summary>Listede gösterilen metin başının uzunluğu (önizleme 200 karakter keser).</summary>
        private const int HeadLength = 300;

        private static readonly System.Globalization.CultureInfo Tr =
            System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

        /// <summary>
        /// Arama için küçük harfe çevirir ve I/ı/İ/i'yi tek harfe indirger. Böylece hem Türkçe
        /// ("İSTANBUL" ↔ "istanbul") hem İngilizce ("WINWORD" ↔ "winword") kelimeler bulunur;
        /// sadece Türkçe kural kullanılsaydı I → ı olduğu için ikincisi eşleşmezdi.
        /// </summary>
        internal static string NormalizeForSearch(string s) => s.ToLower(Tr).Replace('ı', 'i');

        private readonly SqliteConnection _conn;
        private readonly string _path;
        private readonly byte[] _key;
        private readonly Dictionary<long, ImageSource?> _thumbCache = new();

        /// <param name="path">Veritabanı dosyası. Yoksa şifreli olarak oluşturulur; şifresiz (eski) ise şifrelenir.</param>
        /// <param name="key">SQLCipher anahtarı (<see cref="DatabaseKey.KeyBytes"/> bayt).</param>
        /// <exception cref="DatabaseKeyException">Dosya başka bir anahtarla şifrelenmiş.</exception>
        public ClipDatabase(string path, byte[] key)
        {
            if (key.Length != DatabaseKey.KeyBytes)
                throw new ArgumentException("Geçersiz anahtar uzunluğu.", nameof(key));
            _path = path;
            _key = key;

            if (IsPlaintextDatabase(path))
            {
                Log.Info("Şifresiz geçmiş şifreleniyor: " + path);
                EncryptPlaintextFile(path, key);
            }
            _conn = OpenConnection(path, key);

            // Büyük/küçük harf duyarsız "içerir" (Türkçe karakterler dahil). SQLite'ın LIKE'ı sadece ASCII'de
            // duyarsız olduğu için arama C# fonksiyonuyla yapılır, ama filtreleme ve LIMIT SQLite'ta
            // kalır; böylece tüm geçmiş belleğe yüklenmez. İğne (aranan kelime) Search'te önceden normalleşir.
            _conn.CreateFunction("tr_has", (string haystack, string normalizedNeedle) =>
                NormalizeForSearch(haystack).Contains(normalizedNeedle, StringComparison.Ordinal),
                isDeterministic: true);

            EnableAutoVacuum();
            Exec("PRAGMA journal_mode=WAL;");
            // Checkpoint sonrası WAL dosyası bu boyutun üstünde kalmasın (büyük görsellerden sonra şişmesin).
            Exec("PRAGMA journal_size_limit = 4194304;");
            Exec(@"
                CREATE TABLE IF NOT EXISTS clips (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    text          TEXT    NOT NULL,
                    hash          TEXT    NOT NULL UNIQUE,
                    source_app    TEXT,
                    created_utc   INTEGER NOT NULL,
                    last_used_utc INTEGER NOT NULL,
                    pinned        INTEGER NOT NULL DEFAULT 0,
                    kind          TEXT    NOT NULL DEFAULT 'text',
                    image         BLOB,
                    thumb         BLOB,
                    width         INTEGER,
                    height        INTEGER,
                    html          BLOB,
                    rtf           BLOB
                );
                CREATE INDEX IF NOT EXISTS ix_clips_last_used ON clips(last_used_utc DESC);
                CREATE TABLE IF NOT EXISTS stats (
                    key   TEXT PRIMARY KEY,
                    value INTEGER NOT NULL
                );");
            Migrate();
            // Listenin sırası (sabitlenenler önce, sonra en yeni) bu indeksten okunur; LIMIT dolunca tarama durur.
            Exec("CREATE INDEX IF NOT EXISTS ix_clips_pinned_last_used ON clips(pinned DESC, last_used_utc DESC);");
            Exec("INSERT OR IGNORE INTO stats (key, value) VALUES ($k, $v);", ("$k", StatSince), ("$v", Now()));
        }

        // ---------- Şifreleme ----------

        private const int SqliteNotADatabase = 26;

        /// <summary>
        /// Bağlantı açar ve (anahtar verildiyse) SQLCipher anahtarını uygular. Havuz kapalıdır: her bağlantının
        /// anahtarı açılışta bir kez verilir, havuzdan anahtarsız bağlantı gelmesi riski olmaz.
        /// </summary>
        /// <exception cref="DatabaseKeyException">Anahtar dosyayı açmıyorsa.</exception>
        internal static SqliteConnection OpenConnection(string path, byte[]? key, bool readOnly = false)
        {
            string mode = readOnly ? ";Mode=ReadOnly" : "";
            var conn = new SqliteConnection($"Data Source={path};Pooling=False{mode}");
            conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                if (key != null)
                {
                    cmd.CommandText = $"PRAGMA key = {DatabaseKey.ToSqlLiteral(key)};";
                    cmd.ExecuteNonQuery();
                }
                // Anahtar yanlışsa SQLCipher hatayı ilk okumada verir; burada okuyup erkenden yakala.
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
                cmd.ExecuteScalar();
                return conn;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteNotADatabase)
            {
                conn.Dispose();
                throw new DatabaseKeyException(path, ex);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        /// <summary>Dosya şifresiz bir SQLite veritabanı mı (başlık "SQLite format 3\0" ile başlar).</summary>
        internal static bool IsPlaintextDatabase(string path)
        {
            if (!File.Exists(path)) return false;
            var header = new byte[16];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(header, 0, 16) < 16) return false;
            return header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
        }

        /// <summary>
        /// Şifresiz veritabanını yerinde şifreler: şifreli kopyası çıkarılır, sonra eskisinin yerine konur.
        /// Yarıda kesilirse orijinal dosya bozulmadan kalır.
        /// </summary>
        internal static void EncryptPlaintextFile(string path, byte[] key)
        {
            string tmp = path + ".encrypting";
            File.Delete(tmp);
            using (var conn = OpenConnection(path, key: null))
            {
                ExecOn(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
                ExportTo(conn, tmp, key);
            }
            SqliteConnection.ClearAllPools();

            File.Move(tmp, path, overwrite: true);
            foreach (string side in new[] { path + "-wal", path + "-shm" })
                File.Delete(side); // checkpoint'ten sonra boştur; şifresiz artık kalmasın
        }

        /// <summary>
        /// Açık bağlantıdaki veritabanını yeni bir dosyaya aktarır. <paramref name="key"/> null ise
        /// hedef şifresiz olur. Artımlı otomatik sıkıştırma hedefte de açık olur.
        /// </summary>
        private static void ExportTo(SqliteConnection conn, string destination, byte[]? key)
        {
            using (var attach = conn.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $p AS export_target KEY $k;";
                attach.Parameters.AddWithValue("$p", destination);
                attach.Parameters.AddWithValue("$k", key == null ? "" : "x'" + Convert.ToHexString(key) + "'");
                attach.ExecuteNonQuery();
            }
            try
            {
                ExecOn(conn, "PRAGMA export_target.auto_vacuum = INCREMENTAL;");
                ExecOn(conn, "SELECT sqlcipher_export('export_target');");
            }
            finally
            {
                ExecOn(conn, "DETACH DATABASE export_target;");
            }
        }

        private static void ExecOn(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader(); // sqlcipher_export bir satır döndürür; okuyup bitir
            while (r.Read()) { }
        }

        /// <summary>
        /// Klasördeki şifresiz yedekleri (eski sürümden kalan) yerinde şifreler. Şifrelenen dosya sayısını döndürür.
        /// </summary>
        public int EncryptPlaintextBackups(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            int count = 0;
            foreach (string f in Directory.GetFiles(folder, "history-*.db"))
            {
                try
                {
                    if (!IsPlaintextDatabase(f)) continue;
                    EncryptPlaintextFile(f, _key);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Error("Eski yedek şifrelenemedi: " + f, ex);
                }
            }
            return count;
        }

        /// <summary>
        /// Silinen kayıtların boşalttığı sayfaların dosyadan geri verilebilmesi için artımlı otomatik
        /// sıkıştırmayı açar. Eski veritabanlarında bu ayar ancak bir kerelik VACUUM ile etkinleşir.
        /// </summary>
        private void EnableAutoVacuum()
        {
            const long Incremental = 2;
            if (Scalar("PRAGMA auto_vacuum;") == Incremental)
                return;
            Exec("PRAGMA auto_vacuum = INCREMENTAL;");
            if (Scalar("SELECT COUNT(*) FROM sqlite_master;") > 0)
            {
                Log.Info("Veritabanı artımlı sıkıştırmaya geçiriliyor (tek seferlik VACUUM).");
                Exec("VACUUM;");
            }
        }

        /// <summary>Boş sayfaları dosyadan geri verir. Silme işlemlerinden sonra çağrılır; ucuzdur.</summary>
        private readonly object _maintenanceGate = new();
        private Task _maintenance = Task.CompletedTask;

        /// <summary>
        /// Boş sayfaların dosyadan geri verilmesini arka planda sıraya koyar. Büyük bir görsel silindiğinde
        /// incremental_vacuum megabaytlarca sayfa taşıyabilir; arayüz iş parçacığında yapılırsa takılma olur.
        /// İşler sırayla çalışır, aynı anda iki temizlik olmaz.
        /// </summary>
        private void ReleaseFreePages()
        {
            lock (_maintenanceGate)
            {
                _maintenance = _maintenance.ContinueWith(_ =>
                {
                    try
                    {
                        using var conn = OpenConnection(_path, _key);
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "PRAGMA freelist_count;";
                        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
                            ExecOn(conn, "PRAGMA incremental_vacuum;");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Boş sayfalar geri verilemedi", ex);
                    }
                }, TaskScheduler.Default);
            }
        }

        /// <summary>Sıradaki arka plan temizliklerinin bitmesini bekler (testler ve kapanış için).</summary>
        public Task WaitForMaintenanceAsync()
        {
            lock (_maintenanceGate) return _maintenance;
        }

        /// <summary>İlk sürümle oluşturulmuş veritabanına görsel sütunlarını ekler.</summary>
        private void Migrate()
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(clips);";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            if (!cols.Contains("kind"))   Exec("ALTER TABLE clips ADD COLUMN kind TEXT NOT NULL DEFAULT 'text';");
            if (!cols.Contains("image"))  Exec("ALTER TABLE clips ADD COLUMN image BLOB;");
            if (!cols.Contains("thumb"))  Exec("ALTER TABLE clips ADD COLUMN thumb BLOB;");
            if (!cols.Contains("width"))  Exec("ALTER TABLE clips ADD COLUMN width INTEGER;");
            if (!cols.Contains("height")) Exec("ALTER TABLE clips ADD COLUMN height INTEGER;");
            if (!cols.Contains("html"))   Exec("ALTER TABLE clips ADD COLUMN html BLOB;");
            if (!cols.Contains("rtf"))    Exec("ALTER TABLE clips ADD COLUMN rtf BLOB;");
        }

        /// <summary>
        /// Metni ekler; aynı metin zaten varsa sadece en üste taşır.
        /// Bu sayede uygulamanın kendi panoya yazdıkları da kopya oluşturmaz.
        /// </summary>
        /// <remarks>
        /// Aynı metin farklı biçimle (ör. önce web'den, sonra Not Defteri'nden) tekrar kopyalanırsa
        /// en son kopyanın biçimi geçerli olur ve kaynak uygulama güncellenir.
        /// </remarks>
        public void AddOrTouch(string text, byte[]? html, byte[]? rtf, string? sourceApp)
        {
            long now = Now();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO clips (text, hash, source_app, created_utc, last_used_utc, kind, html, rtf)
                    VALUES ($text, $hash, $app, $now, $now, 'text', $html, $rtf)
                    ON CONFLICT(hash) DO UPDATE SET
                        last_used_utc = excluded.last_used_utc,
                        source_app    = excluded.source_app,
                        html          = excluded.html,
                        rtf           = excluded.rtf;";
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.Add("$html", SqliteType.Blob).Value = (object?)html ?? DBNull.Value;
                cmd.Parameters.Add("$rtf", SqliteType.Blob).Value = (object?)rtf ?? DBNull.Value;
                cmd.Parameters.AddWithValue("$hash", Hash(Encoding.UTF8.GetBytes(text)));
                cmd.Parameters.AddWithValue("$app", (object?)sourceApp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            IncrementStat(StatCopies);
            ApplyLimits();
        }

        /// <summary>
        /// PNG olarak görsel ekler (küçük resmi burada, çağıran iş parçacığında üretir). Uygulama bunun yerine
        /// küçük resmi arka planda hazırlayıp <see cref="AddImage(PreparedImage, string?)"/> çağırır.
        /// </summary>
        public void AddImage(byte[] png, string? sourceApp) =>
            AddImage(ImageProcessing.Prepare(png) ?? throw new InvalidDataException("Görsel okunamadı."), sourceApp);

        /// <summary>Hazırlanmış görseli ana bağlantıyla ekler; aynı görsel varsa en üste taşır.</summary>
        public void AddImage(PreparedImage image, string? sourceApp)
        {
            InsertImage(_conn, image, sourceApp);
            ApplyLimits();
        }

        /// <summary>
        /// Görseli kendi bağlantısıyla, arka plan iş parçacığında yazar. 20 MB'lık bir görseli şifreleyip diske
        /// yazmak yarım saniyeyi bulabildiği için uygulama bunu kullanır. Bittikten sonra arayüz iş parçacığında
        /// <see cref="ApplyLimits"/> çağrılmalıdır.
        /// </summary>
        public Task AddImageInBackgroundAsync(PreparedImage image, string? sourceApp) => Task.Run(() =>
        {
            using var conn = OpenConnection(_path, _key);
            InsertImage(conn, image, sourceApp);
        });

        private static void InsertImage(SqliteConnection conn, PreparedImage image, string? sourceApp)
        {
            long now = Now();
            using (var cmd = conn.CreateCommand())
            {
                // Aynı görsel zaten varsa 20 MB'ı yeniden göndermeden sadece tarihini güncelle.
                cmd.CommandText = "UPDATE clips SET last_used_utc = $now WHERE hash = $hash;";
                cmd.Parameters.AddWithValue("$hash", image.Hash);
                cmd.Parameters.AddWithValue("$now", now);
                if (cmd.ExecuteNonQuery() == 0)
                {
                    cmd.CommandText = @"
                        INSERT INTO clips (text, hash, source_app, created_utc, last_used_utc, kind, image, thumb, width, height)
                        VALUES ('', $hash, $app, $now, $now, 'image', $image, $thumb, $w, $h)
                        ON CONFLICT(hash) DO UPDATE SET last_used_utc = excluded.last_used_utc;";
                    cmd.Parameters.AddWithValue("$app", (object?)sourceApp ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$image", image.Png);
                    cmd.Parameters.AddWithValue("$thumb", image.Thumbnail);
                    cmd.Parameters.AddWithValue("$w", image.Width);
                    cmd.Parameters.AddWithValue("$h", image.Height);
                    cmd.ExecuteNonQuery();
                }
            }
            using var stat = conn.CreateCommand();
            stat.CommandText = @"INSERT INTO stats (key, value) VALUES ($k, 1)
                                 ON CONFLICT(key) DO UPDATE SET value = value + 1;";
            stat.Parameters.AddWithValue("$k", StatCopies);
            stat.ExecuteNonQuery();
        }

        /// <summary>
        /// Kopyalanan dosya listesini ekler (dosyaların kendisi değil, tam yolları saklanır).
        /// Aynı liste zaten varsa en üste taşır.
        /// </summary>
        public void AddFiles(IReadOnlyList<string> paths, string? sourceApp)
        {
            if (paths.Count == 0) return;
            string text = string.Join("\n", paths);
            long now = Now();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO clips (text, hash, source_app, created_utc, last_used_utc, kind)
                    VALUES ($text, $hash, $app, $now, $now, 'files')
                    ON CONFLICT(hash) DO UPDATE SET
                        last_used_utc = excluded.last_used_utc,
                        source_app    = excluded.source_app;";
                cmd.Parameters.AddWithValue("$text", text);
                cmd.Parameters.AddWithValue("$hash", "files:" + Hash(Encoding.UTF8.GetBytes(text)));
                cmd.Parameters.AddWithValue("$app", (object?)sourceApp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.ExecuteNonQuery();
            }
            IncrementStat(StatCopies);
            ApplyLimits();
        }

        /// <summary>Dosya kaydındaki tam yollar.</summary>
        public string[] GetFiles(long id) =>
            (GetText(id) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Tam boyutlu görseli sadece yapıştırırken okuruz; listede küçük resim yeterli.</summary>
        public byte[]? GetImage(long id)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT image FROM clips WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as byte[];
        }

        /// <summary>Kaydın saklanan HTML ve RTF biçimlerini döndürür (yoksa null).</summary>
        public (byte[]? html, byte[]? rtf) GetFormats(long id)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT html, rtf FROM clips WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, null);
            return (r.IsDBNull(0) ? null : (byte[])r.GetValue(0),
                    r.IsDBNull(1) ? null : (byte[])r.GetValue(1));
        }

        /// <summary>
        /// Sabitlenmişler önce, sonra en son kullanılana göre sıralı, en fazla <paramref name="limit"/> kayıt.
        /// Her kelime metinde veya kaynak uygulama adında geçmeli (sıra fark etmez, Türkçe büyük/küçük harf duyarsız).
        /// Görseller "görsel/resim/image" kelimeleri ve boyutlarıyla (ör. 800x600), dosya kayıtları
        /// "dosya/file" kelimeleri ve yollarıyla bulunabilir.
        /// </summary>
        public List<ClipItem> Search(string? query, int limit = Settings.MaxListItems)
        {
            string[] terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            using var cmd = _conn.CreateCommand();
            var where = new StringBuilder();
            for (int i = 0; i < terms.Length; i++)
            {
                where.Append(i == 0 ? "WHERE " : " AND ");
                where.Append($@"(tr_has(CASE WHEN kind = 'image'
                                             THEN 'görsel resim image ' || IFNULL(width, 0) || 'x' || IFNULL(height, 0)
                                             WHEN kind = 'files' THEN 'dosya file ' || text
                                             ELSE text END, $t{i})
                                 OR tr_has(IFNULL(source_app, ''), $t{i}))");
                cmd.Parameters.AddWithValue("$t" + i, NormalizeForSearch(terms[i]));
            }

            cmd.CommandText = $@"
                SELECT id, substr(text, 1, $head), length(text),
                       length(text) - length(replace(text, char(10), '')) + 1,
                       source_app, last_used_utc, pinned, kind, width, height,
                       (html IS NOT NULL OR rtf IS NOT NULL) AS has_format
                FROM clips
                {where}
                ORDER BY pinned DESC, last_used_utc DESC
                LIMIT $limit;";
            cmd.Parameters.AddWithValue("$head", HeadLength);
            cmd.Parameters.AddWithValue("$limit", limit);

            var result = new List<ClipItem>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new ClipItem
                {
                    Id = r.GetInt64(0),
                    Head = r.GetString(1),
                    Length = r.GetInt32(2),
                    LineCount = r.GetInt32(3),
                    SourceApp = r.IsDBNull(4) ? null : r.GetString(4),
                    LastUsed = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)).LocalDateTime,
                    Pinned = r.GetInt64(6) != 0,
                    Kind = r.GetString(7),
                    Width = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                    Height = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                    HasFormat = r.GetInt64(10) != 0,
                    Owner = this,
                });
            }
            return result;
        }

        /// <summary>Kaydın tam metnini döndürür (yoksa null).</summary>
        public string? GetText(long id)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT text FROM clips WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as string;
        }

        /// <summary>Görsel kaydın küçük resmi. Bir kez çözülür, sonra bellekten verilir.</summary>
        public ImageSource? GetThumbnail(long id)
        {
            if (_thumbCache.TryGetValue(id, out var cached))
                return cached;

            ImageSource? result = null;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT thumb FROM clips WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteScalar() is byte[] bytes)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource = new MemoryStream(bytes);
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        result = bi;
                    }
                    catch { /* bozuk küçük resim: boş göster */ }
                }
            }
            _thumbCache[id] = result;
            return result;
        }

        public void Touch(long id) =>
            Exec("UPDATE clips SET last_used_utc = $now WHERE id = $id;", ("$now", Now()), ("$id", id));

        /// <summary>
        /// Kaydı siler ve geri alınabilmesi için tüm sütunlarını döndürür. Kayıt yoksa null.
        /// </summary>
        public DeletedClip? Delete(long id)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM clips WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                for (int i = 0; i < r.FieldCount; i++)
                    values[r.GetName(i)] = r.GetValue(i); // NULL → DBNull, geri yazarken de öyle kalır
            }

            Exec("DELETE FROM clips WHERE id = $id;", ("$id", id));
            _thumbCache.Remove(id);
            ReleaseFreePages();
            return new DeletedClip(id, values);
        }

        /// <summary>
        /// Silinen kaydı aynı kimlikle geri yazar. Bu arada aynı içerik yeniden kopyalandıysa
        /// (hash çakışması) geri yazılmaz ve false döner; içerik zaten listededir.
        /// </summary>
        public bool Restore(DeletedClip clip)
        {
            using var cmd = _conn.CreateCommand();
            var cols = clip.Values.Keys.ToList();
            var names = new List<string>();
            for (int i = 0; i < cols.Count; i++)
            {
                names.Add("$p" + i);
                cmd.Parameters.AddWithValue("$p" + i, clip.Values[cols[i]]);
            }
            cmd.CommandText = $"INSERT OR IGNORE INTO clips ({string.Join(", ", cols.Select(Quote))}) " +
                              $"VALUES ({string.Join(", ", names)});";
            return cmd.ExecuteNonQuery() > 0;
        }

        public void TogglePin(long id) =>
            Exec("UPDATE clips SET pinned = 1 - pinned WHERE id = $id;", ("$id", id));

        public void ClearUnpinned()
        {
            Exec("DELETE FROM clips WHERE pinned = 0;");
            _thumbCache.Clear();
            ReleaseFreePages();
        }

        /// <summary>
        /// Süre dolumu ve kayıt sınırlarını uygular. Sabitlenmiş kayıtlara asla dokunmaz.
        /// Her yeni kopyada, ayarlar kaydedildiğinde ve saatte bir çağrılır.
        /// </summary>
        public void ApplyLimits()
        {
            var s = Settings.Current;
            int deleted = 0;

            // Süre dolumu: son kullanımdan bu yana geçen süre
            if (s.ExpireEnabled)
            {
                long cutoff = DateTimeOffset.UtcNow.AddDays(-s.ExpireDays).ToUnixTimeMilliseconds();
                deleted += Exec("DELETE FROM clips WHERE pinned = 0 AND last_used_utc < $cutoff;", ("$cutoff", cutoff));
            }

            // Toplam sınır
            deleted += Exec(@"
                DELETE FROM clips
                WHERE pinned = 0 AND id NOT IN (
                    SELECT id FROM clips WHERE pinned = 0
                    ORDER BY last_used_utc DESC LIMIT $max);",
                ("$max", s.MaxHistory));

            // Görseller yer kapladığı için ayrıca sınırlanır
            deleted += Exec(@"
                DELETE FROM clips
                WHERE pinned = 0 AND kind = 'image' AND id NOT IN (
                    SELECT id FROM clips WHERE pinned = 0 AND kind = 'image'
                    ORDER BY last_used_utc DESC LIMIT $max);",
                ("$max", s.MaxImages));

            if (deleted > 0)
            {
                _thumbCache.Clear(); // silinen görsellerin küçük resimleri bellekte kalmasın
                ReleaseFreePages();  // ve diskte yer kaplamasın
            }
        }

        // ---------- İstatistikler ----------

        public void RecordPaste() => IncrementStat(StatPastes);

        private void IncrementStat(string key) =>
            Exec(@"INSERT INTO stats (key, value) VALUES ($k, 1)
                   ON CONFLICT(key) DO UPDATE SET value = value + 1;", ("$k", key));

        public void ResetStats()
        {
            Exec("DELETE FROM stats;");
            Exec("INSERT INTO stats (key, value) VALUES ($k, $v);", ("$k", StatSince), ("$v", Now()));
        }

        public ClipStats GetStats()
        {
            long todayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            long oldest = Scalar("SELECT IFNULL(MIN(created_utc), 0) FROM clips;");

            long dbBytes = 0;
            foreach (string f in new[] { _path, _path + "-wal" })
            {
                try { if (File.Exists(f)) dbBytes += new FileInfo(f).Length; } catch { }
            }

            return new ClipStats
            {
                TotalCopies = Scalar("SELECT IFNULL((SELECT value FROM stats WHERE key = 'copies'), 0);"),
                TotalPastes = Scalar("SELECT IFNULL((SELECT value FROM stats WHERE key = 'pastes'), 0);"),
                Since = DateTimeOffset.FromUnixTimeMilliseconds(
                            Scalar("SELECT IFNULL((SELECT value FROM stats WHERE key = 'since_utc'), 0);")).LocalDateTime,
                Items = Scalar("SELECT COUNT(*) FROM clips;"),
                Texts = Scalar("SELECT COUNT(*) FROM clips WHERE kind = 'text';"),
                Images = Scalar("SELECT COUNT(*) FROM clips WHERE kind = 'image';"),
                Files = Scalar("SELECT COUNT(*) FROM clips WHERE kind = 'files';"),
                Formatted = Scalar("SELECT COUNT(*) FROM clips WHERE html IS NOT NULL OR rtf IS NOT NULL;"),
                Pinned = Scalar("SELECT COUNT(*) FROM clips WHERE pinned = 1;"),
                AddedToday = Scalar("SELECT COUNT(*) FROM clips WHERE created_utc >= $t;", ("$t", todayStart)),
                Oldest = oldest == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(oldest).LocalDateTime,
                DatabaseBytes = dbBytes
            };
        }

        // ---------- Arka planda çalışabilenler ----------
        // Aşağıdaki metotlar ana bağlantıya (_conn, arayüz iş parçacığının) dokunmaz; kendi bağlantılarını açarlar.
        // Bu yüzden Task.Run içinden güvenle çağrılabilirler. SQLite (WAL) eşzamanlı okumaya izin verir;
        // ana bağlantı bu sırada yazmak isterse Microsoft.Data.Sqlite kilidin açılmasını bekler.

        /// <summary>Silinen kayıtların boşalttığı alanı diske geri verir (VACUUM). Büyük geçmişte saniyeler sürebilir.</summary>
        public void Compact()
        {
            using var conn = OpenConnection(_path, _key);
            ExecOn(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
            ExecOn(conn, "VACUUM;");
        }

        /// <summary><see cref="Compact"/>'ı arka plan iş parçacığında çalıştırır; arayüz donmaz.</summary>
        public Task CompactAsync() => Task.Run(Compact);

        /// <summary>
        /// Küçük resim önbelleğini boşaltır. Arka planda yapılan bir içe aktarmadan sonra arayüz iş parçacığında çağrılır.
        /// </summary>
        public void InvalidateThumbnails() => _thumbCache.Clear();

        // ---------- Yedekleme ----------

        /// <summary>
        /// Veritabanının tutarlı bir kopyasını <paramref name="destination"/> dosyasına yazar.
        /// Kendi bağlantısını açar; bu yüzden arka plan iş parçacığından da çağrılabilir.
        /// </summary>
        /// <param name="encrypted">
        /// true: geçmişle aynı anahtarla şifreli (sadece bu Windows hesabında geri yüklenebilir).
        /// false: şifresiz (başka bilgisayarda da açılır ama dosyayı ele geçiren herkes okuyabilir).
        /// </param>
        public void BackupTo(string destination, bool encrypted = true)
        {
            string full = Path.GetFullPath(destination);
            if (string.Equals(full, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Yedek, kullanılan veritabanının üzerine yazılamaz.");

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string tmp = full + ".tmp";
            File.Delete(tmp);
            using (var conn = OpenConnection(_path, _key))
                ExportTo(conn, tmp, encrypted ? _key : null);
            File.Move(tmp, full, overwrite: true);
        }

        /// <summary>
        /// Günlük otomatik yedek: <paramref name="folder"/> içindeki en yeni yedek <paramref name="interval"/>
        /// süresinden eskiyse yenisini alır ve en yeni <paramref name="keep"/> tanesi dışındakileri siler.
        /// Yedek alındıysa dosya yolunu, gerek yoksa null döndürür.
        /// </summary>
        public string? AutoBackupIfDue(string folder, TimeSpan interval, int keep)
        {
            Directory.CreateDirectory(folder);
            var existing = new DirectoryInfo(folder).GetFiles("history-*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (existing.Count > 0 && DateTime.UtcNow - existing[0].LastWriteTimeUtc < interval)
                return null;

            string path = Path.Combine(folder, $"history-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            BackupTo(path);

            foreach (var old in existing.Skip(Math.Max(0, keep - 1)))
            {
                try { old.Delete(); } catch (Exception ex) { Log.Error("Eski yedek silinemedi: " + old.FullName, ex); }
            }
            return path;
        }

        /// <summary>
        /// Bir yedekteki kayıtları mevcut geçmişe ekler (birleştirir). Zaten var olan içerik (aynı hash)
        /// atlanır; mevcut kayıtlar silinmez veya değişmez. Eklenen kayıt sayısını döndürür.
        /// </summary>
        /// <exception cref="InvalidDataException">Dosya bir ClipStack yedeği değilse.</exception>
        public int ImportFrom(string backupPath)
        {
            string full = Path.GetFullPath(backupPath);
            if (string.Equals(full, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Kullanılan veritabanı kendisine aktarılamaz.");
            if (!File.Exists(full))
                throw new FileNotFoundException("Yedek dosyası bulunamadı.", full);

            // Şifresiz yedekler (başka bilgisayardan, eski sürümden) olduğu gibi; diğerleri bizim anahtarımızla açılır.
            byte[]? backupKey = IsPlaintextDatabase(full) ? null : _key;
            var backupCols = ReadColumns(full, backupKey);
            if (backupCols.Count == 0 || RequiredColumns.Any(c => !backupCols.Contains(c)))
                throw new InvalidDataException(backupKey == null
                    ? "Seçilen dosya geçerli bir ClipStack yedeği değil."
                    : "Seçilen dosya açılamadı: ClipStack yedeği değil ya da başka bir Windows hesabında/bilgisayarda şifrelenmiş. " +
                      "Başka bilgisayara taşımak için yedeği şifresiz alın.");

            // Kendi bağlantısı: arka plan iş parçacığından çağrılabilsin (çok görselli yedekte saniyeler sürebilir).
            using var conn = OpenConnection(_path, _key);
            var mainCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(clips);";
                using var r = cmd.ExecuteReader();
                while (r.Read()) mainCols.Add(r.GetString(1));
            }

            // Kimlikler yeniden verilir (yedekteki id'ler mevcut kayıtlarla çakışabilir).
            string cols = string.Join(", ", mainCols.Where(c => backupCols.Contains(c) &&
                !string.Equals(c, "id", StringComparison.OrdinalIgnoreCase)).Select(Quote));

            using (var attach = conn.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $p AS bk KEY $k;";
                attach.Parameters.AddWithValue("$p", full);
                attach.Parameters.AddWithValue("$k", backupKey == null ? "" : "x'" + Convert.ToHexString(backupKey) + "'");
                attach.ExecuteNonQuery();
            }
            try
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = $"INSERT OR IGNORE INTO clips ({cols}) SELECT {cols} FROM bk.clips;";
                return insert.ExecuteNonQuery();
            }
            finally
            {
                ExecOn(conn, "DETACH DATABASE bk;");
            }
        }

        /// <summary>
        /// Yedek dosyasındaki clips tablosunun sütunları; tablo yoksa, dosya SQLite değilse veya
        /// anahtar uymuyorsa boş.
        /// </summary>
        private static HashSet<string> ReadColumns(string dbPath, byte[]? key)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = OpenConnection(dbPath, key, readOnly: true);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(clips);";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add(r.GetString(1));
            }
            catch (Exception ex) when (ex is SqliteException or DatabaseKeyException)
            {
                cols.Clear(); // SQLite dosyası değil ya da başka anahtarla şifrelenmiş
            }
            return cols;
        }

        /// <summary>
        /// Tüm metin kayıtlarını (en yeni önce) okunabilir bir UTF-8 metin dosyasına yazar.
        /// Yazılan kayıt sayısını döndürür.
        /// </summary>
        public int ExportTexts(string destination)
        {
            int count = 0;
            using var conn = OpenConnection(_path, _key, readOnly: true); // arka plandan çağrılabilsin
            using var writer = new StreamWriter(destination, false, new UTF8Encoding(true));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT text, source_app, last_used_utc, pinned FROM clips
                                WHERE kind = 'text' ORDER BY pinned DESC, last_used_utc DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var when = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)).LocalDateTime;
                string app = r.IsDBNull(1) ? "?" : r.GetString(1);
                string pin = r.GetInt64(3) != 0 ? " · sabitlenmiş" : "";
                writer.WriteLine($"===== {when:dd.MM.yyyy HH:mm} · {app}{pin} =====");
                writer.WriteLine(r.GetString(0));
                writer.WriteLine();
                count++;
            }
            return count;
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private long Scalar(string sql, params (string name, object value)[] args)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        /// <summary>Komutu çalıştırır, etkilenen satır sayısını döndürür.</summary>
        private int Exec(string sql, params (string name, object value)[] args)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            return cmd.ExecuteNonQuery();
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        public void Dispose()
        {
            // Arka planda süren boş sayfa temizliği varsa bitmesini kısa süre bekle (yarım kalmasın).
            try { WaitForMaintenanceAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
            _conn.Dispose();
        }
    }
}
