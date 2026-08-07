using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MiniLms.Data;
using MiniLms.Interfaces;
using MiniLms.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace MiniLms.Services
{
    public class CourseDocumentService : ICourseDocumentService
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IVectorDbService _vectorDbService;
        private readonly IAiService? _aiService;

        // Constructor güncellenerek IVectorDbService ve IAiService bağımlılıkları enjekte edildi
        public CourseDocumentService(
            ApplicationDbContext context,
            IWebHostEnvironment webHostEnvironment,
            IVectorDbService vectorDbService,
            IAiService? aiService = null)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
            _vectorDbService = vectorDbService;
            _aiService = aiService;
        }

        public async Task SaveDocumentAsync(int courseId, IFormFile file)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Geçersiz dosya!");

            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads");

            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            string uniqueFileName = Guid.NewGuid().ToString() + "_" + Path.GetFileName(file.FileName);
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            var document = new CourseDocument
            {
                CourseId = courseId,
                FileName = file.FileName,
                FilePath = "/uploads/" + uniqueFileName,
                UploadedDate = DateTime.Now
            };

            await _context.CourseDocuments.AddAsync(document);
            await _context.SaveChangesAsync();
        }

        public async Task<IEnumerable<CourseDocument>> GetDocumentsByCourseIdAsync(int courseId)
        {
            return await _context.CourseDocuments
                .Where(d => d.CourseId == courseId)
                .OrderByDescending(d => d.UploadedDate)
                .ToListAsync();
        }

        public async Task<CourseDocument?> GetDocumentByIdAsync(int id)
        {
            return await _context.CourseDocuments.FindAsync(id);
        }

        // 🎯 GÜNCELLENEN VE TAM SENKRONİZASYON SAĞLAYAN SİLME METODU
        public async Task DeleteDocumentAsync(int id)
        {
            var document = await _context.CourseDocuments.FindAsync(id);
            if (document != null)
            {
                // 🎯 0. Bu dökümana bağlı DocumentSummaries kayıtlarını sil (SQL FK hatasını engellemek için)
                var associatedSummaries = await _context.DocumentSummaries
                    .Where(s => s.CourseDocumentId == id)
                    .ToListAsync();
                if (associatedSummaries.Any())
                {
                    _context.DocumentSummaries.RemoveRange(associatedSummaries);
                }

                // 1. Bu dökümana ait LessonContents (parçalanmış metin) kayıtlarını SQL'den çek
                var associatedContents = await _context.LessonContents
                    .Where(content => content.ResourceUrl == document.FilePath)
                    .ToListAsync();

                if (associatedContents.Any())
                {
                    var associatedContentIds = associatedContents.Select(c => c.Id).ToList();
                    var associatedLessonIds = associatedContents.Select(c => c.LessonId).Distinct().ToList();

                    // 2. Yapay zeka belleğinden (Qdrant) bu parçaların vektörlerini sil
                    var pointIds = associatedContents.Select(c => (long)c.Id).ToList();
                    await _vectorDbService.DeleteVectorAsync(pointIds);

                    // 3. SQL Veritabanındaki parçalanmış LessonContents kayıtlarını temizle
                    _context.LessonContents.RemoveRange(associatedContents);

                    foreach (int lessonId in associatedLessonIds)
                    {
                        bool hasOtherContents = await _context.LessonContents
                            .AnyAsync(content => content.LessonId == lessonId && !associatedContentIds.Contains(content.Id));

                        if (!hasOtherContents)
                        {
                            var emptyLesson = await _context.Lessons.FindAsync(lessonId);
                            if (emptyLesson != null &&
                                (emptyLesson.Title == "Yüklenen Dokümanlar" ||
                                 emptyLesson.Title.StartsWith("Doküman Konuları:", StringComparison.OrdinalIgnoreCase)))
                            {
                                _context.Lessons.Remove(emptyLesson);
                            }
                        }
                    }
                }

                // 4. Fiziksel dosyayı sunucu diskinden (wwwroot/uploads) sil
                string physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, document.FilePath.TrimStart('/'));
                if (File.Exists(physicalPath))
                {
                    File.Delete(physicalPath);
                }

                // 5. Veri tabanından ana döküman kaydını sil
                _context.CourseDocuments.Remove(document);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<List<string>> GetDocumentTextChunksAsync(int documentId, int maxChunks = 50)
        {
            var document = await _context.CourseDocuments.FindAsync(documentId);
            if (document == null)
            {
                return new List<string>();
            }

            // 🎯 1. Önce doğrudan fiziksel PDF veya TXT dosyasından tam doküman metnini oku
            string fullExtractedText = await ReadDocumentTextAsync(document);
            if (!string.IsNullOrWhiteSpace(fullExtractedText))
            {
                var chunks = SplitText(fullExtractedText, 2500)
                    .Take(maxChunks)
                    .ToList();

                if (chunks.Count > 0)
                {
                    return chunks;
                }
            }

            // 🎯 2. Fiziksel dosya okunamazsa veritabanındaki indeksli parçaları kullan (DocumentTopic hariç)
            var indexedChunks = await _context.LessonContents
                .Where(content => content.ResourceUrl == document.FilePath && content.Type != "DocumentTopic")
                .OrderBy(content => content.Order)
                .Select(content => !string.IsNullOrWhiteSpace(content.Body) ? content.Body : content.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Take(maxChunks)
                .ToListAsync();

            return indexedChunks;
        }

        public async Task EnsureDocumentTopicLessonsAsync(int courseId)
        {
            var documents = await _context.CourseDocuments
                .Where(document => document.CourseId == courseId)
                .OrderBy(document => document.UploadedDate)
                .ToListAsync();

            foreach (var document in documents)
            {
                var existingTopics = await _context.LessonContents
                    .Where(content => content.ResourceUrl == document.FilePath && content.Type == "DocumentTopic")
                    .ToListAsync();

                // Eğer eski gürültülü veya yazar ismi / bitişik sözcük içeren başlıklar varsa temizle ve yenile
                bool needsRebuild = existingTopics.Count == 0 || existingTopics.Any(t => 
                    t.Text.StartsWith("Kaynak dokümandan") || 
                    t.Title.EndsWith(":") || 
                    t.Title.EndsWith(".") || 
                    t.Title.Contains("Doküman Konuları: .") ||
                    t.Title.Contains("Ali Gülbağ", StringComparison.OrdinalIgnoreCase) ||
                    t.Text.Contains("Ali Gülbağ", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(t.Title, @"Mantık\s+Devreleri", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(t.Text, @"[a-zçğıöşü][A-ZÇĞİÖŞÜ]"));

                if (!needsRebuild)
                {
                    continue;
                }

                if (existingTopics.Count > 0)
                {
                    var lessonIds = existingTopics.Select(t => t.LessonId).Distinct().ToList();
                    _context.LessonContents.RemoveRange(existingTopics);
                    
                    var oldLessons = await _context.Lessons
                        .Where(l => lessonIds.Contains(l.Id) && !l.Contents.Any(c => c.Type != "DocumentTopic"))
                        .ToListAsync();
                    _context.Lessons.RemoveRange(oldLessons);
                    await _context.SaveChangesAsync();
                }

                string extractedText = await ReadDocumentTextAsync(document);
                if (!string.IsNullOrWhiteSpace(extractedText))
                {
                    await AddDocumentTopicLessonAsync(courseId, document, extractedText);
                }
            }

            await _context.SaveChangesAsync();
        }

        public async Task UploadDocumentAsync(int courseId, IFormFile file)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Geçersiz dosya!");

            var courseExists = await _context.Courses.AnyAsync(c => c.Id == courseId);
            if (!courseExists)
                throw new KeyNotFoundException("Doküman eklenecek kurs bulunamadı.");

            string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads");
            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension != ".pdf" && extension != ".txt")
            {
                throw new InvalidOperationException("Sadece PDF veya TXT dosyası yükleyebilirsiniz.");
            }

            string uniqueFileName = Guid.NewGuid() + "_" + Path.GetFileName(file.FileName);
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(fileStream);
            }

            string extractedText = extension == ".pdf"
                ? ExtractTextFromPdf(filePath)
                : await File.ReadAllTextAsync(filePath);

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                throw new InvalidOperationException("Dosyadan okunabilir metin çıkarılamadı.");
            }

            var document = new CourseDocument
            {
                CourseId = courseId,
                FileName = file.FileName,
                FilePath = "/uploads/" + uniqueFileName,
                UploadedDate = DateTime.Now
            };

            await _context.CourseDocuments.AddAsync(document);
            await AddDocumentTopicLessonAsync(courseId, document, extractedText);

            var lesson = await _context.Lessons
                .Where(l => l.CourseId == courseId && l.Title == "Yüklenen Dokümanlar")
                .FirstOrDefaultAsync();

            if (lesson == null)
            {
                int nextWeekNumber = await _context.Lessons
                    .Where(l => l.CourseId == courseId)
                    .Select(l => (int?)l.WeekNumber)
                    .MaxAsync() ?? 0;

                lesson = new Lesson
                {
                    CourseId = courseId,
                    Title = "Yüklenen Dokümanlar",
                    WeekNumber = nextWeekNumber + 1
                };

                await _context.Lessons.AddAsync(lesson);
                await _context.SaveChangesAsync();
            }

            int nextOrder = await _context.LessonContents
                .Where(c => c.LessonId == lesson.Id)
                .Select(c => (int?)c.Order)
                .MaxAsync() ?? 0;

            foreach (string chunk in SplitText(extractedText, 3000))
            {
                nextOrder++;

                await _context.LessonContents.AddAsync(new LessonContent
                {
                    LessonId = lesson.Id,
                    Title = $"{Path.GetFileNameWithoutExtension(file.FileName)} - Bölüm {nextOrder}",
                    Text = chunk,
                    Body = chunk,
                    ResourceUrl = document.FilePath,
                    Order = nextOrder,
                    Type = extension == ".pdf" ? "Pdf" : "Text",
                    IsIndexed = false
                });
            }

            await _context.SaveChangesAsync();
        }

        public class TopicItem
        {
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        private async Task AddDocumentTopicLessonAsync(int courseId, CourseDocument document, string extractedText)
        {
            var topicItems = await ExtractTopicItemsAsync(extractedText, document.FileName);
            if (topicItems == null || topicItems.Count == 0)
            {
                return;
            }

            int nextWeekNumber = await _context.Lessons
                .Where(l => l.CourseId == courseId && l.Title != "Yüklenen Dokümanlar")
                .Select(l => (int?)l.WeekNumber)
                .MaxAsync() ?? 0;

            string rawFileName = Path.GetFileNameWithoutExtension(document.FileName);
            string cleanDocName = rawFileName;
            string candidateDocName = Regex.Replace(cleanDocName, @"^(Bölüm|Hafta|Ders|Konu)\s*\d+[\s\-_.]*", "", RegexOptions.IgnoreCase).Trim();
            candidateDocName = Regex.Replace(candidateDocName, @"(Derlenmi[şs]?|Ders|Notu|Notları)\s*", "", RegexOptions.IgnoreCase).Trim();
            candidateDocName = candidateDocName.Trim('.', '-', '_', ' ');

            if (!string.IsNullOrWhiteSpace(candidateDocName) && candidateDocName.Count(char.IsLetter) >= 3)
            {
                cleanDocName = candidateDocName;
            }

            var topicLesson = new Lesson
            {
                CourseId = courseId,
                Title = $"Doküman Konuları: {cleanDocName}",
                WeekNumber = nextWeekNumber + 1
            };

            await _context.Lessons.AddAsync(topicLesson);
            await _context.SaveChangesAsync();

            int order = 0;
            foreach (var item in topicItems)
            {
                order++;
                await _context.LessonContents.AddAsync(new LessonContent
                {
                    LessonId = topicLesson.Id,
                    Title = $"{order}. {item.Title}",
                    Text = item.Description,
                    Body = item.Title,
                    ResourceUrl = document.FilePath,
                    Order = order,
                    Type = "DocumentTopic",
                    IsIndexed = true
                });
            }
        }

        private async Task<List<TopicItem>> ExtractTopicItemsAsync(string text, string fileName)
        {
            // 🎯 1. Yapay Zekâ (Gemini AI) ile Dokümandaki TÜM Konu Başlıklarını Eksiksiz Çıkar
            if (_aiService != null && !string.IsNullOrWhiteSpace(text) && text.Length > 100)
            {
                try
                {
                    // Tüm PDF metnini taranması için metin boyutunu genişlet (35,000 karakter)
                    string fullTextToScan = text.Length > 35000 ? text.Substring(0, 35000) : text;
                    string prompt = $@"
Aşağıdaki ders dokümanı metninde işlenen TÜM AKADEMİK DERS KONULARINI VE BÖLÜM BAŞLIKLARINI EKSİKSİZ ÇIKAR.
Metnin başından sonuna kadar ele alınan NE KADAR KONU VARSA HEPSİNİ LİSTELE (konu adedi sınırlaması yoktur).

Her konu için:
- Temiz, kısa ve anlaşılır bir konu başlığı (Örn: 'Analog ve Sayısal Büyüklükler', 'Sayı Sistemleri ve Dönüşümler')
- 1 cümlelik net Türkçe açıklama (Örn: 'İkilik ve onaltılık sayı sistemleri arasındaki dönüşüm adımları.')

ZORUNLU KURALLAR:
1. Başlıklarda veya açıklamalarda yazar adı (Ali Gülbağ, Zafer Cömert vb.), slayt başlık birleştirmeleri ('ANALOG-SAYISAL BÜYÜKLÜK...'), gürültü karakterler VEYA 'Doküman içeriğinden...' gibi genel jenerik ifadeler KESİNLİKLE OLMASIN.
2. Kelimeler arasındaki boşluklar düzgün Türkçe kurallarına uygun olsun (bitişik kelimeler olmasın).
3. Yalnızca aşağıdaki geçerli JSON formatında yanıt ver:

[
  {{ ""title"": ""Analog ve Sayısal Büyüklükler"", ""description"": ""Sayısal değerlerin voltaj seviyeleri ve 0-1 mantığı ile ifade edilme ilkeleri."" }},
  {{ ""title"": ""Sayı Sistemleri ve Dönüşümler"", ""description"": ""Onluk, ikilik ve onaltılık sayı sistemleri arasındaki dönüşüm adımları."" }}
]

DERS METNİ:
{fullTextToScan}
";

                    string aiResult = await _aiService.SummarizeTextAsync(prompt);
                    if (!string.IsNullOrWhiteSpace(aiResult) && !aiResult.StartsWith("AI servisi hatası"))
                    {
                        var jsonMatch = Regex.Match(aiResult, @"\[[\s\S]*\]");
                        if (jsonMatch.Success)
                        {
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<TopicItem>>(jsonMatch.Value, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed != null && parsed.Count > 0)
                            {
                                var cleanList = parsed.Where(t => !string.IsNullOrWhiteSpace(t.Title) && t.Title.Length >= 3)
                                                      .Select(t => new TopicItem
                                                      {
                                                          Title = CleanHeading(t.Title),
                                                          Description = FixSquishedSpaces(t.Description)
                                                      })
                                                      .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                                                      .ToList(); // 🎯 Herhangi bir üst sınır (limit) koymadan tüm başlıkları al
                                if (cleanList.Count > 0)
                                {
                                    return cleanList;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AiTopicExtraction Warning]: {ex.Message}");
                }
            }

            // 🎯 2. Fallback: Gelişmiş Metin Temizleyici & Regex Ayrıştırıcısı
            var topics = new List<TopicItem>();
            string cleanedFullText = FixSquishedSpaces(text ?? string.Empty);
            var headings = ExtractTopicHeadings(cleanedFullText, fileName);

            foreach (string heading in headings)
            {
                string desc = ExtractHeadingDescription(cleanedFullText, heading);
                topics.Add(new TopicItem
                {
                    Title = heading,
                    Description = desc
                });
            }

            return topics;
        }

        private static string FixSquishedSpaces(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            string text = input;

            // 1. PDF Slayt Yazar/Kurum isimleri gürültüsünü temizle
            text = Regex.Replace(text, @"Ali\s+Gülbağ(\s+Mantık\s+Devreleri)?", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"Zafer\s+Cömert", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"BTK\s+Akademi", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"Mantık\s+Devreleri", "", RegexOptions.IgnoreCase);

            // 2. Bitişik kelimelerin arasına boşluk yerleştir
            text = Regex.Replace(text, @"([a-zçğıöşü])([A-ZÇĞİÖŞÜ])", "$1 $2");
            text = Regex.Replace(text, @"([a-zçğıöşü0-9])([•;:])", "$1 $2");
            text = Regex.Replace(text, @"([•;:])([a-zçğıöşüA-ZÇĞİÖŞÜ])", "$1 $2");
            text = Regex.Replace(text, @"•+", " • ");

            // 3. Tekrarlanan ana başlık birleştirmelerini ve gürültüleri temizle
            text = Regex.Replace(text, @"(ANALOG-SAYISAL BÜYÜKLÜK VE SAYI SİSTEMLERİ)\s*•?\s*", "", RegexOptions.IgnoreCase);

            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static string ExtractTextFromPdf(string filePath)
        {
            using var document = PdfDocument.Open(filePath);
            return string.Join(Environment.NewLine, document.GetPages().Select(page => page.Text));
        }

        private async Task<string> ReadDocumentTextAsync(CourseDocument document)
        {
            try
            {
                string physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, document.FilePath.TrimStart('/'));
                if (!File.Exists(physicalPath))
                {
                    return string.Empty;
                }

                string extension = Path.GetExtension(physicalPath).ToLowerInvariant();
                return extension == ".pdf"
                    ? ExtractTextFromPdf(physicalPath)
                    : extension == ".txt"
                        ? await File.ReadAllTextAsync(physicalPath)
                        : string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReadDocumentText Hata]: Doküman okunamadı ({document.FileName}): {ex.Message}");
                return string.Empty;
            }
        }

        private static List<string> ExtractTopicHeadings(string text, string fileName)
        {
            var topics = new List<string>();

            string normalizedText = FixSquishedSpaces(text ?? string.Empty);
            var lines = normalizedText
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => CleanHeading(line))
                .Where(line => IsLikelyHeading(line))
                .ToList();

            foreach (string line in lines)
            {
                AddTopic(topics, line);
            }

            if (topics.Count < 3)
            {
                foreach (Match match in Regex.Matches(
                    normalizedText,
                    @"(?:^|[.!?]\s+)((?:\d+(?:\.\d+)*\.?\s+|Hafta\s+\d+[:\-.]?\s+|Bölüm\s+\d+[:\-.]?\s+|Chapter\s+\d+[:\-.]?\s+|Lecture\s+\d+[:\-.]?\s+)[A-ZÇĞİÖŞÜa-zçğıöşü][^.!?\r\n]{4,85})",
                    RegexOptions.IgnoreCase))
                {
                    AddTopic(topics, CleanHeading(match.Groups[1].Value));
                }
            }

            if (topics.Count == 0)
            {
                AddTopic(topics, Path.GetFileNameWithoutExtension(fileName));
            }

            return topics;
        }

        private static string ExtractHeadingDescription(string fullText, string heading)
        {
            if (string.IsNullOrWhiteSpace(fullText) || string.IsNullOrWhiteSpace(heading))
            {
                return "Dokümandaki kavramsal ders konusu ve çalışma alanı.";
            }

            int idx = fullText.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string afterText = fullText.Substring(idx + heading.Length).Trim();
                afterText = FixSquishedSpaces(afterText);

                var sentences = afterText.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(s => s.Trim())
                                         .Where(s => s.Length >= 15 && !s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                         .ToList();

                if (sentences.Count > 0)
                {
                    string candidate = sentences[0];
                    if (candidate.Length > 140)
                    {
                        candidate = TruncateAtWord(candidate, 130) + "...";
                    }
                    return candidate;
                }
            }

            return "Dokümandaki kavramsal ders konusu ve çalışma alanı.";
        }

        private static string CleanHeading(string heading)
        {
            if (string.IsNullOrWhiteSpace(heading)) return string.Empty;

            heading = FixSquishedSpaces(heading);
            heading = Regex.Replace(heading, @"^[•\-–—*]+\s*", string.Empty).Trim();
            heading = Regex.Replace(heading, @"^\d+(\.\d+)*\.?\s*", string.Empty).Trim();
            heading = Regex.Replace(heading, @"\s+\.{2,}\s*\d+$", string.Empty).Trim();
            heading = heading.Trim(':', '-', '–', '—', '.', ' ', '•');

            if (heading.Length > 85)
            {
                heading = TruncateAtWord(heading, 80);
            }

            return heading;
        }

        private static string TruncateAtWord(string input, int length)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length <= length) return input ?? string.Empty;

            int lastSpace = input.Substring(0, length).LastIndexOf(' ');
            if (lastSpace > 20)
            {
                return input.Substring(0, lastSpace).Trim();
            }
            return input.Substring(0, length).Trim();
        }

        private static bool IsLikelyHeading(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < 4 || line.Length > 115)
            {
                return false;
            }

            if (line.Count(char.IsLetter) < 4 || line.EndsWith(","))
            {
                return false;
            }

            // Slayt sunu gürültüsü ve yazar/sayfa üst yazısı kalıplarını filtrele
            if (Regex.IsMatch(line, @"(slayt[ıa]?|omurgasını|vurgu|kullanıcıya|bu başlık|bu özet|şekil|tablo|page|sayfa|references|kaynakça|Ali\s+Gülbağ|Zafer\s+Cömert|BTK\s+Akademi)", RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (Regex.IsMatch(line, @"^(\d+(\.\d+)*\.?\s+|Hafta\s+\d+[:\-.]?\s+|Bölüm\s+\d+[:\-.]?\s+|Konu\s+\d+[:\-.]?\s+|Chapter\s+\d+[:\-.]?\s+|Lecture\s+\d+[:\-.]?\s+)", RegexOptions.IgnoreCase))
            {
                return true;
            }

            int letterCount = line.Count(char.IsLetter);
            int upperCount = line.Count(char.IsUpper);
            bool mostlyUpper = letterCount > 0 && upperCount >= Math.Max(3, (int)(letterCount * 0.45));

            if (mostlyUpper && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12)
            {
                return true;
            }

            bool shortTitle = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10 &&
                               !line.Contains(". ") &&
                               !line.EndsWith(".", StringComparison.Ordinal);

            return shortTitle && char.IsUpper(line[0]);
        }

        private static void AddTopic(List<string> topics, string topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            bool exists = topics.Any(existing =>
                existing.Equals(topic, StringComparison.OrdinalIgnoreCase) ||
                existing.Contains(topic, StringComparison.OrdinalIgnoreCase) ||
                topic.Contains(existing, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                topics.Add(topic);
            }
        }

        private static IEnumerable<string> SplitText(string text, int chunkSize)
        {
            for (int start = 0; start < text.Length; start += chunkSize)
            {
                int length = Math.Min(chunkSize, text.Length - start);
                string chunk = text.Substring(start, length).Trim();

                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    yield return chunk;
                }
            }
        }
    }
}
