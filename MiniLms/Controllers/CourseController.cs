using Microsoft.AspNetCore.Mvc;
using MiniLms.Interfaces;
using MiniLms.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MiniLms.Controllers
{
    [Authorize]
    public class CourseController : Controller
    {
        private readonly ICourseService _courseService;
        private readonly IAiService _aiService;
        private readonly IVectorDbService _vectorDbService;
        private readonly ICourseDocumentService _courseDocumentService;

        public CourseController(
            ICourseService courseService,
            IAiService aiService,
            IVectorDbService vectorDbService,
            ICourseDocumentService courseDocumentService)
        {
            _courseService = courseService;
            _aiService = aiService;
            _vectorDbService = vectorDbService;
            _courseDocumentService = courseDocumentService;
        }

        // Tüm kursları ana sayfada listeler
        public async Task<IActionResult> Index()
        {
            var courses = await _courseService.GetAllCoursesAsync();
            return View(courses);
        }

        // Kursun detaylarını ve haftalık konularını (Lesson) getirir
        public async Task<IActionResult> Details(int id)
        {
            var course = await _courseService.GetCourseByIdAsync(id);
            if (course == null)
            {
                return NotFound();
            }

            await _courseDocumentService.EnsureDocumentTopicLessonsAsync(id);
            course = await _courseService.GetCourseByIdAsync(id);
            if (course == null)
            {
                return NotFound();
            }

            return View(course);
        }

        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public async Task<IActionResult> UploadDocument(int courseId, IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return RedirectToAction("Details", new { id = courseId });
            }

            try
            {
                await _courseDocumentService.UploadDocumentAsync(courseId, file);
                TempData["SuccessMessage"] = "Doküman başarıyla yüklendi ve yapay zeka için indekslendi.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Doküman yüklenirken hata oluştu: {ex.Message}";
            }

            return RedirectToAction("Details", new { id = courseId });
        }

        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)] // Sadece öğretmenler döküman silebilir
        public async Task<IActionResult> DeleteDocument(int id, int courseId)
        {
            try
            {
                // 🎯 DÜZELTİLDİ: Servis metodu Task (void) döndüğü için try-catch bloğu ile sarmalandı.
                // SQL, wwwroot ve Qdrant temizliği artık tek hattan güvenle tetikleniyor.
                await _courseDocumentService.DeleteDocumentAsync(id);
                TempData["SuccessMessage"] = "Döküman, SQL kayıtları ve yapay zeka bellek vektörleri başarıyla silindi.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Döküman silinirken teknik bir hata oluştu: {ex.Message}";
            }

            // Silme işleminden sonra tekrar dersin detay sayfasına yönlendiriyoruz
            return RedirectToAction("Details", new { id = courseId });
        }

        [HttpPost]
        public async Task<IActionResult> AskAi(int courseId, string question, int? documentId)
        {
            if (string.IsNullOrEmpty(question))
            {
                return Json(new { success = false, response = "Lütfen boş bir soru göndermeyin." });
            }

            try
            {
                var relevantTexts = new List<string>();
                string selectedSourceName = "Tüm ders kaynakları";
                string? selectedDocumentPath = null;

                if (documentId.HasValue && documentId.Value > 0)
                {
                    var selectedDocument = await _courseDocumentService.GetDocumentByIdAsync(documentId.Value);
                    if (selectedDocument == null || selectedDocument.CourseId != courseId)
                    {
                        return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                    }

                    selectedSourceName = selectedDocument.FileName;
                    selectedDocumentPath = selectedDocument.FilePath;
                }

                // Adım A: Öğrencinin sorusunu Gemini API yardımıyla vektör dizisine çevirmeyi deniyoruz.
                List<float>? questionVector = selectedDocumentPath == null
                    ? await _aiService.GetEmbeddingAsync(question)
                    : null;

                if (questionVector != null && questionVector.Count > 0)
                {
                    // Adım B: Qdrant Vector DB üzerinde bu soruya en yakın/en alakalı 3 metin parçasını arıyoruz
                    relevantTexts = await _vectorDbService.SearchSimilarTextsAsync(
                        collectionName: "lesson_contents",
                        vectorData: questionVector,
                        limit: 3
                    );
                }

                if (relevantTexts == null || relevantTexts.Count == 0)
                {
                    if (documentId.HasValue && documentId.Value > 0)
                    {
                        relevantTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId.Value);
                    }
                    else
                    {
                        var course = await _courseService.GetCourseByIdAsync(courseId);
                        relevantTexts = course?.Lessons?
                            .SelectMany(lesson => lesson.Contents ?? Enumerable.Empty<Models.LessonContent>())
                            .Select(content => !string.IsNullOrWhiteSpace(content.Body) ? content.Body : content.Text)
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                            .Take(5)
                            .ToList() ?? new List<string>();
                    }
                }

                if (relevantTexts.Count == 0)
                {
                    string emptyMessage = selectedDocumentPath == null
                        ? "Bu kurs için cevap üretilecek ders içeriği bulunamadı. Önce haftalık içerik veya doküman ekleyin."
                        : "Seçilen doküman için cevap üretilecek metin bulunamadı. Dokümanı tekrar yükleyip işlendiğinden emin olun.";

                    return Json(new { success = false, response = emptyMessage });
                }

                // Adım C: Gelen kaynak metinleri tek bir "Context" (Bağlam) bloğu haline getiriyoruz
                string context = relevantTexts.Count > 0
                    ? string.Join("\n\n", relevantTexts)
                    : "Bu kursa ait herhangi bir döküman veya ders içeriği bulunamadı.";

                // Adım D: Gemini'a sınırlarını ve kurallarını çizen akıllı bir RAG Prompt'u hazırlıyoruz
                string finalPrompt = $@"
                    Sen bu dersin yapay zeka asistanısın. Aşağıda sana bu dersin içeriğinden alınan kaynak metinler (Bağlam) verilmiştir.
                    Lütfen ÖĞRENCİNİN SORUSU'nu sadece ve sadece verilen BAĞLAM'a sadık kalarak, kendi yorumunu veya dışarıdan bilgi eklemeden, akademik ve net bir dilde cevapla.
                    Eğer soru bağlamla ilgili değilse veya bağlamda kesin bir cevabı yoksa, kibarca 'Bu sorunun cevabı ders içeriklerinde yer almamaktadır.' de.

                    SEÇİLEN KAYNAK:
                    {selectedSourceName}

                    BAĞLAM:
                    {context}

                    ÖĞRENCİNİN SORUSU:
                    {question}
                ";

                // Adım E: Prompt'u Gemini'a gönderip ders kaynaklarına göre filtrelenmiş cevabı alıyoruz
                string aiResponse = await _aiService.SummarizeTextAsync(finalPrompt);
                if (IsAiServiceError(aiResponse))
                {
                    aiResponse = BuildLocalFallbackAnswer(relevantTexts);
                }

                return Json(new { success = true, response = aiResponse });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, response = $"Teknik bir hata oluştu: {ex.Message}" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DocumentSummary(int courseId, int documentId)
        {
            try
            {
                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                var documentTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId, maxChunks: 4);
                if (documentTexts.Count == 0)
                {
                    return Json(new { success = false, response = "Bu dokümandan özet üretilecek metin çıkarılamadı." });
                }

                string sourceText = string.Join("\n\n", documentTexts);
                string summaryPrompt = $@"
                    Aşağıdaki ders dokümanını birebir akademik ders rehberi formatında Türkçe olarak özetle.

                    GÖRSEL VE BİÇİMLENDİRME FORMATI (TAM OLARAK UYGULA):
                    1. En başa BÜYÜK HARFLERLE ders dokümanının adını içeren bir ana başlık yaz (Örn: # SAYI SİSTEMLERİ VE SAYISAL ELEKTRONİK DERS REHBERİ).
                    2. Başlığın hemen altına 2-3 cümlelik genel giriş açıklamasını yaz. Kesinlikle '---' veya '--' gibi ayırıcı çizgiler KULLANMA.
                    3. Konuları Numaralandırılmış Bölüm Başlıkları şeklinde yaz (Örn: '1. ANALOG VE SAYISAL BÜYÜKLÜK KAVRAMI', '2. İKİLİK (BINARY) SAYI SİSTEMİ', '2.1. Taban ve Terminoloji').
                    4. Tanım ve terimleri **Tanım Adı:** Açıklama formatında koyu bold yaparak yaz (Örn: **Analog Büyüklük:** ..., **Taban:** 2, **Terminoloji:** En sağdaki bit **LSB**).
                    5. En sonda 'SINAV İÇİN DİKKAT EDİLMESİ GEREKEN NOKTALAR' başlığı altında önemli püf noktalarını ver.
                    6. KESİNLİKLE madde işareti (• veya -) ve ayırıcı çizgi (---) KULLANMA. Tanımları paragraflar ve kalın terim isimleriyle ayır.
                    7. Kesinlikle LaTeX dolar işareti ($) kullanma.

                    DOKÜMAN ADI:
                    {document.FileName}

                    DOKÜMAN METNİ:
                    {sourceText}
                ";

                string summary = await _aiService.SummarizeTextAsync(summaryPrompt);
                if (IsAiServiceError(summary))
                {
                    summary = BuildLocalDocumentSummary(document.FileName, documentTexts);
                }

                return Json(new { success = true, response = summary });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, response = $"Doküman özeti alınırken teknik bir hata oluştu: {ex.Message}" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DocumentQuiz(int courseId, int documentId, int questionCount = 5)
        {
            try
            {
                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                questionCount = Math.Clamp(questionCount, 3, 10);

                var documentTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId, maxChunks: 8);
                if (documentTexts.Count == 0)
                {
                    return Json(new { success = false, response = "Bu dokümandan quiz üretilecek metin çıkarılamadı." });
                }

                string sourceText = string.Join("\n\n", documentTexts);
                string quizPrompt = $@"
                    Aşağıdaki ders dokümanına göre Türkçe {questionCount} adet çoktan seçmeli quiz sorusu hazırla.
                    Kurallar:
                    - Her soru yalnızca verilen doküman metnine dayanmalı.
                    - Her soru için A, B, C, D olmak üzere 4 seçenek ver.
                    - Her sorudan sonra doğru cevabı ve 1 cümlelik kısa açıklamayı yaz.
                    - Formatı şu şekilde koru:
                      1. Soru metni
                      A) ...
                      B) ...
                      C) ...
                      D) ...
                      Doğru Cevap: ...
                      Açıklama: ...

                    DOKÜMAN ADI:
                    {document.FileName}

                    DOKÜMAN METNİ:
                    {sourceText}
                ";

                string quiz = await _aiService.SummarizeTextAsync(quizPrompt);
                if (IsAiServiceError(quiz))
                {
                    quiz = BuildLocalDocumentQuiz(document.FileName, documentTexts, questionCount);
                }

                return Json(new { success = true, response = quiz });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, response = $"Quiz oluşturulurken teknik bir hata oluştu: {ex.Message}" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> DocumentQuizSession(int courseId, int documentId, int questionCount = 5, string difficulty = "mixed")
        {
            try
            {
                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                questionCount = Math.Clamp(questionCount, 3, 10);

                var documentTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId, maxChunks: 8);
                if (documentTexts.Count == 0)
                {
                    return Json(new { success = false, response = "Bu dokümandan quiz üretilecek metin çıkarılamadı." });
                }

                difficulty = NormalizeDifficulty(difficulty);
                var questions = await BuildInteractiveDocumentQuizAsync(document.FileName, documentTexts, questionCount, difficulty);
                if (questions.Count == 0)
                {
                    return Json(new
                    {
                        success = false,
                        response = "Bu dokümandan kaliteli quiz sorusu çıkarılamadı. Dokümanda daha açıklayıcı metinler olduğundan emin olun veya Gemini API bağlantısını düzeltin."
                    });
                }

                return Json(new
                {
                    success = true,
                    title = $"{document.FileName} Quiz",
                    questions
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, response = $"Quiz hazırlanırken teknik bir hata oluştu: {ex.Message}" });
            }
        }

        private static bool IsAiServiceError(string response)
        {
            return response.Contains("Gemini API", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("Yapay zeka servisi", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("Gemini API anahtarı geçerli değil", StringComparison.OrdinalIgnoreCase) ||
                   response.Contains("Özet oluşturulurken teknik bir hata", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildLocalFallbackAnswer(List<string> relevantTexts)
        {
            string sourcePreview = string.Join(
                "\n\n",
                relevantTexts
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(2)
                    .Select(text => text.Length > 700 ? text.Substring(0, 700) + "..." : text));

            return $@"Gemini bağlantısı şu anda kullanılamıyor, ancak ders kaynaklarından ilgili içerik bulundu.

Ders kaynaklarından bulunan içerik:
{sourcePreview}";
        }

        private static string BuildLocalDocumentSummary(string fileName, List<string> documentTexts)
        {
            string preview = string.Join(
                "\n\n",
                documentTexts
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Take(2)
                    .Select(text => text.Length > 900 ? text.Substring(0, 900) + "..." : text));

            return $@"{fileName} dokümanından metin çıkarıldı, ancak Gemini otomatik özet şu anda kullanılamıyor.

Dokümandan kısa önizleme:
{preview}";
        }

        private static string BuildLocalDocumentQuiz(string fileName, List<string> documentTexts, int questionCount)
        {
            var candidateTopics = documentTexts
                .SelectMany(text => text.Split(new[] { '\r', '\n', '.', ';' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(text => text.Trim())
                .Where(text => text.Length >= 18 && text.Length <= 140)
                .Where(text => text.Count(char.IsLetter) >= 10)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(questionCount)
                .ToList();

            if (candidateTopics.Count == 0)
            {
                candidateTopics.Add(PathSafeTitle(fileName));
            }

            var quizLines = new List<string>
            {
                $"{fileName} dokümanından yerel quiz taslağı:",
                ""
            };

            for (int i = 0; i < questionCount; i++)
            {
                string topic = candidateTopics[i % candidateTopics.Count];
                quizLines.Add($"{i + 1}. Aşağıdaki ifadelerden hangisi dokümandaki bu konu ile en doğrudan ilişkilidir?");
                quizLines.Add($"A) {topic}");
                quizLines.Add("B) Dokümanda yer almayan genel bir tanım");
                quizLines.Add("C) Konuyla ilgisiz bir tarih bilgisi");
                quizLines.Add("D) Kaynakta desteklenmeyen bir yorum");
                quizLines.Add("Doğru Cevap: A");
                quizLines.Add("Açıklama: A seçeneği doğrudan dokümandan çıkarılan içerik parçasına dayanır.");
                quizLines.Add("");
            }

            return string.Join(Environment.NewLine, quizLines);
        }

        private static string PathSafeTitle(string fileName)
        {
            return System.IO.Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ');
        }

        private async Task<List<QuizQuestionDto>> BuildInteractiveDocumentQuizAsync(string fileName, List<string> documentTexts, int questionCount, string difficulty)
        {
            string sourceText = string.Join("\n\n", documentTexts);
            string difficultyInstruction = difficulty switch
            {
                "easy" => "Sorular kolay seviyede olsun: temel kavram, tanım ve doğrudan bilgi yoklama ağırlıklı olsun.",
                "medium" => "Sorular orta seviyede olsun: kavram ilişkisi, neden-sonuç ve kısa yorum gerektirsin.",
                "hard" => "Sorular zor seviyede olsun: senaryo, çıkarım, karşılaştırma ve analiz gerektirsin.",
                _ => "Sorular karma seviyede olsun: kolay, orta ve zor soruları dengeli dağıt."
            };

            string jsonPrompt = $@"
                Aşağıdaki ders dokümanına göre Türkçe {questionCount} adet çoktan seçmeli quiz üret.
                Sadece geçerli JSON döndür. Markdown, açıklama veya kod bloğu kullanma.
                JSON formatı:
                [
                  {{
                    ""question"": ""Soru metni"",
                    ""options"": [""A seçeneği"", ""B seçeneği"", ""C seçeneği"", ""D seçeneği""],
                    ""correctIndex"": 0,
                    ""explanation"": ""Doğru cevabı açıklayan kısa gerekçe"",
                    ""topic"": ""Soru konusu"",
                    ""difficulty"": ""Kolay | Orta | Zor"",
                    ""bloomLevel"": ""Hatırlama | Anlama | Uygulama | Analiz | Değerlendirme"",
                    ""sourceHint"": ""Sorunun dayandığı kısa kaynak ipucu"",
                    ""whyWrong"": [""A yanlışsa nedeni"", ""B yanlışsa nedeni"", ""C yanlışsa nedeni"", ""D yanlışsa nedeni""]
                  }}
                ]
                Kurallar:
                - Sorular yalnızca doküman metnine dayansın.
                - correctIndex 0 ile 3 arasında sayı olsun.
                - Her soruda tam 4 seçenek olsun.
                - Yanlış seçenekler dokümandaki yakın kavramlardan türetilsin, bariz komik/kolay olmasın.
                - 'Hangisi dokümanda geçer?' gibi yüzeysel soru yazma; kavram, ilişki, neden-sonuç veya uygulama sor.
                - Cevabı yalnızca seçenek uzunluğundan veya bariz ifadelerden tahmin edilebilir yapma.
                - Seçenekler birbirine benzer uzunlukta ve aynı türde olsun.
                - whyWrong dizisi tam 4 elemanlı olsun; doğru seçenek için 'Doğru seçenek.' yaz.
                - Bloom seviyelerini çeşitlendir; sadece ezber sorusu yazma.
                - {difficultyInstruction}

                DOKÜMAN ADI:
                {fileName}

                DOKÜMAN METNİ:
                {sourceText}
            ";

            string aiResponse = await _aiService.SummarizeTextAsync(jsonPrompt);
            if (!IsAiServiceError(aiResponse))
            {
                var parsed = TryParseQuizJson(aiResponse, questionCount);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }

            return BuildLocalInteractiveQuiz(fileName, documentTexts, questionCount, difficulty);
        }

        private static List<QuizQuestionDto> TryParseQuizJson(string json, int questionCount)
        {
            try
            {
                json = json.Trim();
                int start = json.IndexOf('[');
                int end = json.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    json = json.Substring(start, end - start + 1);
                }

                var questions = JsonSerializer.Deserialize<List<QuizQuestionDto>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<QuizQuestionDto>();

                return questions
                    .Where(q => !string.IsNullOrWhiteSpace(q.Question))
                    .Where(q => q.Options != null && q.Options.Count == 4)
                    .Where(q => q.CorrectIndex >= 0 && q.CorrectIndex <= 3)
                    .Select(NormalizeQuizQuestion)
                    .Take(questionCount)
                    .ToList();
            }
            catch
            {
                return new List<QuizQuestionDto>();
            }
        }

        private static List<QuizQuestionDto> BuildLocalInteractiveQuiz(string fileName, List<string> documentTexts, int questionCount, string difficulty)
        {
            var facts = ExtractQuizFacts(documentTexts)
                .Take(Math.Max(questionCount * 4, 12))
                .ToList();

            var questions = new List<QuizQuestionDto>();
            if (facts.Count < 4)
            {
                return questions;
            }

            for (int i = 0; i < questionCount; i++)
            {
                var correctFact = facts[i % facts.Count];
                var distractors = facts
                    .Where(fact => !fact.Statement.Equals(correctFact.Statement, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fact => fact.Topic.Equals(correctFact.Topic, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(fact => Math.Abs(fact.Statement.Length - correctFact.Statement.Length))
                    .Skip(i % 2)
                    .Take(3)
                    .ToList();

                if (distractors.Count < 3)
                {
                    continue;
                }

                int correctIndex = (i + correctFact.Topic.Length) % 4;
                var options = distractors
                    .Select(fact => fact.Statement)
                    .Take(3)
                    .ToList();
                options.Insert(correctIndex, correctFact.Statement);

                string questionText = BuildFactQuestionText(difficulty, correctFact);

                questions.Add(new QuizQuestionDto
                {
                    Question = questionText,
                    Options = options,
                    CorrectIndex = correctIndex,
                    Explanation = $"Doğru cevap dokümanda '{correctFact.Topic}' konusu için verilen bilgiye dayanır.",
                    Topic = ShortenTopic(correctFact.Topic),
                    Difficulty = LocalDifficultyLabel(difficulty, i),
                    BloomLevel = LocalBloomLevel(difficulty, i),
                    SourceHint = ShortenTopic(correctFact.Statement),
                    WhyWrong = options.Select((option, optionIndex) =>
                        optionIndex == correctIndex
                            ? "Doğru seçenek."
                            : $"Bu seçenek dokümanda farklı bir bağlama işaret eder: {ShortenTopic(option)}").ToList()
                });
            }

            return questions;
        }

        private static QuizQuestionDto NormalizeQuizQuestion(QuizQuestionDto question)
        {
            question.Topic = string.IsNullOrWhiteSpace(question.Topic) ? "Doküman içeriği" : question.Topic;
            question.Difficulty = string.IsNullOrWhiteSpace(question.Difficulty) ? "Orta" : question.Difficulty;
            question.BloomLevel = string.IsNullOrWhiteSpace(question.BloomLevel) ? "Anlama" : question.BloomLevel;
            question.SourceHint = string.IsNullOrWhiteSpace(question.SourceHint) ? "Yüklenen doküman" : question.SourceHint;
            question.Explanation = string.IsNullOrWhiteSpace(question.Explanation) ? "Cevap doküman içeriğine dayanır." : question.Explanation;

            if (question.WhyWrong == null || question.WhyWrong.Count != 4)
            {
                question.WhyWrong = Enumerable.Range(0, 4)
                    .Select(index => index == question.CorrectIndex ? "Doğru seçenek." : "Bu seçenek dokümandaki bilgiyle tam örtüşmüyor.")
                    .ToList();
            }

            return question;
        }

        private static string NormalizeDifficulty(string difficulty)
        {
            difficulty = (difficulty ?? "mixed").Trim().ToLowerInvariant();
            return difficulty is "easy" or "medium" or "hard" or "mixed" ? difficulty : "mixed";
        }

        private static string BuildLocalQuestionText(string difficulty, string topic)
        {
            return difficulty switch
            {
                "hard" => "Dokümandaki bilgilerden hareketle aşağıdaki ifadelerden hangisi en güçlü çıkarımdır?",
                "medium" => "Dokümana göre aşağıdaki ifadelerden hangisi bu konuyla en doğru ilişkiyi kurar?",
                "easy" => "Dokümana göre aşağıdaki ifadelerden hangisi doğrudur?",
                _ => topic.Length > 60
                    ? "Dokümana göre aşağıdaki ifadelerden hangisi doğrudur?"
                    : $"Dokümanda '{topic}' konusu için hangi ifade doğrudur?"
            };
        }

        private static string BuildFactQuestionText(string difficulty, QuizFact fact)
        {
            return difficulty switch
            {
                "hard" => $"Dokümandaki '{fact.Topic}' konusu dikkate alındığında hangi ifade en doğru çıkarımı verir?",
                "medium" => $"Dokümana göre '{fact.Topic}' konusu ile ilgili en doğru ifade hangisidir?",
                "easy" => $"Dokümana göre '{fact.Topic}' hakkında hangi ifade doğrudur?",
                _ => $"Dokümanda '{fact.Topic}' konusu için hangi ifade desteklenmektedir?"
            };
        }

        private static string LocalDifficultyLabel(string difficulty, int index)
        {
            return difficulty switch
            {
                "easy" => "Kolay",
                "medium" => "Orta",
                "hard" => "Zor",
                _ => index % 3 == 0 ? "Kolay" : index % 3 == 1 ? "Orta" : "Zor"
            };
        }

        private static string LocalBloomLevel(string difficulty, int index)
        {
            if (difficulty == "hard")
            {
                return index % 2 == 0 ? "Analiz" : "Değerlendirme";
            }

            if (difficulty == "medium")
            {
                return index % 2 == 0 ? "Anlama" : "Uygulama";
            }

            if (difficulty == "easy")
            {
                return index % 2 == 0 ? "Hatırlama" : "Anlama";
            }

            string[] levels = { "Hatırlama", "Anlama", "Uygulama", "Analiz", "Değerlendirme" };
            return levels[index % levels.Length];
        }

        private static string ShortenTopic(string topic)
        {
            topic = topic.Trim();
            return topic.Length > 70 ? topic.Substring(0, 70).Trim() + "..." : topic;
        }

        private static List<QuizFact> ExtractQuizFacts(List<string> documentTexts)
        {
            string combinedText = string.Join("\n", documentTexts ?? new List<string>());
            combinedText = Regex.Replace(combinedText, @"[ \t]+", " ");

            var rawSentences = Regex
                .Split(combinedText, @"(?<=[.!?])\s+|\r?\n+")
                .Select(sentence => CleanQuizSentence(sentence))
                .Where(IsGoodQuizSentence)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var facts = new List<QuizFact>();
            foreach (string sentence in rawSentences)
            {
                string topic = ExtractTopicFromSentence(sentence);
                if (string.IsNullOrWhiteSpace(topic))
                {
                    continue;
                }

                facts.Add(new QuizFact
                {
                    Topic = topic,
                    Statement = sentence
                });
            }

            return facts
                .GroupBy(fact => fact.Statement, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(fact => ScoreQuizFact(fact))
                .ToList();
        }

        private static string CleanQuizSentence(string sentence)
        {
            sentence = Regex.Replace(sentence ?? string.Empty, @"\s+", " ").Trim();
            sentence = Regex.Replace(sentence, @"^[•\-–—*]+\s*", string.Empty).Trim();
            sentence = Regex.Replace(sentence, @"^\d+(\.\d+)*\.?\s*", string.Empty).Trim();

            return sentence.Length > 220
                ? sentence.Substring(0, 220).Trim()
                : sentence;
        }

        private static bool IsGoodQuizSentence(string sentence)
        {
            if (string.IsNullOrWhiteSpace(sentence) || sentence.Length < 45 || sentence.Length > 220)
            {
                return false;
            }

            if (sentence.Count(char.IsLetter) < 25)
            {
                return false;
            }

            if (Regex.IsMatch(sentence, @"^(references|kaynakça|table|figure|şekil|tablo|page|sayfa)\b", RegexOptions.IgnoreCase))
            {
                return false;
            }

            if (Regex.IsMatch(sentence, @"https?://|www\.|@"))
            {
                return false;
            }

            return sentence.Contains(" is ", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" are ", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" means ", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" refers ", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" kullan", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" olarak ", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" denir", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" sağlar", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" oluş", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" içer", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" ifade", StringComparison.OrdinalIgnoreCase) ||
                   sentence.Contains(" tanıml", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractTopicFromSentence(string sentence)
        {
            string[] separators =
            {
                " is ", " are ", " means ", " refers to ", " olarak ", " denir", " sağlar", " içerir", " ifade eder", " tanımlanır"
            };

            foreach (string separator in separators)
            {
                int index = sentence.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                if (index > 4)
                {
                    return NormalizeTopic(sentence.Substring(0, index));
                }
            }

            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(6);

            return NormalizeTopic(string.Join(" ", words));
        }

        private static string NormalizeTopic(string topic)
        {
            topic = Regex.Replace(topic ?? string.Empty, @"[^\p{L}\p{N}\s\-/]", " ");
            topic = Regex.Replace(topic, @"\s+", " ").Trim();

            if (topic.Length > 70)
            {
                topic = topic.Substring(0, 70).Trim();
            }

            return topic;
        }

        private static int ScoreQuizFact(QuizFact fact)
        {
            int score = 0;
            string statement = fact.Statement;

            if (Regex.IsMatch(statement, @"\b(is|are|means|refers|denir|tanımlanır|ifade eder)\b", RegexOptions.IgnoreCase))
            {
                score += 4;
            }

            if (Regex.IsMatch(statement, @"\b(because|therefore|however|while|ama|ancak|çünkü|bu nedenle|fakat)\b", RegexOptions.IgnoreCase))
            {
                score += 3;
            }

            if (statement.Length >= 70 && statement.Length <= 170)
            {
                score += 2;
            }

            if (fact.Topic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
            {
                score += 1;
            }

            return score;
        }

        private class QuizQuestionDto
        {
            public string Question { get; set; } = string.Empty;
            public List<string> Options { get; set; } = new();
            public int CorrectIndex { get; set; }
            public string Explanation { get; set; } = string.Empty;
            public string Topic { get; set; } = string.Empty;
            public string Difficulty { get; set; } = string.Empty;
            public string BloomLevel { get; set; } = string.Empty;
            public string SourceHint { get; set; } = string.Empty;
            public List<string> WhyWrong { get; set; } = new();
        }

        private class QuizFact
        {
            public string Topic { get; set; } = string.Empty;
            public string Statement { get; set; } = string.Empty;
        }
    }
}
