using System;

namespace MiniLms.Models
{
    public class CourseDocument
    {
        public int Id { get; set; }

        // Dosyanın kullanıcı tarafından görünen orijinal adı (Örn: "Hafta1_Giris.pdf")
        public string FileName { get; set; } = string.Empty;

        // Sunucuda (wwwroot/uploads içinde) çakışmaları önlemek için saklayacağımız benzersiz yol/isim
        public string FilePath { get; set; } = string.Empty;

        // Dosyanın yüklenme tarihi
        public DateTime UploadedDate { get; set; } = DateTime.Now;

        // 🎯 1-14 Hafta Seçimi: Bu doküman hangi haftaya ait?
        public int WeekNumber { get; set; } = 1;

        // İlişki (Foreign Key): Bu doküman hangi derse ait?
        public int CourseId { get; set; }

        // Navigation Property: EF Core ilişkisi için
        public Course? Course { get; set; }
    }
}