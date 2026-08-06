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
        private readonly IVectorDbService _vectorDbService; // 🎯 YENİ: Vektör silme işlemleri için eklendi

        // Constructor güncellenerek IVectorDbService bağımlılığı enjekte edildi
        public CourseDocumentService(
            ApplicationDbContext context,
            IWebHostEnvironment webHostEnvironment,
            IVectorDbService vectorDbService)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
            _vectorDbService = vectorDbService;
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
                // 1. Bu dökümana ait LessonContents (parçalanmış metin) kayıtlarını SQL'den çek
                var associatedContents = await _context.LessonContents
                    .Where(content => content.ResourceUrl == document.FilePath)
                    .ToListAsync();

                if (associatedContents.Any())
                {
                    var associatedContentIds = associatedContents.Select(c => c.Id).ToList();
                    var associatedLessonIds = associatedContents.Select(c => c.LessonId).Distinct().ToList();

                    // 2. Yapay zeka belleğinden (Qdrant) bu parçaların vektörlerini sil
                    // Senkronizasyon servisindeki Id eşleşmesine göre listeyi ulong/long olarak hazırlıyoruz
                    var pointIds = associatedContents.Select(c => (long)c.Id).ToList();

                    // Not: IVectorDbService'inizdeki metot imzanıza göre DeleteVectorsAsync veya DeleteVectorAsync çağırabilirsiniz
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
                bool topicsAlreadyCreated = await _context.LessonContents
                    .AnyAsync(content => content.ResourceUrl == document.FilePath && content.Type == "DocumentTopic");

                if (topicsAlreadyCreated)
                {
                    continue;
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

        private async Task AddDocumentTopicLessonAsync(int courseId, CourseDocument document, string extractedText)
        {
            var topicHeadings = ExtractTopicHeadings(extractedText, document.FileName);
            if (topicHeadings.Count == 0)
            {
                return;
            }

            int nextWeekNumber = await _context.Lessons
                .Where(l => l.CourseId == courseId && l.Title != "Yüklenen Dokümanlar")
                .Select(l => (int?)l.WeekNumber)
                .MaxAsync() ?? 0;

            var topicLesson = new Lesson
            {
                CourseId = courseId,
                Title = $"Doküman Konuları: {Path.GetFileNameWithoutExtension(document.FileName)}",
                WeekNumber = nextWeekNumber + 1
            };

            await _context.Lessons.AddAsync(topicLesson);
            await _context.SaveChangesAsync();

            int order = 0;
            foreach (string heading in topicHeadings)
            {
                order++;
                await _context.LessonContents.AddAsync(new LessonContent
                {
                    LessonId = topicLesson.Id,
                    Title = heading,
                    Text = $"Kaynak dokümandan çıkarılan konu başlığı: {heading}",
                    Body = heading,
                    ResourceUrl = document.FilePath,
                    Order = order,
                    Type = "DocumentTopic",
                    IsIndexed = true
                });
            }
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

            string normalizedText = Regex.Replace(text ?? string.Empty, @"[ \t]+", " ");
            var lines = normalizedText
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => CleanHeading(line))
                .Where(line => IsLikelyHeading(line))
                .ToList();

            foreach (string line in lines)
            {
                AddTopic(topics, line);
                if (topics.Count >= 18)
                {
                    return topics;
                }
            }

            if (topics.Count < 4)
            {
                foreach (Match match in Regex.Matches(
                    normalizedText,
                    @"(?:^|[.!?]\s+)((?:\d+(?:\.\d+)*\.?\s+|Hafta\s+\d+[:\-.]?\s+|Bölüm\s+\d+[:\-.]?\s+|Chapter\s+\d+[:\-.]?\s+|Lecture\s+\d+[:\-.]?\s+)[A-ZÇĞİÖŞÜa-zçğıöşü][^.!?\r\n]{4,90})",
                    RegexOptions.IgnoreCase))
                {
                    AddTopic(topics, CleanHeading(match.Groups[1].Value));
                    if (topics.Count >= 18)
                    {
                        return topics;
                    }
                }
            }

            if (topics.Count == 0)
            {
                AddTopic(topics, Path.GetFileNameWithoutExtension(fileName));
            }

            return topics;
        }

        private static string CleanHeading(string heading)
        {
            heading = Regex.Replace(heading ?? string.Empty, @"\s+", " ").Trim();
            heading = Regex.Replace(heading, @"^[•\-–—*]+\s*", string.Empty).Trim();
            heading = Regex.Replace(heading, @"\s+\.{2,}\s*\d+$", string.Empty).Trim();
            heading = heading.Trim(':', '-', '–', '—', '.', ' ');

            return heading.Length > 110
                ? heading.Substring(0, 110).Trim()
                : heading;
        }

        private static bool IsLikelyHeading(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length < 4 || line.Length > 110)
            {
                return false;
            }

            if (line.Count(char.IsLetter) < 3 || line.EndsWith(",", StringComparison.Ordinal))
            {
                return false;
            }

            if (Regex.IsMatch(line, @"^(table|figure|şekil|tablo|page|sayfa|references|kaynakça)\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (Regex.IsMatch(line, @"^(\d+(\.\d+)*\.?\s+|Hafta\s+\d+[:\-.]?\s+|Bölüm\s+\d+[:\-.]?\s+|Konu\s+\d+[:\-.]?\s+|Chapter\s+\d+[:\-.]?\s+|Lecture\s+\d+[:\-.]?\s+)", RegexOptions.IgnoreCase))
            {
                return true;
            }

            int letterCount = line.Count(char.IsLetter);
            int upperCount = line.Count(char.IsUpper);
            bool mostlyUpper = letterCount > 0 && upperCount >= Math.Max(3, (int)(letterCount * 0.65));

            if (mostlyUpper && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10)
            {
                return true;
            }

            bool shortTitle = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8 &&
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
