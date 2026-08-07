using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniLms.Data;
using MiniLms.Interfaces;
using MiniLms.Repositories;
using MiniLms.Services;
using MiniLms.Mappings;
using MiniLms.Models;
using MiniLms.Models.Enums;
using MiniLms.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 1. MVC Kontrolcü ve Görünüm Servislerini Ekle (Çift olan satırlardan biri temizlendi)
builder.Services.AddControllersWithViews();

// 2. Entity Framework Core ve SQL Server Veritabanı Bağlantısı
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 6;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(UserPolicies.TeacherOnly, policy =>
        policy.RequireRole(UserRoles.Teacher));

    options.AddPolicy(UserPolicies.StudentOnly, policy =>
        policy.RequireRole(UserRoles.Student));

    options.AddPolicy(UserPolicies.TeacherOrStudent, policy =>
        policy.RequireRole(UserRoles.Teacher, UserRoles.Student));
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// REPOSITORY VE SERVİS KAYITLARI (BAĞIMLILIK ENJEKSİYONU)

// Generic Repository Desteği
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// Öğrenci (Student) Katmanı Servisleri
builder.Services.AddScoped<IStudentRepository, StudentRepository>();
builder.Services.AddScoped<IStudentService, StudentService>();

// Ders (Course) Katmanı Servisleri 
builder.Services.AddScoped<ICourseRepository, CourseRepository>();
builder.Services.AddScoped<ICourseService, CourseService>();

// Ders Kayıtları (Enrollment) Katmanı Servisleri
builder.Services.AddScoped<IEnrollmentRepository, EnrollmentRepository>();
builder.Services.AddScoped<IEnrollmentService, EnrollmentService>();

// Doküman Modülü Servisleri (Eksik repository kaydı eklendi)
builder.Services.AddScoped<ICourseDocumentRepository, CourseDocumentRepository>();
builder.Services.AddScoped<ICourseDocumentService, CourseDocumentService>();

// Haftalık Konular (Lesson) Katmanı Servisleri (Eksik olanlar eklendi)
builder.Services.AddScoped<ILessonRepository, LessonRepository>();
builder.Services.AddScoped<ILessonService, LessonService>();
builder.Services.AddScoped<ILessonContentRepository, LessonContentRepository>();

// HTTPCLIENT ENTEGRASYONLU AI VE VECTOR SERVİSLERİ

builder.Services.AddHttpClient<IAiService, AiService>();
builder.Services.AddHttpClient<IVectorDbService, VectorDbService>();
builder.Services.AddHttpClient<IAzureSpeechService, AzureSpeechService>();
builder.Services.AddHostedService<VectorSyncService>();

// 4. AUTOMAPPER PROFİL HARİTALAMASINI KAYDET (HATAYI ÇÖZEN SATIR)
builder.Services.AddAutoMapper(typeof(Program));

var app = builder.Build();

// HTTP İstek İşleme Boru Hattı (Middleware Pipeline) Yapılandırması
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();

    try
    {
        dbContext.Database.ExecuteSqlRaw(@"
            IF NOT EXISTS (
                SELECT * FROM sys.columns 
                WHERE object_id = OBJECT_ID(N'[DocumentSummaries]') AND name = 'AudioFilePath'
            )
            BEGIN
                ALTER TABLE [DocumentSummaries] ADD [AudioFilePath] nvarchar(max) NULL;
            END;

            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = N'SavedQuizzes')
            BEGIN
                CREATE TABLE [SavedQuizzes] (
                    [Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [CourseId] int NOT NULL,
                    [CourseDocumentId] int NOT NULL,
                    [UserId] nvarchar(450) NOT NULL,
                    [SourceFileName] nvarchar(max) NOT NULL,
                    [Title] nvarchar(max) NOT NULL,
                    [Difficulty] nvarchar(50) NOT NULL,
                    [QuestionCount] int NOT NULL,
                    [QuestionsJson] nvarchar(max) NOT NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
            END;
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DB Migration Auto-Fix]: {ex.Message}");
    }

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { UserRoles.Teacher, UserRoles.Student })
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }
}

// Varsayılan Rota Tanımlaması (Uygulama Home/Index ile açılır)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
