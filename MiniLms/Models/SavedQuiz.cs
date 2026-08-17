using System;

namespace MiniLms.Models
{
    public class SavedQuiz
    {
        public int Id { get; set; }
        
        public int CourseId { get; set; }
        public Course? Course { get; set; }

        public int CourseDocumentId { get; set; }
        public CourseDocument? CourseDocument { get; set; }

        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        public string SourceFileName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Difficulty { get; set; } = "mixed";
        public int QuestionCount { get; set; } = 5;

        public string QuestionsJson { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Öğretmen Konu Odağı ve Yayınlama Alanları
        public string? TopicFocus { get; set; }
        public bool IsTeacherPublished { get; set; } = false;
        public string? PublishedByTeacherId { get; set; }
        public ApplicationUser? PublishedByTeacher { get; set; }
        public DateTime? PublishedAt { get; set; }
    }
}
