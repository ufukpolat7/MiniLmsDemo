using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MiniLms.Models
{
    public class StudentKnowledgeGap
    {
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = string.Empty;

        [ForeignKey("UserId")]
        public ApplicationUser? User { get; set; }

        public int CourseId { get; set; }

        [ForeignKey("CourseId")]
        public Course? Course { get; set; }

        public int CourseDocumentId { get; set; }

        [ForeignKey("CourseDocumentId")]
        public CourseDocument? Document { get; set; }

        public string QuestionText { get; set; } = string.Empty;

        public string SelectedAnswer { get; set; } = string.Empty;

        public string CorrectAnswer { get; set; } = string.Empty;

        public string TopicName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public int WrongCount { get; set; } = 1;
    }
}
