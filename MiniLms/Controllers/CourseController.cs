using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MiniLms.Interfaces;
using MiniLms.Models;
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
        private readonly IAzureSpeechService _azureSpeechService;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _webHostEnvironment;
        private readonly MiniLms.Data.ApplicationDbContext _dbContext;
        private readonly Microsoft.AspNetCore.Identity.UserManager<MiniLms.Models.ApplicationUser> _userManager;

        public CourseController(
            ICourseService courseService,
            IAiService aiService,
            IVectorDbService vectorDbService,
            ICourseDocumentService courseDocumentService,
            IAzureSpeechService azureSpeechService,
            Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment,
            MiniLms.Data.ApplicationDbContext dbContext,
            Microsoft.AspNetCore.Identity.UserManager<MiniLms.Models.ApplicationUser> userManager)
        {
            _courseService = courseService;
            _aiService = aiService;
            _vectorDbService = vectorDbService;
            _courseDocumentService = courseDocumentService;
            _azureSpeechService = azureSpeechService;
            _webHostEnvironment = webHostEnvironment;
            _dbContext = dbContext;
            _userManager = userManager;
        }

        // Öğretmen için kendi eklediği/atandığı dersleri, öğrenci için tüm dersleri listeler
        public async Task<IActionResult> Index()
        {
            IEnumerable<MiniLms.Models.Course> courses;

            if (User.IsInRole("Teacher"))
            {
                var currentUserId = _userManager.GetUserId(User);
                if (!string.IsNullOrEmpty(currentUserId))
                {
                    courses = await _courseService.GetCoursesByTeacherIdAsync(currentUserId);
                }
                else
                {
                    courses = await _courseService.GetAllCoursesAsync();
                }
            }
            else
            {
                courses = await _courseService.GetAllCoursesAsync();
            }

            return View(courses);
        }

        // GET: Course/Create (Sadece Öğretmenler yeni ders açabilir)
        [HttpGet]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public IActionResult Create()
        {
            return View();
        }

        // POST: Course/Create
        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(MiniLms.Models.Course course)
        {
            if (ModelState.IsValid)
            {
                var currentUserId = _userManager.GetUserId(User);
                if (!string.IsNullOrEmpty(currentUserId))
                {
                    course.TeacherId = currentUserId; // 🎯 Dersi oluşturan öğretmeni kaydet
                }

                await _courseService.AddCourseAsync(course);
                TempData["SuccessMessage"] = $"'{course.Title}' dersi başarıyla oluşturuldu ve hesabınıza tanımlandı.";
                return RedirectToAction(nameof(Index));
            }
            return View(course);
        }

        // GET: Course/Edit/5 (Sadece Öğretmenler ders düzenleyebilir)
        [HttpGet]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public async Task<IActionResult> Edit(int id)
        {
            var course = await _courseService.GetCourseByIdAsync(id);
            if (course == null)
            {
                return NotFound();
            }
            return View(course);
        }

        // POST: Course/Edit/5
        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, MiniLms.Models.Course course)
        {
            if (id != course.Id)
            {
                return BadRequest();
            }

            if (ModelState.IsValid)
            {
                await _courseService.UpdateCourseAsync(course);
                TempData["SuccessMessage"] = $"'{course.Title}' ders bilgileri güncellendi.";
                return RedirectToAction(nameof(Index));
            }
            return View(course);
        }

        // GET: Course/Delete/5 (Sadece Öğretmenler ders silebilir)
        [HttpGet]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public async Task<IActionResult> Delete(int id)
        {
            var course = await _courseService.GetCourseByIdAsync(id);
            if (course == null)
            {
                return NotFound();
            }
            return View(course);
        }

        // POST: Course/Delete/5
        [HttpPost, ActionName("Delete")]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            try
            {
                await _courseService.DeleteCourseAsync(id);
                TempData["SuccessMessage"] = "Ders sistemden silindi.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                ViewBag.ErrorMessage = $"Ders silinirken hata oluştu: {ex.Message}";
                var course = await _courseService.GetCourseByIdAsync(id);
                return View(course);
            }
        }

        // Kursun detaylarını ve haftalık konularını (Lesson) getirir
        public async Task<IActionResult> Details(int id)
        {
            var course = await _courseService.GetCourseByIdAsync(id);
            if (course == null)
            {
                return NotFound();
            }

            var currentUserId = _userManager.GetUserId(User);
            var isTeacher = User.IsInRole("Teacher");

            if (isTeacher)
            {
                // 🎯 ÖĞRETMEN TARAFINDA: Derse kayıtlı öğrencileri ve her öğrencinin bu derse ait özetlerini getir
                var enrollments = await _dbContext.Enrollments
                    .Include(e => e.Student)
                    .Where(e => e.CourseId == id)
                    .ToListAsync();

                var studentSummariesList = new List<MiniLms.ViewModels.EnrolledStudentSummaryDto>();
                foreach (var enrollment in enrollments)
                {
                    var user = await _userManager.FindByEmailAsync(enrollment.Student.Email);
                    var userSummaries = new List<MiniLms.Models.DocumentSummary>();
                    if (user != null)
                    {
                        userSummaries = await _dbContext.DocumentSummaries
                            .Include(ds => ds.CourseDocument)
                            .Where(ds => ds.CourseId == id && ds.UserId == user.Id)
                            .OrderByDescending(ds => ds.CreatedAt)
                            .ToListAsync();
                    }

                    studentSummariesList.Add(new MiniLms.ViewModels.EnrolledStudentSummaryDto
                    {
                        StudentId = enrollment.Student.Id,
                        UserId = user?.Id,
                        StudentName = $"{enrollment.Student.FirstName} {enrollment.Student.LastName}".Trim(),
                        StudentNumber = enrollment.Student.StudentNumber,
                        Email = enrollment.Student.Email,
                        Summaries = userSummaries
                    });
                }

                ViewBag.EnrolledStudentSummaries = studentSummariesList;
                ViewBag.SavedSummaries = new List<MiniLms.Models.DocumentSummary>();
            }
            else
            {
                // 🎯 ÖĞRENCİ TARAFINDA: Sadece öğrencinin kendi oluşturduğu özetleri getir
                var savedSummaries = await _dbContext.DocumentSummaries
                    .Include(s => s.CourseDocument)
                    .Include(s => s.User)
                    .Where(s => s.CourseId == id && s.UserId == currentUserId)
                    .OrderByDescending(s => s.CreatedAt)
                    .ToListAsync();

                ViewBag.SavedSummaries = savedSummaries;
            }

            return View(course);
        }

        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        [RequestSizeLimit(104857600)]
        [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
        public async Task<IActionResult> UploadDocument(int courseId, IFormFile file, int weekNumber = 1)
        {
            if (file == null || file.Length == 0)
            {
                return RedirectToAction("Details", new { id = courseId });
            }

            try
            {
                await _courseDocumentService.UploadDocumentAsync(courseId, file, weekNumber);
                TempData["SuccessMessage"] = $"Doküman {weekNumber}. Hafta için başarıyla yüklendi ve indekslendi.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Doküman yüklenirken hata oluştu: {ex.Message}";
            }

            return RedirectToAction("Details", new { id = courseId });
        }

        [HttpPost]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public async Task<IActionResult> UpdateDocumentWeek(int id, int courseId, int weekNumber)
        {
            try
            {
                await _courseDocumentService.UpdateDocumentWeekAsync(id, weekNumber);
                TempData["SuccessMessage"] = $"Dokümanın ait olduğu hafta ({weekNumber}. Hafta) başarıyla güncellendi.";
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"Hafta güncellenirken hata oluştu: {ex.Message}";
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

                // Adım D: Asistanı gerçek bir akademisyen/danışman kılan AI Danışman Prompt'u hazırlıyoruz
                string finalPrompt = $@"
Sen bu dersin ve dokümanın Yapay Zekâ Akademik Danışmanı ve Özel Eğitmenisin (AI Academic Advisor & Tutor).
Görevin: Öğrencinin dersle, doküman içeriğiyle, sınav hazırlığıyla veya çalışma yöntemleriyle ilgili tüm sorularını motive edici, yardımsever, net ve akademik bir dilde yanıtlamaktır.

Aşağıda öğrencinin seçtiği ders dokümanına / ders materyallerine ait BAĞLAM (İçerik Metinleri) verilmiştir:

SEÇİLEN DOKÜMAN / KAYNAK:
{selectedSourceName}

BAĞLAM (Ders Materyali İçeriği):
{context}

ÖĞRENCİNİN SORUSU:
{question}

YAPAY ZEKÂ DANIŞMAN KURALLARI:
1. **Çalışma Tavsiyesi & Yol Haritası Soruları (Örn: 'Nereden başlayayım?', 'Nasıl çalışmalıyım?', 'Önemli noktalar nelerdir?'):**
   - Öğrenciye verilen BAĞLAM (Ders İçeriği) içerisindeki ana başlıkları ve konuları inceleyerek adım adım net bir çalışma rehberi ve çalışma sırası sun.
   - Örnek: 'Bu dokümana çalışmaya başlarken ilk olarak 1. bölümdeki temel kavramlardan başlayıp, ardından...' şeklinde yol göster.
2. **Doküman ve Konu İle İlgili Akademik Sorular:**
   - Soruyu verilen BAĞLAM ve genel akademik bilginle öğrenciye dersi kavratacak şekilde açık, anlaşılır ve öğretici biçimde yanıtla.
3. **Soruda Eksik veya Belirsiz Bir Nokta Varsa:**
   - Asla katı ve soğuk bir ifade kullanma. Öğrencinin konusunu dersle bağdaştırarak rehberlik et.
4. **Tamamen Alakasız Sorular (Ders ve Eğitim Dışı Konular):**
   - Yalnızca soru dersle veya eğitimle tamamen alakasızsa (Örn: hava durumu, spor vb.), kibarca: 'Ben bu dersin akademik danışmanıyım. Dersinizle, konularınızla veya çalışma yöntemlerinizle ilgili her türlü sorunuzda size yardımcı olmaktan memnuniyet duyarım!' şeklinde yönlendir.
5. **Üslup:** Samimi, motivasyon verici, öğretici ve profesyonel bir akademisyen/danışman üslubu kullan.
6. **FORMATLAMA VE DÜZEN KURALLARI:**
   - Yol haritası ve adım cevaplarında başlıkları '### 1. Adım: Başlık Adı' şeklinde ayrı bir başlık satırı olarak yaz.
   - Her adımın altındaki odaklanılacak noktaları ve mantığı yeni satırlarda '- **Nereye Odaklanmalısın?:** Açıklama' şeklinde maddeler halinde yaz (asla tüm maddeleri tek bir paragrafta yan yana birleştirme).
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
        public async Task<IActionResult> DocumentSummary(int courseId, int documentId, bool forceRefresh = false)
        {
            try
            {
                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                var currentUserId = _userManager.GetUserId(User);
                if (string.IsNullOrEmpty(currentUserId))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    currentUserId = currentUser?.Id;
                }
                if (string.IsNullOrEmpty(currentUserId))
                {
                    var firstStudent = await _dbContext.Users.FirstOrDefaultAsync();
                    currentUserId = firstStudent?.Id ?? "";
                }

                // 🎯 1. Eğer zorunlu yenileme istenmediyse, GİRİŞ YAPAN KULLANICININ bu dokümana ait kişisel özetini getir
                if (!forceRefresh)
                {
                    var existingSummary = await _dbContext.DocumentSummaries
                        .Include(s => s.User)
                        .Where(s => s.CourseDocumentId == documentId && s.UserId == currentUserId)
                        .OrderByDescending(s => s.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (existingSummary != null)
                    {
                        return Json(new { 
                            success = true, 
                            summaryId = existingSummary.Id, 
                            documentId = documentId,
                            documentName = document.FileName,
                            response = existingSummary.SummaryText, 
                            isCached = true,
                            authorName = existingSummary.User != null ? $"{existingSummary.User.FirstName} {existingSummary.User.LastName}".Trim() : "Sistem",
                            createdAt = existingSummary.CreatedAt.ToString("dd.MM.yyyy HH:mm")
                        });
                    }
                }

                // 🎯 2. Kayıtlı kişisel özet yoksa veya forceRefresh=true ise yeni özet üret
                var documentTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId, maxChunks: 50);
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

                // 🎯 3. Üretilen özeti giriş yapan öğrencinin ID'si ile kaydet (Varsa mevcut kişisel kaydı güncelle, yoksa ekle)
                var docSummaryRecord = await _dbContext.DocumentSummaries
                    .FirstOrDefaultAsync(s => s.CourseDocumentId == documentId && s.UserId == currentUserId);

                if (docSummaryRecord != null)
                {
                    docSummaryRecord.UserId = currentUserId;
                    docSummaryRecord.SummaryText = summary;
                    docSummaryRecord.CreatedAt = DateTime.Now;
                    _dbContext.DocumentSummaries.Update(docSummaryRecord);
                }
                else
                {
                    docSummaryRecord = new MiniLms.Models.DocumentSummary
                    {
                        UserId = currentUserId,
                        CourseId = courseId,
                        CourseDocumentId = documentId,
                        SummaryText = summary,
                        CreatedAt = DateTime.Now
                    };
                    _dbContext.DocumentSummaries.Add(docSummaryRecord);
                }

                await _dbContext.SaveChangesAsync();

                return Json(new { 
                    success = true, 
                    summaryId = docSummaryRecord.Id, 
                    documentId = documentId,
                    documentName = document.FileName,
                    response = summary, 
                    isCached = false,
                    createdAt = docSummaryRecord.CreatedAt.ToString("dd.MM.yyyy HH:mm")
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentSummary Hata]: {ex.Message}\n{ex.StackTrace}");
                return Json(new { success = false, response = $"Doküman özeti üretilirken bir sorun oluştu. Lütfen dokümanın geçerli bir PDF olduğundan emin olun ve tekrar deneyin." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAudioSummary(int courseId, int documentId, bool forceRefresh = false)
        {
            try
            {
                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, message = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                var currentUserId = _userManager.GetUserId(User);
                if (string.IsNullOrEmpty(currentUserId))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    currentUserId = currentUser?.Id ?? "";
                }

                // 🎯 1. Kullanıcının kayıtlı özeti varsa kontrol et
                var existingSummary = await _dbContext.DocumentSummaries
                    .FirstOrDefaultAsync(s => s.CourseDocumentId == documentId && s.UserId == currentUserId);

                // 🎯 2. Zorunlu yenileme istenmediyse ve ses dosyası zaten üretilmişse doğrudan dön
                if (!forceRefresh && existingSummary != null && !string.IsNullOrWhiteSpace(existingSummary.AudioFilePath))
                {
                    string physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, existingSummary.AudioFilePath.TrimStart('/'));
                    if (System.IO.File.Exists(physicalPath))
                    {
                        return Json(new { 
                            success = true, 
                            audioUrl = existingSummary.AudioFilePath, 
                            summaryText = existingSummary.SummaryText,
                            isCached = true,
                            hasAzureAudio = true
                        });
                    }
                }

                // 🎯 3. Özet metni henüz çıkarılmadıysa kullanıcıya önce metin özetini çıkartması gerektiğini bildir
                if (existingSummary == null || string.IsNullOrWhiteSpace(existingSummary.SummaryText))
                {
                    return Json(new { 
                        success = false, 
                        message = "Bu dokümanın henüz özeti çıkarılmamış. Lütfen önce 'Özetle' butonuna tıklayarak dokümanın metin özetini oluşturun." 
                    });
                }

                string textSummary = existingSummary.SummaryText;

                // 🎯 4. Sesli Özet İçin Konuşma Diline Dönüştürülmüş Akıcı Metin Üret (Hiçbir selamlama / giriş cümlesi olmadan direkt konuya giren)
                string conversationalText = await BuildConversationalSpeechTextAsync(textSummary);

                // 🎯 5. Microsoft Edge Free Nöral TTS / Azure Speech ile ses dosyasını üret
                string? audioUrl = await _azureSpeechService.GenerateAudioSummaryAsync(conversationalText, documentId, currentUserId ?? "guest");

                existingSummary.AudioFilePath = audioUrl;
                _dbContext.DocumentSummaries.Update(existingSummary);

                await _dbContext.SaveChangesAsync();

                return Json(new { 
                    success = true, 
                    audioUrl = audioUrl, 
                    summaryText = textSummary,
                    hasAzureAudio = !string.IsNullOrWhiteSpace(audioUrl)
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GetAudioSummary Hata]: {ex.Message}\n{ex.StackTrace}");
                return Json(new { success = false, message = $"Sesli özet üretilirken bir sorun oluştu: {ex.Message}" });
            }
        }

        private async Task<string> BuildConversationalSpeechTextAsync(string rawSummaryText)
        {
            if (string.IsNullOrWhiteSpace(rawSummaryText)) return string.Empty;

            string prompt = $@"
Aşağıdaki ders özet metnini doğal, akıcı ve samimi bir TÜRKÇE SESLİ ANLATIM METNİNE dönüştür.

KESİN VE ZORUNLU KURALLAR:
1. 'Merhaba', 'Selam', 'Selamlar', 'Hoş geldiniz', 'Bugün bu derste...', 'Merhaba bugün şunu yapacağız' gibi HİÇBİR GİRİŞ VEYA AÇILIŞ CÜMLESİ ASLA YAZMA.
2. İLK KELİMEDEN İTİBAREN DİREKT OLARAK DERSİN ANA KONUSUNA VE TANIMINA GİRİŞ YAP.
3. Kelime kalabalığı, ağdalı akademik jargon, resmi basılı kitap dili ve maddeli/numaralı liste yapılarından kaçın.
4. Sanki birisi konuyu karşısındakine en net ve en anlaşılır şekilde anlatıyormuş gibi akıcı, yalın ve kısa konuşma cümleleri kur.
5. Sadece seslendirilecek konuşma metnini döndür. Başlık, markdown işareti (*, #, **), madde işareti veya parantez içi not ekleme.
6. Anlatım net, öz ve dinlemesi keyifli olsun (150 - 250 kelime arası).

DERS ÖZET METNİ:
{rawSummaryText}
";

            try
            {
                string speechScript = await _aiService.SummarizeTextAsync(prompt);
                if (!IsAiServiceError(speechScript) && !string.IsNullOrWhiteSpace(speechScript) && speechScript.Length > 30)
                {
                    // Ek temizlik: Başta kalan giriş/selamlaşma kelimelerini kesip at
                    speechScript = Regex.Replace(speechScript, @"^(Merhaba|Selam|Selamlar|Hoş geldiniz|Arkadaşlar|Bugün bu derste|Bugünkü dersimizde|Bu bölümde|Merhaba bugün)[,!.\s]*", "", RegexOptions.IgnoreCase).Trim();
                    return speechScript;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConversationalSpeechText Warning]: {ex.Message}");
            }

            return rawSummaryText;
        }

        // 🎯 DOKÜMANA AİT SES KAYDINI SİL
        [HttpPost]
        public async Task<IActionResult> DeleteAudioSummary(int courseId, int documentId)
        {
            try
            {
                var currentUserId = _userManager.GetUserId(User);
                if (string.IsNullOrEmpty(currentUserId))
                {
                    var currentUser = await _userManager.GetUserAsync(User);
                    currentUserId = currentUser?.Id ?? "";
                }

                var summary = await _dbContext.DocumentSummaries
                    .FirstOrDefaultAsync(s => s.CourseDocumentId == documentId && s.UserId == currentUserId);

                if (summary != null && !string.IsNullOrWhiteSpace(summary.AudioFilePath))
                {
                    string physicalPath = Path.Combine(_webHostEnvironment.WebRootPath, summary.AudioFilePath.TrimStart('/'));
                    if (System.IO.File.Exists(physicalPath))
                    {
                        try { System.IO.File.Delete(physicalPath); } catch { }
                    }

                    summary.AudioFilePath = null;
                    _dbContext.DocumentSummaries.Update(summary);
                    await _dbContext.SaveChangesAsync();
                }

                return Json(new { success = true, message = "Dokümana ait ses kaydı veritabanından ve sunucudan başarıyla silindi." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Ses kaydı silinirken hata oluştu: {ex.Message}" });
            }
        }

        // 🎯 ÖĞRETMENİN ÖĞRENCİ BAZLI PDF ÖZETLERİNİ GÖRÜNTÜLEMESİ
        [HttpGet]
        [Authorize(Policy = UserPolicies.TeacherOnly)]
        public async Task<IActionResult> GetStudentSummariesByCourse(int courseId, int studentId)
        {
            var student = await _dbContext.Students.FindAsync(studentId);
            if (student == null)
            {
                return Json(new { success = false, message = "Öğrenci bulunamadı." });
            }

            var user = await _userManager.FindByEmailAsync(student.Email);
            if (user == null)
            {
                return Json(new { success = false, message = "Öğrenciye ait sistem kullanıcısı bulunamadı." });
            }

            var summaries = await _dbContext.DocumentSummaries
                .Include(s => s.CourseDocument)
                .Where(s => s.CourseId == courseId && s.UserId == user.Id)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    summaryId = s.Id,
                    documentName = s.CourseDocument != null ? s.CourseDocument.FileName : "Doküman",
                    documentPath = s.CourseDocument != null ? (s.CourseDocument.FilePath.StartsWith("/") ? s.CourseDocument.FilePath : "/" + s.CourseDocument.FilePath.Replace("\\", "/")) : "",
                    summaryText = s.SummaryText,
                    createdAt = s.CreatedAt.ToString("dd.MM.yyyy HH:mm")
                })
                .ToListAsync();

            return Json(new
            {
                success = true,
                studentName = $"{student.FirstName} {student.LastName}".Trim(),
                studentNumber = student.StudentNumber,
                summaries = summaries
            });
        }

        // 🎯 SADECE ÖĞRENCİLERE ÖZEL KİŞİSEL ÖZETLER SAYFASI (Sekme)
        [HttpGet]
        [Authorize(Policy = UserPolicies.StudentOnly)]
        public async Task<IActionResult> MySummaries()
        {
            var userId = _userManager.GetUserId(User);

            var summaries = await _dbContext.DocumentSummaries
                .Include(s => s.Course)
                .Include(s => s.CourseDocument)
                .Include(s => s.User)
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var savedQuizzes = await _dbContext.SavedQuizzes
                .Include(q => q.Course)
                .Include(q => q.CourseDocument)
                .Where(q => q.UserId == userId)
                .OrderByDescending(q => q.CreatedAt)
                .ToListAsync();

            ViewBag.SavedQuizzes = savedQuizzes;

            return View(summaries);
        }

        // 🎯 Özet ID ile tekil özet metnini getirme
        [HttpGet]
        public async Task<IActionResult> GetSummaryById(int summaryId)
        {
            var summary = await _dbContext.DocumentSummaries
                .Include(s => s.CourseDocument)
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == summaryId);

            if (summary == null)
            {
                return Json(new { success = false, message = "Özet bulunamadı." });
            }

            return Json(new
            {
                success = true,
                summaryId = summary.Id,
                documentId = summary.CourseDocumentId,
                documentName = summary.CourseDocument?.FileName ?? "Doküman",
                summaryText = summary.SummaryText,
                authorName = summary.User != null ? $"{summary.User.FirstName} {summary.User.LastName}".Trim() : "Sistem",
                createdAt = summary.CreatedAt.ToString("dd.MM.yyyy HH:mm")
            });
        }

        // 🎯 Öğrenci ve Öğretmenlerin Özeti Düzenlemesi (Edit)
        [HttpPost]
        public async Task<IActionResult> UpdateSummary(int summaryId, string summaryText)
        {
            try
            {
                var summary = await _dbContext.DocumentSummaries.FindAsync(summaryId);
                if (summary == null)
                {
                    return Json(new { success = false, message = "Güncellenecek özet bulunamadı." });
                }

                if (string.IsNullOrWhiteSpace(summaryText))
                {
                    return Json(new { success = false, message = "Özet metni boş olamaz." });
                }

                summary.SummaryText = summaryText;
                summary.CreatedAt = DateTime.Now;

                _dbContext.DocumentSummaries.Update(summary);
                await _dbContext.SaveChangesAsync();

                return Json(new { success = true, summaryId = summary.Id, message = "Özet başarıyla güncellendi.", response = summaryText });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Özet güncellenirken hata oluştu: {ex.Message}" });
            }
        }

        // 🎯 Özet Silme (Delete)
        [HttpPost]
        public async Task<IActionResult> DeleteSummary(int summaryId)
        {
            try
            {
                var summary = await _dbContext.DocumentSummaries.FindAsync(summaryId);
                if (summary == null)
                {
                    return Json(new { success = false, message = "Silinecek özet bulunamadı." });
                }

                _dbContext.DocumentSummaries.Remove(summary);
                await _dbContext.SaveChangesAsync();

                return Json(new { success = true, message = "Özet başarıyla silindi." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Özet silinirken hata oluştu: {ex.Message}" });
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

        // 🎯 Etkileşimli Doküman Quiz Oturumu Endpoint'i (Önbellekleme, ID Atama ve Yeniden Üretme Destekli)
        [HttpGet]
        public async Task<IActionResult> DocumentQuizSession(int courseId, int documentId, int questionCount = 5, string difficulty = "mixed", bool forceRefresh = true, int? quizId = null)
        {
            try
            {
                var userId = _userManager.GetUserId(User) ?? string.Empty;

                // 🎯 1. Eğer "Özel Özetlerim/Quizlerim" sayfasından belirli bir Quiz ID ile çağrıldıysa veritabanındaki o quizi getir!
                if (quizId.HasValue && quizId.Value > 0)
                {
                    var savedQuiz = await _dbContext.SavedQuizzes
                        .Include(q => q.CourseDocument)
                        .FirstOrDefaultAsync(q => q.Id == quizId.Value);

                    if (savedQuiz != null)
                    {
                        var quizQuestions = JsonSerializer.Deserialize<List<QuizQuestionDto>>(savedQuiz.QuestionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<QuizQuestionDto>();
                        return Json(new
                        {
                            success = true,
                            quizId = savedQuiz.Id,
                            title = $"{savedQuiz.SourceFileName} Quiz (ID: #{savedQuiz.Id})",
                            sourceFileName = savedQuiz.SourceFileName,
                            isCached = true,
                            questions = quizQuestions
                        });
                    }
                }

                var document = await _courseDocumentService.GetDocumentByIdAsync(documentId);
                if (document == null || document.CourseId != courseId)
                {
                    return Json(new { success = false, response = "Seçilen doküman bu derse ait değil veya bulunamadı." });
                }

                questionCount = Math.Clamp(questionCount, 3, 10);
                difficulty = NormalizeDifficulty(difficulty);

                // 🎯 2. Ders detayından "Quiz Oluştur" dendiğinde varsayılan olarak HER SEFERİNDE YENİ QUİZ üretilir! (forceRefresh = true)
                if (!forceRefresh)
                {
                    var existingQuiz = await _dbContext.SavedQuizzes
                        .Where(q => q.CourseDocumentId == documentId && q.UserId == userId)
                        .OrderByDescending(q => q.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (existingQuiz != null && !string.IsNullOrWhiteSpace(existingQuiz.QuestionsJson))
                    {
                        var cachedQuestions = JsonSerializer.Deserialize<List<QuizQuestionDto>>(existingQuiz.QuestionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<QuizQuestionDto>();
                        if (cachedQuestions.Count > 0)
                        {
                            return Json(new
                            {
                                success = true,
                                quizId = existingQuiz.Id,
                                title = $"{existingQuiz.SourceFileName} Quiz (ID: #{existingQuiz.Id})",
                                sourceFileName = existingQuiz.SourceFileName,
                                isCached = true,
                                questions = cachedQuestions
                            });
                        }
                    }
                }

                // 🎯 3. Kayıtlı quiz yoksa veya forceRefresh == true ise yeni quiz üret!
                var documentTexts = await _courseDocumentService.GetDocumentTextChunksAsync(documentId, maxChunks: 8);
                if (documentTexts.Count == 0)
                {
                    return Json(new { success = false, response = "Bu dokümandan quiz üretilecek metin çıkarılamadı." });
                }

                var questions = await BuildInteractiveDocumentQuizAsync(document.FileName, documentTexts, questionCount, difficulty);
                if (questions.Count == 0)
                {
                    return Json(new
                    {
                        success = false,
                        response = "Bu dokümandan kaliteli quiz sorusu çıkarılamadı. Dokümanda daha açıklayıcı metinler olduğundan emin olun."
                    });
                }

                // 🎯 4. Üretilen quizi veritabanına kaydet ve ID ata!
                string jsonQuestions = JsonSerializer.Serialize(questions);
                var newQuiz = new SavedQuiz
                {
                    CourseId = courseId,
                    CourseDocumentId = documentId,
                    UserId = userId,
                    SourceFileName = document.FileName,
                    Title = $"{document.FileName} Quiz",
                    Difficulty = difficulty,
                    QuestionCount = questions.Count,
                    QuestionsJson = jsonQuestions,
                    CreatedAt = DateTime.Now
                };

                _dbContext.SavedQuizzes.Add(newQuiz);
                await _dbContext.SaveChangesAsync();

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

        // 🎯 Kaydedilmiş Quizi Silme Endpoint'i (ID veya DocumentId ile)
        [HttpPost]
        public async Task<IActionResult> DeleteQuiz(int? quizId, int? documentId)
        {
            try
            {
                var userId = _userManager.GetUserId(User);

                SavedQuiz? quiz = null;

                if (quizId.HasValue && quizId.Value > 0)
                {
                    quiz = await _dbContext.SavedQuizzes.FirstOrDefaultAsync(q => q.Id == quizId.Value && q.UserId == userId);
                }
                
                if (quiz == null && documentId.HasValue && documentId.Value > 0)
                {
                    quiz = await _dbContext.SavedQuizzes.FirstOrDefaultAsync(q => q.CourseDocumentId == documentId.Value && q.UserId == userId);
                }

                if (quiz == null && quizId.HasValue)
                {
                    quiz = await _dbContext.SavedQuizzes.FindAsync(quizId.Value);
                }

                if (quiz == null)
                {
                    return Json(new { success = false, message = "Silinecek quiz bulunamadı veya daha önce silinmiş." });
                }

                _dbContext.SavedQuizzes.Remove(quiz);
                await _dbContext.SaveChangesAsync();

                return Json(new { success = true, quizId = quiz.Id, message = "Quiz başarıyla silindi." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"Quiz silinirken hata oluştu: {ex.Message}" });
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
            string rawText = string.Join("\n\n", documentTexts);
            string prompt = $"DOKÜMAN ADI: {fileName}\n\nDOKÜMAN METNİ:\n{rawText}";
            return Services.AiService.CleanPdfText(prompt);
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

            string bloomRulesInstruction = @"
Eğitimsel Yaklaşım ve Zorluk Derecelendirme Kuralları (Bloom Taksonomisi):

1. KOLAY SEVİYE (Hatırlama ve Kavrama):
   - Metin içinde doğrudan yer alan bilgilerin, tanımların, tarihlerin veya isimlerin sorulduğu sorulardır.
   - Öğrencinin sadece okuduğunu hatırlaması ve kavraması beklenir.
   - Örnek Soru Tipleri: 'X nedir?', 'Y olayı ne zaman gerçekleşmiştir?', 'Z tanımı ne anlama gelir?'

2. ORTA SEVİYE (Uygulama ve Analiz):
   - Doğrudan ezber yerine, metindeki bilginin yorumlanmasını veya iki farklı kavramın karşılaştırılmasını gerektiren sorulardır.
   - Öğrencinin konunun mantığını anlamış, uygulayabilir ve analiz edebilir olması gerekir.
   - Örnek Soru Tipleri: 'X ve Y arasındaki temel fark nedir?', 'Bu duruma metinden hangi örnek verilebilir?', 'X mekanizmasının çalışma mantığı hangisidir?'

3. ZOR SEVİYE (Değerlendirme ve Sentez):
   - Parçanın bütününe hakimiyet gerektiren, doğrudan metinde yazmayan ancak metinden çıkarım (inference) yapılmasıyla bulunabilecek sorulardır.
   - Birden fazla paragraftaki veya sayfadaki bilgiyi birleştirerek (multi-hop reasoning) çözüme ulaşmayı gerektirir.
   - Örnek Soru Tipleri: 'Metindeki bilgilere dayanarak hangi genel yargıya varılabilir?', 'X ve Y süreçleri birlikte düşünüldüğünde ortaya çıkacak sonuç nedir?'
";

            string difficultyInstruction = difficulty switch
            {
                "easy" => "Sadece KOLAY seviye (Hatırlama ve Kavrama) sorular üret. Metindeki tanımları ve doğrudan bilgileri yokla.",
                "medium" => "Sadece ORTA seviye (Uygulama ve Analiz) sorular üret. Yorumlama, analiz ve kavram karşılaştırması gerektiren sorular sor.",
                "hard" => "Sadece ZOR seviye (Değerlendirme ve Sentez) sorular üret. Doğrudan metinde yazmayan, metinden çıkarım (inference) ve çoklu paragraf birleştirmesi (multi-hop reasoning) gerektiren üst düzey sorular sor.",
                _ => "Dengeli karma dağılım yap: %30 Kolay (Hatırlama/Kavrama), %40 Orta (Uygulama/Analiz), %30 Zor (Değerlendirme/Sentez) soruları ekle."
            };

            string jsonPrompt = $@"
Aşağıdaki ders dokümanına dayanarak Bloom Taksonomisi prensiplerine tam uyumlu Türkçe {questionCount} adet çoktan seçmeli quiz sorusu üret.

{bloomRulesInstruction}

SEÇİLEN ZORLUK STRATEJİSİ:
{difficultyInstruction}

Sadece geçerli JSON döndür. Markdown, açıklama veya kod bloğu kullanma.
JSON formatı:
[
  {{
    ""question"": ""Soru metni"",
    ""options"": [""A seçeneği"", ""B seçeneği"", ""C seçeneği"", ""D seçeneği""],
    ""correctIndex"": 0,
    ""explanation"": ""Doğru cevabı açıklayan detaylı gerekçe"",
    ""topic"": ""Soru konusu"",
    ""difficulty"": ""Kolay | Orta | Zor"",
    ""bloomLevel"": ""Hatırlama ve Kavrama | Uygulama ve Analiz | Değerlendirme ve Sentez"",
    ""sourceHint"": ""Sorunun dayandığı kısa kaynak ipucu"",
    ""whyWrong"": [""A yanlışsa nedeni"", ""B yanlışsa nedeni"", ""C yanlışsa nedeni"", ""D yanlışsa nedeni""]
  }}
]

Kurallar:
- Sorular yalnızca doküman metnine dayansın.
- correctIndex 0 ile 3 arasında sayı olsun.
- Her soruda tam 4 seçenek olsun.
- Yanlış seçenekler dokümandaki yakın kavramlardan türetilsin, bariz komik/kolay olmasın.
- Bloom seviyelerine tam olarak uy: Kolay soruda doğrudan metinsel tanım/hatırlama, orta soruda ilişki/analiz, zor soruda sentez/çıkarım tekniklerini uygula.
- Zor sorularda doğrudan metindeki cümleyi kopyalama; öğrencinin metindeki birden fazla bilgiyi birleştirerek çıkarım yapmasını zorunlu kıl.
- whyWrong dizisi tam 4 elemanlı olsun; doğru seçenek için 'Doğru seçenek.' yaz.

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

        // POST: Course/RecordWrongAnswer (Yanlış yapılan soruları veritabanına kaydeder)
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> RecordWrongAnswer([FromBody] RecordWrongAnswerDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.QuestionText))
            {
                return BadRequest("Geçersiz veri.");
            }

            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var existingGap = await _dbContext.StudentKnowledgeGaps
                .FirstOrDefaultAsync(g => g.UserId == userId && g.CourseId == dto.CourseId && g.QuestionText == dto.QuestionText);

            if (existingGap != null)
            {
                existingGap.WrongCount++;
                existingGap.CreatedAt = DateTime.Now;
                existingGap.SelectedAnswer = dto.SelectedAnswer;
            }
            else
            {
                var newGap = new MiniLms.Models.StudentKnowledgeGap
                {
                    UserId = userId,
                    CourseId = dto.CourseId,
                    CourseDocumentId = dto.CourseDocumentId,
                    QuestionText = dto.QuestionText,
                    SelectedAnswer = dto.SelectedAnswer,
                    CorrectAnswer = dto.CorrectAnswer,
                    TopicName = string.IsNullOrWhiteSpace(dto.TopicName) ? "Genel Konu" : dto.TopicName,
                    CreatedAt = DateTime.Now,
                    WrongCount = 1
                };
                await _dbContext.StudentKnowledgeGaps.AddAsync(newGap);
            }

            await _dbContext.SaveChangesAsync();
            return Json(new { success = true });
        }

        // GET: Course/MyKnowledgeGaps (Öğrencinin Yanlış Cevapladığı Sorular & Zayıf Konuları)
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> MyKnowledgeGaps()
        {
            var userId = _userManager.GetUserId(User);
            var gaps = await _dbContext.StudentKnowledgeGaps
                .Include(g => g.Course)
                .Include(g => g.Document)
                .Where(g => g.UserId == userId)
                .OrderByDescending(g => g.WrongCount)
                .ThenByDescending(g => g.CreatedAt)
                .ToListAsync();

            return View(gaps);
        }

        public class RecordWrongAnswerDto
        {
            public int CourseId { get; set; }
            public int CourseDocumentId { get; set; }
            public string QuestionText { get; set; } = string.Empty;
            public string SelectedAnswer { get; set; } = string.Empty;
            public string CorrectAnswer { get; set; } = string.Empty;
            public string TopicName { get; set; } = string.Empty;
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
