using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MiniLms.Models;

namespace MiniLms.Data
{
    // 🎯 KRİTİK DEĞİŞİKLİK: Standart DbContext yerine IdentityDbContext<ApplicationUser> entegre edildi.
    // Bu sayede hem öğretmen/öğrenci giriş tabloları hem de mevcut LMS tabloları tek bir veritabanında birleşir.
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // --- MEVCUT LMS TABLOLARINIZ ---
        public DbSet<Course> Courses { get; set; }
        public DbSet<Lesson> Lessons { get; set; }
        public DbSet<LessonContent> LessonContents { get; set; }
        public DbSet<CourseDocument> CourseDocuments { get; set; }
        public DbSet<Enrollment> Enrollments { get; set; }
        public DbSet<Student> Students { get; set; }
        public DbSet<DocumentSummary> DocumentSummaries { get; set; }
        public DbSet<SavedQuiz> SavedQuizzes { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            // 🎯 ÇOK KRİTİK: Identity tablolarının (Roller, Yetkiler, Kullanıcılar) arka plandaki 
            // Fluent API yapılandırmalarının hatasız kurulması için base metodu MUTLAKA ilk satırda çağrılmalıdır.
            base.OnModelCreating(builder);

            builder.Entity<Enrollment>()
                .HasKey(e => e.Id);

            builder.Entity<Enrollment>()
                .HasIndex(e => new { e.StudentId, e.CourseId })
                .IsUnique();

            builder.Entity<Enrollment>()
                .HasOne(e => e.Student)
                .WithMany(s => s.Enrollments)
                .HasForeignKey(e => e.StudentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<Enrollment>()
                .HasOne(e => e.Course)
                .WithMany(c => c.Enrollments)
                .HasForeignKey(e => e.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<CourseDocument>()
                .HasOne(d => d.Course)
                .WithMany(c => c.Documents)
                .HasForeignKey(d => d.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<DocumentSummary>()
                .HasOne(s => s.Course)
                .WithMany()
                .HasForeignKey(s => s.CourseId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<DocumentSummary>()
                .HasOne(s => s.CourseDocument)
                .WithMany()
                .HasForeignKey(s => s.CourseDocumentId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<SavedQuiz>()
                .HasOne(q => q.Course)
                .WithMany()
                .HasForeignKey(q => q.CourseId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<SavedQuiz>()
                .HasOne(q => q.CourseDocument)
                .WithMany()
                .HasForeignKey(q => q.CourseDocumentId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
