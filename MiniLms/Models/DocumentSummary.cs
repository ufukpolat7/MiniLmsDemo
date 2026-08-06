using System;

namespace MiniLms.Models
{
    public class DocumentSummary
    {
        public int Id { get; set; }

        // Öğrenci / Kullanıcı İlişkisi
        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }

        // Ders Dokümanı (PDF/TXT) İlişkisi
        public int CourseDocumentId { get; set; }
        public CourseDocument? CourseDocument { get; set; }

        // Ders İlişkisi
        public int CourseId { get; set; }
        public Course? Course { get; set; }

        // Çıkarılan Özet Metni
        public string SummaryText { get; set; } = string.Empty;

        // Çıkarılma Tarihi
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
